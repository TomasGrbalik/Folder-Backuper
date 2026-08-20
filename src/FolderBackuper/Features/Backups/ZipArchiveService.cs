using System.Diagnostics;
using System.IO.Compression;
using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Features.Backups;

public sealed record LocalArchiveResult(
    string? StagingPath,
    long ArchiveBytes,
    TimeSpan CompressionDuration,
    IReadOnlyList<BackupProblem> Problems)
{
    public bool Succeeded => StagingPath is not null && Problems.All(problem => problem.Severity != BackupProblemSeverity.Error);
}

public sealed class ZipArchiveService
{
    private const int CopyBufferSize = 128 * 1024;

    public async Task<LocalArchiveResult> CreateAsync(
        string sourcePath,
        string stagingRoot,
        string topLevelFolder,
        BackupManifest manifest,
        ArchiveOwnership ownership,
        Action<BackupCopyProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? exactStagingPath = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var stagingPath = exactStagingPath ?? Path.Combine(stagingRoot,
            $".folder-backuper-{ownership.RunId:N}-{Guid.NewGuid():N}.zip.tmp");
        var created = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var entryNames = ArchivePathLayout.CreateEntryNames(topLevelFolder, manifest.Entries);
            await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                archive.Comment = ownership.Format();
                archive.CreateEntry(ArchivePathLayout.CreateTopLevelName(topLevelFolder), CompressionLevel.NoCompression);

                long filesProcessed = 0;
                long bytesProcessed = 0;
                for (var index = 0; index < manifest.Entries.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = manifest.Entries[index];
                    var entryName = entryNames[index];
                    if (!item.IsFile)
                    {
                        archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                        continue;
                    }

                    var sourceFile = Path.Combine(sourcePath,
                        item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        EnsureMetadataMatches(sourceFile, item);
                    }
                    catch (SourceChangedException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new SourceReadException(sourceFile, exception);
                    }
                    var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
                    FileStream input;
                    try
                    {
                        input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                            FileShare.Read, CopyBufferSize,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new SourceReadException(sourceFile, exception);
                    }

                    await using (input)
                    await using (var entryStream = entry.Open())
                    {
                        var buffer = new byte[CopyBufferSize];
                        while (true)
                        {
                            int read;
                            try
                            {
                                read = await input.ReadAsync(buffer, cancellationToken);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                throw new SourceReadException(sourceFile, exception);
                            }
                            if (read == 0) break;
                            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            bytesProcessed += read;
                            progress?.Invoke(new(filesProcessed, bytesProcessed, output.Position, item.RelativePath));
                        }
                    }

                    try
                    {
                        EnsureMetadataMatches(sourceFile, item);
                    }
                    catch (SourceChangedException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new SourceReadException(sourceFile, exception);
                    }
                    filesProcessed++;
                    progress?.Invoke(new(filesProcessed, bytesProcessed, output.Position, item.RelativePath));
                }
            }

            var length = new FileInfo(stagingPath).Length;
            progress?.Invoke(new(manifest.FileCount, manifest.SourceBytes, length, null));
            return new(stagingPath, length, stopwatch.Elapsed, []);
        }
        catch (OperationCanceledException exception)
        {
            var cleanupProblems = new List<BackupProblem>();
            Cleanup(stagingPath, created, cleanupProblems);
            if (cleanupProblems.Count > 0)
            {
                throw new BackupOperationCanceledException(cleanupProblems.AsReadOnly(), exception, cancellationToken);
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            var problems = new List<BackupProblem>
            {
                ClassifyCreationFailure(exception, sourcePath, stagingRoot)
            };
            Cleanup(stagingPath, created, problems);
            return new(null, 0, stopwatch.Elapsed, problems.AsReadOnly());
        }
    }

    public async Task<IReadOnlyList<BackupProblem>> ValidateAsync(
        string archivePath,
        string topLevelFolder,
        BackupManifest manifest,
        ArchiveOwnership ownership,
        RunPhase phase,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArchiveOwnership.TryParse(archive.Comment, out var foundOwnership) || foundOwnership != ownership)
            {
                return [InvalidArchive(phase, archivePath, BackupProblemMessage.OwnershipCommentInvalid)];
            }

            var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                [ArchivePathLayout.CreateTopLevelName(topLevelFolder)] = 0
            };
            var names = ArchivePathLayout.CreateEntryNames(topLevelFolder, manifest.Entries);
            for (var index = 0; index < manifest.Entries.Count; index++)
            {
                expected.Add(names[index], manifest.Entries[index].IsFile ? manifest.Entries[index].Size : 0);
            }

            if (archive.Entries.Count != expected.Count)
            {
                return [InvalidArchive(phase, archivePath, BackupProblemMessage.EntryCountMismatch)];
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var expectedLength) ||
                    entry.Length != expectedLength)
                {
                    return [InvalidArchive(phase, archivePath, BackupProblemMessage.EntriesMismatch)];
                }
            }

            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return [new(
                BackupProblemSeverity.Error,
                BackupProblemCategory.InvalidArchive,
                phase,
                BackupOperation.ValidateZipArchive,
                BackupProblemMessage.ZipValidationFailed,
                archivePath,
                exception.HResult & 0xFFFF)];
        }
    }

    private static void EnsureMetadataMatches(string path, BackupManifestEntry expected)
    {
        var info = new FileInfo(path);
        var attributes = File.GetAttributes(path);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (!info.Exists || info.Length != expected.Size || modified != expected.LastWriteTime || attributes != expected.Attributes)
        {
            throw new SourceChangedException(path);
        }
    }

    private static BackupProblem ClassifyCreationFailure(Exception exception, string sourcePath, string stagingRoot)
    {
        if (exception is SourceChangedException changed)
        {
            return new(BackupProblemSeverity.Error, BackupProblemCategory.SourceChanged,
                RunPhase.Compressing, BackupOperation.ReadSourceFile, BackupProblemMessage.SourceFileChanged, changed.SourcePath);
        }

        if (exception is SourceReadException sourceRead)
        {
            var denied = sourceRead.InnerException is UnauthorizedAccessException ||
                (sourceRead.InnerException?.HResult & 0xFFFF) is 5 or 32 or 33;
            return new(BackupProblemSeverity.Error,
                denied ? BackupProblemCategory.SourceInaccessible : BackupProblemCategory.SourceUnavailable,
                RunPhase.Compressing,
                BackupOperation.ReadSourceFile,
                denied ? BackupProblemMessage.SourceFileAccessDenied : BackupProblemMessage.SourceFileUnreadable,
                sourceRead.SourcePath,
                sourceRead.InnerException?.HResult & 0xFFFF);
        }

        var code = exception.HResult & 0xFFFF;
        var category = code is 39 or 112
            ? BackupProblemCategory.StagingInsufficientSpace
            : exception is UnauthorizedAccessException
                ? BackupProblemCategory.StagingInaccessible
                : BackupProblemCategory.GeneralIo;
        return new(BackupProblemSeverity.Error, category, RunPhase.Compressing,
            BackupOperation.CreateStagingArchive,
            category == BackupProblemCategory.StagingInsufficientSpace
                ? BackupProblemMessage.StagingInsufficientSpace
                : BackupProblemMessage.StagingArchiveNotCreated,
            category == BackupProblemCategory.GeneralIo ? sourcePath : stagingRoot,
            code);
    }

    private static BackupProblem InvalidArchive(RunPhase phase, string path, BackupProblemMessage message) => new(
        BackupProblemSeverity.Error,
        BackupProblemCategory.InvalidArchive,
        phase,
        BackupOperation.ValidateZipArchive,
        message,
        path);

    private static void Cleanup(string path, bool created, List<BackupProblem>? problems)
    {
        if (!created || !File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems?.Add(new(BackupProblemSeverity.Warning, BackupProblemCategory.CleanupFailed,
                RunPhase.Compressing, BackupOperation.CleanStagingArchive,
                BackupProblemMessage.StagingArchiveNotRemoved, path, exception.HResult & 0xFFFF));
        }
    }

    private sealed class SourceChangedException(string sourcePath) : IOException
    {
        public string SourcePath { get; } = sourcePath;
    }

    private sealed class SourceReadException(string sourcePath, Exception innerException)
        : IOException("The source file could not be read.", innerException)
    {
        public string SourcePath { get; } = sourcePath;
    }
}
