using FolderBackuper.Milestone0.Filesystem;

namespace FolderBackuper.Milestone0.Tests;

public sealed class LocalHostUncDetectorTests
{
    private readonly LocalHostUncDetector detector = new(["backup-alias"]);

    [Theory]
    [InlineData(@"\\localhost\backups")]
    [InlineData(@"\\127.0.0.1\backups")]
    [InlineData(@"\\[::1]\backups")]
    [InlineData(@"\\backup-alias\backups")]
    public void IsHostedLocally_RejectsKnownLocalForms(string path) => Assert.True(detector.IsHostedLocally(path));

    [Fact]
    public void IsHostedLocally_RejectsMachineName() =>
        Assert.True(detector.IsHostedLocally($@"\\{Environment.MachineName}\backups"));

    [Theory]
    [InlineData(@"C:\backups")]
    [InlineData(@"\\server-only")]
    public void IsHostedLocally_RejectsInvalidUncPaths(string path) =>
        Assert.Throws<ArgumentException>(() => detector.IsHostedLocally(path));
}
