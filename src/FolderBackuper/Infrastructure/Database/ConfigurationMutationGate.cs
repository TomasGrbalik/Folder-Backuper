using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

public enum ConfigurationMutationStatus
{
    Executed,
    Busy
}

public sealed record ConfigurationMutationOutcome<T>(
    ConfigurationMutationStatus Status,
    T? Value,
    string Message)
{
    public bool Succeeded => Status == ConfigurationMutationStatus.Executed;
}

public sealed class ConfigurationMutationGate(IDbContextFactory<FolderBackuperDbContext> contextFactory)
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<ConfigurationMutationOutcome<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            // Planned and queued rows are also reserved work and may be picked up concurrently.
            if (await context.Runs.AsNoTracking().AnyAsync(x => x.Outcome == null, cancellationToken))
            {
                return new(ConfigurationMutationStatus.Busy, default,
                    "Configuration cannot be changed while backup work is pending or running.");
            }

            return new(ConfigurationMutationStatus.Executed, await operation(cancellationToken),
                "The configuration operation completed.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    // Queue insertion and claiming introduced in later milestones must use this path so the
    // pending-work check and a configuration mutation cannot race inside the single process.
    public async Task<T> ExecuteRunStateChangeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
