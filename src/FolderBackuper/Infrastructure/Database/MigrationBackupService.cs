using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.Data.Sqlite;

namespace FolderBackuper.Infrastructure.Database;

public sealed class MigrationBackupService(
    ApplicationPaths paths,
    TimeProvider timeProvider,
    ILogger<MigrationBackupService> logger)
{
    public const int RetainedBackupCount = 3;

    public async Task<string> CreateValidatedBackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.MigrationBackups);
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
        var backupPath = Path.Combine(paths.MigrationBackups, $"folder-backuper-{timestamp}.db");

        var sourceString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var backupString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        try
        {
            await using (var source = new SqliteConnection(sourceString))
            await using (var backup = new SqliteConnection(backupString))
            {
                await source.OpenAsync(cancellationToken);
                await backup.OpenAsync(cancellationToken);
                source.BackupDatabase(backup);
            }

            await ValidateAsync(backupPath, cancellationToken);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            File.Delete(backupPath);
            throw;
        }

        RemoveExpiredBackups(backupPath);
        logger.LogInformation("Created and validated pre-migration database backup {BackupPath}", backupPath);
        return backupPath;
    }

    public static async Task ValidateAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite validation failed for '{databasePath}': {result}");
        }
    }

    private void RemoveExpiredBackups(string newestBackup)
    {
        var backups = Directory.EnumerateFiles(paths.MigrationBackups, "folder-backuper-*.db")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var expired in backups.Skip(RetainedBackupCount))
        {
            if (!string.Equals(expired, newestBackup, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(expired);
            }
        }
    }
}
