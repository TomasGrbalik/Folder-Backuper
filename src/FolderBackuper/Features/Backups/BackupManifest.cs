namespace FolderBackuper.Features.Backups;

public enum BackupManifestEntryType { File, Directory }

public sealed record BackupManifestEntry(
    string RelativePath,
    BackupManifestEntryType Type,
    long Size,
    DateTimeOffset LastWriteTime,
    FileAttributes Attributes)
{
    public bool IsFile => Type == BackupManifestEntryType.File;
}

public sealed class BackupManifest
{
    private readonly IReadOnlyList<BackupManifestEntry> entries;

    public BackupManifest(IEnumerable<BackupManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copy = entries.Select(e => e with { RelativePath = Normalize(e.RelativePath) })
            .OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
        if (copy.Any(e => e.Size < 0) || copy.Select(e => e.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            throw new ArgumentException("Manifest entries are invalid.", nameof(entries));
        var files = copy.Where(e => e.IsFile).Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in copy)
        {
            var ancestor = Parent(entry.RelativePath);
            while (ancestor is not null)
            {
                if (files.Contains(ancestor)) throw new ArgumentException("A manifest file cannot contain entries.", nameof(entries));
                ancestor = Parent(ancestor);
            }
        }
        this.entries = Array.AsReadOnly(copy);
        FileCount = copy.LongCount(e => e.IsFile);
        DirectoryCount = copy.LongCount(e => !e.IsFile);
        SourceBytes = copy.Where(e => e.IsFile).Sum(e => e.Size);
    }

    public IReadOnlyList<BackupManifestEntry> Entries => entries;
    public long FileCount { get; }
    public long DirectoryCount { get; }
    public long SourceBytes { get; }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains('\\'))
            throw new ArgumentException("Manifest paths must be normalized relative paths.");
        var parts = path.Split('/');
        if (parts.Any(p => p is "" or "." or "..") || path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Manifest paths must be normalized relative paths.");
        return path;
    }

    private static string? Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[..slash];
    }
}
