using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

public sealed class DatabaseInitializer(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    MigrationBackupService backupService,
    ApplicationPaths paths,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databaseExisted = File.Exists(paths.Database) && new FileInfo(paths.Database).Length > 0;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pendingMigrations = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

        if (databaseExisted && pendingMigrations.Length > 0)
        {
            await backupService.CreateValidatedBackupAsync(cancellationToken);
        }

        if (pendingMigrations.Length > 0)
        {
            logger.LogInformation("Applying {MigrationCount} pending database migration(s)", pendingMigrations.Length);
            await context.Database.MigrateAsync(cancellationToken);
        }

        await ConfigureJournalModeAsync(context, cancellationToken);
    }

    private async Task ConfigureJournalModeAsync(
        FolderBackuperDbContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SQLite WAL mode is unavailable; database is using {JournalMode}", mode);
            }
        }
        catch (SqliteException exception)
        {
            logger.LogWarning(exception, "SQLite WAL mode is unavailable; using the filesystem-compatible journal mode");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
