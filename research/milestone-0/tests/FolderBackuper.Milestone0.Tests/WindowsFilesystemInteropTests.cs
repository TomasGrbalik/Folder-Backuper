using FolderBackuper.Milestone0.Filesystem;

namespace FolderBackuper.Milestone0.Tests;

public sealed class WindowsFilesystemInteropTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-Test-{Guid.NewGuid():N}");

    public WindowsFilesystemInteropTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Identity_RemainsStableAcrossRename()
    {
        var original = Path.Combine(directory, "original.dat");
        var renamed = Path.Combine(directory, "renamed.dat");
        File.WriteAllBytes(original, [1, 2, 3]);

        var before = WindowsFilesystemInterop.GetIdentity(original);
        File.Move(original, renamed);
        var after = WindowsFilesystemInterop.GetIdentity(renamed);

        Assert.Equal(before, after);
    }

    [Fact]
    public void FinalPath_ResolvesAnExistingDirectory()
    {
        var finalPath = WindowsFilesystemInterop.GetFinalPath(directory);

        Assert.Equal(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(finalPath), ignoreCase: true);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
