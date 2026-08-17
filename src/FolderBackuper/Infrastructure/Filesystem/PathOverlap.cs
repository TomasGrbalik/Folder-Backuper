namespace FolderBackuper.Infrastructure.Filesystem;

public static class PathOverlap
{
    public static bool IsSameOrDescendant(string candidate, string parent)
    {
        candidate = Path.TrimEndingDirectorySeparator(candidate);
        parent = Path.TrimEndingDirectorySeparator(parent);
        var parentPrefix = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Overlaps(string left, string right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    public static string? FindDestinationOverlap(string destination, IEnumerable<string> sources)
    {
        var destinationPath = ResolveExisting(destination);
        foreach (var source in sources)
        {
            var sourcePath = ResolveExisting(source);
            if (Overlaps(destination, source) || Overlaps(destinationPath, sourcePath))
            {
                return source;
            }
        }

        return null;
    }

    private static string ResolveExisting(string path)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var missingSegments = new Stack<string>();
        while (!Directory.Exists(current))
        {
            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                return path;
            }

            missingSegments.Push(Path.GetFileName(current));
            current = parent.FullName;
        }

        var resolved = WindowsFilesystemInterop.GetFinalPath(current);
        while (missingSegments.TryPop(out var segment))
        {
            resolved = Path.Combine(resolved, segment);
        }

        return resolved;
    }
}
