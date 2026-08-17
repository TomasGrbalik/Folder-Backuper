using FolderBackuper.Milestone0.Probes;

namespace FolderBackuper.Milestone0.Tests;

public sealed class SourceReadProbeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-Test-{Guid.NewGuid():N}");

    public SourceReadProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "visible.txt"), "visible");
        var hidden = Path.Combine(directory, "nested", "hidden.txt");
        File.WriteAllText(hidden, "hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
    }

    [Fact]
    public async Task RunAsync_ReadsOrdinaryAndHiddenFilesWithoutMutation()
    {
        var result = await SourceReadProbe.RunAsync(directory, 100, CancellationToken.None);

        Assert.Equal(ProbeStatus.Passed, result.Status);
        Assert.Contains("2 representative files", result.Summary, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
