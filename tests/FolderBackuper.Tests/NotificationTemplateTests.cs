using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Formatting;

namespace FolderBackuper.Tests;

public sealed class NotificationTemplateTests
{
    private static DateTimeOffset Utc(int hour) => new(2026, 8, 19, hour, 0, 0, TimeSpan.Zero);

    private static NotificationPayload Payload(
        RunOutcome outcome,
        IReadOnlyCollection<RunProblem>? problems = null,
        string jobName = "Finance")
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, jobName);
        var run = MonitoringTestSeed.Terminal(job, destination, outcome, Utc(9));
        var artifact = outcome == RunOutcome.Failed
            ? null
            : MonitoringTestSeed.Artifact(run, destination, 4096, Utc(9));
        return NotificationPayloadBuilder.Build(run, problems ?? [], artifact);
    }

    [Theory]
    [InlineData(RunOutcome.Successful, "backup successful")]
    [InlineData(RunOutcome.SuccessfulWithWarnings, "completed with warnings")]
    [InlineData(RunOutcome.Failed, "backup failed")]
    public void RunResult_SubjectNamesTheJobAndTheOutcome(RunOutcome outcome, string expected)
    {
        var message = NotificationTemplates.RunResult(Payload(outcome));

        Assert.Equal($"Folder Backuper: Finance - {expected}", message.Subject);
        Assert.Contains(expected, message.Html, StringComparison.Ordinal);
        Assert.Contains(expected, message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunOutcome.Successful)]
    [InlineData(RunOutcome.SuccessfulWithWarnings)]
    [InlineData(RunOutcome.Failed)]
    public void RunResult_RendersEveryAcceptedFieldInBothBodies(RunOutcome outcome)
    {
        var payload = Payload(outcome);
        var message = NotificationTemplates.RunResult(payload);

        foreach (var body in new[] { message.Html, message.Text })
        {
            Assert.Contains(payload.JobName, body, StringComparison.Ordinal);
            Assert.Contains(payload.SourcePath, body, StringComparison.Ordinal);
            Assert.Contains(payload.DestinationName, body, StringComparison.Ordinal);
            Assert.Contains("2026-08-19 09:00:00", body, StringComparison.Ordinal);
            Assert.Contains(TimeZoneInfo.Utc.Id, body, StringComparison.Ordinal);
            Assert.Contains("Duration", body, StringComparison.Ordinal);
            Assert.Contains("Problems", body, StringComparison.Ordinal);
        }

        if (payload.ArchiveFileName is not null)
        {
            Assert.Contains(payload.ArchiveFileName, message.Text, StringComparison.Ordinal);
            Assert.Contains(DisplayFormat.Bytes(4096), message.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunResult_EncodesMarkupCharactersFoundInPathsAndMessages()
    {
        var payload = Payload(RunOutcome.Failed, [
            MonitoringTestSeed.Problem(
                Guid.NewGuid(),
                BackupProblemSeverity.Error,
                "Access denied to <config> & retry",
                @"C:\Data\R&D\<draft>.txt")
        ]);

        var message = NotificationTemplates.RunResult(payload);

        // The raw characters must not survive into the markup, or a path could inject elements.
        Assert.DoesNotContain("<draft>", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<config>", message.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;draft&gt;", message.Html, StringComparison.Ordinal);
        Assert.Contains("&amp;", message.Html, StringComparison.Ordinal);

        // The plain-text alternative keeps them readable.
        Assert.Contains(@"C:\Data\R&D\<draft>.txt", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_StatesTheTotalWhenTheProblemListIsTruncated()
    {
        var runId = Guid.NewGuid();
        var problems = Enumerable.Range(0, 150)
            .Select(index => MonitoringTestSeed.Problem(runId, BackupProblemSeverity.Warning, $"Problem {index}"))
            .ToList();

        var message = NotificationTemplates.RunResult(Payload(RunOutcome.SuccessfulWithWarnings, problems));

        foreach (var body in new[] { message.Html, message.Text })
        {
            Assert.Contains("first 100 of 150 problems", body, StringComparison.Ordinal);
            Assert.Contains("local run details", body, StringComparison.Ordinal);
        }

        Assert.Contains("Problem 99", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Problem 100", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_MentionsRetentionWarningsSeparatelyFromProblems()
    {
        var problems = new List<RunProblem>
        {
            new()
            {
                RunId = Guid.NewGuid(),
                Phase = RunPhase.Finalizing,
                Severity = BackupProblemSeverity.Warning,
                Operation = "Remove expired backup",
                ErrorCategory = nameof(BackupProblemCategory.CleanupFailed),
                UserMessage = "An older backup could not be removed."
            }
        };

        var message = NotificationTemplates.RunResult(Payload(RunOutcome.SuccessfulWithWarnings, problems));

        Assert.Contains("One retention warning", message.Text, StringComparison.Ordinal);
        Assert.Contains("One retention warning", message.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunResult_IncludesTheErrorSummaryForAFailure()
    {
        var message = NotificationTemplates.RunResult(Payload(RunOutcome.Failed));

        Assert.Contains("Simulated failure.", message.Text, StringComparison.Ordinal);
        Assert.Contains("Simulated failure.", message.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_NamesTheRecipientsItVerifies()
    {
        var message = NotificationTemplates.Test(["one@example.test", "two@example.test"]);

        Assert.Equal("Folder Backuper: test email", message.Subject);
        Assert.Contains("one@example.test", message.Text, StringComparison.Ordinal);
        Assert.Contains("two@example.test", message.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeHeadline_RejectsCancelledBecauseItNeverNotifies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationTemplates.OutcomeHeadline(RunOutcome.Cancelled));
    }

    [Fact]
    public void RunResult_StylesEveryElementInlineBecauseClientsStripStyleElements()
    {
        var problems = new List<RunProblem>
        {
            MonitoringTestSeed.Problem(
                Guid.NewGuid(), BackupProblemSeverity.Error, "Access denied.", @"C:\Source\open.docx")
        };

        var message = NotificationTemplates.RunResult(Payload(RunOutcome.Failed, problems));

        // A style element is commonly stripped, and a bare th then falls back to the browser default,
        // which is centred. Every table header must therefore carry its own left alignment.
        Assert.DoesNotContain("<style", message.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", message.Html, StringComparison.OrdinalIgnoreCase);

        var headers = System.Text.RegularExpressions.Regex.Matches(message.Html, @"<th(?:\s[^>]*)?>");
        Assert.NotEmpty(headers);
        Assert.All(headers, header =>
            Assert.Contains("text-align:left", header.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void Test_AlsoStylesEveryElementInline()
    {
        var message = NotificationTemplates.Test(["operator@example.test"]);

        Assert.DoesNotContain("<style", message.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", message.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunResult_ProducesSelfContainedHtmlWithNoExternalReference()
    {
        var message = NotificationTemplates.RunResult(Payload(RunOutcome.Successful));

        // Email clients block remote content, so the message must not depend on any.
        Assert.DoesNotContain("<img", message.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", message.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", message.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", message.Html, StringComparison.OrdinalIgnoreCase);
    }
}
