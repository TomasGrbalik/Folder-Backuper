namespace FolderBackuper.Features.Monitoring;

public static class MonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddMonitoringServices(this IServiceCollection services)
    {
        services.AddSingleton<RunQueryService>();
        services.AddSingleton<DashboardQueryService>();
        services.AddSingleton<CalendarEntryService>();
        return services;
    }
}
