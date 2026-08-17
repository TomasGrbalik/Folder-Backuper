using System.Text.Json;
using FolderBackuper.Milestone0.Filesystem;

namespace FolderBackuper.Milestone0.Configuration;

public sealed class ProbeConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? SourceReadPath { get; init; }
    public int WebPort { get; init; } = 5180;
    public int MaximumSourceFiles { get; init; } = 1000;
    public NasConfiguration? Nas { get; init; }
    public string[] LocalHostAliases { get; init; } = [];

    public static async Task<ProbeConfiguration> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<ProbeConfiguration>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The probe configuration is empty.");
    }

    public void Validate(bool requireNas = false, bool requireSource = false)
    {
        if (WebPort is < 1024 or > 65535)
        {
            throw new InvalidDataException("WebPort must be between 1024 and 65535.");
        }

        if (MaximumSourceFiles <= 0)
        {
            throw new InvalidDataException("MaximumSourceFiles must be positive.");
        }

        if (requireNas && Nas is null)
        {
            throw new InvalidDataException("A NAS configuration is required for this command.");
        }

        if (requireSource && string.IsNullOrWhiteSpace(SourceReadPath))
        {
            throw new InvalidDataException("SourceReadPath is required for this command.");
        }

        Nas?.Validate();
        if (Nas is not null && new LocalHostUncDetector(LocalHostAliases).IsHostedLocally(Nas.UncRoot))
        {
            throw new InvalidDataException("Nas.UncRoot resolves to the backup PC; configure its local filesystem path instead.");
        }

        if (!string.IsNullOrWhiteSpace(Nas?.DeniedControlPath)
            && new LocalHostUncDetector(LocalHostAliases).IsHostedLocally(Nas.DeniedControlPath))
        {
            throw new InvalidDataException("Nas.DeniedControlPath cannot be hosted by the backup PC.");
        }
    }
}

public sealed class NasConfiguration
{
    public required string UncRoot { get; init; }
    public required string Username { get; init; }
    public string? Domain { get; init; }
    public string[] Aliases { get; init; } = [];
    public string? DeniedControlPath { get; init; }

    public void Validate()
    {
        ValidateUncPath(UncRoot, "Nas.UncRoot");

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidDataException("Nas.Username is required.");
        }

        if (!string.IsNullOrWhiteSpace(Domain) && (Username.Contains('\\', StringComparison.Ordinal) || Username.Contains('@', StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Nas.Domain cannot be combined with a qualified or user-principal username.");
        }

        foreach (var alias in Aliases)
        {
            ValidateUncPath(alias, "Every NAS alias");
        }

        if (!string.IsNullOrWhiteSpace(DeniedControlPath))
        {
            ValidateUncPath(DeniedControlPath, "Nas.DeniedControlPath");
        }
    }

    private static void ValidateUncPath(string path, string field)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.Contains('/', StringComparison.Ordinal)
            || Uri.CheckHostName(GetServer(path)) is UriHostNameType.Unknown)
        {
            throw new InvalidDataException($"{field} must be a conventional UNC path.");
        }

        var segments = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"{field} must include a server and share and cannot contain traversal segments.");
        }
    }

    private static string GetServer(string uncPath)
    {
        var separator = uncPath.IndexOf('\\', 2);
        return separator < 0 ? string.Empty : uncPath[2..separator];
    }
}
