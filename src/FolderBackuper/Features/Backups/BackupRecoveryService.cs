using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Backups;

public sealed class BackupRecoveryService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    RunPersistenceService runs,
    BackupRetentionService retention)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await context.Runs.Include(run => run.Artifact).AsNoTracking()
            .Where(run => run.Outcome == null && run.Phase != RunPhase.Queued)
            .ToListAsync(cancellationToken);

        foreach (var run in pending)
        {
            if (run.FinalCommittedAtUtc is not null)
            {
                await retention.ApplyAsync(run.Id, cancellationToken);
                await runs.CompleteAsync(run.Id, RunOutcome.Successful, null, cancellationToken);
                continue;
            }

            if (run.FinalCommitStartedAtUtc is not null)
            {
                var artifact = run.Artifact;
                if (artifact is not null && File.Exists(Path.Combine(artifact.EffectivePath, artifact.FinalFileName)))
                {
                    await runs.MarkFinalCommittedAsync(run.Id, cancellationToken);
                    await retention.ApplyAsync(run.Id, cancellationToken);
                    await runs.CompleteAsync(run.Id, RunOutcome.Successful, null, cancellationToken);
                    continue;
                }

                DeleteOwnedPath(run.DestinationPartialPath);
                await runs.CompleteAsync(run.Id, RunOutcome.Failed,
                    "Backup finalization was interrupted before the destination rename could be reconciled.", cancellationToken);
                continue;
            }

            DeleteOwnedPath(run.StagingPath);
            DeleteOwnedPath(run.DestinationPartialPath);
            await runs.CompleteAsync(run.Id, RunOutcome.Failed,
                "Backup execution was interrupted before finalization.", cancellationToken);
        }
    }

    private static void DeleteOwnedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The terminal error preserves the interruption; cleanup is retried only when a path remains registered.
        }
    }
}
