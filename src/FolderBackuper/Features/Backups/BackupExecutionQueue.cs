using System.Threading.Channels;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.ServiceHosting;
using FolderBackuper.Features.Settings;
using Microsoft.Extensions.Hosting;

namespace FolderBackuper.Features.Backups;

public sealed class BackupExecutionQueue
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public void Signal() => wake.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) => wake.Reader.ReadAsync(cancellationToken);
}

public sealed class BackupCancellationRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, CancellationTokenSource> active = [];
    private readonly HashSet<Guid> pending = [];

    public CancellationToken Register(Guid runId)
    {
        lock (sync)
        {
            var source = new CancellationTokenSource();
            active.Add(runId, source);
            if (pending.Remove(runId)) source.Cancel();
            return source.Token;
        }
    }

    public void Request(Guid runId)
    {
        lock (sync)
        {
            if (active.TryGetValue(runId, out var source)) source.Cancel();
            else pending.Add(runId);
        }
    }

    public void Remove(Guid runId)
    {
        lock (sync)
        {
            if (active.Remove(runId, out var source)) source.Dispose();
            pending.Remove(runId);
        }
    }
}

public sealed class BackupExecutionWorker(
    BackupExecutionQueue queue,
    BackupCancellationRegistry cancellations,
    RunPersistenceService runs,
    BackupEngine engine,
    BackupRetentionService retention,
    BackupRecoveryService recovery,
    StartupRecoveryBarrier startupRecovery,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startupRecovery.WaitAsync(stoppingToken);
        queue.Signal(); // Durable rows may predate this process.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.WaitAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var run = await runs.ClaimNextAsync(stoppingToken);
                    if (run is null) break;
                    var token = cancellations.Register(run.Id);
                    try
                    {
                        BackupEngineResult result;
                        try
                        {
                            result = await engine.ExecuteAsync(
                                new(run.Id, run.JobId, timeProvider.GetUtcNow()), token, stoppingToken);
                        }
                        catch (FinalCommitRecoveryRequiredException)
                        {
                            await recovery.RecoverRunAsync(run.Id, CancellationToken.None);
                            continue;
                        }
                        if (result.Outcome is RunOutcome.Successful or RunOutcome.SuccessfulWithWarnings)
                        {
                            var warnings = await retention.ApplyAsync(run.Id, stoppingToken);
                            if (warnings.Count > 0)
                            {
                                result = result with { Outcome = RunOutcome.SuccessfulWithWarnings, Problems = result.Problems.Concat(warnings).ToArray() };
                            }
                        }
                        await runs.RecordExecutionResultAsync(result, CancellationToken.None);
                        await runs.CompleteAsync(run.Id, result.Outcome,
                            result.Problems.FirstOrDefault(problem => problem.Severity == BackupProblemSeverity.Error)?.Message,
                            CancellationToken.None);
                    }
                    finally
                    {
                        cancellations.Remove(run.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

public sealed class BackupExecutionService(
    RunPersistenceService runs,
    BackupExecutionQueue queue,
    BackupCancellationRegistry cancellations,
    EffectiveDestinationService effectiveDestinations,
    InstallationIdentityService installationIdentity,
    OwnershipMarkerService ownershipMarkers)
{
    public async Task<ManualRunEnqueueOutcome> RunNowAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var outcome = await runs.EnqueueManualAsync(jobId, ValidateOwnershipAsync, cancellationToken);
        if (outcome.Status == ManualRunEnqueueStatus.Queued) queue.Signal();
        return outcome;

        async Task<string?> ValidateOwnershipAsync(
            Features.Jobs.BackupJob job,
            Features.Destinations.Destination destination,
            CancellationToken ct)
        {
            if (destination.VerificationResult != Features.Destinations.DestinationVerificationResult.Succeeded ||
                string.IsNullOrWhiteSpace(destination.VerificationFingerprint))
                return "The destination must have a current successful verification before queueing.";
            var effective = await effectiveDestinations.ResolveAsync(
                destination, job.DestinationSubfolder, job.SourcePath, create: false, ct);
            if (!effective.Succeeded || effective.EffectivePath is null)
                return effective.Message;
            var installationId = await installationIdentity.GetInstallationIdAsync(ct);
            var marker = await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                effectiveDestinations.Configuration(destination),
                () => ownershipMarkers.VerifyAsync(effective.EffectivePath, installationId, job.Id, ct));
            return marker.Result == OwnershipMarkerResult.Owned ? null : marker.Message;
        }
    }

    public async Task<RunCancellationOutcome> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var outcome = await runs.RequestCancellationAsync(runId, cancellationToken);
        if (outcome.Status == RunCancellationStatus.Requested) cancellations.Request(runId);
        return outcome;
    }
}
