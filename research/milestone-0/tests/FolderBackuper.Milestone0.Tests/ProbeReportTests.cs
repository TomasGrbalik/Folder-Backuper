using FolderBackuper.Milestone0.Probes;

namespace FolderBackuper.Milestone0.Tests;

public sealed class ProbeReportTests
{
    [Fact]
    public void Succeeded_AllowsPassedAndSkippedResults()
    {
        var report = CreateReport(
            new ProbeResult("pass", ProbeStatus.Passed, "ok"),
            new ProbeResult("skip", ProbeStatus.Skipped, "not configured"));

        Assert.True(report.Succeeded);
    }

    [Theory]
    [InlineData(ProbeStatus.Failed)]
    [InlineData(ProbeStatus.Inconclusive)]
    public void Succeeded_RejectsUnresolvedResults(ProbeStatus status)
    {
        var report = CreateReport(new ProbeResult("probe", status, "unresolved"));

        Assert.False(report.Succeeded);
    }

    private static ProbeReport CreateReport(params ProbeResult[] results) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", "Windows", ".NET", results);
}
