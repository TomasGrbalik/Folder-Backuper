using System.IO.Compression;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class DurableExecutionTests
{
    [Fact]
    public async Task IndependentClaimers_ReturnAQueuedRunOnlyOnce()
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
        await database.RunPersistence.EnqueueManualAsync(job.Id);
        var first = new RunPersistenceService(database.ContextFactory,
            new ConfigurationMutationGate(database.ContextFactory), TimeProvider.System);
        var second = new RunPersistenceService(database.ContextFactory,
            new ConfigurationMutationGate(database.ContextFactory), TimeProvider.System);

        var claims = await Task.WhenAll(first.ClaimNextAsync(), second.ClaimNextAsync());

        Assert.Single(claims, claim => claim is not null);
    }

    [Fact]
    public void CancellationRequestedBeforeRegistration_IsDelivered()
    {
        var registry = new BackupCancellationRegistry();
        var runId = Guid.NewGuid();

        registry.Request(runId);
        var token = registry.Register(runId);

        Assert.True(token.IsCancellationRequested);
        registry.Remove(runId);
    }

    [Fact]
    public async Task RunNow_RejectsInvalidOwnershipBeforeInsertingRun()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var setup = await SetupOwnedJobAsync(database);
        await File.WriteAllTextAsync(Path.Combine(setup.EffectivePath, OwnershipMarkerService.MarkerName), "invalid");
        var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], new PassthroughProtector());
        var service = new BackupExecutionService(database.RunPersistence, new BackupExecutionQueue(),
            new BackupCancellationRegistry(), effective,
            new InstallationIdentityService(database.ContextFactory, TimeProvider.System),
            new OwnershipMarkerService());

        var outcome = await service.RunNowAsync(setup.Job.Id);

        Assert.Equal(ManualRunEnqueueStatus.OwnershipInvalid, outcome.Status);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.Runs.ToListAsync());
    }

    [Fact]
    public void SameLengthReplacement_IsNotDeleted()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var installationId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var path = Path.Combine(root, "archive.zip");
            CreateZip(path, new(installationId, runId));
            var original = new FileInfo(path);
            var artifact = new BackupArtifact
            {
                RunId = runId,
                DestinationName = "Local",
                DestinationRootPath = root,
                EffectivePath = root,
                FinalFileName = original.Name,
                Size = original.Length,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                OwnershipRunId = runId,
                OwnershipExpectedLength = original.Length,
                OwnershipCreatedAtUtc = new DateTimeOffset(original.CreationTimeUtc, TimeSpan.Zero),
                OwnershipFileSystemIdentity = WindowsFilesystemInterop.GetIdentity(path).ToString()
            };
            File.Delete(path);
            CreateZip(path, new(installationId, Guid.NewGuid()));
            Assert.Equal(artifact.OwnershipExpectedLength, new FileInfo(path).Length);

            var result = new BackupArtifactOwnershipVerifier().DeleteIfOwned(path, artifact, installationId);

            Assert.Equal(OwnedArchiveResult.OwnershipMismatch, result);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recovery_RefusesUnownedFinalFileAndFailsPendingFinalization()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var setup = await SetupOwnedJobAsync(database);
        var queued = await database.RunPersistence.EnqueueManualAsync(setup.Job.Id);
        var runId = queued.RunId!.Value;
        await database.RunPersistence.ClaimNextAsync();
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Compressing);
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Transferring);
        var finalName = "replacement.zip";
        var finalPath = Path.Combine(setup.EffectivePath, finalName);
        CreateZip(finalPath, new(setup.InstallationId, Guid.NewGuid()));
        var file = new FileInfo(finalPath);
        var intent = new BackupCommitIntent(runId, Path.Combine(setup.EffectivePath, "missing.partial"),
            setup.EffectivePath, finalName, file.Length, DateTimeOffset.UtcNow,
            new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
            WindowsFilesystemInterop.GetIdentity(finalPath).ToString());
        await database.RunPersistence.BeginFinalCommitAsync(intent);

        await Recovery(database).RecoverAsync();

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.Include(item => item.Artifact).SingleAsync();
        Assert.Equal(RunOutcome.Failed, stored.Outcome);
        Assert.Equal(FinalizationOperationState.Failed, stored.Artifact!.FinalizationState);
        Assert.True(File.Exists(finalPath));
    }

    [Fact]
    public async Task Recovery_PreservesOwnedArchiveRenamedBeforeCommitWasRecorded()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var setup = await SetupOwnedJobAsync(database);
        var queued = await database.RunPersistence.EnqueueManualAsync(setup.Job.Id);
        var runId = queued.RunId!.Value;
        await database.RunPersistence.ClaimNextAsync();
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Compressing);
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Transferring);
        var finalName = "owned.zip";
        var finalPath = Path.Combine(setup.EffectivePath, finalName);
        CreateZip(finalPath, new(setup.InstallationId, runId));
        var file = new FileInfo(finalPath);
        await database.RunPersistence.BeginFinalCommitAsync(new(runId,
            Path.Combine(setup.EffectivePath, "missing.partial"), setup.EffectivePath, finalName,
            file.Length, DateTimeOffset.UtcNow, new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
            WindowsFilesystemInterop.GetIdentity(finalPath).ToString()));
        await using (var verification = await database.ContextFactory.CreateDbContextAsync())
        {
            var pendingArtifact = await verification.BackupArtifacts.AsNoTracking().SingleAsync();
            Assert.Equal(new FileInfo(finalPath).Length, pendingArtifact.OwnershipExpectedLength);
            Assert.Equal(WindowsFilesystemInterop.GetIdentity(finalPath).ToString(),
                pendingArtifact.OwnershipFileSystemIdentity);
            using var handle = File.OpenHandle(finalPath);
            Assert.True((WindowsFilesystemInterop.GetCreationTimeUtc(handle) -
                pendingArtifact.OwnershipCreatedAtUtc!.Value).Duration() < TimeSpan.FromSeconds(2));
            Assert.Equal(OwnedArchiveResult.Owned,
                new BackupArtifactOwnershipVerifier().Inspect(finalPath, pendingArtifact, setup.InstallationId));
        }

        await Recovery(database).RecoverAsync();

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.Include(item => item.Artifact).SingleAsync();
        Assert.Equal(RunOutcome.Successful, stored.Outcome);
        Assert.Equal(ArtifactState.Retained, stored.Artifact!.State);
        Assert.True(File.Exists(finalPath));
    }

    [Fact]
    public async Task Recovery_ReconcilesMissingPendingRetentionAsRemoved()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var setup = await SetupOwnedJobAsync(database);
        var run = PersistenceModelTests.Run(setup.Job, setup.Destination);
        var now = DateTimeOffset.UtcNow;
        run.AdvanceTo(RunPhase.Queued, now);
        run.AdvanceTo(RunPhase.Scanning, now);
        run.AdvanceTo(RunPhase.Compressing, now);
        run.AdvanceTo(RunPhase.Transferring, now);
        run.AdvanceTo(RunPhase.Finalizing, now);
        run.BeginFinalCommit(now);
        run.MarkFinalCommitted(now);
        run.Complete(RunOutcome.Successful, now);
        var artifact = new BackupArtifact
        {
            RunId = run.Id,
            DestinationName = setup.Destination.Name,
            DestinationRootPath = setup.Destination.RootPath,
            EffectivePath = setup.EffectivePath,
            FinalFileName = "already-deleted.zip",
            Size = 42,
            CreatedAtUtc = now,
            OwnershipRunId = run.Id,
            OwnershipExpectedLength = 42
        };
        artifact.MarkRetained(now);
        artifact.BeginRetentionDeletion(run.Id, now);
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(run, artifact);
            await context.SaveChangesAsync();
        }

        await Recovery(database).RecoverAsync();

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(ArtifactState.RemovedByRetention, (await inspection.BackupArtifacts.SingleAsync()).State);
    }

    [Fact]
    public async Task Recovery_LeavesFinalizationPendingWhenDestinationIsUnavailable()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var setup = await SetupOwnedJobAsync(database);
        var queued = await database.RunPersistence.EnqueueManualAsync(setup.Job.Id);
        var runId = queued.RunId!.Value;
        await database.RunPersistence.ClaimNextAsync();
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Compressing);
        await database.RunPersistence.AdvancePhaseAsync(runId, RunPhase.Transferring);
        await database.RunPersistence.BeginFinalCommitAsync(new(runId,
            Path.Combine(setup.EffectivePath, "archive.partial"), setup.EffectivePath, "archive.zip",
            42, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        await Recovery(database, new UnavailableDestinationAdapter()).RecoverAsync();

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.Runs.Include(item => item.Artifact).Include(item => item.Problems).SingleAsync();
        Assert.Null(stored.Outcome);
        Assert.Equal(FinalizationOperationState.Pending, stored.Artifact!.FinalizationState);
        Assert.Contains(stored.Problems, problem => problem.Severity == BackupProblemSeverity.Warning);
    }

    [Fact]
    public async Task Recovery_PersistsWarningForRefusedPendingRetention()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var setup = await SetupOwnedJobAsync(database);
        var run = PersistenceModelTests.Run(setup.Job, setup.Destination);
        var now = DateTimeOffset.UtcNow;
        run.AdvanceTo(RunPhase.Queued, now);
        run.AdvanceTo(RunPhase.Scanning, now);
        run.AdvanceTo(RunPhase.Compressing, now);
        run.AdvanceTo(RunPhase.Transferring, now);
        run.AdvanceTo(RunPhase.Finalizing, now);
        run.BeginFinalCommit(now);
        run.MarkFinalCommitted(now);
        run.Complete(RunOutcome.Successful, now);
        var path = Path.Combine(setup.EffectivePath, "replacement.zip");
        CreateZip(path, new(setup.InstallationId, Guid.NewGuid()));
        var file = new FileInfo(path);
        var artifact = new BackupArtifact
        {
            RunId = run.Id,
            DestinationName = setup.Destination.Name,
            DestinationRootPath = setup.Destination.RootPath,
            EffectivePath = setup.EffectivePath,
            FinalFileName = file.Name,
            Size = file.Length,
            CreatedAtUtc = now,
            OwnershipRunId = run.Id,
            OwnershipExpectedLength = file.Length,
            OwnershipCreatedAtUtc = new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
            OwnershipFileSystemIdentity = WindowsFilesystemInterop.GetIdentity(path).ToString()
        };
        artifact.MarkRetained(now);
        artifact.BeginRetentionDeletion(run.Id, now);
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(run, artifact);
            await context.SaveChangesAsync();
        }

        await Recovery(database).RecoverAsync();

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        var stored = await inspection.BackupArtifacts.SingleAsync();
        Assert.Equal(RetentionOperationState.OwnershipRefused, stored.RetentionState);
        Assert.Contains(await inspection.RunProblems.ToListAsync(),
            problem => problem.RunId == run.Id && problem.Severity == BackupProblemSeverity.Warning);
        Assert.True(File.Exists(path));
    }

    private static BackupRecoveryService Recovery(
        TemporaryDatabase database,
        IDestinationAdapter? adapter = null)
    {
        var identity = new InstallationIdentityService(database.ContextFactory, TimeProvider.System);
        var effective = new EffectiveDestinationService([adapter ?? new LocalDestinationAdapter()], new PassthroughProtector());
        var verifier = new BackupArtifactOwnershipVerifier();
        var markers = new OwnershipMarkerService();
        var retention = new BackupRetentionService(database.ContextFactory, database.MutationGate,
            database.RunPersistence, identity, effective, markers, verifier, TimeProvider.System);
        return new(database.ContextFactory, database.RunPersistence, retention, identity,
            effective, markers, verifier, database.Paths);
    }

    private static async Task<(Destination Destination, BackupJob Job, string EffectivePath, Guid InstallationId)>
        SetupOwnedJobAsync(TemporaryDatabase database)
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
            ScheduledTime = new(2, 0),
            RetentionCount = 1,
            DestinationOwnershipKey = "test"
        };
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }
        var identity = new InstallationIdentityService(database.ContextFactory, TimeProvider.System);
        var installationId = await identity.GetInstallationIdAsync();
        Assert.True((await new OwnershipMarkerService().ClaimAsync(
            effective, installationId, job.Id, CancellationToken.None)).Succeeded);
        return (destination, job, effective, installationId);
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
        public Task<DestinationOperationResult> TestAsync(
            DestinationAccessConfiguration configuration, CancellationToken cancellationToken) =>
            throw new IOException("Destination unavailable.");
        public Task<long?> GetAvailableBytesAsync(
            DestinationAccessConfiguration configuration, CancellationToken cancellationToken) =>
            throw new IOException("Destination unavailable.");
        public Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) =>
            throw new IOException("Destination unavailable.");
    }
}
