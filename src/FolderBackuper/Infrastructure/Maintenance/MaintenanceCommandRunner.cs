using System.Net.Http;
using System.ServiceProcess;
using FolderBackuper.Infrastructure.Security;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Infrastructure.Maintenance;

/// <summary>
/// Executes the installer-facing commands.
/// </summary>
/// <remarks>
/// These run while the service may be live, so they never acquire
/// <see cref="ApplicationInstanceLock"/>, open the database, or start a host.
/// </remarks>
public static class MaintenanceCommandRunner
{
    public static async Task<int> RunAsync(MaintenanceParseResult parsed, TextWriter output)
    {
        if (parsed.Error is not null)
        {
            await output.WriteLineAsync(parsed.Error);
            return MaintenanceExitCode.InvalidArguments;
        }

        return parsed.Command switch
        {
            ConfigurePortCommand configure => ConfigurePort(configure, output),
            WaitReadyCommand wait => await WaitReadyAsync(wait, output),
            _ => MaintenanceExitCode.InvalidArguments
        };
    }

    private static int ConfigurePort(ConfigurePortCommand command, TextWriter output)
    {
        ApplicationPaths paths;
        try
        {
            paths = MaintenanceCommandLine.ResolvePaths(command);
            paths.CreateDirectories();
        }
        catch (Exception exception)
        {
            output.WriteLine($"The application data root could not be prepared: {exception.Message}");
            return MaintenanceExitCode.DataRootUnavailable;
        }

        int port;
        if (command.Port is { } requested)
        {
            if (!LoopbackPortSelector.IsAvailable(requested))
            {
                output.WriteLine($"Port {requested} is already in use. Choose a different port.");
                return MaintenanceExitCode.PortUnavailable;
            }

            port = requested;
        }
        else if (LoopbackPortSelector.FindAvailable() is { } discovered)
        {
            port = discovered;
        }
        else
        {
            output.WriteLine(
                $"No free loopback port was found between {WindowsServiceMetadata.DefaultPort} and "
                + $"{WindowsServiceMetadata.LastCandidatePort}.");
            return MaintenanceExitCode.NoCandidatePortAvailable;
        }

        try
        {
            MachineConfiguration.Write(paths, port);
        }
        catch (Exception exception)
        {
            output.WriteLine($"{MachineConfiguration.GetFilePath(paths)} could not be written: {exception.Message}");
            return MaintenanceExitCode.ConfigurationNotWritten;
        }

        try
        {
            new AppDataAclService(paths).Apply(includeCurrentUser: false);
        }
        catch (Exception exception)
        {
            output.WriteLine($"Access controls could not be applied to {paths.Root}: {exception.Message}");
            return MaintenanceExitCode.AccessControlDenied;
        }

        output.WriteLine(port.ToString());
        return MaintenanceExitCode.Success;
    }

    private static async Task<int> WaitReadyAsync(WaitReadyCommand command, TextWriter output)
    {
        int port;
        if (command.Port is { } requested)
        {
            port = requested;
        }
        else
        {
            ApplicationPaths paths;
            try
            {
                paths = MaintenanceCommandLine.ResolvePaths(command);
            }
            catch (Exception exception)
            {
                await output.WriteLineAsync($"The application data root could not be resolved: {exception.Message}");
                return MaintenanceExitCode.DataRootUnavailable;
            }

            int? configured;
            try
            {
                configured = MachineConfiguration.TryReadPort(paths);
            }
            catch (Exception exception)
            {
                await output.WriteLineAsync(
                    $"{MachineConfiguration.GetFilePath(paths)} could not be read: {exception.Message}");
                return MaintenanceExitCode.DataRootUnavailable;
            }

            if (configured is not { } resolved)
            {
                await output.WriteLineAsync(
                    $"No loopback port is configured in {MachineConfiguration.GetFilePath(paths)}.");
                return MaintenanceExitCode.PortNotConfigured;
            }

            port = resolved;
        }

        var probe = $"http://127.0.0.1:{port}{WindowsServiceMetadata.ReadinessPath}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(command.TimeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(probe);
                if (response.IsSuccessStatusCode)
                {
                    await output.WriteLineAsync($"Folder Backuper is ready on http://localhost:{port}.");
                    return MaintenanceExitCode.Success;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // The service has not started listening yet.
            }

            // A service that has already stopped will never become ready; fail without waiting out
            // the whole timeout.
            if (HasStopped())
            {
                await output.WriteLineAsync(
                    $"The {WindowsServiceMetadata.DisplayName} service stopped before it became ready.");
                return MaintenanceExitCode.ServiceNotRunning;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        await output.WriteLineAsync(
            $"Folder Backuper did not become ready on {probe} within {command.TimeoutSeconds} seconds.");
        return MaintenanceExitCode.ReadinessTimedOut;
    }

    private static bool HasStopped()
    {
        try
        {
            using var controller = new ServiceController(WindowsServiceMetadata.ServiceName);
            controller.Refresh();
            return controller.Status is ServiceControllerStatus.Stopped;
        }
        catch (Exception)
        {
            // The service may not be installed when the probe is used from a development console.
            return false;
        }
    }
}
