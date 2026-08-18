using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.ServiceHosting;

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
                "Validate job lifecycle", "An archived job cannot be executed.", job.SourcePath));
        }

        string? source = null;
        try
        {
            source = SourceInspection.ValidateBrowsableDirectory(job.SourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            problems.Add(Problem(BackupProblemCategory.SourceUnavailable, RunPhase.Scanning,
                "Validate source", exception.Message, job.SourcePath, exception));
        }

        if (destination.Lifecycle != DestinationLifecycle.Active ||
            destination.VerificationResult != DestinationVerificationResult.Succeeded ||
            string.IsNullOrWhiteSpace(destination.VerificationFingerprint))
        {
            problems.Add(Problem(BackupProblemCategory.DestinationUnavailable, RunPhase.Scanning,
                "Validate destination verification", "The destination must have a current successful verification.", destination.RootPath));
        }

        if (destination.Type == DestinationType.Smb)
        {
            try
            {
                if (localHostUncDetector.IsHostedLocally(destination.RootPath))
                {
                    problems.Add(Problem(BackupProblemCategory.InvalidPath, RunPhase.Scanning,
                        "Validate SMB destination", "An SMB destination hosted by this computer is not supported.", destination.RootPath));
                }
            }
            catch (ArgumentException exception)
            {
                problems.Add(Problem(BackupProblemCategory.InvalidPath, RunPhase.Scanning,
                    "Validate SMB destination", exception.Message, destination.RootPath, exception));
            }
        }

        ValidateStaging(sources, problems);
        if (problems.Count > 0)
        {
            return new(source, null, problems.AsReadOnly());
        }

        var effective = await effectiveDestinations.ResolveAgainstSourcesAsync(
            destination, job.DestinationSubfolder, sources, create: false, cancellationToken);
        if (!effective.Succeeded || effective.EffectivePath is null || !Directory.Exists(effective.EffectivePath))
        {
            problems.Add(Problem(
                effective.Result == EffectiveDestinationResult.SourceOverlap
                    ? BackupProblemCategory.InvalidPath
                    : BackupProblemCategory.DestinationUnavailable,
                RunPhase.Scanning,
                "Validate effective destination",
                effective.Succeeded ? "The effective destination directory does not exist." : effective.Message,
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
                    "Verify destination ownership", marker.Message, effective.EffectivePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            problems.Add(Problem(BackupProblemCategory.DestinationInaccessible, RunPhase.Scanning,
                "Verify destination ownership", "The destination ownership marker could not be verified.",
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
                "Validate staging", validation.Error ?? "The staging directory does not exist.", applicationPaths.Staging));
            return;
        }

        try
        {
            var overlap = PathOverlap.FindDestinationOverlap(applicationPaths.Staging, sources);
            if (overlap is not null)
            {
                problems.Add(Problem(BackupProblemCategory.InvalidPath, RunPhase.Scanning,
                    "Validate staging overlap", "The staging directory overlaps a configured source.", applicationPaths.Staging));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add(Problem(BackupProblemCategory.StagingInaccessible, RunPhase.Scanning,
                "Validate staging", "The staging directory could not be resolved safely.", applicationPaths.Staging, exception));
        }
    }

    private static BackupProblem Problem(
        BackupProblemCategory category,
        RunPhase phase,
        string operation,
        string message,
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
