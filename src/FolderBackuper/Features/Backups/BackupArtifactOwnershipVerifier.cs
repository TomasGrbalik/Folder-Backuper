using System.ComponentModel;
using System.IO.Compression;
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

public sealed class BackupArtifactOwnershipVerifier
{
    private static readonly TimeSpan CreationTimeTolerance = TimeSpan.FromSeconds(2);

    public OwnedArchiveResult Inspect(
        string path,
        BackupArtifact artifact,
        Guid installationId,
        string? containmentRoot = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!IsContained(stream.SafeFileHandle, containmentRoot)) return OwnedArchiveResult.OwnershipMismatch;
            return IsOwned(stream, artifact, installationId) ? OwnedArchiveResult.Owned : OwnedArchiveResult.OwnershipMismatch;
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
            return OwnedArchiveResult.AccessFailed;
        }
    }

    public OwnedArchiveResult DeleteIfOwned(
        string path,
        BackupArtifact artifact,
        Guid installationId,
        string? containmentRoot = null)
    {
        try
        {
            using var handle = WindowsFilesystemInterop.OpenReadDeleteHandle(path);
            if (!IsContained(handle, containmentRoot)) return OwnedArchiveResult.OwnershipMismatch;
            using var stream = new FileStream(handle, FileAccess.Read);
            if (!IsOwned(stream, artifact, installationId)) return OwnedArchiveResult.OwnershipMismatch;
            WindowsFilesystemInterop.MarkForDeletion(handle);
            return OwnedArchiveResult.Deleted;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return OwnedArchiveResult.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
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
            if (!IsContained(handle, containmentRoot)) return OwnedArchiveResult.OwnershipMismatch;
            using var stream = new FileStream(handle, FileAccess.Read);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (!ArchiveOwnership.TryParse(archive.Comment, out var actual) || actual != expected)
                    return OwnedArchiveResult.OwnershipMismatch;
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
            return OwnedArchiveResult.AccessFailed;
        }
    }

    private static bool IsOwned(FileStream stream, BackupArtifact artifact, Guid installationId)
    {
        if (stream.Length != artifact.OwnershipExpectedLength) return false;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (!ArchiveOwnership.TryParse(archive.Comment, out var ownership) ||
                ownership.InstallationId != installationId || ownership.RunId != artifact.OwnershipRunId)
            {
                return false;
            }
        }

        if (artifact.OwnershipCreatedAtUtc is { } expectedCreation)
        {
            var actualCreation = WindowsFilesystemInterop.GetCreationTimeUtc(stream.SafeFileHandle);
            if ((actualCreation - expectedCreation).Duration() > CreationTimeTolerance) return false;
        }

        if (artifact.OwnershipFileSystemIdentity is { } expectedIdentity &&
            WindowsFilesystemInterop.GetIdentity(stream.SafeFileHandle).ToString() != expectedIdentity)
        {
            return false;
        }

        return true;
    }

    private static bool IsContained(SafeFileHandle handle, string? containmentRoot)
    {
        if (containmentRoot is null) return true;
        var resolvedRoot = PathOverlap.ResolveExisting(containmentRoot);
        var resolvedTarget = WindowsFilesystemInterop.GetFinalPath(handle);
        return PathOverlap.IsSameOrDescendant(resolvedTarget, resolvedRoot);
    }
}
