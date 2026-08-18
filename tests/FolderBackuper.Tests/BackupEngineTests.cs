using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class BackupEngineTests : IAsyncLifetime
{
    private readonly TemporaryDatabase database = new();
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly LocalDestinationAdapter adapter = new();
    private string source = null!;
    private string destinationRoot = null!;
    private string effectivePath = null!;
    private Destination destination = null!;
    private BackupJob job = null!;
    private Guid installationId;

    public async Task InitializeAsync()
    {
        await database.Initializer.InitializeAsync();
        source = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "source")).FullName;
        destinationRoot = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "destination")).FullName;
        effectivePath = Directory.CreateDirectory(Path.Combine(destinationRoot, "job")).FullName;
        destination = new()
        {
            Name = "Local",
            Type = DestinationType.Local,
            RootPath = destinationRoot,
            VerificationResult = DestinationVerificationResult.Succeeded,
            VerificationFingerprint = "verified"
        };
        job = new()
        {
            Name = "Accounting",
            SourcePath = source,
            DestinationId = destination.Id,
            DestinationSubfolder = "job",
            Weekdays = ScheduledWeekdays.Monday,
            ScheduledTime = new TimeOnly(23, 0),
            DestinationOwnershipKey = "test"
        };
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.Destinations.Add(destination);
            context.Jobs.Add(job);
            await context.SaveChangesAsync();
        }

        installationId = await new InstallationIdentityService(database.ContextFactory, timeProvider)
            .GetInstallationIdAsync();
        var marker = await new OwnershipMarkerService().ClaimAsync(
            effectivePath, installationId, job.Id, CancellationToken.None);
        Assert.True(marker.Succeeded);
    }

    [Fact]
    public async Task Execute_CreatesValidatedArchiveCleansStagingAndRecordsAccess()
    {
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), new byte[15 * 1024 * 1024]);
        var progress = new BackupProgressRegistry(minimumInterval: TimeSpan.Zero);
        var phases = new List<RunPhase>();
        var runId = Guid.NewGuid();
        using var subscription = progress.Subscribe(runId, snapshot => phases.Add(snapshot.Phase));

        var result = await Engine(progress).ExecuteAsync(new(
            runId, job.Id, new DateTimeOffset(2026, 8, 17, 23, 0, 0, TimeSpan.Zero)));

        Assert.Equal(RunOutcome.Successful, result.Outcome);
        Assert.True(File.Exists(result.FinalPath));
        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.DirectoryCount);
        Assert.Equal(15 * 1024 * 1024, result.SourceBytes);
        Assert.Empty(Directory.GetFiles(database.Paths.Staging));
        Assert.Contains(RunPhase.Scanning, phases);
        Assert.Contains(RunPhase.Compressing, phases);
        Assert.Contains(RunPhase.Transferring, phases);
        Assert.Contains(RunPhase.Finalizing, phases);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var stored = await context.Destinations.AsNoTracking().SingleAsync(item => item.Id == destination.Id);
        Assert.Equal(DestinationAccessResult.Succeeded, stored.LastAccessResult);
        Assert.Equal(DestinationAccessSource.Backup, stored.LastAccessSource);
        Assert.Equal(DestinationVerificationResult.Succeeded, stored.VerificationResult);
    }

    [Fact]
    public async Task Execute_SourceChangeDuringCompressionFailsWithoutPublication()
    {
        var file = Path.Combine(source, "data.bin");
        await File.WriteAllBytesAsync(file, new byte[1024 * 1024]);
        var progress = new BackupProgressRegistry(minimumInterval: TimeSpan.Zero);
        var changed = false;
        var runId = Guid.NewGuid();
        using var subscription = progress.Subscribe(runId, snapshot =>
        {
            if (!changed && snapshot.Phase == RunPhase.Compressing && snapshot.FilesProcessed == 1)
            {
                File.AppendAllText(file, "changed");
                changed = true;
            }
        });

        var result = await Engine(progress).ExecuteAsync(new(runId, job.Id, DateTimeOffset.UtcNow));

        Assert.True(changed);
        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains(result.Problems, problem => problem.Category == BackupProblemCategory.SourceChanged);
        Assert.Empty(Directory.GetFiles(effectivePath, "*.zip"));
        Assert.Empty(Directory.GetFiles(database.Paths.Staging));
    }

    [Fact]
    public async Task Execute_CancellationDuringCompressionPublishesNothing()
    {
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), new byte[2 * 1024 * 1024]);
        var progress = new BackupProgressRegistry(minimumInterval: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var runId = Guid.NewGuid();
        using var subscription = progress.Subscribe(runId, snapshot =>
        {
            if (snapshot.Phase == RunPhase.Compressing && snapshot.BytesProcessed > 0) cancellation.Cancel();
        });

        var result = await Engine(progress).ExecuteAsync(
            new(runId, job.Id, DateTimeOffset.UtcNow), cancellation.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.Contains(result.Problems, problem => problem.Category == BackupProblemCategory.Cancelled);
        Assert.Empty(Directory.GetFiles(effectivePath, "*.zip"));
        Assert.Empty(Directory.GetFiles(database.Paths.Staging));
    }

    [Fact]
    public async Task Execute_SkippedReparsePointCompletesWithWarning()
    {
        var target = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "outside"));
        await File.WriteAllTextAsync(Path.Combine(target.FullName, "outside.txt"), "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source, "link"), target.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var result = await Engine(new BackupProgressRegistry(minimumInterval: TimeSpan.Zero))
            .ExecuteAsync(new(Guid.NewGuid(), job.Id, DateTimeOffset.UtcNow));

        Assert.Equal(RunOutcome.SuccessfulWithWarnings, result.Outcome);
        Assert.Contains(result.Problems,
            problem => problem.Category == BackupProblemCategory.SkippedReparsePoint &&
                       problem.Severity == BackupProblemSeverity.Warning);
        Assert.True(File.Exists(result.FinalPath));
    }

    [Fact]
    public async Task FaultAfterStagingIntent_PersistsPathBeforeCreatingFile()
    {
        var queued = await database.RunPersistence.EnqueueManualAsync(job.Id);
        var run = await database.RunPersistence.ClaimNextAsync();
        Assert.Equal(queued.RunId, run!.Id);
        await using (var mutation = await database.ContextFactory.CreateDbContextAsync())
        {
            var currentJob = await mutation.Jobs.SingleAsync();
            var currentDestination = await mutation.Destinations.SingleAsync();
            currentJob.SourcePath = Path.Combine(database.Paths.Root, "mutated-source");
            currentDestination.RootPath = Path.Combine(database.Paths.Root, "mutated-destination");
            await mutation.SaveChangesAsync();
        }
        var injector = new ThrowingFaultInjector(BackupFaultPoint.AfterStagingIntentPersisted);

        await Assert.ThrowsAsync<InjectedBackupFaultException>(() =>
            DurableEngine(new BackupProgressRegistry(minimumInterval: TimeSpan.Zero), injector).ExecuteAsync(
                new(run.Id, job.Id, DateTimeOffset.UtcNow)));

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var stored = await context.Runs.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.StagingPath);
        Assert.False(File.Exists(stored.StagingPath));
        Assert.Null(stored.Outcome);
    }

    [Fact]
    public async Task ServiceInterruption_IsNotReportedAsUserCancellation()
    {
        using var interruption = new CancellationTokenSource();
        interruption.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine(new BackupProgressRegistry(minimumInterval: TimeSpan.Zero)).ExecuteAsync(
                new(Guid.NewGuid(), job.Id, DateTimeOffset.UtcNow),
                userCancellationToken: CancellationToken.None,
                interruptionToken: interruption.Token));
    }

    private BackupEngine Engine(BackupProgressRegistry progress)
    {
        var effectiveDestinations = new EffectiveDestinationService([adapter], new NoOpSecretProtector());
        var zip = new ZipArchiveService();
        return new(
            database.ContextFactory,
            new InstallationIdentityService(database.ContextFactory, timeProvider),
            new BackupPreflightService(database.Paths, effectiveDestinations,
                new OwnershipMarkerService(), new NeverLocalDetector()),
            new SourceManifestBuilder(),
            zip,
            new DestinationArchiveService(zip, new DirectBackupCommitCoordinator()),
            effectiveDestinations,
            new DestinationAccessRecorder(database.ContextFactory, timeProvider),
            progress,
            database.Paths,
            timeProvider);
    }

    private BackupEngine DurableEngine(BackupProgressRegistry progress, IBackupFaultInjector injector)
    {
        var effectiveDestinations = new EffectiveDestinationService([adapter], new NoOpSecretProtector());
        var zip = new ZipArchiveService();
        return new(
            database.ContextFactory,
            new InstallationIdentityService(database.ContextFactory, timeProvider),
            new BackupPreflightService(database.Paths, effectiveDestinations,
                new OwnershipMarkerService(), new NeverLocalDetector()),
            new SourceManifestBuilder(),
            zip,
            new DestinationArchiveService(zip, new DurableBackupCommitCoordinator(database.RunPersistence), injector),
            effectiveDestinations,
            new DestinationAccessRecorder(database.ContextFactory, timeProvider),
            progress,
            database.Paths,
            timeProvider,
            database.RunPersistence,
            injector);
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    private sealed class NoOpSecretProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => throw new NotSupportedException();
        public string Unprotect(byte[] protectedData) => throw new NotSupportedException();
    }

    private sealed class NeverLocalDetector : ILocalHostUncDetector
    {
        public bool IsHostedLocally(string uncPath) => false;
    }

    private sealed class ThrowingFaultInjector(BackupFaultPoint target) : IBackupFaultInjector
    {
        public ValueTask HitAsync(BackupFaultPoint point, Guid runId, CancellationToken cancellationToken)
        {
            if (point == target) throw new InjectedBackupFaultException(point);
            return ValueTask.CompletedTask;
        }
    }
}
