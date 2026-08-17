using FolderBackuper.Features.Backups;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class ConfigurationMutationGateTests
{
    [Fact]
    public async Task Execute_BlocksForPlannedNonterminalRun_UsingPersistedState()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        var run = PersistenceModelTests.Run(job, destination, RunTrigger.Manual);
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job, run);
            await context.SaveChangesAsync();
        }
        var gate = new ConfigurationMutationGate(database.ContextFactory);
        var called = false;

        var result = await gate.ExecuteAsync(_ =>
        {
            called = true;
            return Task.FromResult(42);
        });

        Assert.Equal(ConfigurationMutationStatus.Busy, result.Status);
        Assert.False(called);
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Null((await inspection.Runs.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task RunStateChange_WaitsUntilConfigurationMutationCompletes()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var gate = new ConfigurationMutationGate(database.ContextFactory);
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runStateChanged = false;

        var mutation = gate.ExecuteAsync(async _ =>
        {
            mutationEntered.SetResult();
            await releaseMutation.Task;
            return 1;
        });
        await mutationEntered.Task;

        var runStateChange = gate.ExecuteRunStateChangeAsync(_ =>
        {
            runStateChanged = true;
            return Task.FromResult(2);
        });
        await Task.Delay(25);
        Assert.False(runStateChanged);

        releaseMutation.SetResult();
        Assert.True((await mutation).Succeeded);
        Assert.Equal(2, await runStateChange);
        Assert.True(runStateChanged);
    }

    [Fact]
    public async Task RunCreation_WaitsForMutation_ThenBlocksLaterMutation()
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

        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = database.MutationGate.ExecuteAsync(async _ =>
        {
            mutationEntered.SetResult();
            await releaseMutation.Task;
            return true;
        });
        await mutationEntered.Task;

        var creation = database.RunPersistence.CreateAsync(
            PersistenceModelTests.Run(job, destination, RunTrigger.Manual));
        Assert.False(creation.IsCompleted);

        releaseMutation.SetResult();
        Assert.True((await mutation).Succeeded);
        await creation;

        var laterMutationCalled = false;
        var laterMutation = await database.MutationGate.ExecuteAsync(_ =>
        {
            laterMutationCalled = true;
            return Task.FromResult(true);
        });
        Assert.Equal(ConfigurationMutationStatus.Busy, laterMutation.Status);
        Assert.False(laterMutationCalled);
    }
}
