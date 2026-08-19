using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Scheduling;

public interface IMachineTimeZoneProvider
{
    TimeZoneInfo GetCurrent();
}

public sealed class MachineTimeZoneProvider : IMachineTimeZoneProvider
{
    public TimeZoneInfo GetCurrent() => TimeZoneInfo.Local;
}

public sealed record SchedulerEvaluationResult(int QueuedRuns, int CoalescedOccurrences);

public sealed class BackupScheduler(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate,
    ScheduleOccurrenceCalculator calculator,
    IMachineTimeZoneProvider timeZones,
    TimeProvider timeProvider)
{
    public Task<SchedulerEvaluationResult> EvaluateAsync(CancellationToken cancellationToken = default) =>
        mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            var now = timeProvider.GetUtcNow();
            var timeZone = timeZones.GetCurrent();
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            var jobs = await context.Jobs.Include(x => x.Destination)
                .Where(x => x.Lifecycle == JobLifecycle.Active)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
            var queued = 0;
            var coalesced = 0;

            foreach (var job in jobs)
            {
                var schedule = ToSchedule(job);
                var boundary = job.ScheduleEvaluatedThroughUtc is { } evaluated && evaluated > job.ScheduleEffectiveFromUtc
                    ? evaluated
                    : job.ScheduleEffectiveFromUtc;
                if (job.NextOccurrenceLocalDate is { } plannedLocalDate)
                {
                    var reResolved = calculator.ResolveOccurrence(schedule, timeZone, plannedLocalDate);
                    if (reResolved.OccursAtUtc <= boundary && reResolved.OccursAtUtc > DateTimeOffset.MinValue)
                        boundary = reResolved.OccursAtUtc.AddTicks(-1);
                }
                if (now <= boundary)
                {
                    SetNext(job, calculator.GetNextOccurrence(schedule, timeZone, boundary));
                    continue;
                }

                var missed = calculator.FindMissedOccurrences(schedule, timeZone, boundary, now);
                if (missed.Count != 0)
                {
                    var latest = missed[^1];
                    var existingOccurrence = await context.ScheduledOccurrences.SingleOrDefaultAsync(x =>
                        x.JobId == job.Id && x.ScheduleRevision == latest.ScheduleRevision &&
                        x.ScheduledLocalDate == latest.LocalDate, ct);
                    if (existingOccurrence is null)
                    {
                        var activeRun = await context.Runs.Include(x => x.Occurrence).SingleOrDefaultAsync(x =>
                            x.JobId == job.Id && x.Outcome == null && x.Phase != RunPhase.Planned, ct);
                        if (activeRun is not null &&
                            (activeRun.Trigger != RunTrigger.Manual || activeRun.Occurrence is not null))
                        {
                            SetNext(job, latest);
                            continue;
                        }

                        var occurrence = CreateOccurrence(job.Id, latest);
                        if (activeRun is not null)
                        {
                            occurrence.RunId = activeRun.Id;
                            coalesced++;
                        }
                        else
                        {
                            var wasPlanned = job.NextOccurrenceAtUtc == latest.OccursAtUtc &&
                                job.NextOccurrenceLocalDate == latest.LocalDate;
                            var trigger = missed.Count == 1 && wasPlanned
                                ? RunTrigger.Scheduled
                                : RunTrigger.CatchUp;
                            var run = RunPersistenceService.CreateSnapshot(
                                job, job.Destination!, trigger, latest.OccursAtUtc, now, latest);
                            run.AdvanceTo(RunPhase.Queued, now);
                            context.Runs.Add(run);
                            occurrence.RunId = run.Id;
                            queued++;
                        }
                        context.ScheduledOccurrences.Add(occurrence);
                    }

                    job.LastSatisfiedScheduleRevision = latest.ScheduleRevision;
                    job.LastSatisfiedScheduledLocalDate = latest.LocalDate;
                }

                job.ScheduleEvaluatedThroughUtc = now;
                SetNext(job, calculator.GetNextOccurrence(schedule, timeZone, now));
            }

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SchedulerEvaluationResult(queued, coalesced);
        }, cancellationToken);

    private static WeeklySchedule ToSchedule(BackupJob job) => new(
        new ScheduleWeekdays(job.Weekdays),
        new ScheduleLocalTime(job.ScheduledTime),
        job.ScheduleRevision,
        job.ScheduleEffectiveFromUtc);

    private static ScheduledOccurrence CreateOccurrence(Guid jobId, ScheduleOccurrence occurrence) => new()
    {
        JobId = jobId,
        ScheduleRevision = occurrence.ScheduleRevision,
        ScheduledLocalDate = occurrence.LocalDate,
        ScheduledLocalTime = occurrence.LocalTime.Value,
        OccursAtUtc = occurrence.OccursAtUtc,
        TimeZoneId = occurrence.TimeZoneId,
        UtcOffsetMinutes = occurrence.UtcOffsetMinutes
    };

    private static void SetNext(BackupJob job, ScheduleOccurrence occurrence)
    {
        job.NextOccurrenceAtUtc = occurrence.OccursAtUtc;
        job.NextOccurrenceLocalDate = occurrence.LocalDate;
        job.NextOccurrenceTimeZoneId = occurrence.TimeZoneId;
        job.NextOccurrenceUtcOffsetMinutes = occurrence.UtcOffsetMinutes;
    }
}

public sealed class BackupSchedulerWorker(
    BackupScheduler scheduler,
    BackupExecutionQueue queue,
    RunActivitySignal activity,
    StartupRecoveryBarrier startupRecovery,
    TimeProvider timeProvider,
    ILogger<BackupSchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await startupRecovery.WaitAsync(stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await scheduler.EvaluateAsync(stoppingToken);
                if (result.QueuedRuns != 0) queue.Signal();

                // The scheduler writes runs and job planning columns directly, so its evaluation is the
                // one durable change that run persistence cannot announce. Every pass is announced, not
                // only a queueing one, because each pass also re-plans next-run times.
                activity.Signal();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled backup evaluation failed");
            }

            await Task.Delay(EvaluationInterval, timeProvider, stoppingToken);
        }
    }
}
