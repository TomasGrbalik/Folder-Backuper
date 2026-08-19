namespace FolderBackuper.Infrastructure.ServiceHosting;

public sealed class StartupRecoveryBarrier
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCompleted => completion.Task.IsCompletedSuccessfully;

    public bool IsFaulted => completion.Task.IsFaulted;

    /// <summary>
    /// Waits for startup initialization to finish.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the queue and the scheduler may proceed, and
    /// <see langword="false"/> when initialization failed and the host is shutting down.
    /// </returns>
    public async Task<bool> WaitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await completion.Task.WaitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The failure is already classified and reported by StartupInitializationService.
            return false;
        }
    }

    public void Complete() => completion.TrySetResult();

    /// <summary>
    /// Releases every waiter instead of leaving the queue and the scheduler blocked forever on a
    /// barrier that will never complete.
    /// </summary>
    public void Fault(Exception exception) => completion.TrySetException(exception);
}
