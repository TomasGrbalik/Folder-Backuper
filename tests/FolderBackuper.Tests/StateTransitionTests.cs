using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;

namespace FolderBackuper.Tests;

public sealed class StateTransitionTests
{
    [Fact]
    public void JobLifecycle_RejectsInvalidTransitions()
    {
        var job = DatabaseInitializationTests.Job(Guid.NewGuid(), "Job");

        job.Activate();
        Assert.Equal(JobLifecycle.Active, job.Lifecycle);
        job.Pause();
        job.Archive();
        Assert.Throws<InvalidOperationException>(job.Archive);
        job.Restore();
        Assert.Equal(JobLifecycle.Paused, job.Lifecycle);
    }

    [Fact]
    public void DestinationLifecycle_RejectsRepeatedArchive()
    {
        var destination = DatabaseInitializationTests.Destination("Destination");

        destination.Archive();

        Assert.Equal(DestinationLifecycle.Archived, destination.Lifecycle);
        Assert.Throws<InvalidOperationException>(destination.Archive);
    }

    [Fact]
    public void RunCancellation_ClosesAtFinalCommitBoundary()
    {
        var run = CreateRun();
        var now = DateTimeOffset.UtcNow;
        AdvanceToFinalizing(run, now);

        run.BeginFinalCommit(now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => run.BeginFinalCommit(now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => run.RequestCancellation(now.AddMinutes(2)));
        run.MarkFinalCommitted(now.AddMinutes(3));
        Assert.Throws<InvalidOperationException>(() => run.MarkFinalCommitted(now.AddMinutes(4)));
        run.Complete(RunOutcome.Successful, now.AddMinutes(4));
        Assert.Throws<InvalidOperationException>(() => run.Complete(RunOutcome.Failed, now.AddMinutes(5)));
    }

    [Fact]
    public void QueuedCancellation_BecomesTerminalWithoutStarting()
    {
        var run = CreateRun();
        var now = DateTimeOffset.UtcNow;
        run.AdvanceTo(RunPhase.Queued, now);

        run.RequestCancellation(now.AddSeconds(1));

        Assert.Equal(RunOutcome.Cancelled, run.Outcome);
        Assert.Null(run.StartedAtUtc);
        Assert.Throws<InvalidOperationException>(() => run.AdvanceTo(RunPhase.Scanning, now.AddSeconds(2)));
    }

    [Fact]
    public void ArtifactRetention_RequiresRetainedManagedArtifact()
    {
        var artifact = CreateArtifact();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<InvalidOperationException>(() => artifact.BeginRetentionDeletion(now));
        artifact.MarkRetained(now);
        artifact.BeginRetentionDeletion(now.AddSeconds(1));
        artifact.MarkRemovedByRetention(now.AddSeconds(2));

        Assert.Equal(ArtifactState.RemovedByRetention, artifact.State);
        Assert.Throws<InvalidOperationException>(() => artifact.MarkUnmanaged(now.AddSeconds(3)));
    }

    [Fact]
    public void ArtifactOperations_RejectContradictoryIntents()
    {
        var failedFinalization = CreateArtifact();
        failedFinalization.MarkFinalizationFailed(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => failedFinalization.MarkRetained(DateTimeOffset.UtcNow));

        var pendingRetention = CreateArtifact();
        pendingRetention.MarkRetained(DateTimeOffset.UtcNow);
        pendingRetention.BeginRetentionDeletion(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => pendingRetention.MarkMissing(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => pendingRetention.MarkUnmanaged(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NotificationSending_RecoversAsDeliveryUnknownWithoutRetry()
    {
        var notification = new NotificationOutboxItem
        {
            RunId = Guid.NewGuid(),
            RunOutcome = RunOutcome.Successful,
            PayloadSnapshot = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        notification.Claim(DateTimeOffset.UtcNow);
        notification.MarkDeliveryUnknown(
            UiMessage.For(NotificationResultMessage.InterruptedMidAttempt), DateTimeOffset.UtcNow);

        Assert.Equal(NotificationDeliveryState.DeliveryUnknown, notification.State);
        Assert.Equal(1, notification.AttemptCount);
        Assert.Throws<InvalidOperationException>(() => notification.Claim(DateTimeOffset.UtcNow));
    }

    private static BackupRun CreateRun()
    {
        var destination = DatabaseInitializationTests.Destination("Destination");
        var job = DatabaseInitializationTests.Job(destination.Id, "Job");
        return PersistenceModelTests.Run(job, destination);
    }

    private static void AdvanceToFinalizing(BackupRun run, DateTimeOffset now)
    {
        run.AdvanceTo(RunPhase.Queued, now);
        run.AdvanceTo(RunPhase.Scanning, now);
        run.AdvanceTo(RunPhase.Compressing, now);
        run.AdvanceTo(RunPhase.Transferring, now);
        run.AdvanceTo(RunPhase.Finalizing, now);
    }

    private static BackupArtifact CreateArtifact() => new()
    {
        RunId = Guid.NewGuid(),
        DestinationName = "Destination",
        DestinationRootPath = @"D:\Backups",
        EffectivePath = @"D:\Backups\Job",
        FinalFileName = "backup.zip",
        Size = 100,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OwnershipRunId = Guid.NewGuid(),
        OwnershipExpectedLength = 100
    };
}
