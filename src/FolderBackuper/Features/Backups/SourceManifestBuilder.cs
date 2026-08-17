using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Features.Backups;

public sealed record SourceManifestScanResult(
    BackupManifest? Manifest,
    IReadOnlyList<BackupProblem> Problems)
{
    public bool CanProceed => Manifest is not null && Problems.All(problem => problem.Severity != BackupProblemSeverity.Error);
}

public sealed class SourceManifestBuilder
{
    public Task<SourceManifestScanResult> BuildAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(sourcePath, cancellationToken), cancellationToken);

    public IReadOnlyList<BackupProblem> Compare(BackupManifest expected, BackupManifest actual)
    {
        var problems = new List<BackupProblem>();
        var expectedByPath = expected.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualByPath = actual.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in expected.Entries)
        {
            if (!actualByPath.TryGetValue(entry.RelativePath, out var found))
            {
                problems.Add(Changed(entry.RelativePath, "The source entry was removed during backup."));
            }
            else if (entry.Type != found.Type || entry.Size != found.Size ||
                     entry.LastWriteTime != found.LastWriteTime || entry.Attributes != found.Attributes)
            {
                problems.Add(Changed(entry.RelativePath, "The source entry changed during backup."));
            }
        }

        foreach (var entry in actual.Entries.Where(entry => !expectedByPath.ContainsKey(entry.RelativePath)))
        {
            problems.Add(Changed(entry.RelativePath, "The source entry was added during backup."));
        }

        return problems;
    }

    private static SourceManifestScanResult Build(string sourcePath, CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = SourceInspection.ValidateBrowsableDirectory(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(null, [Problem(sourcePath, "Validate source", exception)]);
        }

        var entries = new List<BackupManifestEntry>();
        var problems = new List<BackupProblem>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerator<string>? enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false
                }).GetEnumerator();

                while (MoveNext(enumerator, directory, problems))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = enumerator.Current;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                        if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            problems.Add(new(
                                BackupProblemSeverity.Warning,
                                BackupProblemCategory.SkippedReparsePoint,
                                RunPhase.Scanning,
                                "Skip reparse point",
                                "A reparse point was skipped and was not included in the backup.",
                                relative));
                            continue;
                        }

                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            var info = new DirectoryInfo(path);
                            entries.Add(new(relative, BackupManifestEntryType.Directory, 0,
                                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), attributes));
                            pending.Push(path);
                        }
                        else
                        {
                            var info = new FileInfo(path);
                            entries.Add(new(relative, BackupManifestEntryType.File, info.Length,
                                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), attributes));
                        }
                    }
                    catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
                    {
                        problems.Add(Problem(path, "Read source metadata", exception));
                    }
                }
            }
            catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
            {
                problems.Add(Problem(directory, "Enumerate source directory", exception));
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        if (problems.Any(problem => problem.Severity == BackupProblemSeverity.Error))
        {
            return new(null, problems.AsReadOnly());
        }

        try
        {
            return new(new BackupManifest(entries), problems.AsReadOnly());
        }
        catch (ArgumentException exception)
        {
            problems.Add(new(BackupProblemSeverity.Error, BackupProblemCategory.InvalidPath,
                RunPhase.Scanning, "Build source manifest", exception.Message));
            return new(null, problems.AsReadOnly());
        }
    }

    private static bool MoveNext(IEnumerator<string> enumerator, string directory, List<BackupProblem> problems)
    {
        try
        {
            return enumerator.MoveNext();
        }
        catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
        {
            problems.Add(Problem(directory, "Enumerate source directory", exception));
            return false;
        }
    }

    private static BackupProblem Problem(string path, string operation, Exception exception) => new(
        BackupProblemSeverity.Error,
        exception is UnauthorizedAccessException or System.Security.SecurityException
            ? BackupProblemCategory.SourceInaccessible
            : BackupProblemCategory.SourceUnavailable,
        RunPhase.Scanning,
        operation,
        SourceInspection.Problem(exception),
        path,
        exception.HResult & 0xFFFF);

    private static BackupProblem Changed(string path, string message) => new(
        BackupProblemSeverity.Error,
        BackupProblemCategory.SourceChanged,
        RunPhase.Compressing,
        "Compare source manifest",
        message,
        path);
}
