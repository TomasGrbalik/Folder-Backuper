using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Notifications;

namespace FolderBackuper.Tests;

public sealed class NotificationPayloadTests
{
    private static DateTimeOffset Utc(int hour) => new(2026, 8, 19, hour, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RunOutcome.Successful)]
    [InlineData(RunOutcome.SuccessfulWithWarnings)]
    [InlineData(RunOutcome.Failed)]
    public void Build_CoversEveryEligibleOutcome(RunOutcome outcome)
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, outcome, Utc(9));
        var artifact = MonitoringTestSeed.Artifact(run, destination, 4096, Utc(9));

        var payload = NotificationPayloadBuilder.Build(run, [], artifact);

        Assert.Equal(outcome, payload.Outcome);
        Assert.Equal(run.Id, payload.RunId);
        Assert.Equal(job.Id, payload.JobId);
        Assert.Equal("Finance", payload.JobName);
        Assert.Equal(job.SourcePath, payload.SourcePath);
        Assert.Equal("Andromeda", payload.DestinationName);
        Assert.Equal(artifact.EffectivePath, payload.DestinationEffectivePath);
        Assert.Equal(artifact.FinalFileName, payload.ArchiveFileName);
        Assert.Equal(4096, payload.ArchiveBytes);
        Assert.Equal(run.DueAtUtc, payload.ScheduledDueAtUtc);
        Assert.Equal(run.StartedAtUtc, payload.StartedAtUtc);
        Assert.Equal(run.CompletedAtUtc, payload.CompletedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), payload.Duration);
        Assert.Equal(TimeZoneInfo.Utc.Id, payload.TimeZoneId);
        Assert.Equal(0, payload.TotalProblemCount);
        Assert.Empty(payload.Problems);
        Assert.False(payload.ProblemsTruncated);
    }

    [Fact]
    public void Build_CapsProblemsAtOneHundredAndReportsTheTrueTotal()
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.SuccessfulWithWarnings, Utc(9));
        var problems = Enumerable.Range(0, 250)
            .Select(index => MonitoringTestSeed.Problem(
                run.Id, BackupProblemSeverity.Warning, $"Problem {index}", $@"C:\Data\File{index}.txt"))
            .ToList();

        var payload = NotificationPayloadBuilder.Build(run, problems, null);

        Assert.Equal(100, payload.Problems.Count);
        Assert.Equal(250, payload.TotalProblemCount);
        Assert.True(payload.ProblemsTruncated);
    }

    [Fact]
    public void Build_OrdersErrorsBeforeWarningsSoTruncationKeepsActionableEntries()
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Failed, Utc(9));

        // A hundred warnings recorded before the single error would push it past the cap if the
        // builder preserved insertion order.
        var problems = Enumerable.Range(0, 120)
            .Select(index => MonitoringTestSeed.Problem(run.Id, BackupProblemSeverity.Warning, $"Warning {index}"))
            .Append(MonitoringTestSeed.Problem(run.Id, BackupProblemSeverity.Error, "The source became unavailable."))
            .ToList();

        var payload = NotificationPayloadBuilder.Build(run, problems, null);

        Assert.Equal(BackupProblemSeverity.Error, payload.Problems[0].Severity);
        Assert.Equal("The source became unavailable.", payload.Problems[0].Message);
        Assert.Equal(121, payload.TotalProblemCount);
    }

    [Fact]
    public void Build_CountsRetentionWarningsSeparately()
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.SuccessfulWithWarnings, Utc(9));
        var problems = new List<RunProblem>
        {
            RetentionWarning(run.Id, "An older backup could not be removed."),
            RetentionWarning(run.Id, "A second older backup could not be removed."),
            MonitoringTestSeed.Problem(run.Id, BackupProblemSeverity.Warning, "A source file was skipped.")
        };

        var payload = NotificationPayloadBuilder.Build(run, problems, null);

        Assert.Equal(2, payload.RetentionWarningCount);
        Assert.Equal(3, payload.TotalProblemCount);
    }

    [Fact]
    public void Build_FallsBackToTheRunDestinationSnapshotWhenNoArtifactExists()
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Failed, Utc(9));

        var payload = NotificationPayloadBuilder.Build(run, [], null);

        Assert.Null(payload.ArchiveFileName);
        Assert.Null(payload.ArchiveBytes);
        Assert.Contains(destination.RootPath, payload.DestinationEffectivePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsARunWithNoTerminalOutcome()
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Running(job, destination, RunPhase.Compressing, Utc(9));

        Assert.Throws<InvalidOperationException>(() => NotificationPayloadBuilder.Build(run, [], null));
    }

    [Fact]
    public void Payload_ExposesNoCredentialOrProtectedMember()
    {
        // Redaction is structural: if no member can hold a secret, no formatting path can leak one.
        foreach (var type in new[] { typeof(NotificationPayload), typeof(NotificationProblem) })
        {
            Assert.DoesNotContain(type.GetProperties(), property =>
                property.PropertyType == typeof(byte[])
                || property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Username", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Payload_DoesNotCarryTheDestinationUsernameEvenForAnSmbRun()
    {
        var destination = DatabaseInitializationTests.Destination("Andromeda");
        destination.Type = DestinationType.Smb;
        destination.RootPath = @"\\nas\backups";
        destination.SmbUsername = "backup-operator";

        var job = DatabaseInitializationTests.Job(destination.Id, "Finance");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, Utc(9));
        Assert.Equal("backup-operator", run.DestinationUsername);

        var payload = NotificationPayloadBuilder.Build(run, [], null);
        var rendered = System.Text.Json.JsonSerializer.Serialize(payload);

        Assert.DoesNotContain("backup-operator", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsNotifiable_ExcludesOnlyCancelled()
    {
        Assert.True(NotificationOutboxWriter.IsNotifiable(RunOutcome.Successful));
        Assert.True(NotificationOutboxWriter.IsNotifiable(RunOutcome.SuccessfulWithWarnings));
        Assert.True(NotificationOutboxWriter.IsNotifiable(RunOutcome.Failed));
        Assert.False(NotificationOutboxWriter.IsNotifiable(RunOutcome.Cancelled));
    }

    private static RunProblem RetentionWarning(Guid runId, string message) => new()
    {
        RunId = runId,
        Phase = RunPhase.Finalizing,
        Severity = BackupProblemSeverity.Warning,
        Operation = "Remove expired backup",
        ErrorCategory = nameof(BackupProblemCategory.CleanupFailed),
        UserMessage = message
    };
}
