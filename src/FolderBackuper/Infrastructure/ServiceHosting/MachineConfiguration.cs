using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// The installer-owned hosting configuration stored under the application data root so that it
/// survives an upgrade, which replaces everything below <c>Program Files</c>.
/// </summary>
/// <remarks>
/// Only the Kestrel port belongs in this file. The data root cannot: it is the location of the
/// file itself, and settings consumed while the host builder is constructed are already fixed by
/// the time this source is added.
/// </remarks>
public static class MachineConfiguration
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetFilePath(ApplicationPaths paths) =>
        Path.Combine(paths.Config, WindowsServiceMetadata.MachineConfigurationFileName);

    /// <summary>
    /// Adds the machine configuration file below the environment and command-line sources so that
    /// a development override still wins over an installed value.
    /// </summary>
    public static void Apply(IConfigurationBuilder configuration, ApplicationPaths paths, string[] args)
    {
        var filePath = GetFilePath(paths);
        Guard(filePath);

        configuration
            .AddJsonFile(filePath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }

    public static void Write(ApplicationPaths paths, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The loopback port must be between 1 and 65535.");
        }

        Directory.CreateDirectory(paths.Config);

        var filePath = GetFilePath(paths);
        var temporaryPath = filePath + ".tmp";
        var document = new MachineConfigurationDocument(new MachineConfigurationSection(port));

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    /// <summary>
    /// Reads the configured port, or <see langword="null"/> when no machine configuration exists.
    /// </summary>
    /// <remarks>
    /// A file that exists but cannot be read is not the same as an absent one, and must not be
    /// reported as "no port configured". Access failures are propagated so the caller can say so.
    /// </remarks>
    public static int? TryReadPort(ApplicationPaths paths)
    {
        string content;
        try
        {
            content = File.ReadAllText(GetFilePath(paths));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MachineConfigurationDocument>(content)?.FolderBackuper?.Port;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rejects a data root written into the machine configuration file. Silently ignoring the one
    /// key that cannot take effect there is the failure mode nobody can diagnose.
    /// </summary>
    private static void Guard(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        MachineConfigurationDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<MachineConfigurationDocument>(File.ReadAllText(filePath));
        }
        catch (JsonException)
        {
            // A malformed file is reported by the JSON configuration provider itself.
            return;
        }

        if (!string.IsNullOrWhiteSpace(document?.FolderBackuper?.DataRoot))
        {
            throw new InvalidOperationException(
                $"{filePath} must not define FolderBackuper:DataRoot. The data root locates this file and can only be "
                + "set through the FolderBackuper__DataRoot environment variable or the --FolderBackuper:DataRoot argument.");
        }
    }

    private sealed record MachineConfigurationDocument(
        [property: JsonPropertyName("FolderBackuper")] MachineConfigurationSection? FolderBackuper);

    private sealed record MachineConfigurationSection(
        [property: JsonPropertyName("Port")] int? Port,
        [property: JsonPropertyName("DataRoot")] string? DataRoot = null);
}
