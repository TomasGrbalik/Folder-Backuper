using System.Threading.Channels;
using FolderBackuper.Infrastructure.Database;
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

    public CancellationToken Register(Guid runId, CancellationToken serviceStopping)
    {
        lock (sync)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping);
            active.Add(runId, source);
            return source.Token;
        }
    }

    public void Request(Guid runId)
    {
        lock (sync)
        {
            if (active.TryGetValue(runId, out var source)) source.Cancel();
        }
    }

    public void Remove(Guid runId)
    {
        lock (sync)
        {
            if (active.Remove(runId, out var source)) source.Dispose();
        }
    }
}

public sealed class BackupExecutionWorker(
    BackupExecutionQueue queue,
    BackupCancellationRegistry cancellations,
    RunPersistenceService runs,
    BackupEngine engine,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    var token = cancellations.Register(run.Id, stoppingToken);
                    try
                    {
                        var result = await engine.ExecuteAsync(new(run.Id, run.JobId, timeProvider.GetUtcNow()), token);
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
    BackupCancellationRegistry cancellations)
{
    public async Task<ManualRunEnqueueOutcome> RunNowAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var outcome = await runs.EnqueueManualAsync(jobId, cancellationToken);
        if (outcome.Status == ManualRunEnqueueStatus.Queued) queue.Signal();
        return outcome;
    }

    public async Task<RunCancellationOutcome> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var outcome = await runs.RequestCancellationAsync(runId, cancellationToken);
        if (outcome.Status == RunCancellationStatus.Requested) cancellations.Request(runId);
        return outcome;
    }
}
