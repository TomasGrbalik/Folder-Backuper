using System.Text;
using System.Text.Json;
using FolderBackuper.Milestone0.Probes;

namespace FolderBackuper.Milestone0.Reporting;

public static class ProbeReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task WriteAsync(ProbeReport report, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var baseName = $"milestone-0-{report.RunId:N}";
        var jsonPath = Path.Combine(outputDirectory, $"{baseName}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{baseName}.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(report), cancellationToken);
    }

    private static string RenderMarkdown(ProbeReport report)
    {
        var builder = new StringBuilder()
            .AppendLine("# Milestone 0 Compatibility Result")
            .AppendLine()
            .AppendLine($"- Run: `{report.RunId}`")
            .AppendLine($"- Started (UTC): `{report.StartedAtUtc:O}`")
            .AppendLine($"- Machine: `{report.MachineName}`")
            .AppendLine($"- Windows: `{report.WindowsVersion}`")
            .AppendLine($"- .NET: `{report.FrameworkVersion}`")
            .AppendLine()
            .AppendLine("| Probe | Status | Summary | Native error |")
            .AppendLine("|---|---|---|---|");

        foreach (var result in report.Results)
        {
            builder.AppendLine($"| {Escape(result.Name)} | {result.Status} | {Escape(result.Summary)} | {result.NativeErrorCode?.ToString() ?? string.Empty} |");
        }

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
