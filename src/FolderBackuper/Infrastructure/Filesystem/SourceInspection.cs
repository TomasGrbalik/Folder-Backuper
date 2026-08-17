using System.Security;

namespace FolderBackuper.Infrastructure.Filesystem;

public static class SourceInspection
{
    public static IReadOnlyList<SourceRoot> GetEligibleRoots()
    {
        var roots = new List<SourceRoot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    var name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel;
                    roots.Add(new(name, drive.RootDirectory.FullName, drive.DriveType));
                }
            }
            catch (Exception exception) when (IsFilesystemException(exception))
            {
                // A drive can disappear or become unavailable while roots are being inspected.
            }
        }

        return roots
            .OrderBy(root => root.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(root => root.FullPath, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string ValidateBrowsableDirectory(string? requestedPath)
    {
        var normalizedPrefix = requestedPath?.Replace('/', '\\');
        if (normalizedPrefix?.StartsWith(@"\\?\", StringComparison.Ordinal) == true ||
            normalizedPrefix?.StartsWith(@"\\.\", StringComparison.Ordinal) == true ||
            normalizedPrefix?.StartsWith(@"\??\", StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Device paths are not supported.", nameof(requestedPath));
        }

        var validation = WindowsPath.Local(requestedPath);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Error, nameof(requestedPath));
        }

        var path = validation.Path!;
        var rootPath = Path.GetPathRoot(path)!;
        try
        {
            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                throw new ArgumentException("The source must be on an attached fixed or removable drive.", nameof(requestedPath));
            }

            if (!Directory.Exists(path))
            {
                throw new ArgumentException("The source directory does not exist or is unavailable.", nameof(requestedPath));
            }

            EnsurePathDoesNotTraverseReparsePoint(path, rootPath);
            return path;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (IsFilesystemException(exception))
        {
            throw new ArgumentException("The source directory is invalid or unavailable.", nameof(requestedPath), exception);
        }
    }

    internal static bool IsFilesystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    internal static string Problem(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => "Access was denied.",
        FileNotFoundException or DirectoryNotFoundException => "The entry is no longer available.",
        PathTooLongException => "The path is too long.",
        _ => "Filesystem metadata could not be read."
    };

    private static void EnsurePathDoesNotTraverseReparsePoint(string path, string rootPath)
    {
        var current = Path.TrimEndingDirectorySeparator(rootPath);
        EnsureNotReparsePoint(current);

        var relative = Path.GetRelativePath(rootPath, path);
        if (relative == ".") return;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException("Reparse points may be reported but cannot be traversed.", nameof(path));
        }
    }
}
