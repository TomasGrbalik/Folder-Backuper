using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Infrastructure.Maintenance;

public abstract record MaintenanceCommand
{
    public string? DataRoot { get; init; }
}

/// <summary>Writes the loopback port chosen by the installer. A strict writer, never a chooser.</summary>
public sealed record ConfigurePortCommand : MaintenanceCommand
{
    /// <summary>The requested port, or <see langword="null"/> for <c>--port=auto</c>.</summary>
    public int? Port { get; init; }
}

public sealed record WaitReadyCommand : MaintenanceCommand
{
    public int TimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// The port to probe. When omitted it is read from the machine configuration file, which
    /// requires the caller to have access to the application data root.
    /// </summary>
    public int? Port { get; init; }
}

public sealed record MaintenanceParseResult(MaintenanceCommand? Command, string? Error)
{
    public static MaintenanceParseResult None { get; } = new(null, null);

    public bool IsMaintenance => Command is not null || Error is not null;
}

/// <summary>
/// Parses the installer-facing commands.
/// </summary>
/// <remarks>
/// This runs before <c>WebApplication.CreateBuilder</c> and never reaches the configuration
/// system. <c>CommandLineConfigurationProvider</c> would throw on <c>--configure-port --port 5180</c>
/// because it treats <c>--port</c> as the value of <c>--configure-port</c> and then finds an
/// argument with no pending key.
/// </remarks>
public static class MaintenanceCommandLine
{
    public const string ConfigurePortVerb = "--configure-port";
    public const string WaitReadyVerb = "--wait-ready";

    public static MaintenanceParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return MaintenanceParseResult.None;
        }

        var verb = args[0];
        var isConfigurePort = string.Equals(verb, ConfigurePortVerb, StringComparison.OrdinalIgnoreCase);
        var isWaitReady = string.Equals(verb, WaitReadyVerb, StringComparison.OrdinalIgnoreCase);

        if (!isConfigurePort && !isWaitReady)
        {
            return MaintenanceParseResult.None;
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                return Invalid($"Unrecognized argument '{argument}'.");
            }

            var name = argument[2..];
            string value;

            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                value = name[(separator + 1)..];
                name = name[..separator];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }
            else
            {
                return Invalid($"Option '--{name}' requires a value.");
            }

            if (name.Length == 0)
            {
                return Invalid($"Unrecognized argument '{argument}'.");
            }

            options[name] = value;
        }

        var dataRoot = Take(options, "data-root");

        if (isConfigurePort)
        {
            int? port = null;
            var requested = Take(options, "port");
            if (requested is null)
            {
                return Invalid($"'{ConfigurePortVerb}' requires --port=<number> or --port=auto.");
            }

            if (!string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(requested, out var parsed) || parsed is < 1 or > 65535)
                {
                    return Invalid($"'--port' must be a number between 1 and 65535 or 'auto', not '{requested}'.");
                }

                port = parsed;
            }

            return Remaining(options)
                ?? new MaintenanceParseResult(new ConfigurePortCommand { Port = port, DataRoot = dataRoot }, null);
        }

        var timeout = 90;
        var requestedTimeout = Take(options, "timeout-seconds");
        if (requestedTimeout is not null)
        {
            if (!int.TryParse(requestedTimeout, out timeout) || timeout is < 1 or > 3600)
            {
                return Invalid($"'--timeout-seconds' must be a number between 1 and 3600, not '{requestedTimeout}'.");
            }
        }

        int? probePort = null;
        var requestedPort = Take(options, "port");
        if (requestedPort is not null)
        {
            if (!int.TryParse(requestedPort, out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                return Invalid($"'--port' must be a number between 1 and 65535, not '{requestedPort}'.");
            }

            probePort = parsedPort;
        }

        return Remaining(options)
            ?? new MaintenanceParseResult(
                new WaitReadyCommand { TimeoutSeconds = timeout, Port = probePort, DataRoot = dataRoot },
                null);
    }

    public static ApplicationPaths ResolvePaths(MaintenanceCommand command) =>
        ApplicationPaths.Resolve(
            command.DataRoot
            ?? Environment.GetEnvironmentVariable("FolderBackuper__DataRoot"));

    private static string? Take(Dictionary<string, string> options, string name)
    {
        if (!options.Remove(name, out var value))
        {
            return null;
        }

        return value;
    }

    private static MaintenanceParseResult? Remaining(Dictionary<string, string> options) =>
        options.Count == 0 ? null : Invalid($"Unrecognized option '--{options.Keys.First()}'.");

    private static MaintenanceParseResult Invalid(string error) => new(null, error);
}
