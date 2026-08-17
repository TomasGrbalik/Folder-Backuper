using FolderBackuper.Features.Backups;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

public sealed class RunPersistenceService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate)
{
    public async Task CreateAsync(
        BackupRun run,
        ScheduledOccurrence? occurrence = null,
        CancellationToken cancellationToken = default)
    {
        var requiresOccurrence = run.Trigger is RunTrigger.Scheduled or RunTrigger.CatchUp;
        if (requiresOccurrence && occurrence is null)
        {
            throw new InvalidOperationException($"A {run.Trigger} run requires a scheduled occurrence.");
        }

        if (occurrence is not null && occurrence.JobId != run.JobId)
        {
            throw new InvalidOperationException("The run and scheduled occurrence must belong to the same job.");
        }

        await mutationGate.ExecuteRunStateChangeAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.Runs.Add(run);
            if (occurrence is not null)
            {
                occurrence.RunId = run.Id;
                context.ScheduledOccurrences.Add(occurrence);
            }

            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }
}
