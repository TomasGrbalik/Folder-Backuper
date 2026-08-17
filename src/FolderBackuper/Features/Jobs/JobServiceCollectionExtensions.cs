using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Features.Jobs;

public static class JobServiceCollectionExtensions
{
    public static IServiceCollection AddJobCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<InstallationIdentityService>();
        services.AddSingleton<OwnershipMarkerService>();
        services.AddSingleton<EffectiveDestinationService>();
        services.AddSingleton<JobDestinationTestService>();
        services.AddSingleton<JobService>();
        return services;
    }
}
