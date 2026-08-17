using System.Text.RegularExpressions;

namespace FolderBackuper.Features.Backups;

public sealed record ArchiveOwnership(Guid InstallationId, Guid RunId)
{
    private const string Prefix = "FolderBackuper-Archive;v=1;";

    public string Format() => $"{Prefix}installation={InstallationId:D};run={RunId:D}";

    public static bool TryParse(string? comment, out ArchiveOwnership ownership)
    {
        ownership = null!;
        if (comment is null || !comment.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var match = Regex.Match(comment[Prefix.Length..], "^installation=([0-9a-fA-F-]{36});run=([0-9a-fA-F-]{36})$");
        return match.Success && Guid.TryParseExact(match.Groups[1].Value, "D", out var installation)
            && Guid.TryParseExact(match.Groups[2].Value, "D", out var run)
            && (ownership = new ArchiveOwnership(installation, run)) is not null;
    }
}

public enum BackupProblemCategory
{
    SourceUnavailable, SourceInaccessible, SourceChanged, SkippedReparsePoint,
    StagingUnavailable, StagingInaccessible, StagingInsufficientSpace,
    DestinationUnavailable, DestinationInaccessible, DestinationInsufficientSpace,
    InvalidArchive, InvalidPath, CleanupFailed, Cancelled, GeneralIo
}

public enum BackupProblemSeverity
{
    Warning,
    Error
}

public sealed record BackupProblem(
    BackupProblemSeverity Severity,
    BackupProblemCategory Category,
    RunPhase Phase,
    string Operation,
    string Message,
    string? Path = null,
    int? NativeErrorCode = null);

public static class ArchivePathLayout
{
    public static string CreateEntryName(string topLevelFolder, string relativePath, bool directory)
    {
        var top = SanitizeSegment(topLevelFolder);
        if (string.IsNullOrEmpty(top)) throw new ArgumentException("Top-level folder is invalid.", nameof(topLevelFolder));
        var path = ValidateRelative(relativePath);
        var result = top + "/" + path;
        return directory ? result.TrimEnd('/') + "/" : result;
    }

    public static IReadOnlyList<string> CreateEntryNames(string topLevelFolder, IEnumerable<BackupManifestEntry> entries)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var name = CreateEntryName(topLevelFolder, entry.RelativePath, !entry.IsFile);
            if (!seen.Add(name)) throw new ArgumentException("Manifest contains duplicate ZIP paths.", nameof(entries));
            var path = name.TrimEnd('/');
            if (!paths.Add(path) || (entry.IsFile && !files.Add(path)))
                throw new ArgumentException("Manifest contains conflicting ZIP paths.", nameof(entries));
            if (!entry.IsFile && files.Contains(path))
                throw new ArgumentException("A ZIP file cannot also be a directory.", nameof(entries));
            var ancestor = Parent(path);
            while (ancestor is not null)
            {
                if (files.Contains(ancestor)) throw new ArgumentException("A ZIP file cannot contain entries.", nameof(entries));
                ancestor = Parent(ancestor);
            }
            result.Add(name);
        }
        foreach (var file in files)
            if (paths.Any(path => path.StartsWith(file + "/", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A ZIP file cannot contain entries.", nameof(entries));
        return result.AsReadOnly();
    }

    private static string? Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[..slash];
    }

    private static string ValidateRelative(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0') || value.Contains('\\'))
            throw new ArgumentException("Relative path is invalid.", nameof(value));
        var normalized = value;
        if (normalized.StartsWith('/') || normalized.Contains(":", StringComparison.Ordinal)) throw new ArgumentException("Relative path is rooted.", nameof(value));
        var segments = normalized.Split('/');
        if (segments.Any(s => s is "" or "." or "..")) throw new ArgumentException("Relative path contains unsafe segments.", nameof(value));
        return normalized;
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value is "." or ".." || value.Contains('/') || value.Contains('\\')) return "";
        return value.TrimEnd(' ', '.');
    }
}

public static class ArchiveFileName
{
    private const int MaximumLength = 180;
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).Distinct().ToArray();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
          // Win32 also treats the superscript forms of COM1-3 and LPT1-3 as device stems.
          "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };

    public static string Create(string jobName, DateTimeOffset instant, Guid runId)
    {
        var stamp = instant.ToUniversalTime().ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = $"_{stamp}_{runId.ToString("N")[..8]}.zip";
        var job = new string((jobName ?? "").Select(c => Invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        var stem = job.Split('.', 2)[0].TrimEnd(' ', '.');
        if (Reserved.Contains(stem)) job = "_" + job;
        if (string.IsNullOrWhiteSpace(job)) job = "job";
        var maxJob = Math.Max(1, MaximumLength - suffix.Length - 1);
        if (job.Length > maxJob) job = job[..maxJob].TrimEnd(' ', '.');
        return job + suffix;
    }
}
