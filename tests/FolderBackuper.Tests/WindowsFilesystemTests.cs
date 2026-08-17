using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Tests;

public sealed class WindowsFilesystemTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));

    public WindowsFilesystemTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Metadata_ResolvesFinalPathIdentityAndAttributes()
    {
        var metadata = WindowsFilesystemInterop.GetMetadata(root);
        Assert.Equal(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(metadata.FinalPath), StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(metadata.Identity.FileId);
        Assert.True(metadata.Attributes.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void Identity_DistinguishesEntries()
    {
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        Assert.NotEqual(WindowsFilesystemInterop.GetIdentity(root), WindowsFilesystemInterop.GetIdentity(child));
    }

    [Fact]
    public void LocalHostDetector_RejectsNamesAddressesAndConfiguredAliases()
    {
        var detector = new LocalHostUncDetector(["backup-alias"]);
        Assert.True(detector.IsHostedLocally(@"\\localhost\share"));
        Assert.True(detector.IsHostedLocally(@"\\127.0.0.1\share"));
        Assert.True(detector.IsHostedLocally(@"\\backup-alias\share"));
    }

    [Fact]
    public void Overlap_ResolvesDeepestExistingAncestorForUncreatedDestination()
    {
        var source = Path.Combine(root, "source");
        var aliases = Path.Combine(root, "aliases");
        var alias = Path.Combine(aliases, "source-link");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(aliases);
        try
        {
            Directory.CreateSymbolicLink(alias, source);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // The production behavior is exercised where Developer Mode or the link privilege is available.
            return;
        }

        var uncreatedDestination = Path.Combine(alias, "future-backups");
        Assert.Equal(source, PathOverlap.FindDestinationOverlap(uncreatedDestination, [source]));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
