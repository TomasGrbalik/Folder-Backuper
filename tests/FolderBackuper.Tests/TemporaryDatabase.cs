using FolderBackuper.Features.Monitoring;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolderBackuper.Tests;

internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private readonly ServiceProvider serviceProvider;

    public TemporaryDatabase(TimeProvider? timeProvider = null)
    {
        Paths = ApplicationPaths.Resolve(Path.Combine(
            Path.GetTempPath(),
            "FolderBackuper.Tests",
            Guid.NewGuid().ToString("N")));
        Paths.CreateDirectories();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Paths);
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddSingleton<RunActivitySignal>();
        services.AddFolderBackuperDatabase(Paths);
        serviceProvider = services.BuildServiceProvider();
    }

    public ApplicationPaths Paths { get; }

    public IDbContextFactory<FolderBackuperDbContext> ContextFactory =>
        serviceProvider.GetRequiredService<IDbContextFactory<FolderBackuperDbContext>>();

    public DatabaseInitializer Initializer => serviceProvider.GetRequiredService<DatabaseInitializer>();

    public MigrationBackupService BackupService => serviceProvider.GetRequiredService<MigrationBackupService>();

    public ConfigurationMutationGate MutationGate => serviceProvider.GetRequiredService<ConfigurationMutationGate>();

    public RunPersistenceService RunPersistence => serviceProvider.GetRequiredService<RunPersistenceService>();

    /// <summary>The instance <see cref="RunPersistence"/> raises, so a page test can subscribe to real transitions.</summary>
    public RunActivitySignal ActivitySignal => serviceProvider.GetRequiredService<RunActivitySignal>();

    public async ValueTask DisposeAsync()
    {
        await serviceProvider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Paths.Root))
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
    }
}

internal sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset current = initial;

    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration) => current += duration;
}
