using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Formatting;

using FolderBackuper.Infrastructure.Localization;
using FolderBackuper.Resources;
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

    // Resolved per call rather than cached in a static, because the reading language can change
    // while the process runs.
    private static string[] ProblemColumns =>
    [
        EmailStrings.ColumnSeverity,
        EmailStrings.ColumnPhase,
        EmailStrings.ColumnOperation,
        EmailStrings.ColumnDetail
    ];

    public static NotificationMessage RunResult(NotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var outcome = OutcomeHeadline(payload.Outcome);
        var subject = string.Format(
            CultureInfo.CurrentCulture, EmailStrings.SubjectRunResult, payload.JobName, outcome);
        return new NotificationMessage(subject, RunResultHtml(payload, outcome), RunResultText(payload, outcome));
    }

    public static NotificationMessage Test(IReadOnlyList<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var subject = EmailStrings.SubjectTest;
        var joined = string.Join(", ", recipients);
        var text = new StringBuilder()
            .AppendLine(EmailStrings.TestIntro)
            .AppendLine()
            .AppendLine(EmailStrings.TestBody)
            .AppendLine()
            .AppendLine(string.Format(CultureInfo.CurrentCulture, EmailStrings.TestRecipients, joined))
            .ToString();

        var html = Document(
            EmailStrings.TestHeading,
            "info",
            $"""
             <p style="margin:0 0 12px;">{Encode(EmailStrings.TestIntro)}</p>
             <p style="margin:0 0 12px;">{Encode(EmailStrings.TestBody)}</p>
             <p style="{MutedStyle}">{Encode(string.Format(CultureInfo.CurrentCulture, EmailStrings.TestRecipients, joined))}</p>
             """);

        return new NotificationMessage(subject, html, text);
    }

    public static string OutcomeHeadline(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Successful => EmailStrings.OutcomeSuccessful,
        RunOutcome.SuccessfulWithWarnings => EmailStrings.OutcomeWithWarnings,
        RunOutcome.Failed => EmailStrings.OutcomeFailed,
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
        builder.AppendLine(string.Format(CultureInfo.CurrentCulture, EmailStrings.HeadingRunResult, payload.JobName, outcome));
        builder.AppendLine();
        foreach (var (label, value) in Facts(payload))
        {
            builder.AppendLine($"{label}: {value}");
        }

        if (payload.ErrorSummary is { } summary)
        {
            builder.AppendLine();
            builder.AppendLine(string.Format(
                CultureInfo.CurrentCulture, EmailStrings.ErrorLine, MessageText.Resolve(summary)));
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
                builder.AppendLine(string.Format(
                    CultureInfo.CurrentCulture,
                    EmailStrings.ProblemLine,
                    EnumText.For(problem.Severity),
                    EnumText.For(problem.Operation),
                    MessageText.Resolve(problem.Message)));
                if (problem.Path is { Length: > 0 } path)
                {
                    builder.AppendLine($"    {path}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine(EmailStrings.OpenForDetails);
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

        if (payload.ErrorSummary is { } summary)
        {
            body.Append($"<p style=\"{SummaryStyle}\">{Encode(MessageText.Resolve(summary))}</p>");
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
                body.Append($"<td style=\"{ProblemCellStyle}\">{Encode(EnumText.For(problem.Operation))}</td>");
                body.Append($"<td style=\"{ProblemCellStyle}\">{Encode(MessageText.Resolve(problem.Message))}");
                if (problem.Path is { Length: > 0 } path)
                {
                    body.Append($"<div style=\"{PathStyle}\">{Encode(path)}</div>");
                }

                body.Append("</td></tr>");
            }

            body.Append("</tbody></table>");
        }

        body.Append($"<p style=\"{MutedStyle}\">{Encode(EmailStrings.OpenForDetails)}</p>");
        return Document(
            string.Format(CultureInfo.CurrentCulture, EmailStrings.HeadingRunResult, payload.JobName, outcome),
            Accent(payload.Outcome),
            body.ToString());
    }

    private static IEnumerable<(string Label, string Value)> Facts(NotificationPayload payload)
    {
        yield return (EmailStrings.FactJob, payload.JobName);
        yield return (EmailStrings.FactOutcome, OutcomeHeadline(payload.Outcome));
        yield return (EmailStrings.FactSource, payload.SourcePath);
        yield return (EmailStrings.FactDestination, $"{payload.DestinationName} ({payload.DestinationEffectivePath})");
        if (payload.ArchiveFileName is { Length: > 0 } archive)
        {
            yield return (EmailStrings.FactArchive, archive);
        }

        if (payload.ArchiveBytes is { } bytes)
        {
            yield return (EmailStrings.FactArchiveSize, DisplayFormat.Bytes(bytes));
        }

        yield return (EmailStrings.FactScheduled, Instant(payload.ScheduledDueAtUtc, payload.TimeZoneId));
        if (payload.StartedAtUtc is { } started)
        {
            yield return (EmailStrings.FactStarted, Instant(started, payload.TimeZoneId));
        }

        if (payload.CompletedAtUtc is { } completed)
        {
            yield return (EmailStrings.FactCompleted, Instant(completed, payload.TimeZoneId));
        }

        if (payload.Duration is { } duration)
        {
            yield return (EmailStrings.FactDuration, DisplayFormat.Duration(duration));
        }

        yield return (EmailStrings.FactProblems, payload.TotalProblemCount.ToString("N0", CultureInfo.CurrentCulture));
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
        ? string.Format(
            CultureInfo.CurrentCulture,
            EmailStrings.ProblemsHeadingTruncated,
            payload.Problems.Count,
            payload.TotalProblemCount)
        : string.Format(CultureInfo.CurrentCulture, EmailStrings.ProblemsHeading, payload.TotalProblemCount);

    private static string RetentionSentence(int count) => Plural.Format(
        count,
        EmailStrings.RetentionWarnings_One,
        EmailStrings.RetentionWarnings_Few,
        EmailStrings.RetentionWarnings_Many);

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
                      <div style="font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#6b7280;">{Encode(EmailStrings.BrandLabel)}</div>
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
