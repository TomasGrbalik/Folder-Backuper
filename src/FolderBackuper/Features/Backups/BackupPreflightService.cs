using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.ServiceHosting;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Backups;

public sealed record BackupPreflightResult(
    string? SourcePath,
    string? EffectiveDestinationPath,
    IReadOnlyList<BackupProblem> Problems)
{
    public bool Succeeded => SourcePath is not null && EffectiveDestinationPath is not null && Problems.Count == 0;
}

public sealed class BackupPreflightService(
    ApplicationPaths applicationPaths,
    EffectiveDestinationService effectiveDestinations,
    OwnershipMarkerService ownershipMarkers,
    ILocalHostUncDetector localHostUncDetector)
{
    public async Task<BackupPreflightResult> ValidateAsync(
        BackupJob job,
        Destination destination,
        IEnumerable<string> configuredSourcePaths,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var problems = new List<BackupProblem>();
        var sources = configuredSourcePaths.Append(job.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (job.Lifecycle == JobLifecycle.Archived)
        {
            problems.Add(Problem(BackupProblemCategory.SourceUnavailable, RunPhase.Scanning,
                BackupOperation.ValidateJobLifecycle, UiMessage.For(BackupProblemMessage.ArchivedJobCannotRun), job.SourcePath));
        }

        string? source = null;
        try
        {
            source = SourceInspection.ValidateBrowsableDirectory(job.SourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A rejected source path carries its reason as a code; anything else is an unexpected
            // filesystem failure and is reported as one rather than by leaking the exception's own text.
            problems.Add(Problem(BackupProblemCategory.SourceUnavailable, RunPhase.Scanning,
                BackupOperation.ValidateSource,
                exception is SourcePathException rejected
                    ? rejected.Reason
                    : UiMessage.For(SourceMessage.DirectoryInvalid),
                job.SourcePath,
                exception));
        }

        if (destination.Lifecycle != DestinationLifecycle.Active ||
            destination.VerificationResult != DestinationVerificationResult.Succeeded ||
            string.IsNullOrWhiteSpace(destination.VerificationFingerprint))
        {
            problems.Add(Problem(BackupProblemCategory.DestinationUnavailable, RunPhase.Scanning,
                BackupOperation.ValidateDestinationVerification, UiMessage.For(BackupProblemMessage.DestinationNeedsVerification), destination.RootPath));
        }

        if (destination.Type == DestinationType.Smb)
        {
            try
            {
                if (localHostUncDetector.IsHostedLocally(destination.RootPath))
                {
                    problems.Add(Problem(BackupProblemCategory.InvalidPath, RunPhase.Scanning,
                        BackupOperation.ValidateSmbDestination, UiMessage.For(BackupProblemMessage.SmbHostedLocallyUnsupported), destination.RootPath));
                }
            }
            catch (ArgumentException exception)
            {
                problems.Add(Problem(BackupProblemCategory.InvalidPath, RunPhase.Scanning,
                    BackupOperation.ValidateSmbDestination, UiMessage.For(PathMessage.UncInvalid), destination.RootPath, exception));
            }
        }

        ValidateStaging(sources, problems);
        if (problems.Count > 0)
        {
            return new(source, null, problems.AsReadOnly());
        }

        var effective = await effectiveDestinations.ResolveAgainstSourcesAsync(
            destination, job.DestinationSubfolder, sources, create: false, cancellationToken);
        // Existence is taken from the resolution itself, which observed the path inside the destination
        // adapter's access scope. Probing it again here would run outside that scope and report an SMB
        // share reachable only under the destination's credentials as a missing directory.
        if (!effective.Succeeded || effective.EffectivePath is null || !effective.Exists)
        {
            problems.Add(Problem(
                effective.Result == EffectiveDestinationResult.SourceOverlap
                    ? BackupProblemCategory.InvalidPath
                    : BackupProblemCategory.DestinationUnavailable,
                RunPhase.Scanning,
                BackupOperation.ValidateEffectiveDestination,
                effective.Succeeded ? UiMessage.For(BackupProblemMessage.EffectiveDestinationMissing) : effective.Message,
                effective.EffectivePath ?? destination.RootPath));
            return new(source, null, problems.AsReadOnly());
        }

        var configuration = effectiveDestinations.Configuration(destination);
        try
        {
            var marker = await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(configuration,
                () => ownershipMarkers.VerifyAsync(effective.EffectivePath, installationId, job.Id, cancellationToken));
            if (marker.Result != OwnershipMarkerResult.Owned)
            {
                problems.Add(Problem(BackupProblemCategory.DestinationInaccessible, RunPhase.Scanning,
                    BackupOperation.VerifyDestinationOwnership, marker.Message, effective.EffectivePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            problems.Add(Problem(BackupProblemCategory.DestinationInaccessible, RunPhase.Scanning,
                BackupOperation.VerifyDestinationOwnership, UiMessage.For(BackupProblemMessage.OwnershipMarkerUnverified),
                effective.EffectivePath, exception));
        }

        return new(source, problems.Count == 0 ? effective.EffectivePath : null, problems.AsReadOnly());
    }

    private void ValidateStaging(IReadOnlyCollection<string> sources, List<BackupProblem> problems)
    {
        var validation = WindowsPath.Local(applicationPaths.Staging);
        if (!validation.IsValid || !Directory.Exists(applicationPaths.Staging))
        {
            problems.Add(Problem(BackupProblemCategory.StagingUnavailable, RunPhase.Scanning,
                BackupOperation.ValidateStaging, validation.Error ?? UiMessage.For(BackupProblemMessage.StagingDirectoryMissing), applicationPaths.Staging));
            return;
        }

        try
        {
            var overlap = PathOverlap.FindDestinationOverlap(applicationPaths.Staging, sources);
            if (overlap is not null)
            {
                problems.Add(Problem(BackupProblemCategory.InvalidPath, RunPhase.Scanning,
                    BackupOperation.ValidateStagingOverlap, UiMessage.For(BackupProblemMessage.StagingOverlapsSource), applicationPaths.Staging));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add(Problem(BackupProblemCategory.StagingInaccessible, RunPhase.Scanning,
                BackupOperation.ValidateStaging, UiMessage.For(BackupProblemMessage.StagingNotResolvable), applicationPaths.Staging, exception));
        }
    }

    private static BackupProblem Problem(
        BackupProblemCategory category,
        RunPhase phase,
        BackupOperation operation,
        UiMessage message,
        string? path,
        Exception? exception = null) => new(
            BackupProblemSeverity.Error,
            category,
            phase,
            operation,
            message,
            path,
            exception is null ? null : exception.HResult & 0xFFFF);
}
