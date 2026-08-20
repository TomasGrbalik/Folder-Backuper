using System.Text;
using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Infrastructure.Filesystem;

public enum OwnershipMarkerResult { Claimed, Owned, OwnedByAnotherJob, Invalid, Missing, Released, CleanupFailed }

public sealed record OwnershipMarkerOutcome(OwnershipMarkerResult Result, UiMessage Message)
{
    public bool Succeeded => Result is OwnershipMarkerResult.Claimed or OwnershipMarkerResult.Owned or OwnershipMarkerResult.Released;

    public OwnershipMarkerOutcome(OwnershipMarkerResult result, OwnershipMessage message)
        : this(result, UiMessage.For(message))
    {
    }
}

public sealed class OwnershipMarkerService
{
    public const string MarkerName = ".folder-backuper-owner";

    public async Task<OwnershipMarkerOutcome> ClaimAsync(string directory, Guid installationId, Guid jobId, CancellationToken cancellationToken)
    {
        var expected = Content(installationId, jobId);
        var path = Path.Combine(directory, MarkerName);
        cancellationToken.ThrowIfCancellationRequested();
        var created = false;
        FilesystemIdentity? createdIdentity = null;
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            created = true;
            createdIdentity = WindowsFilesystemInterop.GetIdentity(path);
            // Once CreateNew succeeds, finish atomically from the caller's perspective rather than
            // allowing cancellation to leave a truncated ownership marker.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(expected), CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
            return new(OwnershipMarkerResult.Claimed, OwnershipMessage.Claimed);
        }
        catch (IOException) when (!created && File.Exists(path))
        {
            return await VerifyAsync(directory, installationId, jobId, cancellationToken);
        }
        catch when (created)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var handle = WindowsFilesystemInterop.OpenReadDeleteHandle(path);
                    if (WindowsFilesystemInterop.GetIdentity(path) != createdIdentity)
                    {
                        return new(OwnershipMarkerResult.CleanupFailed,
                            OwnershipMessage.IncompleteMarkerReplaced);
                    }
                    WindowsFilesystemInterop.MarkForDeletion(handle);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return new(OwnershipMarkerResult.CleanupFailed,
                    OwnershipMessage.IncompleteMarkerNotRemoved);
            }
            throw;
        }
    }

    public async Task<OwnershipMarkerOutcome> VerifyAsync(string directory, Guid installationId, Guid jobId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path)) return new(OwnershipMarkerResult.Missing, OwnershipMessage.MarkerMissing);
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (content == Content(installationId, jobId)) return new(OwnershipMarkerResult.Owned, OwnershipMessage.OwnedByThisJob);
        if (TryParse(content, out var foundInstallation, out var foundJob))
        {
            return new(OwnershipMarkerResult.OwnedByAnotherJob,
                foundInstallation == installationId && foundJob != jobId
                    ? OwnershipMessage.OwnedByAnotherJob
                    : OwnershipMessage.OwnedByAnotherInstallation);
        }
        return new(OwnershipMarkerResult.Invalid, OwnershipMessage.MarkerInvalid);
    }

    public async Task<OwnershipMarkerOutcome> ReleaseAsync(string directory, Guid installationId, Guid jobId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path)) return new(OwnershipMarkerResult.Missing, OwnershipMessage.MarkerMissing);
        try
        {
            using var handle = WindowsFilesystemInterop.OpenReadDeleteHandle(path);
            await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (content != Content(installationId, jobId))
            {
                return TryParse(content, out var foundInstallation, out var foundJob)
                    ? new(
                        OwnershipMarkerResult.OwnedByAnotherJob,
                        foundInstallation == installationId && foundJob != jobId
                            ? OwnershipMessage.OwnedByAnotherJob
                            : OwnershipMessage.OwnedByAnotherInstallation)
                    : new(OwnershipMarkerResult.Invalid, OwnershipMessage.MarkerInvalid);
            }

            WindowsFilesystemInterop.MarkForDeletion(handle);
            return new(OwnershipMarkerResult.Released, OwnershipMessage.Released);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(OwnershipMarkerResult.CleanupFailed, OwnershipMessage.VerifiedMarkerNotRemoved); }
        catch (System.ComponentModel.Win32Exception)
        { return new(OwnershipMarkerResult.CleanupFailed, OwnershipMessage.VerifiedMarkerNotRemoved); }
    }

    private static string Content(Guid installationId, Guid jobId) =>
        $"FolderBackuper:v1\ninstallation={installationId:D}\njob={jobId:D}\n";

    private static bool TryParse(string content, out Guid installationId, out Guid jobId)
    {
        installationId = default; jobId = default;
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 3 && lines[0] == "FolderBackuper:v1" &&
            lines[1].StartsWith("installation=", StringComparison.Ordinal) && Guid.TryParse(lines[1][13..], out installationId) &&
            lines[2].StartsWith("job=", StringComparison.Ordinal) && Guid.TryParse(lines[2][4..], out jobId);
    }
}
