using System.IO.Compression;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class ArtifactInventoryReconcileTests
{
    [Fact]
    public async Task Reconcile_KeepsPresentOwnedArchivesAndRefreshesAggregates()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var (retention, destination, job, effective, installationId) = await SetupAsync(database);
        var seeded = await SeedRetainedArchiveAsync(database, destination, job, effective, installationId);

        var result = await retention.ReconcileInventoryAsync(job.Id);

        Assert.True(result.DestinationReachable);
        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.MarkedMissing);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(ArtifactState.Retained, (await context.BackupArtifacts.SingleAsync()).State);
        var refreshed = await context.Jobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(1, refreshed.ManagedArtifactCount);
        Assert.Equal(seeded.Size, refreshed.ManagedArtifactBytes);
        Assert.NotNull(refreshed.StorageConfirmedAtUtc);
    }

    [Fact]
    public async Task Reconcile_MarksDeletedArchiveMissingAndDropsItFromTotals()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var (retention, destination, job, effective, installationId) = await SetupAsync(database);
        var seeded = await SeedRetainedArchiveAsync(database, destination, job, effective, installationId);

        File.Delete(Path.Combine(seeded.Artifact.EffectivePath, seeded.Artifact.FinalFileName));

        var result = await retention.ReconcileInventoryAsync(job.Id);

        Assert.True(result.DestinationReachable);
        Assert.Equal(1, result.MarkedMissing);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(ArtifactState.FoundMissing, (await context.BackupArtifacts.SingleAsync()).State);
        var refreshed = await context.Jobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(0, refreshed.ManagedArtifactCount);
        Assert.Equal(0, refreshed.ManagedArtifactBytes);
    }

    [Fact]
    public async Task Reconcile_PreservesLastConfirmedTotalsWhenDestinationUnavailable()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var (_, destination, job, effective, installationId) =
            await SetupAsync(database, new UnavailableDestinationAdapter());
        _ = await SeedRetainedArchiveAsync(database, destination, job, effective, installationId);

        var confirmedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Jobs.SingleAsync(x => x.Id == job.Id);
            stored.ManagedArtifactCount = 1;
            stored.ManagedArtifactBytes = 999;
            stored.StorageConfirmedAtUtc = confirmedAt;
            await context.SaveChangesAsync();
        }

        var retention = CreateRetention(database, new UnavailableDestinationAdapter());
        var result = await retention.ReconcileInventoryAsync(job.Id);

        Assert.False(result.DestinationReachable);

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(ArtifactState.Retained, (await inspection.BackupArtifacts.SingleAsync()).State);
        var unchanged = await inspection.Jobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(1, unchanged.ManagedArtifactCount);
        Assert.Equal(999, unchanged.ManagedArtifactBytes);
        Assert.Equal(confirmedAt, unchanged.StorageConfirmedAtUtc);
    }

    private static BackupRetentionService CreateRetention(TemporaryDatabase database, IDestinationAdapter adapter)
    {
        var identity = new InstallationIdentityService(database.ContextFactory, TimeProvider.System);
        var effective = new EffectiveDestinationService([adapter], new PassthroughProtector());
        return new BackupRetentionService(database.ContextFactory, database.MutationGate, database.RunPersistence,
            identity, effective, new OwnershipMarkerService(), new BackupArtifactOwnershipVerifier(), TimeProvider.System);
    }

    private static async Task<(BackupRetentionService Retention, Destination Destination, BackupJob Job, string Effective, Guid InstallationId)>
        SetupAsync(TemporaryDatabase database, IDestinationAdapter? adapter = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "destination")).FullName;
        var effective = Directory.CreateDirectory(Path.Combine(root, "job")).FullName;
        var destination = new Destination
        {
            Name = "Local",
            Type = DestinationType.Local,
            RootPath = root,
            VerificationResult = DestinationVerificationResult.Succeeded,
            VerificationFingerprint = "verified"
        };
        var job = new BackupJob
        {
            Name = "Documents",
            SourcePath = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "source")).FullName,
            DestinationId = destination.Id,
            DestinationSubfolder = "job",
            Weekdays = ScheduledWeekdays.Monday,
            ScheduledTime = new TimeOnly(2, 0),
            RetentionCount = 3,
            DestinationOwnershipKey = "test"
        };
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        var installationId = await new InstallationIdentityService(database.ContextFactory, TimeProvider.System)
            .GetInstallationIdAsync();
        return (CreateRetention(database, adapter ?? new LocalDestinationAdapter()), destination, job, effective, installationId);
    }

    private static async Task<(BackupRun Run, BackupArtifact Artifact, long Size)> SeedRetainedArchiveAsync(
        TemporaryDatabase database, Destination destination, BackupJob job, string effective, Guid installationId)
    {
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(effective, "backup.zip");
        CreateZip(path, new ArchiveOwnership(installationId, run.Id));
        var size = new FileInfo(path).Length;

        var artifact = new BackupArtifact
        {
            RunId = run.Id,
            DestinationName = destination.Name,
            DestinationRootPath = destination.RootPath,
            EffectivePath = effective,
            FinalFileName = "backup.zip",
            Size = size,
            CreatedAtUtc = run.CompletedAtUtc ?? DateTimeOffset.UtcNow,
            OwnershipRunId = run.Id,
            OwnershipExpectedLength = size
        };
        artifact.MarkRetained(DateTimeOffset.UtcNow);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        context.AddRange(run, artifact);
        await context.SaveChangesAsync();
        return (run, artifact, size);
    }

    private static void CreateZip(string path, ArchiveOwnership ownership)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        archive.Comment = ownership.Format();
        archive.CreateEntry("data.txt");
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => System.Text.Encoding.UTF8.GetString(protectedData);
    }

    private sealed class UnavailableDestinationAdapter : IDestinationAdapter
    {
        public DestinationType Type => DestinationType.Local;
        public Task<DestinationOperationResult> TestAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken) =>
            throw new IOException("Destination unavailable.");
        public Task<long?> GetAvailableBytesAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken) =>
            throw new IOException("Destination unavailable.");
        public Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) =>
            throw new IOException("Destination unavailable.");
    }
}
