using System.Text;

namespace FolderBackuper.Infrastructure.Filesystem;

public enum OwnershipMarkerResult { Claimed, Owned, OwnedByAnotherJob, Invalid, Missing, Released, CleanupFailed }

public sealed record OwnershipMarkerOutcome(OwnershipMarkerResult Result, string Message)
{
    public bool Succeeded => Result is OwnershipMarkerResult.Claimed or OwnershipMarkerResult.Owned or OwnershipMarkerResult.Released;
}

public sealed class OwnershipMarkerService
{
    public const string MarkerName = ".folder-backuper-owner";

    public async Task<OwnershipMarkerOutcome> ClaimAsync(string directory, Guid installationId, Guid jobId, CancellationToken cancellationToken)
    {
        var expected = Content(installationId, jobId);
        var path = Path.Combine(directory, MarkerName);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(expected), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return new(OwnershipMarkerResult.Claimed, "The destination folder was claimed.");
        }
        catch (IOException) when (File.Exists(path))
        {
            return await VerifyAsync(directory, installationId, jobId, cancellationToken);
        }
    }

    public async Task<OwnershipMarkerOutcome> VerifyAsync(string directory, Guid installationId, Guid jobId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path)) return new(OwnershipMarkerResult.Missing, "The ownership marker is missing.");
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (content == Content(installationId, jobId)) return new(OwnershipMarkerResult.Owned, "The folder is owned by this job.");
        if (TryParse(content, out var foundInstallation, out var foundJob))
        {
            return new(OwnershipMarkerResult.OwnedByAnotherJob,
                foundInstallation == installationId && foundJob != jobId
                    ? "The folder is owned by another job."
                    : "The folder is owned by another Folder Backuper installation.");
        }
        return new(OwnershipMarkerResult.Invalid, "The ownership marker is invalid.");
    }

    public async Task<OwnershipMarkerOutcome> ReleaseAsync(string directory, Guid installationId, Guid jobId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path)) return new(OwnershipMarkerResult.Missing, "The ownership marker is missing.");
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
                            ? "The folder is owned by another job."
                            : "The folder is owned by another Folder Backuper installation.")
                    : new(OwnershipMarkerResult.Invalid, "The ownership marker is invalid.");
            }

            WindowsFilesystemInterop.MarkForDeletion(handle);
            return new(OwnershipMarkerResult.Released, "The ownership marker was released.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(OwnershipMarkerResult.CleanupFailed, "The verified ownership marker could not be removed."); }
        catch (System.ComponentModel.Win32Exception)
        { return new(OwnershipMarkerResult.CleanupFailed, "The verified ownership marker could not be removed."); }
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
