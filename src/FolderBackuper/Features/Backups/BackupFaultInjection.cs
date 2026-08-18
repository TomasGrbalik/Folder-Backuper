namespace FolderBackuper.Features.Backups;

public enum BackupFaultPoint
{
    AfterStagingIntentPersisted,
    AfterStagingFileCreated,
    AfterPartialIntentPersisted,
    AfterPartialFileCreated,
    AfterCommitIntentPersisted,
    AfterFinalRename
}

public interface IBackupFaultInjector
{
    ValueTask HitAsync(BackupFaultPoint point, Guid runId, CancellationToken cancellationToken);
}

public sealed class NoOpBackupFaultInjector : IBackupFaultInjector
{
    public ValueTask HitAsync(BackupFaultPoint point, Guid runId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class InjectedBackupFaultException(BackupFaultPoint point)
    : Exception($"A test fault was injected at {point}.")
{
    public BackupFaultPoint Point { get; } = point;
}
