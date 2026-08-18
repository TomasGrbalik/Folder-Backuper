using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Monitoring;

/// <summary>
/// Produces calendar/agenda entries by unioning durable past run attempts with future planned occurrences.
/// Planned entries come from the same production occurrence calculator used by the scheduler, so displayed
/// future events agree with execution behavior.
/// </summary>
public sealed class CalendarEntryService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    CalendarOccurrenceService plannedOccurrences,
    IMachineTimeZoneProvider timeZones,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<CalendarEntry>> GetEntriesAsync(
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset throughUtcExclusive,
        RunHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (throughUtcExclusive <= fromUtcInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(throughUtcExclusive));
        }

        var now = timeProvider.GetUtcNow();
        var entries = new List<CalendarEntry>();
        entries.AddRange(await GetPastAsync(fromUtcInclusive, throughUtcExclusive, filter, cancellationToken));

        // Planned entries are only added when the status filter admits entries without an outcome.
        if (filter.Status == RunStatusFilter.All)
        {
            var plannedFrom = fromUtcInclusive > now ? fromUtcInclusive : now;
            if (plannedFrom < throughUtcExclusive)
            {
                var planned = await plannedOccurrences.GetPlannedAsync(plannedFrom, throughUtcExclusive, cancellationToken);
                foreach (var occurrence in planned)
                {
                    if (filter.JobId is { } jobId && occurrence.JobId != jobId)
                    {
                        continue;
                    }

                    entries.Add(new CalendarEntry(null, occurrence.JobId, occurrence.JobName,
                        occurrence.LocalDate, occurrence.LocalTime, occurrence.OccursAtUtc,
                        IsPlanned: true, Trigger: null, Phase: RunPhase.Planned, Outcome: null));
                }
            }
        }

        return entries
            .OrderBy(x => x.OccursAtUtc).ThenBy(x => x.JobName).ThenBy(x => x.JobId)
            .ToArray();
    }

    private async Task<IReadOnlyList<CalendarEntry>> GetPastAsync(
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset throughUtcExclusive,
        RunHistoryFilter filter,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Runs.AsNoTracking().Where(x => x.Phase != RunPhase.Planned);
        if (filter.JobId is { } jobId)
        {
            query = query.Where(x => x.JobId == jobId);
        }

        query = filter.Status switch
        {
            RunStatusFilter.Successful => query.Where(x => x.Outcome == RunOutcome.Successful),
            RunStatusFilter.Warnings => query.Where(x => x.Outcome == RunOutcome.SuccessfulWithWarnings),
            RunStatusFilter.Failed => query.Where(x => x.Outcome == RunOutcome.Failed),
            RunStatusFilter.Cancelled => query.Where(x => x.Outcome == RunOutcome.Cancelled),
            _ => query
        };

        var runs = await query
            .Select(x => new PastRunProjection(
                x.Id, x.JobId, x.JobName, x.Trigger, x.Phase, x.Outcome,
                x.StartedAtUtc, x.QueuedAtUtc, x.DueAtUtc, x.TimeZoneId))
            .ToListAsync(cancellationToken);

        var machineZone = timeZones.GetCurrent();
        var result = new List<CalendarEntry>(runs.Count);
        foreach (var run in runs)
        {
            var instant = run.StartedAtUtc ?? run.QueuedAtUtc;
            if (instant < fromUtcInclusive || instant >= throughUtcExclusive)
            {
                continue;
            }

            var zone = ResolveZone(run.TimeZoneId, machineZone);
            var local = TimeZoneInfo.ConvertTime(instant, zone);
            result.Add(new CalendarEntry(run.Id, run.JobId, run.JobName,
                DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime), instant,
                IsPlanned: false, run.Trigger, run.Phase, run.Outcome));
        }

        return result;
    }

    private static TimeZoneInfo ResolveZone(string timeZoneId, TimeZoneInfo fallback)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return fallback;
        }
    }

    private sealed record PastRunProjection(
        Guid Id, Guid JobId, string JobName, RunTrigger Trigger, RunPhase Phase, RunOutcome? Outcome,
        DateTimeOffset? StartedAtUtc, DateTimeOffset QueuedAtUtc, DateTimeOffset DueAtUtc, string TimeZoneId);
}
