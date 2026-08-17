using FolderBackuper.Components;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Hosting.WindowsServices;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

ApplicationInstanceLock? instanceLock = null;

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

    var paths = ApplicationPaths.Resolve(builder.Configuration);
    instanceLock = ApplicationInstanceLock.Acquire(paths.Root);
    paths.CreateDirectories();

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

    builder.Services.AddWindowsService(options => options.ServiceName = "Folder Backuper");
    builder.Services.AddSingleton(paths);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddFolderBackuperDatabase(paths);
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddMudServices();
    builder.Services.PostConfigure<HostFilteringOptions>(LoopbackHosting.ConfigureHostFiltering);

    var port = builder.Configuration.GetValue("FolderBackuper:Port", 5180);
    builder.WebHost.UseUrls(LoopbackHosting.GetUrls(port));

    var app = builder.Build();
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    app.UseHostFiltering();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseAntiforgery();
    app.MapStaticAssets();
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        Log.Information("Application started on {Addresses} using data root {DataRoot}", addresses, paths.Root);
    });

    await app.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "Folder Backuper terminated during startup");
    return 1;
}
finally
{
    instanceLock?.Dispose();
    await Log.CloseAndFlushAsync();
}

public partial class Program;
