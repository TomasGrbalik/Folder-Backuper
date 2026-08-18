using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Backups;

public sealed class BackupRecoveryService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    RunPersistenceService runs,
    BackupRetentionService retention,
    InstallationIdentityService installationIdentity,
    EffectiveDestinationService effectiveDestinations,
    OwnershipMarkerService ownershipMarkers,
    BackupArtifactOwnershipVerifier ownershipVerifier,
    ApplicationPaths applicationPaths)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await retention.RecoverPendingAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await context.Runs.Include(run => run.Artifact).AsNoTracking()
            .Where(run => run.Outcome == null && run.Phase != RunPhase.Queued)
            .ToListAsync(cancellationToken);
        foreach (var run in pending) await RecoverRunAsync(run, cancellationToken);
    }

    public async Task RecoverRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.Runs.Include(item => item.Artifact).AsNoTracking()
            .SingleAsync(item => item.Id == runId, cancellationToken);
        await RecoverRunAsync(run, cancellationToken);
    }

    private async Task RecoverRunAsync(BackupRun run, CancellationToken cancellationToken)
    {
        if (run.FinalCommittedAtUtc is not null)
        {
            var warnings = await retention.ApplyAsync(run.Id, cancellationToken);
            await runs.AppendProblemsAsync(run.Id, warnings, cancellationToken);
            var hasWarnings = warnings.Count > 0 || await runs.HasWarningsAsync(run.Id, cancellationToken);
            await runs.CompleteAsync(run.Id,
                hasWarnings ? RunOutcome.SuccessfulWithWarnings : RunOutcome.Successful,
                null, cancellationToken);
            return;
        }

        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        if (run.FinalCommitStartedAtUtc is not null && run.Artifact is { } artifact)
        {
            var finalResult = await InspectFinalAsync(run, artifact, installationId, cancellationToken);
            if (finalResult == OwnedArchiveResult.Owned)
            {
                await runs.MarkFinalCommittedAsync(run.Id, cancellationToken);
                var warnings = await retention.ApplyAsync(run.Id, cancellationToken);
                await runs.AppendProblemsAsync(run.Id, warnings, cancellationToken);
                var hasWarnings = warnings.Count > 0 || await runs.HasWarningsAsync(run.Id, cancellationToken);
                await runs.CompleteAsync(run.Id,
                    hasWarnings ? RunOutcome.SuccessfulWithWarnings : RunOutcome.Successful,
                    null, cancellationToken);
                return;
            }

            if (finalResult == OwnedArchiveResult.AccessFailed)
            {
                await runs.AppendProblemsAsync(run.Id,
                    [new(BackupProblemSeverity.Warning, BackupProblemCategory.DestinationUnavailable,
                        RunPhase.Finalizing, "Recover final archive",
                        "Finalization remains pending because the destination could not be inspected safely.")],
                    cancellationToken);
                return;
            }

            var cleanup = finalResult == OwnedArchiveResult.Missing
                ? await DeleteDestinationRunOwnedAsync(run, run.DestinationPartialPath, installationId, cancellationToken)
                : finalResult;
            await runs.MarkFinalizationFailedAsync(run.Id, cancellationToken);
            var problems = RecoveryProblems(run, finalResult, cleanup);
            await runs.AppendProblemsAsync(run.Id, problems, cancellationToken);
            await runs.CompleteAsync(run.Id, RunOutcome.Failed, problems[0].Message, cancellationToken);
            return;
        }

        var stagingCleanup = DeleteLocalRunOwned(run, run.StagingPath, installationId);
        var partialCleanup = await DeleteDestinationRunOwnedAsync(
            run, run.DestinationPartialPath, installationId, cancellationToken);
        var interruptionProblems = RecoveryProblems(run, stagingCleanup, partialCleanup);
        await runs.AppendProblemsAsync(run.Id, interruptionProblems, cancellationToken);
        await runs.CompleteAsync(run.Id, RunOutcome.Failed, interruptionProblems[0].Message, cancellationToken);
    }

    private async Task<OwnedArchiveResult> InspectFinalAsync(
        BackupRun run,
        BackupArtifact artifact,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var destination = await DestinationSnapshotAsync(run, cancellationToken);
        if (destination is null) return OwnedArchiveResult.AccessFailed;
        try
        {
            return await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                effectiveDestinations.Configuration(destination), async () =>
                {
                    var marker = await ownershipMarkers.VerifyAsync(
                        artifact.EffectivePath, installationId, run.JobId, cancellationToken);
                    if (marker.Result != OwnershipMarkerResult.Owned) return OwnedArchiveResult.OwnershipMismatch;
                    var path = Path.Combine(artifact.EffectivePath, artifact.FinalFileName);
                    var resolvedRoot = PathOverlap.ResolveExisting(destination.RootPath);
                    var resolvedEffective = PathOverlap.ResolveExisting(artifact.EffectivePath);
                    if (!PathOverlap.IsSameOrDescendant(resolvedEffective, resolvedRoot) ||
                        !PathOverlap.IsSameOrDescendant(Path.GetFullPath(path), Path.GetFullPath(artifact.EffectivePath)))
                        return OwnedArchiveResult.OwnershipMismatch;
                    return ownershipVerifier.Inspect(path, artifact, installationId, artifact.EffectivePath);
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            return OwnedArchiveResult.AccessFailed;
        }
    }

    private OwnedArchiveResult DeleteLocalRunOwned(BackupRun run, string? path, Guid installationId)
    {
        if (string.IsNullOrWhiteSpace(path)) return OwnedArchiveResult.Missing;
        if (!PathOverlap.IsSameOrDescendant(path, applicationPaths.Staging))
            return OwnedArchiveResult.OwnershipMismatch;
        return ownershipVerifier.DeleteIfRunOwned(path, new(installationId, run.Id), applicationPaths.Staging);
    }

    private async Task<OwnedArchiveResult> DeleteDestinationRunOwnedAsync(
        BackupRun run,
        string? path,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return OwnedArchiveResult.Missing;
        var destination = await DestinationSnapshotAsync(run, cancellationToken);
        if (destination is null) return OwnedArchiveResult.AccessFailed;
        try
        {
            return await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                effectiveDestinations.Configuration(destination), async () =>
                {
                    var effectivePath = Path.GetFullPath(Path.Combine(
                        destination.RootPath, run.DestinationSubfolder));
                    var resolvedRoot = PathOverlap.ResolveExisting(destination.RootPath);
                    var resolvedEffective = PathOverlap.ResolveExisting(effectivePath);
                    if (!PathOverlap.IsSameOrDescendant(resolvedEffective, resolvedRoot) ||
                        !PathOverlap.IsSameOrDescendant(Path.GetFullPath(path), effectivePath))
                        return OwnedArchiveResult.OwnershipMismatch;
                    var marker = await ownershipMarkers.VerifyAsync(
                        effectivePath, installationId, run.JobId, cancellationToken);
                    return marker.Result == OwnershipMarkerResult.Owned
                        ? ownershipVerifier.DeleteIfRunOwned(path, new(installationId, run.Id), effectivePath)
                        : OwnedArchiveResult.OwnershipMismatch;
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            return OwnedArchiveResult.AccessFailed;
        }
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

    private static BackupProblem[] RecoveryProblems(
        BackupRun run,
        OwnedArchiveResult first,
        OwnedArchiveResult second)
    {
        var problems = new List<BackupProblem>
        {
            new(BackupProblemSeverity.Error, BackupProblemCategory.GeneralIo, run.Phase,
                "Recover interrupted backup", "Backup execution was interrupted and could not be committed safely.")
        };
        if (first is OwnedArchiveResult.OwnershipMismatch or OwnedArchiveResult.AccessFailed ||
            second is OwnedArchiveResult.OwnershipMismatch or OwnedArchiveResult.AccessFailed)
        {
            problems.Add(new(BackupProblemSeverity.Warning, BackupProblemCategory.CleanupFailed, run.Phase,
                "Clean interrupted backup", "A recorded temporary or final path was left untouched because ownership could not be proven."));
        }
        return problems.ToArray();
    }
}
