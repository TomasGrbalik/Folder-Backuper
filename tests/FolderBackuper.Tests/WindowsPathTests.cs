using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Tests;

public sealed class WindowsPathTests
{
    [Fact]
    public void Local_NormalizesAbsolutePath()
    {
        var result = WindowsPath.Local(Path.Combine(Path.GetTempPath(), "FolderBackuper", "."));
        Assert.True(result.IsValid, result.Error);
        Assert.False(Path.EndsInDirectorySeparator(result.Path));
    }

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\folder")]
    [InlineData(@"C:\parent\..\folder")]
    public void Local_RejectsUnsupportedForms(string path) => Assert.False(WindowsPath.Local(path).IsValid);

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server", false)]
    [InlineData(@"Z:\mapped", false)]
    [InlineData(@"\\?\UNC\server\share", false)]
    [InlineData(@"\\server\share\..\other", false)]
    public void Unc_RequiresConventionalServerAndShare(string path, bool valid) =>
        Assert.Equal(valid, WindowsPath.Unc(path).IsValid);

    [Theory]
    [InlineData("", true, "")]
    [InlineData(@"team\daily", true, @"team\daily")]
    [InlineData(@"team/../daily", false, null)]
    [InlineData(@"C:\daily", false, null)]
    [InlineData(@"team:daily", false, null)]
    public void Relative_RejectsRootAndTraversal(string path, bool valid, string? expected)
    {
        var result = WindowsPath.Relative(path);
        Assert.Equal(valid, result.IsValid);
        Assert.Equal(expected, result.Path);
    }

    [Fact]
    public void Overlap_IsCaseInsensitiveAndBoundaryAware()
    {
        Assert.True(PathOverlap.IsSameOrDescendant(@"C:\Source\Backups", @"c:\source"));
        Assert.True(PathOverlap.IsSameOrDescendant(@"C:\Source", @"C:\"));
        Assert.False(PathOverlap.IsSameOrDescendant(@"C:\Source2", @"C:\Source"));
        Assert.True(PathOverlap.Overlaps(@"C:\Source", @"C:\Source\Child"));
        Assert.Equal(@"C:\Source\Child", PathOverlap.FindDestinationOverlap(@"C:\Source", [@"C:\Source\Child"]));
    }
}
