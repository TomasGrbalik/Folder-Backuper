using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class RunQueryServiceTests
{
    private static DateTimeOffset Utc(int day, int hour) => new(2026, 8, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListHistory_PagesAndOrdersNewestFirst()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");
        var job = DatabaseInitializationTests.Job(destination.Id, "Nightly");

        var runs = new List<BackupRun>();
        for (var i = 1; i <= 5; i++)
        {
            runs.Add(MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, Utc(i, 1)));
        }

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            context.AddRange(runs);
            await context.SaveChangesAsync();
        }

        var service = new RunQueryService(database.ContextFactory);
        var firstPage = await service.ListHistoryAsync(new RunHistoryFilter(), page: 0, pageSize: 2);

        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Rows.Count);
        Assert.True(firstPage.Rows[0].CompletedAtUtc >= firstPage.Rows[1].CompletedAtUtc);

        var lastPage = await service.ListHistoryAsync(new RunHistoryFilter(), page: 2, pageSize: 2);
        Assert.Single(lastPage.Rows);
    }

    [Fact]
    public async Task ListHistory_FiltersByJobAndStatus()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");
        var jobA = DatabaseInitializationTests.Job(destination.Id, "JobA");
        var jobB = DatabaseInitializationTests.Job(destination.Id, "JobB");

        var success = MonitoringTestSeed.Terminal(jobA, destination, RunOutcome.Successful, Utc(1, 1));
        var failed = MonitoringTestSeed.Terminal(jobA, destination, RunOutcome.Failed, Utc(2, 1));
        var otherJob = MonitoringTestSeed.Terminal(jobB, destination, RunOutcome.Failed, Utc(3, 1));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, jobA, jobB);
            context.AddRange(success, failed, otherJob);
            await context.SaveChangesAsync();
        }

        var service = new RunQueryService(database.ContextFactory);

        var jobAFailed = await service.ListHistoryAsync(new RunHistoryFilter(jobA.Id, RunStatusFilter.Failed));
        Assert.Single(jobAFailed.Rows);
        Assert.Equal(failed.Id, jobAFailed.Rows[0].RunId);

        var allFailed = await service.ListHistoryAsync(new RunHistoryFilter(Status: RunStatusFilter.Failed));
        Assert.Equal(2, allFailed.TotalCount);
    }

    [Fact]
    public async Task GetRunDetails_IncludesArchiveAndProblemCount()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");
        var job = DatabaseInitializationTests.Job(destination.Id, "Docs");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.SuccessfulWithWarnings, Utc(1, 1));
        var artifact = MonitoringTestSeed.Artifact(run, destination, 4096, Utc(1, 1));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, run, artifact);
            context.RunProblems.Add(MonitoringTestSeed.Problem(run.Id, BackupProblemSeverity.Warning, BackupProblemMessage.ReparsePointSkipped, @"C:\Source\a.txt"));
            await context.SaveChangesAsync();
        }

        var service = new RunQueryService(database.ContextFactory);
        var details = await service.GetRunDetailsAsync(run.Id);

        Assert.NotNull(details);
        Assert.Equal(RunOutcome.SuccessfulWithWarnings, details!.Outcome);
        Assert.Equal(artifact.FinalFileName, details.ArchiveFinalFileName);
        Assert.Equal(ArtifactState.Retained, details.ArtifactState);
        Assert.Equal(1, details.ProblemCount);
        Assert.NotNull(details.Duration);
    }

    [Fact]
    public async Task ListRunProblems_PagesLargeSets()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");
        var job = DatabaseInitializationTests.Job(destination.Id, "Big");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.SuccessfulWithWarnings, Utc(1, 1));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, run);
            for (var i = 0; i < 120; i++)
            {
                context.RunProblems.Add(MonitoringTestSeed.Problem(run.Id, BackupProblemSeverity.Warning, BackupProblemMessage.SourceEntryChanged, $@"C:\Source\{i}.txt"));
            }

            await context.SaveChangesAsync();
        }

        var service = new RunQueryService(database.ContextFactory);
        var page = await service.ListRunProblemsAsync(run.Id, page: 0, pageSize: 50);

        Assert.Equal(120, page.TotalCount);
        Assert.Equal(50, page.Rows.Count);
    }

    [Fact]
    public async Task GetActiveRun_ReturnsNonTerminalRunAndQueueOrder()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Dest");
        // One non-terminal run per job is enforced, so queued rows belong to distinct jobs.
        var liveJob = DatabaseInitializationTests.Job(destination.Id, "Live");
        var earlyJob = DatabaseInitializationTests.Job(destination.Id, "Early");
        var lateJob = DatabaseInitializationTests.Job(destination.Id, "Late");

        var running = MonitoringTestSeed.Running(liveJob, destination, RunPhase.Compressing, Utc(1, 2));
        var queuedEarly = MonitoringTestSeed.NewRun(earlyJob, destination, RunTrigger.Manual, Utc(1, 1));
        queuedEarly.AdvanceTo(RunPhase.Queued, Utc(1, 1));
        var queuedLate = MonitoringTestSeed.NewRun(lateJob, destination, RunTrigger.Manual, Utc(1, 3));
        queuedLate.AdvanceTo(RunPhase.Queued, Utc(1, 3));

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, liveJob, earlyJob, lateJob, running, queuedEarly, queuedLate);
            await context.SaveChangesAsync();
        }

        var service = new RunQueryService(database.ContextFactory);
        var active = await service.GetActiveRunAsync();
        var queue = await service.GetQueueAsync();

        Assert.NotNull(active);
        Assert.Equal(running.Id, active!.RunId);
        Assert.Equal(RunPhase.Compressing, active.Phase);
        Assert.Equal([queuedEarly.Id, queuedLate.Id], queue.Select(x => x.RunId).ToArray());
    }
}
