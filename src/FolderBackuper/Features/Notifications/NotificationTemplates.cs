using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Formatting;

namespace FolderBackuper.Features.Notifications;

/// <summary>A rendered message, still independent of any provider.</summary>
public sealed record NotificationMessage(string Subject, string Html, string Text);

/// <summary>
/// Renders provider-neutral success, warning, and failure messages from a payload snapshot.
/// </summary>
/// <remarks>
/// Every interpolated value is HTML-encoded. File paths and Win32 error messages routinely contain
/// characters such as &amp; and &lt;, which would otherwise corrupt the markup or inject elements.
/// </remarks>
public static class NotificationTemplates
{
    // Styles are inline on every element rather than declared in a style element, because many email
    // clients strip style elements. Without them a th would fall back to the browser default, which is
    // centred and bold, so the label column has to state text-align:left explicitly.
    private const string TableStyle = "border-collapse:collapse;width:100%;margin:0 0 16px;";

    private const string FactLabelStyle =
        "text-align:left;padding:5px 12px 5px 0;color:#6b7280;font-weight:600;"
        + "white-space:nowrap;vertical-align:top;";

    private const string FactValueStyle = "text-align:left;padding:5px 0;word-break:break-word;";

    private const string ProblemHeaderStyle =
        "text-align:left;background:#f4f5f7;padding:6px 8px;border-bottom:1px solid #e3e5e9;font-weight:600;";

    private const string ProblemCellStyle =
        "text-align:left;padding:6px 8px;border-bottom:1px solid #eef0f3;vertical-align:top;font-size:13px;";

    private const string SummaryStyle =
        "margin:0 0 16px;padding:10px 12px;background:#fdf2f2;border-left:3px solid #a3282d;";

    private const string MutedStyle = "color:#6b7280;font-size:13px;";

    private const string HeadingStyle = "font-size:15px;margin:20px 0 8px;";

    private const string PathStyle =
        "font-family:Consolas,'Courier New',monospace;font-size:12px;color:#6b7280;"
        + "margin-top:3px;word-break:break-all;";

    private static readonly string[] ProblemColumns = ["Severity", "Phase", "Operation", "Detail"];

    public static NotificationMessage RunResult(NotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var outcome = OutcomeHeadline(payload.Outcome);
        var subject = $"Folder Backuper: {payload.JobName} - {outcome}";
        return new NotificationMessage(subject, RunResultHtml(payload, outcome), RunResultText(payload, outcome));
    }

    public static NotificationMessage Test(IReadOnlyList<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        const string subject = "Folder Backuper: test email";
        var text = new StringBuilder()
            .AppendLine("This is a test email from Folder Backuper.")
            .AppendLine()
            .AppendLine("The saved notification configuration on the backup PC can reach Resend and")
            .AppendLine("deliver to the configured recipients.")
            .AppendLine()
            .AppendLine($"Recipients: {string.Join(", ", recipients)}")
            .ToString();

        var html = Document(
            "Test email",
            "info",
            $"""
             <p style="margin:0 0 12px;">This is a test email from Folder Backuper.</p>
             <p style="margin:0 0 12px;">The saved notification configuration on the backup PC can reach
             Resend and deliver to the configured recipients.</p>
             <p style="{MutedStyle}">Recipients: {Encode(string.Join(", ", recipients))}</p>
             """);

        return new NotificationMessage(subject, html, text);
    }

    public static string OutcomeHeadline(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Successful => "backup successful",
        RunOutcome.SuccessfulWithWarnings => "completed with warnings",
        RunOutcome.Failed => "backup failed",
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Only terminal notifiable outcomes have a template.")
    };

    private static string Accent(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Successful => "ok",
        RunOutcome.SuccessfulWithWarnings => "warn",
        _ => "err"
    };

    private static string RunResultText(NotificationPayload payload, string outcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{payload.JobName} - {outcome}");
        builder.AppendLine();
        foreach (var (label, value) in Facts(payload))
        {
            builder.AppendLine($"{label}: {value}");
        }

        if (payload.ErrorSummary is { Length: > 0 } summary)
        {
            builder.AppendLine();
            builder.AppendLine($"Error: {summary}");
        }

        if (payload.RetentionWarningCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine(RetentionSentence(payload.RetentionWarningCount));
        }

        if (payload.TotalProblemCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine(ProblemHeading(payload));
            foreach (var problem in payload.Problems)
            {
                builder.AppendLine($"- [{problem.Severity}] {problem.Operation}: {problem.Message}");
                if (problem.Path is { Length: > 0 } path)
                {
                    builder.AppendLine($"    {path}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("Open Folder Backuper on the backup PC for complete run details.");
        return builder.ToString();
    }

    private static string RunResultHtml(NotificationPayload payload, string outcome)
    {
        var body = new StringBuilder();
        body.Append($"<table role=\"presentation\" style=\"{TableStyle}\">");
        foreach (var (label, value) in Facts(payload))
        {
            body.Append($"<tr><th scope=\"row\" style=\"{FactLabelStyle}\">{Encode(label)}</th>");
            body.Append($"<td style=\"{FactValueStyle}\">{Encode(value)}</td></tr>");
        }

        body.Append("</table>");

        if (payload.ErrorSummary is { Length: > 0 } summary)
        {
            body.Append($"<p style=\"{SummaryStyle}\">{Encode(summary)}</p>");
        }

        if (payload.RetentionWarningCount > 0)
        {
            body.Append($"<p style=\"{MutedStyle}\">{Encode(RetentionSentence(payload.RetentionWarningCount))}</p>");
        }

        if (payload.TotalProblemCount > 0)
        {
            body.Append($"<h2 style=\"{HeadingStyle}\">{Encode(ProblemHeading(payload))}</h2>");
            body.Append($"<table role=\"presentation\" style=\"{TableStyle}\"><thead><tr>");
            foreach (var column in ProblemColumns)
            {
                body.Append($"<th scope=\"col\" style=\"{ProblemHeaderStyle}\">{column}</th>");
            }

            body.Append("</tr></thead><tbody>");
            foreach (var problem in payload.Problems)
            {
                body.Append("<tr>");
                body.Append($"<td style=\"{ProblemCellStyle}\">{Encode(problem.Severity.ToString())}</td>");
                body.Append($"<td style=\"{ProblemCellStyle}\">{Encode(problem.Phase.ToString())}</td>");
                body.Append($"<td style=\"{ProblemCellStyle}\">{Encode(problem.Operation)}</td>");
                body.Append($"<td style=\"{ProblemCellStyle}\">{Encode(problem.Message)}");
                if (problem.Path is { Length: > 0 } path)
                {
                    body.Append($"<div style=\"{PathStyle}\">{Encode(path)}</div>");
                }

                body.Append("</td></tr>");
            }

            body.Append("</tbody></table>");
        }

        body.Append($"<p style=\"{MutedStyle}\">Open Folder Backuper on the backup PC for complete run details.</p>");
        return Document($"{payload.JobName} - {outcome}", Accent(payload.Outcome), body.ToString());
    }

    private static IEnumerable<(string Label, string Value)> Facts(NotificationPayload payload)
    {
        yield return ("Job", payload.JobName);
        yield return ("Outcome", OutcomeHeadline(payload.Outcome));
        yield return ("Source", payload.SourcePath);
        yield return ("Destination", $"{payload.DestinationName} ({payload.DestinationEffectivePath})");
        if (payload.ArchiveFileName is { Length: > 0 } archive)
        {
            yield return ("Archive", archive);
        }

        if (payload.ArchiveBytes is { } bytes)
        {
            yield return ("Archive size", DisplayFormat.Bytes(bytes));
        }

        yield return ("Scheduled", Instant(payload.ScheduledDueAtUtc, payload.TimeZoneId));
        if (payload.StartedAtUtc is { } started)
        {
            yield return ("Started", Instant(started, payload.TimeZoneId));
        }

        if (payload.CompletedAtUtc is { } completed)
        {
            yield return ("Completed", Instant(completed, payload.TimeZoneId));
        }

        if (payload.Duration is { } duration)
        {
            yield return ("Duration", DisplayFormat.Duration(duration));
        }

        yield return ("Problems", payload.TotalProblemCount.ToString("N0", CultureInfo.InvariantCulture));
    }

    // Email is read away from the backup PC, so the run's own time zone is used and named rather
    // than the reader's. The format stays locale-independent for the same reason.
    private static string Instant(DateTimeOffset value, string timeZoneId)
    {
        var zone = ResolveTimeZone(timeZoneId);
        var local = zone is null ? value : TimeZoneInfo.ConvertTime(value, zone);
        var label = zone is null ? "UTC" : zone.Id;
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ({label})";
    }

    private static TimeZoneInfo? ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A time zone removed since the run was recorded must not stop the notification.
            return null;
        }
    }

    private static string ProblemHeading(NotificationPayload payload) => payload.ProblemsTruncated
        ? $"Showing the first {payload.Problems.Count:N0} of {payload.TotalProblemCount:N0} problems; "
          + "the complete list is in local run details"
        : $"Problems ({payload.TotalProblemCount:N0})";

    private static string RetentionSentence(int count) => count == 1
        ? "One retention warning was recorded. An older backup may not have been removed."
        : $"{count:N0} retention warnings were recorded. Older backups may not have been removed.";

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    // Inline styles only: email clients do not reliably honour style elements, and none of them
    // load external stylesheets.
    private static string Document(string heading, string accent, string body)
    {
        var color = accent switch
        {
            "ok" => "#0f7b55",
            "warn" => "#8a5a00",
            "err" => "#a3282d",
            _ => "#06727c"
        };

        return $"""
                <div style="margin:0;padding:24px;background:#f4f5f7;font-family:'Segoe UI',system-ui,sans-serif;color:#22262b;">
                  <div style="max-width:680px;margin:0 auto;background:#ffffff;border:1px solid #e3e5e9;border-radius:9px;overflow:hidden;">
                    <div style="padding:16px 24px;border-bottom:1px solid #e3e5e9;">
                      <div style="font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#6b7280;">Folder Backuper</div>
                      <div style="font-size:19px;font-weight:650;color:{color};margin-top:4px;">{Encode(heading)}</div>
                    </div>
                    <div style="padding:20px 24px;font-size:14px;line-height:1.55;text-align:left;">
                      {body}
                    </div>
                  </div>
                </div>
                """;
    }
}
