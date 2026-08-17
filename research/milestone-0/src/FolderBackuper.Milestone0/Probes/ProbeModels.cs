using System.Text.Json.Serialization;

namespace FolderBackuper.Milestone0.Probes;

[JsonConverter(typeof(JsonStringEnumConverter<ProbeStatus>))]
public enum ProbeStatus
{
    Passed,
    Failed,
    Skipped,
    Inconclusive
}

public sealed record ProbeResult(
    string Name,
    ProbeStatus Status,
    string Summary,
    IReadOnlyDictionary<string, string>? Evidence = null,
    int? NativeErrorCode = null);

public sealed record ProbeReport(
    Guid RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string MachineName,
    string WindowsVersion,
    string FrameworkVersion,
    IReadOnlyList<ProbeResult> Results)
{
    public bool Succeeded => Results.All(result => result.Status is ProbeStatus.Passed or ProbeStatus.Skipped);
}
