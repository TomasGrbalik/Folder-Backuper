namespace FolderBackuper.Features.Backups;

public static class BackupServiceCollectionExtensions
{
    public static IServiceCollection AddBackupEngine(this IServiceCollection services)
    {
        services.AddSingleton<BackupProgressRegistry>();
        services.AddSingleton<SourceManifestBuilder>();
        services.AddSingleton<BackupPreflightService>();
        services.AddSingleton<ZipArchiveService>();
        services.AddSingleton<IBackupCommitCoordinator, DurableBackupCommitCoordinator>();
        services.AddSingleton<DestinationArchiveService>();
        services.AddSingleton<DestinationAccessRecorder>();
        services.AddSingleton<BackupRetentionService>();
        services.AddSingleton<BackupRecoveryService>();
        services.AddSingleton<BackupEngine>();
        services.AddSingleton<BackupExecutionQueue>();
        services.AddSingleton<BackupCancellationRegistry>();
        services.AddSingleton<BackupExecutionService>();
        services.AddHostedService<BackupExecutionWorker>();
        return services;
    }
}
