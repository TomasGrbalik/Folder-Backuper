namespace FolderBackuper.Features.Backups;

public static class BackupServiceCollectionExtensions
{
    public static IServiceCollection AddBackupEngine(this IServiceCollection services)
    {
        services.AddSingleton<BackupProgressRegistry>();
        services.AddSingleton<SourceManifestBuilder>();
        services.AddSingleton<BackupPreflightService>();
        services.AddSingleton<ZipArchiveService>();
        services.AddSingleton<IBackupCommitCoordinator, DirectBackupCommitCoordinator>();
        services.AddSingleton<DestinationArchiveService>();
        services.AddSingleton<DestinationAccessRecorder>();
        services.AddSingleton<BackupEngine>();
        return services;
    }
}
