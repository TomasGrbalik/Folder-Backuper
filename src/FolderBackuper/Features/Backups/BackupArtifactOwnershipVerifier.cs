using System.ComponentModel;
using System.IO.Compression;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Filesystem;
using Microsoft.Win32.SafeHandles;

namespace FolderBackuper.Features.Backups;

public enum OwnedArchiveResult
{
    Owned,
    Missing,
    OwnershipMismatch,
    AccessFailed,
    Deleted
}

/// <remarks>
/// The ZIP comment (installation and run), the exact length and containment prove ownership on every
/// destination. The recorded creation time and file identity are enforced only on local destinations: an SMB
/// server synthesises both, and a NAS can report different values for the same file after a restart, which
/// would otherwise leave every archive older than that restart undeletable by retention.
/// </remarks>
public sealed class BackupArtifactOwnershipVerifier(ILogger<BackupArtifactOwnershipVerifier>? logger = null)
{
    private static readonly TimeSpan CreationTimeTolerance = TimeSpan.FromSeconds(2);

    public OwnedArchiveResult Inspect(
        string path,
        BackupArtifact artifact,
        Guid installationId,
        DestinationType destinationType,
        string? containmentRoot = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!IsContained(stream.SafeFileHandle, containmentRoot, path)) return OwnedArchiveResult.OwnershipMismatch;
            return IsOwned(stream, path, artifact, installationId, destinationType)
                ? OwnedArchiveResult.Owned
                : OwnedArchiveResult.OwnershipMismatch;
        }
        catch (FileNotFoundException)
        {
            return OwnedArchiveResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return OwnedArchiveResult.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            LogAccessFailure(exception, path);
            return OwnedArchiveResult.AccessFailed;
        }
    }

    public OwnedArchiveResult DeleteIfOwned(
        string path,
        BackupArtifact artifact,
        Guid installationId,
        DestinationType destinationType,
        string? containmentRoot = null)
    {
        try
        {
            using var handle = WindowsFilesystemInterop.OpenReadDeleteHandle(path);
            if (!IsContained(handle, containmentRoot, path)) return OwnedArchiveResult.OwnershipMismatch;
            using var stream = new FileStream(handle, FileAccess.Read);
            if (!IsOwned(stream, path, artifact, installationId, destinationType)) return OwnedArchiveResult.OwnershipMismatch;
            WindowsFilesystemInterop.MarkForDeletion(handle);
            return OwnedArchiveResult.Deleted;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return OwnedArchiveResult.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            LogAccessFailure(exception, path);
            return OwnedArchiveResult.AccessFailed;
        }
    }

    public OwnedArchiveResult DeleteIfRunOwned(
        string path,
        ArchiveOwnership expected,
        string? containmentRoot = null)
    {
        try
        {
            using var handle = WindowsFilesystemInterop.OpenReadDeleteHandle(path);
            if (!IsContained(handle, containmentRoot, path)) return OwnedArchiveResult.OwnershipMismatch;
            using var stream = new FileStream(handle, FileAccess.Read);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (!ArchiveOwnership.TryParse(archive.Comment, out var actual) || actual != expected)
                {
                    LogMismatch(path, "the ZIP comment does not name this installation and run");
                    return OwnedArchiveResult.OwnershipMismatch;
                }
            }
            WindowsFilesystemInterop.MarkForDeletion(handle);
            return OwnedArchiveResult.Deleted;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return OwnedArchiveResult.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            LogAccessFailure(exception, path);
            return OwnedArchiveResult.AccessFailed;
        }
    }

    private bool IsOwned(
        FileStream stream,
        string path,
        BackupArtifact artifact,
        Guid installationId,
        DestinationType destinationType)
    {
        if (stream.Length != artifact.OwnershipExpectedLength)
        {
            LogMismatch(path, $"length is {stream.Length} bytes, expected {artifact.OwnershipExpectedLength}");
            return false;
        }

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (!ArchiveOwnership.TryParse(archive.Comment, out var ownership))
            {
                LogMismatch(path, "the ZIP comment is not a Folder Backuper ownership record");
                return false;
            }

            if (ownership.InstallationId != installationId || ownership.RunId != artifact.OwnershipRunId)
            {
                LogMismatch(path, $"the ZIP comment names installation {ownership.InstallationId} and run " +
                    $"{ownership.RunId}, expected {installationId} and {artifact.OwnershipRunId}");
                return false;
            }
        }

        var enforceFilesystemMetadata = destinationType != DestinationType.Smb;
        if (artifact.OwnershipCreatedAtUtc is { } expectedCreation)
        {
            var actualCreation = WindowsFilesystemInterop.GetCreationTimeUtc(stream.SafeFileHandle);
            if ((actualCreation - expectedCreation).Duration() > CreationTimeTolerance)
            {
                var detail = $"creation time is {actualCreation:O}, recorded {expectedCreation:O}";
                if (enforceFilesystemMetadata)
                {
                    LogMismatch(path, detail);
                    return false;
                }

                LogUnenforcedDifference(path, detail);
            }
        }

        if (artifact.OwnershipFileSystemIdentity is { } expectedIdentity)
        {
            var actualIdentity = WindowsFilesystemInterop.GetIdentity(stream.SafeFileHandle).ToString();
            if (actualIdentity != expectedIdentity)
            {
                var detail = $"file identity is {actualIdentity}, recorded {expectedIdentity}";
                if (enforceFilesystemMetadata)
                {
                    LogMismatch(path, detail);
                    return false;
                }

                LogUnenforcedDifference(path, detail);
            }
        }

        return true;
    }

    private bool IsContained(SafeFileHandle handle, string? containmentRoot, string path)
    {
        if (containmentRoot is null) return true;
        var resolvedRoot = PathOverlap.ResolveExisting(containmentRoot);
        var resolvedTarget = WindowsFilesystemInterop.GetFinalPath(handle);
        if (PathOverlap.IsSameOrDescendant(resolvedTarget, resolvedRoot)) return true;
        LogMismatch(path, $"it resolves to {resolvedTarget}, outside {resolvedRoot}");
        return false;
    }

    private void LogMismatch(string path, string reason) =>
        logger?.LogWarning("Archive {Path} failed the ownership check: {Reason}", path, reason);

    private void LogUnenforcedDifference(string path, string detail) =>
        logger?.LogInformation(
            "Archive {Path} on an SMB destination differs from its recorded metadata ({Detail}); not enforced because SMB servers do not keep it stable",
            path, detail);

    private void LogAccessFailure(Exception exception, string path) =>
        logger?.LogWarning(exception, "Archive {Path} could not be opened for an ownership check", path);
}
