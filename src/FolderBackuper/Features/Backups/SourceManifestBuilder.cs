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
                problems.Add(Changed(entry.RelativePath, BackupProblemMessage.SourceEntryRemoved));
            }
            else if (entry.Type != found.Type || entry.Size != found.Size ||
                     entry.LastWriteTime != found.LastWriteTime || entry.Attributes != found.Attributes)
            {
                problems.Add(Changed(entry.RelativePath, BackupProblemMessage.SourceEntryChanged));
            }
        }

        foreach (var entry in actual.Entries.Where(entry => !expectedByPath.ContainsKey(entry.RelativePath)))
        {
            problems.Add(Changed(entry.RelativePath, BackupProblemMessage.SourceEntryAdded));
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
            return new(null, [Problem(sourcePath, BackupOperation.ValidateSource, exception)]);
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
                                BackupOperation.SkipReparsePoint,
                                BackupProblemMessage.ReparsePointSkipped,
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
                        problems.Add(Problem(path, BackupOperation.ReadSourceMetadata, exception));
                    }
                }
            }
            catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
            {
                problems.Add(Problem(directory, BackupOperation.EnumerateSourceDirectory, exception));
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
        catch (ArgumentException)
        {
            // The exception's own text used to be persisted here, which was both untranslatable and a
            // way for incidental detail to reach permanent history.
            problems.Add(new(BackupProblemSeverity.Error, BackupProblemCategory.InvalidPath,
                RunPhase.Scanning, BackupOperation.BuildSourceManifest, BackupProblemMessage.ManifestPathInvalid));
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
            problems.Add(Problem(directory, BackupOperation.EnumerateSourceDirectory, exception));
            return false;
        }
    }

    private static BackupProblem Problem(string path, BackupOperation operation, Exception exception) => new(
        BackupProblemSeverity.Error,
        exception is UnauthorizedAccessException or System.Security.SecurityException
            ? BackupProblemCategory.SourceInaccessible
            : BackupProblemCategory.SourceUnavailable,
        RunPhase.Scanning,
        operation,
        SourceInspection.Problem(exception),
        path,
        exception.HResult & 0xFFFF);

    private static BackupProblem Changed(string path, BackupProblemMessage message) => new(
        BackupProblemSeverity.Error,
        BackupProblemCategory.SourceChanged,
        RunPhase.Compressing,
        BackupOperation.CompareSourceManifest,
        message,
        path);
}
