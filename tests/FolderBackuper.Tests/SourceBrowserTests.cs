using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Tests;

public sealed class SourceBrowserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));

    public SourceBrowserTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Browse_IsOneLevelDeterministicAndPagedByOffsetOrContinuation()
    {
        Directory.CreateDirectory(Path.Combine(root, "z-folder"));
        Directory.CreateDirectory(Path.Combine(root, "A-folder"));
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "bb");
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(root, "z-folder", "not-returned.txt"), "nested");
        var browser = new SourceBrowser();

        var first = await browser.BrowseAsync(new(root, PageSize: 2));
        var second = await browser.BrowseAsync(new(root, PageSize: 2, Continuation: first.Continuation));
        var offsetPage = await browser.BrowseAsync(new(root, PageSize: 2, Offset: 2));

        Assert.Equal(["A-folder", "z-folder"], first.Entries.Select(entry => entry.Name));
        Assert.Equal(["a.txt", "b.txt"], second.Entries.Select(entry => entry.Name));
        Assert.Equal(offsetPage.Entries, second.Entries);
        Assert.NotNull(first.Continuation);
        Assert.False(int.TryParse(first.Continuation, out _));
        Assert.Null(second.Continuation);
        Assert.DoesNotContain(second.Entries, entry => entry.Name == "not-returned.txt");
        Assert.Equal(1, second.Entries[0].FileSize);
        Assert.Equal(SourceEntryType.File, second.Entries[0].EntryType);
    }

    [Fact]
    public async Task Browse_ClampsPageSizeAndRejectsUnsafeRequests()
    {
        var browser = new SourceBrowser();
        var result = await browser.BrowseAsync(new(root, SourceBrowser.MaxPageSize + 1));
        Assert.Equal(SourceBrowser.MaxPageSize, result.PageSize);

        await Assert.ThrowsAsync<SourcePathException>(() => browser.BrowseAsync(new(@"relative\folder")));
        await Assert.ThrowsAsync<SourcePathException>(() => browser.BrowseAsync(new(@"\\server\share")));
        await Assert.ThrowsAsync<SourcePathException>(() => browser.BrowseAsync(new(@"\\?\C:\Windows")));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.BrowseAsync(new(root, Offset: 1, Continuation: "1")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => browser.BrowseAsync(new(root, Offset: SourceBrowser.MaxOffset + 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.BrowseAsync(new(root, Continuation: "not-a-token")));
    }

    [Fact]
    public async Task Continuation_UsesLastSortKeyWhenEarlierEntriesAreAdded()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(root, "c.txt"), "c");
        await File.WriteAllTextAsync(Path.Combine(root, "d.txt"), "d");
        var browser = new SourceBrowser();
        var first = await browser.BrowseAsync(new(root, PageSize: 2));

        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a");
        var second = await browser.BrowseAsync(new(root, PageSize: 2, Continuation: first.Continuation));

        Assert.Equal(["b.txt", "c.txt"], first.Entries.Select(entry => entry.Name));
        Assert.Equal(["d.txt"], second.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task Browse_ReportsHiddenAndSystemMetadataWithoutChangingIt()
    {
        var file = Path.Combine(root, "hidden.txt");
        await File.WriteAllTextAsync(file, "content");
        var modified = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.System);

        var result = await new SourceBrowser().BrowseAsync(new(root));
        var entry = Assert.Single(result.Entries);

        Assert.True(entry.IsHidden);
        Assert.True(entry.IsSystem);
        Assert.Equal(modified, entry.ModifiedTime?.UtcDateTime);
        Assert.Equal(FileAttributes.Hidden | FileAttributes.System,
            File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task Browse_HonorsPreCanceledRequest()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SourceBrowser().BrowseAsync(new(root), cancellation.Token));
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
