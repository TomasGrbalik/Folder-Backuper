using System.Collections.Concurrent;

namespace FolderBackuper.Features.Updates;

/// <summary>
/// Holds the current <see cref="UpdateStatus"/> and tells open pages when it changed.
/// </summary>
/// <remarks>
/// This type has no dependencies on purpose. The app-bar notice and the settings page need the
/// answer, not the machinery that produced it, so both can be rendered in a test by constructing
/// this store and publishing a status into it, with no HTTP stack involved.
/// <para>
/// The fan-out mirrors <see cref="Monitoring.RunActivitySignal"/> deliberately, because it solves
/// the same problem: the Check now button lives on the settings page while the notice lives in the
/// app bar, so without a signal a completed check would leave the notice stale until the next
/// navigation. The monitoring signal is left alone rather than generalised, since the two carry
/// different meanings and share only their shape.
/// </para>
/// <para>
/// Nothing here is persisted. The time of the last check is worth nothing after a restart, and
/// keeping it would only widen the database for no behaviour.
/// </para>
/// </remarks>
public sealed class UpdateStatusStore(ILogger<UpdateStatusStore>? logger = null)
{
    private readonly ConcurrentDictionary<object, Func<Task>> handlers = new();
    private UpdateStatus current = UpdateStatus.ForInstalledBuild();

    public UpdateStatus Current => Volatile.Read(ref current);

    /// <summary>Replaces the snapshot and tells every open page to re-read it.</summary>
    public void Publish(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Volatile.Write(ref current, status);

        foreach (var handler in handlers.Values)
        {
            _ = InvokeAsync(handler);
        }
    }

    public IDisposable Subscribe(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var token = new object();
        handlers[token] = handler;
        return new Subscription(this, token);
    }

    private async Task InvokeAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception exception)
        {
            // A subscriber is a page that may have been torn down between the publication and this
            // callback. That is not the publisher's problem, and a version notice is never worth
            // failing anything over.
            logger?.LogDebug(exception, "A subscriber failed to handle an update status change");
        }
    }

    private sealed class Subscription(UpdateStatusStore owner, object token) : IDisposable
    {
        public void Dispose() => owner.handlers.TryRemove(token, out _);
    }
}
