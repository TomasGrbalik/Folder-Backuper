using FolderBackuper.Milestone0.Probes;

namespace FolderBackuper.Milestone0.Tests;

public sealed class ZipCommentProbeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-Test-{Guid.NewGuid():N}");

    public ZipCommentProbeTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Run_RoundTripsOwnershipComment()
    {
        var result = ZipCommentProbe.Run(directory, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(ProbeStatus.Passed, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
