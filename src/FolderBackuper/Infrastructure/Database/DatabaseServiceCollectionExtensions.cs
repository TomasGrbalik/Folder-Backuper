using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Infrastructure.Database;

public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddFolderBackuperDatabase(
        this IServiceCollection services,
        ApplicationPaths paths)
    {
        var connectionString = CreateConnectionString(paths.Database);
        services.AddSingleton<SqliteConnectionInterceptor>();
        services.AddPooledDbContextFactory<FolderBackuperDbContext>((provider, options) => options
            .UseSqlite(connectionString)
            .AddInterceptors(provider.GetRequiredService<SqliteConnectionInterceptor>()));
        services.AddSingleton<MigrationBackupService>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<ConfigurationMutationGate>();
        services.AddSingleton<RunPersistenceService>();
        return services;
    }

    public static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
}
