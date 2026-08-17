using System.Security.AccessControl;

namespace FolderBackuper.Milestone0.Probes;

public static class SourceReadProbe
{
    public static async Task<ProbeResult> RunAsync(string? sourcePath, int maximumFiles, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new ProbeResult("LocalSystem source read", ProbeStatus.Skipped, "No representative source path was configured.");
        }

        var root = new DirectoryInfo(Path.GetFullPath(sourcePath));
        if (!root.Exists)
        {
            return new ProbeResult("LocalSystem source read", ProbeStatus.Failed, "The configured source path does not exist.");
        }

        var aclBefore = root.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var rootBefore = Snapshot(root);
        var files = EnumerateFilesWithoutReparseTraversal(root.FullName).Take(maximumFiles).ToArray();
        var snapshots = files.ToDictionary(file => file.FullName, Snapshot, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            while (await stream.ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }

        root.Refresh();
        var aclAfter = root.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var unchanged = rootBefore == Snapshot(root)
            && string.Equals(aclBefore, aclAfter, StringComparison.Ordinal)
            && files.All(file =>
            {
                file.Refresh();
                return snapshots[file.FullName] == Snapshot(file);
            });

        return new ProbeResult(
            "LocalSystem source read",
            unchanged ? ProbeStatus.Passed : ProbeStatus.Failed,
            unchanged
                ? $"Read {files.Length} representative files without changing captured metadata or the root ACL."
                : "Captured source metadata or the root ACL changed during the read probe.");
    }

    private static IEnumerable<FileInfo> EnumerateFilesWithoutReparseTraversal(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false
            }))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory.FullName);
                }
                else if (entry is FileInfo file)
                {
                    yield return file;
                }
            }
        }
    }

    private static MetadataSnapshot Snapshot(FileSystemInfo entry)
    {
        var length = entry is FileInfo file ? file.Length : 0;
        return new MetadataSnapshot(entry.Attributes, entry.CreationTimeUtc, entry.LastWriteTimeUtc, length);
    }

    private sealed record MetadataSnapshot(
        FileAttributes Attributes,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        long Length);
}
