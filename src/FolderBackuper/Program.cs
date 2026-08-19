using FolderBackuper.Components;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Maintenance;
using FolderBackuper.Infrastructure.Security;
using FolderBackuper.Infrastructure.ServiceHosting;
using FolderBackuper.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Hosting.WindowsServices;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;

// Installer commands are handled before the host builder so that they never reach the command-line
// configuration provider, which rejects a verb followed by a separate option, and so that they
// never contend for the single-instance mutex held by a running service.
var maintenance = MaintenanceCommandLine.Parse(args);
if (maintenance.IsMaintenance)
{
    return await MaintenanceCommandRunner.RunAsync(maintenance, Console.Out);
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

ApplicationInstanceLock? instanceLock = null;
ApplicationPaths? resolvedPaths = null;

try
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("Folder Backuper requires Windows.");
    }

    var isWindowsService = WindowsServiceHelpers.IsWindowsService();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = isWindowsService ? AppContext.BaseDirectory : null
    });

    if (!isWindowsService)
    {
        builder.WebHost.UseStaticWebAssets();
    }

    ApplicationPaths paths;
    try
    {
        paths = ApplicationPaths.Resolve(builder.Configuration);
        resolvedPaths = paths;
        paths.CreateDirectories();
    }
    catch (Exception exception)
    {
        throw new StartupFailureException(StartupFailure.DataRoot, exception);
    }

    try
    {
        instanceLock = ApplicationInstanceLock.Acquire(paths.Root);
    }
    catch (Exception exception)
    {
        throw new StartupFailureException(StartupFailure.SingleInstance, exception);
    }

    try
    {
        new AppDataAclService(paths).Apply();
    }
    catch (Exception exception)
    {
        throw new StartupFailureException(StartupFailure.AccessControl, exception);
    }

    MachineConfiguration.Apply(builder.Configuration, paths, args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(paths.Logs, "folder-backuper-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    builder.Services.AddWindowsService(options => options.ServiceName = WindowsServiceMetadata.ServiceName);
    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton<StartupRecoveryBarrier>();
    builder.Services.AddSingleton<AppDataAclService>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
    builder.Services.AddSingleton<INetworkImpersonator, NetworkOnlyImpersonator>();
    builder.Services.AddSingleton<ILocalHostUncDetector, LocalHostUncDetector>();
    builder.Services.AddSingleton<IDestinationAdapter, LocalDestinationAdapter>();
    builder.Services.AddSingleton<IDestinationAdapter, SmbDestinationAdapter>();
    builder.Services.AddJobCoreServices();
    builder.Services.AddBackupEngine();
    builder.Services.AddSingleton<SourceBrowser>();
    builder.Services.AddSingleton<SourcePreview>();
    builder.Services.AddSingleton<ScheduleOccurrenceCalculator>();
    builder.Services.AddSingleton<IMachineTimeZoneProvider, MachineTimeZoneProvider>();
    builder.Services.AddSingleton<BackupScheduler>();
    builder.Services.AddSingleton<CalendarOccurrenceService>();
    builder.Services.AddMonitoringServices();
    builder.Services.AddHostedService<StartupInitializationService>();
    builder.Services.AddHostedService<BackupSchedulerWorker>();
    builder.Services.AddScoped<DestinationService>();
    builder.Services.AddFolderBackuperDatabase(paths);
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddMudServices();
    builder.Services.PostConfigure<HostFilteringOptions>(LoopbackHosting.ConfigureHostFiltering);

    var port = builder.Configuration.GetValue(
        WindowsServiceMetadata.PortConfigurationKey,
        WindowsServiceMetadata.DefaultPort);
    builder.WebHost.UseUrls(LoopbackHosting.GetUrls(port));

    var app = builder.Build();
    app.UseHostFiltering();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseAntiforgery();
    app.MapStaticAssets();
    app.MapReadiness();
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var nonLoopback = LoopbackAddressGuard.FindNonLoopbackAddresses(addresses);
        if (nonLoopback.Count != 0)
        {
            var failure = StartupFailure.NonLoopbackBinding;
            var exception = new InvalidOperationException(
                $"{failure.OperatorMessage} Bound: {string.Join(", ", nonLoopback)}.");
            Log.Fatal(exception, "Refusing to serve a non-loopback address");
            StartupFailureReporter.Report(failure, exception, paths);
            app.Lifetime.StopApplication();
            return;
        }

        Log.Information("Application started on {Addresses} using data root {DataRoot}", addresses, paths.Root);
    });

    await app.RunAsync();

    // A failed startup initialization stops the host gracefully, so the failure has to be carried
    // out of RunAsync explicitly rather than being reported as a clean shutdown.
    return app.Services.GetRequiredService<StartupRecoveryBarrier>().IsFaulted
        ? StartupFailure.ServiceExitCode
        : 0;
}
catch (Exception exception)
{
    var failure = StartupFailureClassifier.Classify(exception);
    Log.Fatal(exception, "Folder Backuper terminated during startup: {OperatorMessage}", failure.OperatorMessage);
    StartupFailureReporter.Report(failure, exception, resolvedPaths);

    // The service control manager renders a service exit code as a Win32 error string, so the
    // classification is carried by the event log entry rather than by the exit code.
    return StartupFailure.ServiceExitCode;
}
finally
{
    instanceLock?.Dispose();
    await Log.CloseAndFlushAsync();
}

public partial class Program;
