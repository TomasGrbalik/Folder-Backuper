using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;

namespace FolderBackuper.Tests;

public sealed class DashboardQueryServiceTests
{
    private static DateTimeOffset Utc(int day, int hour) => new(2026, 8, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dashboard_DerivesPerJobStatusStorageAndTallies()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");

        var healthy = DatabaseInitializationTests.Job(destination.Id, "Healthy");
        healthy.Activate();
        healthy.ManagedArtifactCount = 2;
        healthy.ManagedArtifactBytes = 150;
        healthy.LatestArtifactBytes = 90;
        healthy.StorageConfirmedAtUtc = Utc(5, 1);
        healthy.NextOccurrenceAtUtc = Utc(9, 1);

        var failing = DatabaseInitializationTests.Job(destination.Id, "Failing");
        failing.Activate();

        var oldSuccess = MonitoringTestSeed.Terminal(healthy, destination, RunOutcome.Successful, Utc(1, 1));
        var latestSuccess = MonitoringTestSeed.Terminal(healthy, destination, RunOutcome.Successful, Utc(4, 1));
        var missingRun = MonitoringTestSeed.Terminal(healthy, destination, RunOutcome.Successful, Utc(2, 1));
        var unmanagedRun = MonitoringTestSeed.Terminal(healthy, destination, RunOutcome.Successful, Utc(3, 1));
        var missingArtifact = MonitoringTestSeed.Artifact(missingRun, destination, 40, Utc(2, 1), ArtifactState.FoundMissing);
        var unmanagedArtifact = MonitoringTestSeed.Artifact(unmanagedRun, destination, 25, Utc(3, 1), ArtifactState.Unmanaged);

        var failedRun = MonitoringTestSeed.Terminal(failing, destination, RunOutcome.Failed, Utc(4, 2));
        var running = MonitoringTestSeed.Running(failing, destination, RunPhase.Transferring, Utc(6, 1));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, healthy, failing);
            context.AddRange(oldSuccess, latestSuccess, missingRun, unmanagedRun, failedRun, running);
            context.AddRange(missingArtifact, unmanagedArtifact);
            await context.SaveChangesAsync();
        }

        var runs = new RunQueryService(database.ContextFactory);
        var service = new DashboardQueryService(database.ContextFactory, runs);
        var view = await service.GetAsync();

        Assert.NotNull(view.ActiveRun);
        Assert.Equal(running.Id, view.ActiveRun!.RunId);
        Assert.Equal(1, view.FailureCount);
        Assert.Equal(0, view.WarningCount);

        var healthyCard = view.Jobs.Single(x => x.JobName == "Healthy");
        Assert.Equal(RunOutcome.Successful, healthyCard.LastOutcome);
        Assert.Equal(Utc(4, 1).AddMinutes(5), healthyCard.LastSuccessAtUtc);
        Assert.Equal(Utc(9, 1), healthyCard.NextRunAtUtc);
        Assert.Equal(150, healthyCard.ManagedArtifactBytes);
        Assert.Equal(2, healthyCard.ManagedArtifactCount);
        Assert.Equal(1, healthyCard.MissingArtifactCount);
        Assert.Equal(1, healthyCard.UnmanagedArtifactCount);
        Assert.True(healthyCard.StorageStale);

        var failingCard = view.Jobs.Single(x => x.JobName == "Failing");
        Assert.Equal(RunOutcome.Failed, failingCard.LastOutcome);
    }

    [Fact]
    public async Task Dashboard_ExcludesArchivedJobs()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");
        var archived = DatabaseInitializationTests.Job(destination.Id, "Archived");
        archived.Archive();

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, archived);
            await context.SaveChangesAsync();
        }

        var service = new DashboardQueryService(database.ContextFactory, new RunQueryService(database.ContextFactory));
        var view = await service.GetAsync();

        Assert.Empty(view.Jobs);
    }
}
