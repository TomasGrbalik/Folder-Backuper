namespace FolderBackuper.Infrastructure.ServiceHosting;

public sealed class StartupRecoveryBarrier
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        completion.Task.WaitAsync(cancellationToken);

    public void Complete() => completion.TrySetResult();
}
