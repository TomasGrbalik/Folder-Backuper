using System.Runtime.InteropServices;
using FolderBackuper.Milestone0;
using FolderBackuper.Milestone0.Components;
using FolderBackuper.Milestone0.Configuration;
using FolderBackuper.Milestone0.Probes;
using FolderBackuper.Milestone0.Reporting;
using FolderBackuper.Milestone0.Security;
using Microsoft.Extensions.Hosting.WindowsServices;
using MudBlazor.Services;

return await Program.RunAsync(args);

public static partial class Program
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Milestone 0 probes require Windows.");
            return 2;
        }

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "local" => await RunLocalAsync(args[1..]),
                "nas" => await RunNasAsync(args[1..], useFallback: false),
                "nas-fallback" => await RunNasAsync(args[1..], useFallback: true),
                "protect-secret" => await ProtectSecretAsync(args[1..]),
                "service" => await RunServiceAsync(args[1..]),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static ProbeReport CreateReport(Guid runId, DateTimeOffset started, IReadOnlyList<ProbeResult> results) =>
        new(
            runId,
            started,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            results);

    private static async Task<int> RunLocalAsync(string[] args)
    {
        var configuration = await LoadOptionalConfigurationAsync(GetOption(args, "--config"));
        configuration.Validate();
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var results = await LocalProbeRunner.RunAsync(configuration, CancellationToken.None);
        return await CompleteRunAsync(CreateReport(runId, started, results), GetOutput(args));
    }

    private static async Task<int> RunNasAsync(string[] args, bool useFallback)
    {
        var configPath = RequireOption(args, "--config");
        var configuration = await ProbeConfiguration.LoadAsync(configPath, CancellationToken.None);
        configuration.Validate(requireNas: true);
        var password = await GetPasswordAsync(GetOption(args, "--secret"));
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var results = useFallback
            ? await NasProbe.RunWithDevicelessConnectionAsync(configuration.Nas!, password, runId, CancellationToken.None)
            : await NasProbe.RunWithImpersonationAsync(configuration.Nas!, password, runId, CancellationToken.None);
        return await CompleteRunAsync(CreateReport(runId, started, results), GetOutput(args));
    }

    private static async Task<int> ProtectSecretAsync(string[] args)
    {
        var output = RequireOption(args, "--output");
        var password = ReadPassword("NAS password: ");
        await ProtectedSecretFile.WriteAsync(output, password, CancellationToken.None);
        Console.WriteLine($"Protected secret written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static async Task<int> RunServiceAsync(string[] args)
    {
        var configPath = RequireOption(args, "--config");
        var configuration = await ProbeConfiguration.LoadAsync(configPath, CancellationToken.None);
        configuration.Validate(requireNas: true, requireSource: true);
        var output = GetOutput(args);
        var options = new ServiceProbeOptions(Guid.NewGuid(), RequireOption(args, "--secret"), output);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Services.AddWindowsService(options => options.ServiceName = "Folder Backuper Milestone 0 Probe");
        builder.WebHost.UseUrls($"http://127.0.0.1:{configuration.WebPort}", $"http://[::1]:{configuration.WebPort}");
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddMudServices();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ServiceProbeState>();
        builder.Services.AddHostedService<ServiceProbeWorker>();

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> CompleteRunAsync(ProbeReport report, string output)
    {
        await ProbeReportWriter.WriteAsync(report, output, CancellationToken.None);
        foreach (var result in report.Results)
        {
            Console.WriteLine($"{result.Status,-12} {result.Name}: {result.Summary}");
        }

        Console.WriteLine($"Results written to {Path.GetFullPath(output)}");
        return report.Succeeded ? 0 : 1;
    }

    private static async Task<ProbeConfiguration> LoadOptionalConfigurationAsync(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? new ProbeConfiguration()
            : await ProbeConfiguration.LoadAsync(path, CancellationToken.None);

    private static async Task<string> GetPasswordAsync(string? protectedSecretPath) =>
        string.IsNullOrWhiteSpace(protectedSecretPath)
            ? ReadPassword("NAS password: ")
            : await ProtectedSecretFile.ReadAsync(protectedSecretPath, CancellationToken.None);

    private static string ReadPassword(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Redirected password input is not supported; use --secret with a protected secret file.");
        }

        Console.Write(prompt);
        var characters = new List<char>();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }

        Console.WriteLine();
        return new string([.. characters]);
    }

    private static string GetOutput(string[] args) => GetOption(args, "--output") ?? Path.Combine("results", "generated");

    private static string RequireOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException($"Required option missing: {name}");

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Length
                    ? args[index + 1]
                    : throw new ArgumentException($"Option requires a value: {name}");
            }
        }

        return null;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        Folder Backuper Milestone 0 diagnostic harness

          local [--config PATH] [--output DIRECTORY]
          nas --config PATH [--secret PATH] [--output DIRECTORY]
          nas-fallback --config PATH [--secret PATH] [--output DIRECTORY]
          protect-secret --output PATH
          service --config PATH [--secret PATH] [--output DIRECTORY]
        """);
}
