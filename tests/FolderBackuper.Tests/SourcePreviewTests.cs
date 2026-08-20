using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Tests;

public sealed class SourcePreviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));

    public SourcePreviewTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Preview_ProgressivelyCountsAllEntriesAndPreservesMetadata()
    {
        var nested = Directory.CreateDirectory(Path.Combine(root, "hidden-folder"));
        var deeper = Directory.CreateDirectory(Path.Combine(nested.FullName, "deeper"));
        var first = Path.Combine(root, "first.bin");
        var second = Path.Combine(deeper.FullName, "second.bin");
        await File.WriteAllBytesAsync(first, new byte[3]);
        await File.WriteAllBytesAsync(second, new byte[5]);
        File.SetAttributes(nested.FullName, File.GetAttributes(nested.FullName) | FileAttributes.Hidden | FileAttributes.System);
        var modified = new DateTime(2021, 3, 4, 5, 6, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, modified);
        var originalRootAttributes = File.GetAttributes(root);
        var originalFirstAttributes = File.GetAttributes(first);

        var snapshots = await CollectAsync(new SourcePreview().InspectAsync(root, snapshotInterval: 1));
        var final = snapshots[^1];

        Assert.True(snapshots.Count > 2);
        Assert.All(snapshots[..^1], snapshot => Assert.False(snapshot.IsComplete));
        Assert.True(final.IsComplete);
        Assert.Equal(2, final.FileCount);
        Assert.Equal(2, final.FolderCount);
        Assert.Equal(8, final.TotalBytes);
        Assert.Equal(0, final.InaccessibleEntryCount);
        Assert.Equal(0, final.SkippedReparsePointCount);
        Assert.Equal(originalRootAttributes, File.GetAttributes(root));
        Assert.Equal(originalFirstAttributes, File.GetAttributes(first));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(first));
    }

    [Fact]
    public async Task Preview_ReportsButDoesNotTraverseReparseDirectoryWhenPrivilegeAllows()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "target"));
        await File.WriteAllTextAsync(Path.Combine(target.FullName, "once.txt"), "1234");
        var link = Path.Combine(root, "target-link");
        try
        {
            Directory.CreateSymbolicLink(link, target.FullName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var snapshots = await CollectAsync(new SourcePreview().InspectAsync(root, snapshotInterval: 1));
        var final = snapshots[^1];
        var browsedLink = Assert.Single((await new SourceBrowser().BrowseAsync(new(root))).Entries,
            entry => entry.Name == "target-link");

        Assert.True(browsedLink.IsReparsePoint);
        Assert.Equal(SourceEntryType.Directory, browsedLink.EntryType);
        Assert.Equal(1, final.FileCount);
        Assert.Equal(2, final.FolderCount);
        Assert.Equal(4, final.TotalBytes);
        Assert.Equal(1, final.SkippedReparsePointCount);
        var rejected = await Assert.ThrowsAsync<SourcePathException>(async () =>
        {
            await foreach (var _ in new SourcePreview().InspectAsync(link)) { }
        });
        MessageAssert.Is(SourceMessage.ReparsePointNotTraversable, rejected.Reason);
    }

    [Fact]
    public void Preview_RejectsAReparsePointEvenWithoutThePrivilegeToCreateOne()
    {
        // The symlink test above returns early on a machine without the privilege to create one, which
        // is most development machines, so the reparse guard went untested there and a regression in it
        // surfaced only in CI. This exercises the same guard against a reparse point Windows already
        // ships, so it runs everywhere.
        var junction = new[] { @"C:\Users\All Users", @"C:\Documents and Settings", @"C:\ProgramData\Application Data" }
            .FirstOrDefault(path => Directory.Exists(path)
                                    && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));

        Assert.NotNull(junction);

        var rejected = Assert.Throws<SourcePathException>(
            () => SourceInspection.ValidateBrowsableDirectory(junction));
        MessageAssert.Is(SourceMessage.ReparsePointNotTraversable, rejected.Reason);
    }

    [Fact]
    public async Task Preview_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new SourcePreview().InspectAsync(root, cancellationToken: cancellation.Token)) { }
        });
    }

    private static async Task<List<SourcePreviewSnapshot>> CollectAsync(IAsyncEnumerable<SourcePreviewSnapshot> source)
    {
        var snapshots = new List<SourcePreviewSnapshot>();
        await foreach (var snapshot in source) snapshots.Add(snapshot);
        return snapshots;
    }

    public void Dispose()
    {
        NormalizeForDeletion(root);
        Directory.Delete(root, recursive: true);
    }

    private static void NormalizeForDeletion(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                NormalizeForDeletion(entry);
            }
            File.SetAttributes(entry, attributes & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
        }
    }
}
