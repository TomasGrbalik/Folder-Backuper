using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
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
            Operation = "Read",
            ErrorCategory = "AccessDenied",
            NativeErrorCode = "5",
            UserMessage = "The file could not be read.",
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
            JobName = job.Name,
            SourcePath = job.SourcePath,
            DestinationName = destination.Name,
            DestinationType = destination.Type,
            DestinationRootPath = destination.RootPath,
            DestinationSubfolder = job.DestinationSubfolder,
            ScheduledWeekdays = job.Weekdays,
            ScheduledTime = job.ScheduledTime,
            RetentionCount = job.RetentionCount,
            RegionalCulture = "en-US",
            TimeZoneId = "UTC",
            Trigger = trigger,
            QueuedAtUtc = DateTimeOffset.UtcNow
        };

    private static ScheduledOccurrence Occurrence(Guid jobId) => new()
    {
        JobId = jobId,
        ScheduleRevision = 1,
        ScheduledLocalDate = new DateOnly(2026, 8, 17),
        ScheduledLocalTime = new TimeOnly(1, 30),
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
}
