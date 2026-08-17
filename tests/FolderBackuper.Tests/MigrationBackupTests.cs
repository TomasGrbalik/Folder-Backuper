using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace FolderBackuper.Tests;

public sealed class MigrationBackupTests
{
    [Fact]
    public async Task OnlineBackup_ContainsCommittedWalDataAndCanBeOpenedIndependently()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.Destinations.Add(DatabaseInitializationTests.Destination("WAL data"));
            await context.SaveChangesAsync();
        }

        var backup = await database.BackupService.CreateValidatedBackupAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Destinations;";
        Assert.Equal("WAL data", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OnlineBackup_RetainsOnlyThreeNewestValidatedBackups()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        await using var database = new TemporaryDatabase(time);
        await database.Initializer.InitializeAsync();

        for (var index = 0; index < 5; index++)
        {
            await database.BackupService.CreateValidatedBackupAsync();
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var backups = Directory.GetFiles(database.Paths.MigrationBackups, "*.db");
        Assert.Equal(MigrationBackupService.RetainedBackupCount, backups.Length);
        Assert.DoesNotContain(backups, path => path.Contains("120000", StringComparison.Ordinal));
        Assert.DoesNotContain(backups, path => path.Contains("120001", StringComparison.Ordinal));
    }
}
