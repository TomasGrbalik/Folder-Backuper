using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class JobServiceTests
{
    [Fact]
    public async Task ActiveJob_LifecyclePreservesRevisionAndUsesOwnershipMarker()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var source = Path.Combine(database.Paths.Root, "source");
        var destinationRoot = Path.Combine(database.Paths.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationRoot);
        var destination = Destination(destinationRoot);
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.Destinations.Add(destination);
            await setup.SaveChangesAsync();
        }
        var service = Service(database, clock);

        var created = await service.CreateAsync(Command(source, destination.Id, activate: true));

        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(JobLifecycle.Active, created.Job!.Lifecycle);
        Assert.Equal(1, created.Job.ScheduleRevision);
        Assert.Equal(clock.GetUtcNow(), created.Job.ScheduleEffectiveFromUtc);
        var folder = Path.Combine(destinationRoot, "Documents");
        Assert.True(File.Exists(Path.Combine(folder, OwnershipMarkerService.MarkerName)));
        await AssertNoRunsAsync(database);

        clock.Advance(TimeSpan.FromMinutes(5));
        var paused = await service.PauseAsync(created.Job.Id);
        Assert.True(paused.Succeeded);
        Assert.Equal(1, paused.Job!.ScheduleRevision);
        Assert.Equal(created.Job.ScheduleEffectiveFromUtc, paused.Job.ScheduleEffectiveFromUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        var active = await service.ReactivateAsync(created.Job.Id);
        Assert.True(active.Succeeded, active.Message);
        Assert.Equal(1, active.Job!.ScheduleRevision);
        Assert.Equal(clock.GetUtcNow(), active.Job.ScheduleEffectiveFromUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        var edited = await service.EditAsync(created.Job.Id,
            Command(source, destination.Id, activate: true) with { ScheduledTime = new TimeOnly(4, 15) });
        Assert.True(edited.Succeeded, edited.Message);
        Assert.Equal(2, edited.Job!.ScheduleRevision);
        Assert.Equal(clock.GetUtcNow(), edited.Job.ScheduleEffectiveFromUtc);

        var archived = await service.ArchiveAsync(created.Job.Id);
        Assert.True(archived.Succeeded, archived.Message);
        Assert.False(File.Exists(Path.Combine(folder, OwnershipMarkerService.MarkerName)));
        Assert.Equal(2, archived.Job!.ScheduleRevision);

        clock.Advance(TimeSpan.FromMinutes(5));
        var restored = await service.RestoreAsync(created.Job.Id, restoreActive: true);
        Assert.True(restored.Succeeded, restored.Message);
        Assert.Equal(JobLifecycle.Active, restored.Job!.Lifecycle);
        Assert.Equal(2, restored.Job.ScheduleRevision);
        Assert.Equal(clock.GetUtcNow(), restored.Job.ScheduleEffectiveFromUtc);
        Assert.True(File.Exists(Path.Combine(folder, OwnershipMarkerService.MarkerName)));
        await AssertNoRunsAsync(database);
    }

    [Fact]
    public async Task ActivationRejectsUnverifiedDestination_AndDoesNotInsertJobOrRun()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var source = Path.Combine(database.Paths.Root, "source");
        var destinationRoot = Path.Combine(database.Paths.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationRoot);
        var destination = Destination(destinationRoot);
        destination.VerificationResult = DestinationVerificationResult.Unverified;
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.Destinations.Add(destination);
            await setup.SaveChangesAsync();
        }

        var result = await Service(database, TimeProvider.System)
            .CreateAsync(Command(source, destination.Id, activate: true));

        Assert.Equal(JobOperationStatus.DestinationVerificationFailed, result.Status);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.Jobs.ToListAsync());
        Assert.Empty(await context.Runs.ToListAsync());
    }

    [Fact]
    public async Task PausedCreate_ClaimsFolderEvenWhenManagementVerificationIsUnverified()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var source = Path.Combine(database.Paths.Root, "source");
        var destinationRoot = Path.Combine(database.Paths.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationRoot);
        var destination = Destination(destinationRoot);
        destination.VerificationResult = DestinationVerificationResult.Unverified;
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.Destinations.Add(destination);
            await setup.SaveChangesAsync();
        }

        var result = await Service(database, TimeProvider.System)
            .CreateAsync(Command(source, destination.Id));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(JobLifecycle.Paused, result.Job!.Lifecycle);
        Assert.True(File.Exists(Path.Combine(destinationRoot, "Documents", OwnershipMarkerService.MarkerName)));
    }

    [Fact]
    public async Task TwoPausedJobsCannotReserveSameEffectiveFolder()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var source = Path.Combine(database.Paths.Root, "source");
        var destinationRoot = Path.Combine(database.Paths.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationRoot);
        Directory.CreateDirectory(Path.Combine(destinationRoot, "Documents"));
        var destination = Destination(destinationRoot);
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.Destinations.Add(destination);
            await setup.SaveChangesAsync();
        }
        var service = Service(database, TimeProvider.System);
        Assert.True((await service.CreateAsync(Command(source, destination.Id))).Succeeded);

        var collision = await service.CreateAsync(Command(source, destination.Id) with { Name = "Other" });

        Assert.Equal(JobOperationStatus.Conflict, collision.Status);
    }

    [Fact]
    public async Task PausedNeverTestedJob_CanBeArchivedWithoutMarker()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var source = Path.Combine(database.Paths.Root, "source");
        var destinationRoot = Path.Combine(database.Paths.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationRoot);
        var destination = Destination(destinationRoot);
        await using (var setup = await database.ContextFactory.CreateDbContextAsync())
        {
            setup.Destinations.Add(destination);
            await setup.SaveChangesAsync();
        }
        var service = Service(database, TimeProvider.System);
        var created = await service.CreateAsync(Command(source, destination.Id));
        Assert.True(created.Succeeded);

        var archived = await service.ArchiveAsync(created.Job!.Id);

        Assert.True(archived.Succeeded, archived.Message);
        Assert.Equal(JobLifecycle.Archived, archived.Job!.Lifecycle);
    }

    private static JobService Service(TemporaryDatabase database, TimeProvider clock)
    {
        var effective = new EffectiveDestinationService(
            [new LocalDestinationAdapter()], new PassthroughProtector());
        var identity = new InstallationIdentityService(database.ContextFactory, clock);
        var tests = new JobDestinationTestService(effective, new OwnershipMarkerService());
        return new(database.ContextFactory, new ConfigurationMutationGate(database.ContextFactory),
            effective, tests, identity, clock);
    }

    private static Destination Destination(string root) => new()
    {
        Name = "Primary",
        Type = DestinationType.Local,
        RootPath = root,
        VerificationResult = DestinationVerificationResult.Succeeded,
        VerifiedAtUtc = DateTimeOffset.UtcNow
    };

    private static SaveJobCommand Command(string source, Guid destinationId, bool activate = false) => new(
        "Documents", source, destinationId, "Documents", ScheduledWeekdays.Monday,
        new TimeOnly(3, 0), 3, activate);

    private static async Task AssertNoRunsAsync(TemporaryDatabase database)
    {
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Empty(await context.Runs.ToListAsync());
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => System.Text.Encoding.UTF8.GetString(protectedData);
    }
}
