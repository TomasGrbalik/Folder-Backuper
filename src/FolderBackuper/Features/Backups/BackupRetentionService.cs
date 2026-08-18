using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Backups;

public sealed class BackupRetentionService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate,
    RunPersistenceService runs,
    InstallationIdentityService installationIdentity,
    EffectiveDestinationService effectiveDestinations,
    OwnershipMarkerService ownershipMarkers,
    BackupArtifactOwnershipVerifier ownershipVerifier,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<BackupProblem>> ApplyAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.Runs.AsNoTracking().SingleAsync(item => item.Id == runId, cancellationToken);
        var warnings = new List<BackupProblem>();
        warnings.AddRange(await ReconcilePendingForJobAsync(run.JobId, cancellationToken));
        var candidates = await BeginExcessDeletionsAsync(runId, cancellationToken);
        warnings.AddRange(await ProcessAsync(candidates, cancellationToken));
        await RefreshAggregatesAsync(run.JobId, cancellationToken);
        return warnings;
    }

    /// <summary>
    /// Non-destructively verifies each currently retained artifact for a job on explicit UI request. Present,
    /// owned archives are kept; archives that are gone or no longer prove ownership are marked found-missing.
    /// When the destination is unavailable the last-confirmed totals and timestamp are preserved untouched.
    /// This never deletes files and never mutates unmanaged or historical artifacts.
    /// </summary>
    public async Task<InventoryReconcileResult> ReconcileInventoryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        List<BackupArtifact> retained;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            retained = await context.BackupArtifacts.AsNoTracking().Include(item => item.Run)
                .Where(item => item.Run!.JobId == jobId && item.State == ArtifactState.Retained &&
                    item.RetentionState == RetentionOperationState.None)
                .ToListAsync(cancellationToken);
        }

        if (retained.Count == 0)
        {
            await RefreshAggregatesAsync(jobId, cancellationToken);
            return new InventoryReconcileResult(true, 0, 0);
        }

        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        var missing = new List<Guid>();
        foreach (var artifact in retained)
        {
            var path = Path.Combine(artifact.EffectivePath, artifact.FinalFileName);
            OwnedArchiveResult result;
            try
            {
                var destination = await DestinationSnapshotAsync(artifact.Run!, cancellationToken);
                if (destination is null)
                {
                    return new InventoryReconcileResult(false, 0, 0);
                }

                result = await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                    effectiveDestinations.Configuration(destination),
                    () => Task.FromResult(ownershipVerifier.Inspect(path, artifact, installationId, artifact.EffectivePath)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
            {
                return new InventoryReconcileResult(false, 0, 0);
            }

            switch (result)
            {
                case OwnedArchiveResult.Owned:
                    break;
                case OwnedArchiveResult.Missing:
                case OwnedArchiveResult.OwnershipMismatch:
                    missing.Add(artifact.Id);
                    break;
                default:
                    // Access could not be established; treat the destination as unavailable and preserve totals.
                    return new InventoryReconcileResult(false, 0, 0);
            }
        }

        foreach (var artifactId in missing)
        {
            await ChangeArtifactAsync(artifactId, artifact => artifact.MarkMissing(timeProvider.GetUtcNow()), cancellationToken);
        }

        await RefreshAggregatesAsync(jobId, cancellationToken);
        return new InventoryReconcileResult(true, retained.Count, missing.Count);
    }

    public async Task<IReadOnlyList<BackupProblem>> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var jobIds = await context.BackupArtifacts.AsNoTracking()
            .Where(item => item.RetentionState == RetentionOperationState.PendingDeletion)
            .Select(item => item.Run!.JobId).Distinct().ToListAsync(cancellationToken);
        var warnings = new List<BackupProblem>();
        foreach (var jobId in jobIds)
        {
            await using var pendingContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var requestingRuns = await pendingContext.BackupArtifacts.AsNoTracking()
                .Where(item => item.Run!.JobId == jobId &&
                    item.RetentionState == RetentionOperationState.PendingDeletion &&
                    item.RetentionRequestedByRunId != null)
                .Select(item => item.RetentionRequestedByRunId!.Value).Distinct().ToListAsync(cancellationToken);
            var recoveredWarnings = await ReconcilePendingForJobAsync(jobId, cancellationToken);
            warnings.AddRange(recoveredWarnings);
            foreach (var requestingRunId in requestingRuns)
                await runs.AppendProblemsAsync(requestingRunId, recoveredWarnings, cancellationToken);
            await RefreshAggregatesAsync(jobId, cancellationToken);
        }
        return warnings;
    }

    private async Task<IReadOnlyList<BackupProblem>> ReconcilePendingForJobAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await context.BackupArtifacts.AsNoTracking().Include(item => item.Run)
            .Where(item => item.Run!.JobId == jobId && item.RetentionState == RetentionOperationState.PendingDeletion)
            .ToListAsync(cancellationToken);
        return await ProcessAsync(pending, cancellationToken);
    }

    private async Task<IReadOnlyList<BackupProblem>> ProcessAsync(
        IReadOnlyCollection<BackupArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        var warnings = new List<BackupProblem>();
        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        foreach (var artifact in artifacts)
        {
            var path = Path.Combine(artifact.EffectivePath, artifact.FinalFileName);
            OwnedArchiveResult result;
            try
            {
                var destination = await DestinationSnapshotAsync(artifact.Run!, cancellationToken);
                if (destination is null)
                {
                    result = OwnedArchiveResult.AccessFailed;
                }
                else
                {
                    result = await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                        effectiveDestinations.Configuration(destination), async () =>
                        {
                            var resolvedRoot = PathOverlap.ResolveExisting(destination.RootPath);
                            var resolvedEffective = PathOverlap.ResolveExisting(artifact.EffectivePath);
                            if (!PathOverlap.IsSameOrDescendant(resolvedEffective, resolvedRoot) ||
                                !PathOverlap.IsSameOrDescendant(Path.GetFullPath(path), Path.GetFullPath(artifact.EffectivePath)))
                                return OwnedArchiveResult.OwnershipMismatch;
                            var marker = await ownershipMarkers.VerifyAsync(
                                artifact.EffectivePath, installationId, artifact.Run!.JobId, cancellationToken);
                            return marker.Result == OwnershipMarkerResult.Owned
                                ? ownershipVerifier.DeleteIfOwned(path, artifact, installationId, artifact.EffectivePath)
                                : OwnedArchiveResult.OwnershipMismatch;
                        });
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
            {
                result = OwnedArchiveResult.AccessFailed;
            }

            switch (result)
            {
                case OwnedArchiveResult.Deleted:
                case OwnedArchiveResult.Missing:
                    await MarkRemovedAsync(artifact.Id, cancellationToken);
                    break;
                case OwnedArchiveResult.OwnershipMismatch:
                    await MarkFailedAsync(artifact.Id, ownershipRefused: true, cancellationToken);
                    warnings.Add(Warning(path, "Retention ownership verification",
                        "The registered archive was not deleted because ownership could not be proven."));
                    break;
                default:
                    await MarkFailedAsync(artifact.Id, ownershipRefused: false, cancellationToken);
                    warnings.Add(Warning(path, "Delete retained archive",
                        "The retained archive could not be inspected or deleted."));
                    break;
            }
        }
        return warnings;
    }

    private async Task<Destination?> DestinationSnapshotAsync(BackupRun run, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await context.Destinations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == run.DestinationId, cancellationToken);
        return current is null ? null : new Destination
        {
            Id = run.DestinationId,
            Name = run.DestinationName,
            Type = run.DestinationType,
            RootPath = run.DestinationRootPath,
            SmbUsername = run.DestinationUsername,
            ProtectedPassword = current.ProtectedPassword
        };
    }

    private async Task<List<BackupArtifact>> BeginExcessDeletionsAsync(Guid runId, CancellationToken cancellationToken)
    {
        return await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var run = await context.Runs.SingleAsync(item => item.Id == runId, ct);
            var retained = await context.BackupArtifacts.Include(item => item.Run)
                .Where(item => item.Run!.JobId == run.JobId && item.State == ArtifactState.Retained &&
                    item.RetentionState == RetentionOperationState.None).ToListAsync(ct);
            retained = retained.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).ToList();
            var candidates = retained.Take(Math.Max(0, retained.Count - run.RetentionCount)).ToList();
            foreach (var artifact in candidates) artifact.BeginRetentionDeletion(run.Id, timeProvider.GetUtcNow());
            await context.SaveChangesAsync(ct);
            return candidates;
        }, cancellationToken);
    }

    private Task MarkRemovedAsync(Guid artifactId, CancellationToken cancellationToken) =>
        ChangeArtifactAsync(artifactId, artifact => artifact.MarkRemovedByRetention(timeProvider.GetUtcNow()), cancellationToken);

    private Task MarkFailedAsync(Guid artifactId, bool ownershipRefused, CancellationToken cancellationToken) =>
        ChangeArtifactAsync(artifactId, artifact => artifact.MarkRetentionFailed(ownershipRefused, timeProvider.GetUtcNow()), cancellationToken);

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

    private async Task RefreshAggregatesAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var retained = await context.BackupArtifacts.Include(item => item.Run)
                .Where(item => item.Run!.JobId == jobId && item.State == ArtifactState.Retained).ToListAsync(ct);
            retained = retained.OrderByDescending(item => item.CreatedAtUtc).ToList();
            var job = await context.Jobs.SingleAsync(item => item.Id == jobId, ct);
            job.ManagedArtifactCount = retained.Count;
            job.ManagedArtifactBytes = retained.Sum(item => item.Size);
            job.LatestArtifactBytes = retained.FirstOrDefault()?.Size;
            job.StorageConfirmedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    public sealed record InventoryReconcileResult(bool DestinationReachable, int Checked, int MarkedMissing);

    private static BackupProblem Warning(string path, string operation, string message) =>
        new(BackupProblemSeverity.Warning, BackupProblemCategory.CleanupFailed, RunPhase.Finalizing,
            operation, message, path);
}
