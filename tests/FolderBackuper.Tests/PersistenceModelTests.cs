using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class PersistenceModelTests
{
    [Fact]
    public async Task OccurrenceIdentity_CannotBeInsertedTwice()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.AddRange(destination, job);
            await setup.SaveChangesAsync();
        }

        var first = Run(job, destination, RunTrigger.Scheduled);
        var second = Run(job, destination, RunTrigger.CatchUp);

        var results = await Task.WhenAll(
            CreateScheduledAsync(database.RunPersistence, first, Occurrence(job.Id)),
            CreateScheduledAsync(database.RunPersistence, second, Occurrence(job.Id)));
        Assert.Equal(1, results.Count(success => success));
    }

    [Fact]
    public async Task ScheduledRun_RequiresOccurrenceIdentity()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.RunPersistence.CreateAsync(Run(job, destination, RunTrigger.Scheduled)));
        Assert.Contains("requires a scheduled occurrence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSnapshot_RemainsUnchangedWhenConfigurationChanges()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        destination.ProtectedPassword = [1, 2, 3];
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        var run = Run(job, destination);

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, run);
            await context.SaveChangesAsync();
            destination.Name = "Changed";
            destination.RootPath = @"E:\Changed";
            job.Name = "Changed job";
            await context.SaveChangesAsync();
        }

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.SingleAsync();
        Assert.Equal("Documents", stored.JobName);
        Assert.Equal("Primary", stored.DestinationName);
        Assert.Equal(@"D:\Backups", stored.DestinationRootPath);
        Assert.DoesNotContain(
            typeof(BackupRun).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DurableExecutionState_RoundTripsPathsAndDestinationSnapshot()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        var run = Run(job, destination);
        run.StagingPath = @"C:\ProgramData\FolderBackuper\staging\run.zip.part";
        run.DestinationPartialPath = @"D:\Backups\Documents\backup.zip.partial";

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, run);
            await context.SaveChangesAsync();
        }

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.SingleAsync();
        Assert.Equal(destination.Id, stored.DestinationId);
        Assert.Equal(run.StagingPath, stored.StagingPath);
        Assert.Equal(run.DestinationPartialPath, stored.DestinationPartialPath);
    }

    [Fact]
    public async Task ActiveRunIndex_AllowsOnlyOneNonTerminalQueuedOrRunningRunPerJob()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        var first = Run(job, destination);
        first.AdvanceTo(RunPhase.Queued, DateTimeOffset.UtcNow);
        var second = Run(job, destination);
        second.AdvanceTo(RunPhase.Queued, DateTimeOffset.UtcNow);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, job, first);
        await context.SaveChangesAsync();
        context.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ManualEnqueue_CreatesAnImmutableQueuedSnapshot()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var outcome = await database.RunPersistence.EnqueueManualAsync(job.Id);

        Assert.Equal(ManualRunEnqueueStatus.Queued, outcome.Status);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var run = await inspection.Runs.SingleAsync();
        Assert.Equal(outcome.RunId, run.Id);
        Assert.Equal(RunPhase.Queued, run.Phase);
        Assert.Equal(clock.GetUtcNow(), run.QueuedAtUtc);
        Assert.Equal(job.Name, run.JobName);
        Assert.Equal(destination.Id, run.DestinationId);
    }

    [Fact]
    public async Task ManualEnqueue_RejectsAnotherActiveRunForTheSameJob()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var first = await database.RunPersistence.EnqueueManualAsync(job.Id);
        var second = await database.RunPersistence.EnqueueManualAsync(job.Id);

        Assert.Equal(ManualRunEnqueueStatus.Queued, first.Status);
        Assert.Equal(ManualRunEnqueueStatus.Busy, second.Status);
    }

    [Fact]
    public async Task ClaimNext_ClaimsTheOldestQueuedRunAndSkipsCancelledWork()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var firstJob = DatabaseInitializationTests.Job(destination.Id, "First");
        var secondJob = DatabaseInitializationTests.Job(destination.Id, "Second");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, firstJob, secondJob);
            await context.SaveChangesAsync();
        }

        var first = await database.RunPersistence.EnqueueManualAsync(firstJob.Id);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await database.RunPersistence.EnqueueManualAsync(secondJob.Id);
        await database.RunPersistence.RequestCancellationAsync(first.RunId!.Value);

        var claimed = await database.RunPersistence.ClaimNextAsync();

        Assert.Equal(second.RunId, claimed?.Id);
        Assert.Equal(RunPhase.Scanning, claimed?.Phase);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var cancelled = await inspection.Runs.FindAsync(first.RunId!.Value);
        Assert.Equal(RunOutcome.Cancelled, cancelled!.Outcome);
    }

    [Fact]
    public async Task Cancellation_IsPersistedUntilTheFinalCommitWindowCloses()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var queued = await database.RunPersistence.EnqueueManualAsync(job.Id);
        var claimed = await database.RunPersistence.ClaimNextAsync();
        var outcome = await database.RunPersistence.RequestCancellationAsync(claimed!.Id);

        Assert.Equal(RunCancellationStatus.Requested, outcome.Status);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.FindAsync(queued.RunId!.Value);
        Assert.NotNull(stored!.CancellationRequestedAtUtc);
    }

    [Fact]
    public async Task ExecutionPersistence_RecordsCleanupPathsMetricsAndProblems()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var queued = await database.RunPersistence.EnqueueManualAsync(job.Id);
        var runId = queued.RunId!.Value;
        await database.RunPersistence.RecordStagingPathAsync(runId, @"C:\staging\run.zip.tmp");
        await database.RunPersistence.RecordDestinationPartialPathAsync(runId, @"D:\backup\run.zip.partial");
        await database.RunPersistence.RecordExecutionResultAsync(new(
            runId, RunOutcome.Failed, null, null, 2, 1, 12, 10,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
            [new(BackupProblemSeverity.Error, BackupProblemCategory.InvalidArchive, RunPhase.Finalizing,
                BackupOperation.ValidateZipArchive, BackupProblemMessage.ZipValidationFailed,
                @"D:\backup\run.zip.partial")]));

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.Include(item => item.Problems).SingleAsync();
        Assert.Equal(@"C:\staging\run.zip.tmp", stored.StagingPath);
        Assert.Equal(@"D:\backup\run.zip.partial", stored.DestinationPartialPath);
        Assert.Equal(2, stored.FileCount);
        Assert.Equal(10, stored.ArchiveBytes);
        var problem = Assert.Single(stored.Problems);
        Assert.Equal("InvalidArchive", problem.ErrorCategory);
    }

    [Fact]
    public async Task DurableCommit_CreatesPendingArtifactBeforeRenameAndMarksItRetainedAfterward()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var queued = await database.RunPersistence.EnqueueManualAsync(job.Id);
        var runId = queued.RunId!.Value;
        await database.RunPersistence.ClaimNextAsync();
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Compressing);
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Transferring);
        var coordinator = new DurableBackupCommitCoordinator(database.RunPersistence);
        var intent = new BackupCommitIntent(runId, @"D:\backup\run.zip.partial", @"D:\backup",
            "run.zip", 42, clock.GetUtcNow(), clock.GetUtcNow(), "volume:file");

        await coordinator.BeginCommitAsync(intent, CancellationToken.None);

        await using (var inspection = await database.ContextFactory.CreateDbContextAsync())
        {
            var pending = await inspection.Runs.Include(item => item.Artifact).SingleAsync();
            Assert.NotNull(pending.FinalCommitStartedAtUtc);
            Assert.Equal(ArtifactState.PendingFinalization, pending.Artifact!.State);
            Assert.Equal(intent.PartialPath, pending.DestinationPartialPath);
        }

        await coordinator.MarkCommittedAsync(runId, CancellationToken.None);

        await using var completedInspection = await database.ContextFactory.CreateDbContextAsync();
        var completed = await completedInspection.Runs.Include(item => item.Artifact).SingleAsync();
        Assert.NotNull(completed.FinalCommittedAtUtc);
        Assert.Null(completed.DestinationPartialPath);
        Assert.Equal(ArtifactState.Retained, completed.Artifact!.State);
    }

    [Fact]
    public async Task Recovery_FailsInterruptedPreCommitRunAndDeletesOnlyRecordedPaths()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var queued = await database.RunPersistence.EnqueueManualAsync(job.Id);
        var runId = queued.RunId!.Value;
        await database.RunPersistence.ClaimNextAsync();
        var identity = new InstallationIdentityService(database.ContextFactory, TimeProvider.System);
        var installationId = await identity.GetInstallationIdAsync();
        var staging = Path.Combine(database.Paths.Staging, "interrupted.zip.tmp");
        await using (var stream = File.Create(staging))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.Comment = new ArchiveOwnership(installationId, runId).Format();
        }
        await database.RunPersistence.RecordStagingPathAsync(runId, staging);
        var unknown = Path.Combine(database.Paths.Staging, "unknown.zip.tmp");
        await File.WriteAllBytesAsync(unknown, [4, 5, 6]);
        var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], new TestSecretProtector());
        var verifier = new BackupArtifactOwnershipVerifier();
        var retention = new BackupRetentionService(database.ContextFactory, database.MutationGate,
            database.RunPersistence, identity,
            effective, new OwnershipMarkerService(), verifier, TimeProvider.System);
        var recovery = new BackupRecoveryService(database.ContextFactory, database.RunPersistence, retention,
            identity, effective, new OwnershipMarkerService(), verifier, database.Paths);

        await recovery.RecoverAsync();

        Assert.False(File.Exists(staging));
        Assert.True(File.Exists(unknown));
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(RunOutcome.Failed, (await inspection.Runs.FindAsync(runId))!.Outcome);
    }

    [Fact]
    public async Task OwnershipKey_IsUniqueForActiveAndPausedJobsButReusableAfterArchive()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var first = DatabaseInitializationTests.Job(destination.Id, "First");
        var second = DatabaseInitializationTests.Job(destination.Id, "Second");
        first.DestinationOwnershipKey = "volume:répertoire";
        second.DestinationOwnershipKey = "VOLUME:RÉPERTOIRE";

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, first);
        await context.SaveChangesAsync();
        context.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        context.Entry(second).State = EntityState.Detached;
        first.Archive();
        context.Add(second);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task DurableRunAggregate_RoundTripsAllRecordTypes()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        job.ManagedArtifactCount = 2;
        job.ManagedArtifactBytes = 84;
        job.LatestArtifactBytes = 42;
        job.StorageConfirmedAtUtc = DateTimeOffset.UtcNow;
        var run = Run(job, destination);
        var now = DateTimeOffset.UtcNow;
        run.AdvanceTo(RunPhase.Queued, now);
        run.AdvanceTo(RunPhase.Scanning, now);
        run.AdvanceTo(RunPhase.Compressing, now);
        run.AdvanceTo(RunPhase.Transferring, now);
        run.AdvanceTo(RunPhase.Finalizing, now);
        run.BeginFinalCommit(now);
        run.MarkFinalCommitted(now);
        run.Complete(RunOutcome.SuccessfulWithWarnings, now);
        var artifact = new BackupArtifact
        {
            RunId = run.Id,
            DestinationName = destination.Name,
            DestinationRootPath = destination.RootPath,
            EffectivePath = @"D:\Backups\Documents",
            FinalFileName = "documents.zip",
            Size = 42,
            CreatedAtUtc = now,
            OwnershipRunId = run.Id,
            OwnershipExpectedLength = 42,
            OwnershipCreatedAtUtc = now,
            OwnershipFileSystemIdentity = "volume:file"
        };
        artifact.MarkRetained(now);
        var problem = new RunProblem
        {
            RunId = run.Id,
            Path = @"C:\Source\locked.txt",
            Phase = RunPhase.Scanning,
            Operation = BackupOperation.ReadSourceFile,
            ErrorCategory = "AccessDenied",
            NativeErrorCode = "5",
            MessageKey = UiMessage.KeyFor(BackupProblemMessage.SourceFileUnreadable),
            DiagnosticDetail = "Access was denied."
        };
        var notification = new NotificationOutboxItem
        {
            RunId = run.Id,
            RunOutcome = RunOutcome.SuccessfulWithWarnings,
            PayloadSnapshot = "{\"version\":1}",
            CreatedAtUtc = now
        };
        var settings = new ApplicationSettings
        {
            NotificationProvider = "Provider",
            NotificationProviderConfiguration = "{}",
            RecipientList = "[\"operator@example.test\"]",
            ProtectedNotificationSecret = [4, 5, 6]
        };

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, run, artifact, problem, notification, settings);
            await context.SaveChangesAsync();
        }

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs
            .Include(x => x.Artifact)
            .Include(x => x.Problems)
            .Include(x => x.Notification)
            .SingleAsync();
        Assert.Equal(RunOutcome.SuccessfulWithWarnings, stored.Outcome);
        Assert.Equal(ArtifactState.Retained, stored.Artifact?.State);
        Assert.Single(stored.Problems);
        Assert.Equal(NotificationDeliveryState.Pending, stored.Notification?.State);
        Assert.Equal([4, 5, 6], (await inspection.ApplicationSettings.SingleAsync()).ProtectedNotificationSecret);
        var storedJob = await inspection.Jobs.SingleAsync();
        Assert.Equal(2, storedJob.ManagedArtifactCount);
        Assert.Equal(84, storedJob.ManagedArtifactBytes);
        Assert.Equal(42, storedJob.LatestArtifactBytes);
        Assert.NotNull(storedJob.StorageConfirmedAtUtc);
    }

    [Fact]
    public async Task NotificationOutbox_RejectsCancelledRuns()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        var run = Run(job, destination);
        run.AdvanceTo(RunPhase.Queued, DateTimeOffset.UtcNow);
        run.RequestCancellation(DateTimeOffset.UtcNow);
        var notification = new NotificationOutboxItem
        {
            RunId = run.Id,
            RunOutcome = RunOutcome.Cancelled,
            PayloadSnapshot = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(destination, job, run, notification);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    internal static BackupRun Run(
        BackupJob job,
        Destination destination,
        RunTrigger trigger = RunTrigger.Manual) => new()
        {
            JobId = job.Id,
            DestinationId = destination.Id,
            JobName = job.Name,
            SourcePath = job.SourcePath,
            DestinationName = destination.Name,
            DestinationType = destination.Type,
            DestinationRootPath = destination.RootPath,
            DestinationUsername = destination.SmbUsername,
            DestinationVerificationFingerprint = destination.VerificationFingerprint,
            DestinationSubfolder = job.DestinationSubfolder,
            ScheduledWeekdays = job.Weekdays,
            ScheduledTime = job.ScheduledTime,
            RetentionCount = job.RetentionCount,
            RegionalCulture = "en-US",
            TimeZoneId = "UTC",
            Trigger = trigger,
            DueAtUtc = DateTimeOffset.UtcNow,
            QueuedAtUtc = DateTimeOffset.UtcNow
        };

    private static ScheduledOccurrence Occurrence(Guid jobId) => new()
    {
        JobId = jobId,
        ScheduleRevision = 1,
        ScheduledLocalDate = new DateOnly(2026, 8, 17),
        ScheduledLocalTime = new TimeOnly(1, 30),
        OccursAtUtc = new DateTimeOffset(2026, 8, 17, 1, 30, 0, TimeSpan.Zero),
        TimeZoneId = "UTC",
        UtcOffsetMinutes = 0
    };

    private static async Task<bool> CreateScheduledAsync(
        RunPersistenceService service,
        BackupRun run,
        ScheduledOccurrence occurrence)
    {
        try
        {
            await service.CreateAsync(run, occurrence);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private sealed class TestSecretProtector : FolderBackuper.Infrastructure.Security.ISecretProtector
    {
        public byte[] Protect(string plaintext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => System.Text.Encoding.UTF8.GetString(protectedData);
    }
}
