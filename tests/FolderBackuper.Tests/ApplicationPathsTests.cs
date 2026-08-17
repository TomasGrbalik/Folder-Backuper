using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

public sealed class ApplicationPathsTests
{
    [Fact]
    public void Resolve_UsesProgramDataByDefault()
    {
        var paths = ApplicationPaths.Resolve((string?)null);

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FolderBackuper"),
            paths.Root);
    }

    [Fact]
    public void Resolve_NormalizesOverrideAndCreatesChildPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "folder-backuper-path-test", ".");

        var paths = ApplicationPaths.Resolve(root);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "config"), paths.Config);
        Assert.Equal(Path.Combine(paths.Root, "data"), paths.Data);
        Assert.Equal(Path.Combine(paths.Data, "folder-backuper.db"), paths.Database);
        Assert.Equal(Path.Combine(paths.Data, "migrations"), paths.MigrationBackups);
        Assert.Equal(Path.Combine(paths.Root, "staging"), paths.Staging);
        Assert.Equal(Path.Combine(paths.Root, "logs"), paths.Logs);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(".\\data")]
    public void Resolve_RejectsRelativeOverride(string root) =>
        Assert.Throws<InvalidOperationException>(() => ApplicationPaths.Resolve(root));

    [Fact]
    public void Resolve_RejectsDriveRoot() =>
        Assert.Throws<InvalidOperationException>(() => ApplicationPaths.Resolve(Path.GetPathRoot(Environment.SystemDirectory)));
}
