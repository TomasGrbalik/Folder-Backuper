using System.ComponentModel;
using System.Security.Cryptography;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Localization;

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
    UiMessage Message,
    string? EffectivePath = null,
    string? OwnershipKey = null,
    bool NewlyClaimed = false)
{
    public bool Succeeded => Result == JobDestinationTestResult.Succeeded;

    public JobDestinationTestOutcome(
        JobDestinationTestResult result,
        JobDestinationTestMessage message,
        string? effectivePath = null,
        string? ownershipKey = null,
        bool newlyClaimed = false)
        : this(result, UiMessage.For(message), effectivePath, ownershipKey, newlyClaimed)
    {
    }
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
                                JobDestinationTestMessage.VerificationBytesNotPreserved);
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
                                JobDestinationTestMessage.VerificationFileCleanupFailed);
                        }

                        return new(JobDestinationTestResult.Succeeded,
                            JobDestinationTestMessage.OwnershipAndWriteVerified, resolved.EffectivePath, resolved.OwnershipKey,
                            newlyClaimed);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        var cleanup = await CleanupFailureAsync(probe, created, resolved.EffectivePath!,
                            newlyClaimed, installationId, jobId);
                        if (cleanup is not null) return cleanup;
                        return new(JobDestinationTestResult.AccessFailed, JobDestinationTestMessage.WriteVerificationFailed);
                    }
                    catch (OperationCanceledException)
                    {
                        var cleanup = await CleanupFailureAsync(probe, created, resolved.EffectivePath!,
                            newlyClaimed, installationId, jobId);
                        if (cleanup is not null)
                            throw new InvalidOperationException(cleanup.Message.Key);
                        throw;
                    }
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(JobDestinationTestResult.AccessFailed, JobDestinationTestMessage.DestinationNotAccessible);
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
                UiMessage.For(JobDestinationTestMessage.OwnedFolderNotVerifiableForCleanup));
        }

        try
        {
            return await effectiveDestinations.Adapter(destination.Type).ExecuteAsync(
                effectiveDestinations.Configuration(destination),
                () => Directory.Exists(resolved.EffectivePath)
                    ? markers.ReleaseAsync(resolved.EffectivePath, installationId, jobId, cancellationToken)
                    : Task.FromResult(new OwnershipMarkerOutcome(
                        OwnershipMarkerResult.Missing, OwnershipMessage.MarkerMissing)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(OwnershipMarkerResult.CleanupFailed,
                UiMessage.For(JobDestinationTestMessage.OwnedFolderNotAccessibleForCleanup));
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
        // Composed as a message with the ownership reason as an argument rather than by concatenating
        // English fragments, so a nested reason is rendered in the reading language too.
        var probeFailed = false;
        if (probeCreated)
        {
            try { File.Delete(probe); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                probeFailed = true;
            }
        }

        UiMessage? releaseFailure = null;
        if (newlyClaimed)
        {
            var released = await markers.ReleaseAsync(effectivePath, installationId, jobId, CancellationToken.None);
            if (!released.Succeeded)
            {
                releaseFailure = released.Message;
            }
        }

        if (releaseFailure is not null)
        {
            return new(
                JobDestinationTestResult.CleanupFailed,
                UiMessage.For(
                    probeFailed
                        ? JobDestinationTestMessage.CleanupFailedAndMarkerNotReleased
                        : JobDestinationTestMessage.NewlyClaimedMarkerNotReleased,
                    UiMessageArgument.FromMessage(releaseFailure)));
        }

        return probeFailed
            ? new(JobDestinationTestResult.CleanupFailed, JobDestinationTestMessage.ExactVerificationFileNotRemoved)
            : null;
    }
}
