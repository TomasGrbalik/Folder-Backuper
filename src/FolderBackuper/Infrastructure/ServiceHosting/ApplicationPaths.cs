using Microsoft.Extensions.Configuration;

namespace FolderBackuper.Infrastructure.ServiceHosting;

public sealed record ApplicationPaths(
    string Root,
    string Config,
    string Data,
    string Staging,
    string Logs)
{
    public const string DataRootConfigurationKey = "FolderBackuper:DataRoot";

    public static ApplicationPaths Resolve(IConfiguration configuration) =>
        Resolve(configuration[DataRootConfigurationKey]);

    public static ApplicationPaths Resolve(string? configuredRoot)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FolderBackuper")
            : configuredRoot.Trim();

        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException("The Folder Backuper data root must be an absolute path.");
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("The Folder Backuper data root is invalid.", exception);
        }

        if (string.Equals(normalizedRoot, Path.GetPathRoot(normalizedRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Folder Backuper data root cannot be a drive or share root.");
        }

        return new ApplicationPaths(
            normalizedRoot,
            Path.Combine(normalizedRoot, "config"),
            Path.Combine(normalizedRoot, "data"),
            Path.Combine(normalizedRoot, "staging"),
            Path.Combine(normalizedRoot, "logs"));
    }

    public void CreateDirectories()
    {
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Logs);
    }
}
