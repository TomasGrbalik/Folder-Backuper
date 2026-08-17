using System.Text;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class DestinationServiceTests
{
    [Fact]
    public async Task CreateAndList_NeverReturnPassword_AndAttemptCapacity()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var adapter = new FakeAdapter(DestinationType.Local);
        var service = Service(database, adapter);

        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));
        var listed = Assert.Single(await service.ListAsync());

        Assert.Equal(created, listed);
        Assert.Equal(1234, listed.AvailableBytes);
        Assert.DoesNotContain(typeof(DestinationSummary).GetProperties(), x => x.Name.Contains("Password", StringComparison.Ordinal) && x.PropertyType == typeof(string));
        Assert.DoesNotContain(typeof(DestinationOperationResult).GetProperties(), x =>
            x.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || x.PropertyType == typeof(byte[]));
        Assert.True(adapter.CapacityAttempts >= 2);
    }

    [Fact]
    public async Task Edit_KeepsPasswordUnlessReplaced_AndInvalidatesVerification()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var smb = new FakeAdapter(DestinationType.Smb);
        var service = Service(database, smb, new NeverLocalDetector());
        var created = await service.CreateAsync(new("NAS", DestinationType.Smb, @"\\nas\backups", @"NAS\backup", "first"));
        await service.TestAsync(created.Id);

        await service.EditAsync(created.Id, new("NAS", DestinationType.Smb, @"\\nas\backups", @"NAS\changed"));
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var destination = await context.Destinations.SingleAsync();
            Assert.Equal("first", Encoding.UTF8.GetString(destination.ProtectedPassword!));
            Assert.Equal(DestinationVerificationResult.Unverified, destination.VerificationResult);
        }

        await service.EditAsync(created.Id, new("NAS", DestinationType.Smb, @"\\nas\backups", @"NAS\changed", "second"));
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal("second", Encoding.UTF8.GetString((await inspection.Destinations.SingleAsync()).ProtectedPassword!));
    }

    [Fact]
    public async Task Test_PersistsStructuredVerificationAndLastAccess()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var adapter = new FakeAdapter(DestinationType.Local)
        {
            TestResult = new(false, DestinationAccessResult.CleanupFailed, "Cleanup failed", 5)
        };
        var service = Service(database, adapter);
        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));

        var result = await service.TestAsync(created.Id);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var destination = await context.Destinations.SingleAsync();
        Assert.Equal(DestinationAccessResult.CleanupFailed, result.Result);
        Assert.Equal(DestinationVerificationResult.Failed, destination.VerificationResult);
        Assert.Equal(DestinationAccessSource.Management, destination.LastAccessSource);
        Assert.Equal("Cleanup failed", destination.LastAccessErrorSummary);
    }

    [Fact]
    public async Task Create_RejectsLocalHostUnc()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = Service(database, new FakeAdapter(DestinationType.Smb), new AlwaysLocalDetector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new("Local share", DestinationType.Smb, @"\\localhost\share", "user", "secret")));
    }

    [Fact]
    public async Task Create_RejectsLocalDestinationOverlappingAnyConfiguredSource()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var existingDestination = DatabaseInitializationTests.Destination("Existing");
        var job = DatabaseInitializationTests.Job(existingDestination.Id, "Documents");
        job.SourcePath = database.Paths.Staging;
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(existingDestination, job);
            await context.SaveChangesAsync();
        }

        var service = Service(database, new FakeAdapter(DestinationType.Local));
        var nestedDestination = Path.Combine(database.Paths.Staging, "Backups");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(new("Unsafe", DestinationType.Local, nestedDestination)));
        Assert.Contains("overlaps configured source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_RejectsLiveReferences_AndListCanIncludeArchived()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = Service(database, new FakeAdapter(DestinationType.Local));
        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));
        var job = DatabaseInitializationTests.Job(created.Id, "Documents");
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.Jobs.Add(job);
            await setup.SaveChangesAsync();
        }

        var referenced = await service.ArchiveAsync(created.Id);
        Assert.Equal(DestinationOperationStatus.Referenced, referenced.Status);

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var stored = await context.Jobs.SingleAsync();
            stored.Archive();
            await context.SaveChangesAsync();
        }
        Assert.True((await service.ArchiveAsync(created.Id)).Succeeded);
        Assert.Empty(await service.ListAsync());
        var archived = Assert.Single(await service.ListAsync(includeArchived: true));
        Assert.Equal(DestinationLifecycle.Archived, archived.Lifecycle);
    }

    [Fact]
    public async Task Restore_AlwaysInvalidatesVerification()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = Service(database, new FakeAdapter(DestinationType.Local));
        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));
        Assert.True((await service.TestAsync(created.Id)).Succeeded);
        Assert.True((await service.ArchiveAsync(created.Id)).Succeeded);

        var restored = await service.RestoreAsync(created.Id);

        Assert.True(restored.Succeeded);
        Assert.Equal(DestinationVerificationResult.Unverified, restored.Destination!.VerificationResult);
        Assert.Null(restored.Destination.VerifiedAtUtc);
    }

    [Fact]
    public async Task RootEdit_RequiresConfirmation_PausesActiveJobsAndClaimsNewMarker()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var oldRoot = Path.Combine(database.Paths.Root, "old");
        var newRoot = Path.Combine(database.Paths.Root, "new");
        var source = Path.Combine(database.Paths.Root, "source");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        Directory.CreateDirectory(source);
        var destination = new Destination
        {
            Name = "Primary", Type = DestinationType.Local, RootPath = oldRoot,
            VerificationResult = DestinationVerificationResult.Succeeded,
            VerifiedAtUtc = DateTimeOffset.UtcNow
        };
        var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], new PlainTestProtector());
        var identity = new FolderBackuper.Features.Settings.InstallationIdentityService(database.ContextFactory, TimeProvider.System);
        var markerTests = new JobDestinationTestService(effective, new OwnershipMarkerService());
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        job.SourcePath = source;
        var oldEffective = await effective.ResolveAsync(destination, job.DestinationSubfolder, source, create: true);
        job.DestinationOwnershipKey = oldEffective.OwnershipKey!;
        job.Activate();
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.AddRange(destination, job);
            await setup.SaveChangesAsync();
        }
        var installationId = await identity.GetInstallationIdAsync();
        Assert.True((await markerTests.TestAndClaimAsync(destination, job.DestinationSubfolder,
            source, installationId, job.Id)).Succeeded);
        var service = Service(database, new LocalDestinationAdapter());

        var unconfirmed = await service.EditAsync(destination.Id,
            new("Primary", DestinationType.Local, newRoot));
        Assert.Equal(DestinationOperationStatus.ValidationFailed, unconfirmed.Status);

        var edited = await service.EditAsync(destination.Id,
            new("Primary", DestinationType.Local, newRoot, ConfirmRootPathChange: true));
        Assert.True(edited.Succeeded, edited.Message);
        Assert.Equal(1, edited.PausedJobCount);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(JobLifecycle.Paused, (await inspection.Jobs.SingleAsync()).Lifecycle);
        Assert.Equal(DestinationVerificationResult.Unverified,
            (await inspection.Destinations.SingleAsync()).VerificationResult);
        Assert.True(File.Exists(Path.Combine(newRoot, "Documents", OwnershipMarkerService.MarkerName)));
        Assert.False(File.Exists(Path.Combine(oldRoot, "Documents", OwnershipMarkerService.MarkerName)));
    }

    [Fact]
    public async Task RootEdit_MarksRetainedArtifactsUnmanagedAndResetsAggregates()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var oldRoot = Path.Combine(database.Paths.Root, "old");
        var newRoot = Path.Combine(database.Paths.Root, "new");
        var source = Path.Combine(database.Paths.Root, "source");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        Directory.CreateDirectory(source);
        var destination = new Destination { Name = "Primary", Type = DestinationType.Local, RootPath = oldRoot };
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        job.SourcePath = source;
        job.ManagedArtifactCount = 1;
        job.ManagedArtifactBytes = 42;
        job.LatestArtifactBytes = 42;
        var run = PersistenceModelTests.Run(job, destination);
        Complete(run);
        var artifact = new BackupArtifact
        {
            RunId = run.Id, DestinationName = destination.Name, DestinationRootPath = oldRoot,
            EffectivePath = Path.Combine(oldRoot, "Documents"), FinalFileName = "backup.zip",
            Size = 42, CreatedAtUtc = DateTimeOffset.UtcNow, OwnershipRunId = run.Id,
            OwnershipExpectedLength = 42
        };
        artifact.MarkRetained(DateTimeOffset.UtcNow);
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.AddRange(destination, job, run, artifact);
            await setup.SaveChangesAsync();
        }

        var result = await Service(database, new LocalDestinationAdapter()).EditAsync(destination.Id,
            new("Primary", DestinationType.Local, newRoot, ConfirmRootPathChange: true));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.UnmanagedArtifactCount);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(ArtifactState.Unmanaged, (await inspection.BackupArtifacts.SingleAsync()).State);
        var storedJob = await inspection.Jobs.SingleAsync();
        Assert.Equal(0, storedJob.ManagedArtifactCount);
        Assert.Equal(0, storedJob.ManagedArtifactBytes);
        Assert.Null(storedJob.LatestArtifactBytes);
    }

    [Fact]
    public async Task RootEdit_RejectsPhysicalAliasesAndCompensatesNewClaims()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var oldRoot = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "old-alias-test")).FullName;
        var newRoot = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "new-alias-test")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(newRoot, "target")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "source-alias-test")).FullName;
        var firstAlias = Path.Combine(newRoot, "first");
        var secondAlias = Path.Combine(newRoot, "second");
        if (!CreateDirectoryLink(firstAlias, target)) return;
        if (!CreateDirectoryLink(secondAlias, target))
        {
            Directory.Delete(firstAlias);
            return;
        }

        try
        {
            var destination = new Destination { Name = "Primary", Type = DestinationType.Local, RootPath = oldRoot };
            var first = DatabaseInitializationTests.Job(destination.Id, "First");
            first.SourcePath = source;
            first.DestinationSubfolder = "first";
            var second = DatabaseInitializationTests.Job(destination.Id, "Second");
            second.SourcePath = source;
            second.DestinationSubfolder = "second";
            await using (var setup = await database.ContextFactory.CreateDbContextAsync())
            {
                setup.AddRange(destination, first, second);
                await setup.SaveChangesAsync();
            }
            var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], new PlainTestProtector());
            var identity = new FolderBackuper.Features.Settings.InstallationIdentityService(database.ContextFactory, TimeProvider.System);
            var markerTests = new JobDestinationTestService(effective, new OwnershipMarkerService());
            var installationId = await identity.GetInstallationIdAsync();
            Assert.True((await markerTests.TestAndClaimAsync(destination, "first", source,
                installationId, first.Id)).Succeeded);
            Assert.True((await markerTests.TestAndClaimAsync(destination, "second", source,
                installationId, second.Id)).Succeeded);

            var result = await Service(database, new LocalDestinationAdapter()).EditAsync(destination.Id,
                new("Primary", DestinationType.Local, newRoot, ConfirmRootPathChange: true));

            Assert.Equal(DestinationOperationStatus.Conflict, result.Status);
            Assert.False(File.Exists(Path.Combine(target, OwnershipMarkerService.MarkerName)));
            Assert.True(File.Exists(Path.Combine(oldRoot, "first", OwnershipMarkerService.MarkerName)));
            Assert.True(File.Exists(Path.Combine(oldRoot, "second", OwnershipMarkerService.MarkerName)));
            await using var inspection = await database.ContextFactory.CreateDbContextAsync();
            Assert.Equal(oldRoot, (await inspection.Destinations.SingleAsync()).RootPath);
        }
        finally
        {
            if (Directory.Exists(firstAlias)) Directory.Delete(firstAlias);
            if (Directory.Exists(secondAlias)) Directory.Delete(secondAlias);
        }
    }

    [Fact]
    public async Task TestAndMutation_ReturnStructuredBusyWhileRunIsNonterminal()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var adapter = new FakeAdapter(DestinationType.Local);
        var service = Service(database, adapter);
        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));
        var destination = DatabaseInitializationTests.Destination("Other");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        var run = PersistenceModelTests.Run(job, destination);
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.AddRange(destination, job, run);
            await setup.SaveChangesAsync();
        }

        Assert.Equal(DestinationOperationStatus.Busy, (await service.TestAsync(created.Id)).Status);
        Assert.Equal(DestinationOperationStatus.Busy, (await service.EditAsync(created.Id,
            new("Renamed", DestinationType.Local, database.Paths.Staging))).Status);
    }

    private static DestinationService Service(TemporaryDatabase database, IDestinationAdapter adapter, ILocalHostUncDetector? detector = null) =>
        new(database.ContextFactory, new PlainTestProtector(), detector ?? new NeverLocalDetector(), [adapter], TimeProvider.System);

    private static void Complete(BackupRun run)
    {
        var now = DateTimeOffset.UtcNow;
        run.AdvanceTo(RunPhase.Queued, now);
        run.AdvanceTo(RunPhase.Scanning, now);
        run.AdvanceTo(RunPhase.Compressing, now);
        run.AdvanceTo(RunPhase.Transferring, now);
        run.AdvanceTo(RunPhase.Finalizing, now);
        run.BeginFinalCommit(now);
        run.MarkFinalCommitted(now);
        run.Complete(RunOutcome.Successful, now);
    }

    private static bool CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
    }

    private sealed class PlainTestProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => Encoding.UTF8.GetString(protectedData);
    }
    private sealed class NeverLocalDetector : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => false; }
    private sealed class AlwaysLocalDetector : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => true; }
    private sealed class FakeAdapter(DestinationType type) : IDestinationAdapter
    {
        public DestinationType Type => type;
        public int CapacityAttempts { get; private set; }
        public DestinationOperationResult TestResult { get; set; } = DestinationOperationResult.Success("Passed", 1234);
        public Task<DestinationOperationResult> TestAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(TestResult);
        public Task<long?> GetAvailableBytesAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken)
        { CapacityAttempts++; return Task.FromResult<long?>(1234); }
        public Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) => action();
    }
}
