using System.ComponentModel;
using System.Security.Cryptography;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Features.Jobs;

public enum JobDestinationTestResult
{
    Succeeded,
    InvalidPath,
    OwnershipConflict,
    OwnershipInvalid,
    AccessFailed,
    CleanupFailed
}

public sealed record JobDestinationTestOutcome(
    JobDestinationTestResult Result,
    string Message,
    string? EffectivePath = null,
    string? OwnershipKey = null,
    bool NewlyClaimed = false)
{
    public bool Succeeded => Result == JobDestinationTestResult.Succeeded;
}

public sealed class JobDestinationTestService(
    EffectiveDestinationService effectiveDestinations,
    OwnershipMarkerService markers)
{
    public async Task<JobDestinationTestOutcome> TestAndClaimAsync(
        Destination destination,
        string subfolder,
        string localSourcePath,
        Guid installationId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await effectiveDestinations.ResolveAsync(
            destination, subfolder, localSourcePath, create: true, cancellationToken);
        if (!resolved.Succeeded)
        {
            return new(JobDestinationTestResult.InvalidPath, resolved.Message);
        }

        try
        {
            return await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                effectiveDestinations.Configuration(destination), async () =>
                {
                    var marker = await markers.ClaimAsync(resolved.EffectivePath!, installationId, jobId, cancellationToken);
                    if (!marker.Succeeded)
                    {
                        return MarkerFailure(marker);
                    }
                    var newlyClaimed = marker.Result == OwnershipMarkerResult.Claimed;

                    var probe = Path.Combine(resolved.EffectivePath!, $".folder-backuper-job-test-{Guid.NewGuid():N}.tmp");
                    var expected = RandomNumberGenerator.GetBytes(4096);
                    var created = false;
                    try
                    {
                        await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            created = true;
                            await stream.WriteAsync(expected, cancellationToken);
                            await stream.FlushAsync(cancellationToken);
                        }

                        var actual = await File.ReadAllBytesAsync(probe, cancellationToken);
                        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                        {
                            var cleanup = await CleanupFailureAsync(probe, created, resolved.EffectivePath!,
                                newlyClaimed, installationId, jobId);
                            if (cleanup is not null) return cleanup;
                            created = false;
                            return new(JobDestinationTestResult.AccessFailed,
                                "The destination did not preserve the verification bytes.");
                        }

                        try
                        {
                            File.Delete(probe);
                            created = false;
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            var cleanup = await CleanupFailureAsync(probe, created, resolved.EffectivePath!,
                                newlyClaimed, installationId, jobId);
                            return cleanup ?? new(JobDestinationTestResult.CleanupFailed,
                                "The destination verification file cleanup failed.");
                        }

                        return new(JobDestinationTestResult.Succeeded,
                            "Ownership and write verification succeeded.", resolved.EffectivePath, resolved.OwnershipKey,
                            newlyClaimed);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        var cleanup = await CleanupFailureAsync(probe, created, resolved.EffectivePath!,
                            newlyClaimed, installationId, jobId);
                        if (cleanup is not null) return cleanup;
                        return new(JobDestinationTestResult.AccessFailed, "The destination write verification failed.");
                    }
                    catch (OperationCanceledException)
                    {
                        var cleanup = await CleanupFailureAsync(probe, created, resolved.EffectivePath!,
                            newlyClaimed, installationId, jobId);
                        if (cleanup is not null)
                            throw new InvalidOperationException(cleanup.Message);
                        throw;
                    }
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(JobDestinationTestResult.AccessFailed, "The destination could not be accessed.");
        }
    }

    public async Task<OwnershipMarkerOutcome> ReleaseAsync(
        Destination destination,
        string subfolder,
        Guid installationId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await effectiveDestinations.ResolveAsync(destination, subfolder, create: false,
            cancellationToken: cancellationToken);
        if (!resolved.Succeeded || resolved.EffectivePath is null)
        {
            return new(OwnershipMarkerResult.CleanupFailed,
                "The owned destination folder could not be verified for marker cleanup.");
        }

        try
        {
            return await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                effectiveDestinations.Configuration(destination),
                () => Directory.Exists(resolved.EffectivePath)
                    ? markers.ReleaseAsync(resolved.EffectivePath, installationId, jobId, cancellationToken)
                    : Task.FromResult(new OwnershipMarkerOutcome(
                        OwnershipMarkerResult.Missing, "The ownership marker is missing.")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(OwnershipMarkerResult.CleanupFailed,
                "The owned destination folder could not be accessed for marker cleanup.");
        }
    }

    private static JobDestinationTestOutcome MarkerFailure(OwnershipMarkerOutcome marker) => marker.Result switch
    {
        OwnershipMarkerResult.OwnedByAnotherJob => new(JobDestinationTestResult.OwnershipConflict, marker.Message),
        OwnershipMarkerResult.Invalid => new(JobDestinationTestResult.OwnershipInvalid, marker.Message),
        _ => new(JobDestinationTestResult.AccessFailed, marker.Message)
    };

    private async Task<JobDestinationTestOutcome?> CleanupFailureAsync(
        string probe,
        bool probeCreated,
        string effectivePath,
        bool newlyClaimed,
        Guid installationId,
        Guid jobId)
    {
        string? cleanupFailure = null;
        if (probeCreated)
        {
            try { File.Delete(probe); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailure = "The exact destination verification file could not be removed.";
            }
        }

        if (newlyClaimed)
        {
            var released = await markers.ReleaseAsync(effectivePath, installationId, jobId, CancellationToken.None);
            if (!released.Succeeded)
            {
                cleanupFailure = cleanupFailure is null
                    ? $"The newly claimed ownership marker could not be released: {released.Message}"
                    : $"{cleanupFailure} The newly claimed ownership marker also could not be released: {released.Message}";
            }
        }

        return cleanupFailure is null
            ? null
            : new(JobDestinationTestResult.CleanupFailed, cleanupFailure);
    }
}
