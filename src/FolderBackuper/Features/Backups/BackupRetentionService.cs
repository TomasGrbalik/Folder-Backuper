using System.IO.Compression;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Backups;

public sealed class BackupRetentionService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate,
    InstallationIdentityService installationIdentity,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<BackupProblem>> ApplyAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var warnings = new List<BackupProblem>();
        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        var candidates = await BeginExcessDeletionsAsync(runId, cancellationToken);
        foreach (var candidate in candidates)
        {
            try
            {
                var path = Path.Combine(candidate.EffectivePath, candidate.FinalFileName);
                if (!Owns(path, candidate, installationId))
                {
                    await MarkFailedAsync(candidate.Id, ownershipRefused: true, cancellationToken);
                    warnings.Add(Warning(path, "Retention ownership verification", "The registered archive was not deleted because ownership could not be proven."));
                    continue;
                }

                File.Delete(path);
                await MarkRemovedAsync(candidate.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await MarkFailedAsync(candidate.Id, ownershipRefused: false, cancellationToken);
                warnings.Add(Warning(Path.Combine(candidate.EffectivePath, candidate.FinalFileName), "Delete retained archive",
                    "The retained archive could not be deleted.", exception));
            }
        }

        await RefreshAggregatesAsync(runId, cancellationToken);
        return warnings;
    }

    private async Task<List<BackupArtifact>> BeginExcessDeletionsAsync(Guid runId, CancellationToken cancellationToken)
    {
        return await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == runId, ct);
            var retained = await context.BackupArtifacts.Include(item => item.Run)
                .Where(item => item.Run!.JobId == run.JobId && item.State == ArtifactState.Retained &&
                    item.RetentionState == RetentionOperationState.None)
                .ToListAsync(ct);
            retained = retained.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).ToList();
            var excess = Math.Max(0, retained.Count - run.RetentionCount);
            var candidates = retained.Take(excess).ToList();
            foreach (var artifact in candidates) artifact.BeginRetentionDeletion(timeProvider.GetUtcNow());
            await context.SaveChangesAsync(ct);
            return candidates;
        }, cancellationToken);
    }

    private async Task MarkRemovedAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        await ChangeArtifactAsync(artifactId, artifact => artifact.MarkRemovedByRetention(timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task MarkFailedAsync(Guid artifactId, bool ownershipRefused, CancellationToken cancellationToken)
    {
        await ChangeArtifactAsync(artifactId, artifact => artifact.MarkRetentionFailed(ownershipRefused, timeProvider.GetUtcNow()), cancellationToken);
    }

    private async Task ChangeArtifactAsync(Guid artifactId, Action<BackupArtifact> change, CancellationToken cancellationToken)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var artifact = await context.BackupArtifacts.SingleAsync(item => item.Id == artifactId, ct);
            change(artifact);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task RefreshAggregatesAsync(Guid runId, CancellationToken cancellationToken)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == runId, ct);
            var retained = await context.BackupArtifacts.Include(item => item.Run)
                .Where(item => item.Run!.JobId == run.JobId && item.State == ArtifactState.Retained)
                .ToListAsync(ct);
            retained = retained.OrderByDescending(item => item.CreatedAtUtc).ToList();
            var job = await context.Jobs.SingleAsync(item => item.Id == run.JobId, ct);
            job.ManagedArtifactCount = retained.Count;
            job.ManagedArtifactBytes = retained.Sum(item => item.Size);
            job.LatestArtifactBytes = retained.FirstOrDefault()?.Size;
            job.StorageConfirmedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private static bool Owns(string path, BackupArtifact artifact, Guid installationId)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.OwnershipExpectedLength) return false;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return ArchiveOwnership.TryParse(archive.Comment, out var ownership) &&
                ownership.InstallationId == installationId && ownership.RunId == artifact.OwnershipRunId;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static BackupProblem Warning(string path, string operation, string message, Exception? exception = null) =>
        new(BackupProblemSeverity.Warning, BackupProblemCategory.CleanupFailed, RunPhase.Finalizing, operation, message,
            path, exception?.HResult & 0xFFFF);
}
