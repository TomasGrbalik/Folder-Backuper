using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace FolderBackuper.Tests;

public sealed class DatabaseInitializationTests
{
    [Fact]
    public async Task Initialize_CreatesMigratedConfiguredDatabase()
    {
        await using var database = new TemporaryDatabase();

        await database.Initializer.InitializeAsync();
        await database.Initializer.InitializeAsync();

        Assert.True(File.Exists(database.Paths.Database));
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(
            context.Database.GetMigrations(),
            await context.Database.GetAppliedMigrationsAsync());
        await context.Database.OpenConnectionAsync();
        Assert.Equal(1L, await ScalarAsync(context, "PRAGMA foreign_keys;"));
        Assert.Equal(5_000L, await ScalarAsync(context, "PRAGMA busy_timeout;"));
        Assert.Equal("wal", await ScalarAsync(context, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task ContextFactory_ReturnsIndependentContexts()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();

        await using var first = await database.ContextFactory.CreateDbContextAsync();
        await using var second = await database.ContextFactory.CreateDbContextAsync();

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Constraints_RejectDuplicateNamesAndInvalidRetention()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var destination = Destination("Archive");
        context.Destinations.Add(destination);
        context.Destinations.Add(Destination("archive"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        context.Destinations.Add(destination);
        context.Jobs.Add(Job(destination.Id, "Daily", retentionCount: 0));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ExistingDatabaseWithConflictingSchema_IsBackedUpAndMigrationFails()
    {
        await using var database = new TemporaryDatabase();
        var connectionString = DatabaseServiceCollectionExtensions.CreateConnectionString(database.Paths.Database);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ApplicationSettings (WrongColumn TEXT); CREATE TABLE Evidence (Value TEXT); INSERT INTO Evidence VALUES ('before migration');";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => database.Initializer.InitializeAsync());

        var backup = Assert.Single(Directory.GetFiles(database.Paths.MigrationBackups, "*.db"));
        await MigrationBackupService.ValidateAsync(backup);
        await using var backupConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await backupConnection.OpenAsync();
        await using var evidence = backupConnection.CreateCommand();
        evidence.CommandText = "SELECT Value FROM Evidence;";
        Assert.Equal("before migration", await evidence.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MigrationFailure_ExitsApplicationBeforeNormalHosting()
    {
        await using var database = new TemporaryDatabase();
        var connectionString = DatabaseServiceCollectionExtensions.CreateConnectionString(database.Paths.Database);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ApplicationSettings (WrongColumn TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };
        var assemblyPath = typeof(Program).Assembly.Location;
        var outputDirectory = Path.GetDirectoryName(assemblyPath)!;
        process.StartInfo.ArgumentList.Add("exec");
        process.StartInfo.ArgumentList.Add("--runtimeconfig");
        process.StartInfo.ArgumentList.Add(Path.Combine(outputDirectory, "FolderBackuper.Tests.runtimeconfig.json"));
        process.StartInfo.ArgumentList.Add("--depsfile");
        process.StartInfo.ArgumentList.Add(Path.Combine(outputDirectory, "FolderBackuper.Tests.deps.json"));
        process.StartInfo.ArgumentList.Add(assemblyPath);
        process.StartInfo.ArgumentList.Add($"--FolderBackuper:DataRoot={database.Paths.Root}");
        process.StartInfo.ArgumentList.Add("--FolderBackuper:Port=5199");

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        Assert.True(
            process.ExitCode == 1,
            $"Expected exit code 1 but received {process.ExitCode}.{Environment.NewLine}{await standardOutput}{await standardError}");
        var backup = Assert.Single(Directory.GetFiles(database.Paths.MigrationBackups, "*.db"));
        await MigrationBackupService.ValidateAsync(backup);
    }

    [Fact]
    public async Task DurableExecutionMigration_ReconcilesLegacyDuplicateActiveRuns()
    {
        await using var database = new TemporaryDatabase();
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync("20260817180942_AddScheduleEffectiveFromUtc");
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var jobId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        const string insert = """
            INSERT INTO Runs (
                Id, JobId, JobName, SourcePath, DestinationName, DestinationType,
                DestinationRootPath, DestinationSubfolder, ScheduledWeekdays, ScheduledTime,
                RetentionCount, RegionalCulture, TimeZoneId, Trigger, QueuedAtUtc, Phase,
                FileCount, DirectoryCount, SourceBytes, ArchiveBytes)
            VALUES ({0}, {1}, 'Legacy', 'C:\Source', 'Legacy', 'Local', 'D:\Backup',
                'Legacy', 'Monday', '01:00:00', 1, 'en-US', 'UTC', 'Manual',
                '2026-08-18 01:00:00+00:00', 'Queued', 0, 0, 0, 0);
            """;
        await context.Database.ExecuteSqlRawAsync(insert, first, jobId);
        await context.Database.ExecuteSqlRawAsync(insert, second, jobId);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Runs SET Phase = 'Finalizing', StartedAtUtc = QueuedAtUtc,
                FinalCommitStartedAtUtc = QueuedAtUtc, FinalCommittedAtUtc = QueuedAtUtc
            WHERE Id = {second};
            """);

        await context.Database.MigrateAsync();

        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT COUNT(*) FROM Runs WHERE Outcome IS NULL AND Phase <> 'Planned';"));
        Assert.Equal(1L, await ScalarAsync(context,
            "SELECT COUNT(*) FROM Runs WHERE Outcome = 'Failed' "
            + "AND ErrorMessageKey = 'BackupProblemMessage_DuplicateActiveWorkReconciled';"));
        Assert.Equal(2L, await ScalarAsync(context,
            "SELECT COUNT(*) FROM Runs WHERE DueAtUtc = QueuedAtUtc;"));
        Assert.Equal(second, Guid.Parse((string)(await ScalarAsync(context,
            "SELECT Id FROM Runs WHERE Outcome IS NULL AND Phase <> 'Planned';"))!));
    }

    private static async Task<object?> ScalarAsync(FolderBackuperDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    internal static Destination Destination(string name) => new()
    {
        Name = name,
        Type = DestinationType.Local,
        RootPath = @"D:\Backups"
    };

    internal static BackupJob Job(Guid destinationId, string name, int retentionCount = 3) => new()
    {
        Name = name,
        SourcePath = @"C:\Source",
        DestinationId = destinationId,
        DestinationSubfolder = name,
        Weekdays = ScheduledWeekdays.Monday,
        ScheduledTime = new TimeOnly(1, 30),
        RetentionCount = retentionCount,
        DestinationOwnershipKey = $"{destinationId:N}:{name}"
    };
}
