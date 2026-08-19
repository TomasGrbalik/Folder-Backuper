using System.Collections.Concurrent;

namespace FolderBackuper.Features.Monitoring;

/// <summary>
/// Broadcasts that durable run or job state changed so that an open monitoring page can reload itself
/// instead of waiting for a manual refresh. The signal carries no identity, payload, or ordering: a
/// subscriber always re-reads the whole view it renders from SQLite, exactly as the refresh button does.
/// </summary>
/// <remarks>
/// Live per-file progress stays on <see cref="Backups.BackupProgressRegistry"/>, which is keyed by run
/// and therefore cannot announce that a different run became the active one. This signal covers the
/// coarse transitions instead: queued, claimed, phase advanced, and terminal.
/// </remarks>
public sealed class RunActivitySignal(ILogger<RunActivitySignal>? logger = null)
{
    private readonly ConcurrentDictionary<object, Func<Task>> handlers = new();

    public IDisposable Subscribe(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var token = new object();
        handlers[token] = handler;
        return new Subscription(this, token);
    }

    /// <summary>
    /// Raised by the writer after its change committed. Handlers are started and not awaited so that no
    /// browser circuit can slow down or fail the backup that raised the signal.
    /// </summary>
    public void Signal()
    {
        foreach (var handler in handlers.Values)
        {
            _ = InvokeAsync(handler);
        }
    }

    private async Task InvokeAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception exception)
        {
            // A subscriber is a page that may have been torn down between the signal and this callback,
            // and a reload may fail on its own. Neither concerns the writer that raised the signal.
            logger?.LogDebug(exception, "A monitoring subscriber failed to handle a run activity signal");
        }
    }

    private sealed class Subscription(RunActivitySignal owner, object token) : IDisposable
    {
        public void Dispose() => owner.handlers.TryRemove(token, out _);
    }
}
