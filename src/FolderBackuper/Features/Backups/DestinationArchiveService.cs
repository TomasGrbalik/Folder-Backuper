using System.ComponentModel;
using System.Diagnostics;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;

namespace FolderBackuper.Features.Backups;

public interface IBackupCommitCoordinator
{
    ValueTask BeginCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken);
    ValueTask MarkCommittedAsync(Guid runId, CancellationToken cancellationToken);
}

public sealed class DirectBackupCommitCoordinator : IBackupCommitCoordinator
{
    public ValueTask BeginCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkCommittedAsync(Guid runId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class DurableBackupCommitCoordinator(RunPersistenceService runs) : IBackupCommitCoordinator
{
    public ValueTask BeginCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken) =>
        new(runs.BeginFinalCommitAsync(intent, cancellationToken));

    public ValueTask MarkCommittedAsync(Guid runId, CancellationToken cancellationToken) =>
        new(runs.MarkFinalCommittedAsync(runId, cancellationToken));
}

public sealed record BackupTransferProgress(long BytesTransferred, long TotalBytes);

public sealed record DestinationArchiveResult(
    string? FinalPath,
    string? FinalFileName,
    long ArchiveBytes,
    TimeSpan TransferDuration,
    bool CommitStarted,
    IReadOnlyList<BackupProblem> Problems)
{
    public bool Succeeded => FinalPath is not null && Problems.All(problem => problem.Severity != BackupProblemSeverity.Error);
}

public sealed class DestinationArchiveService(
    ZipArchiveService zipArchives,
    IBackupCommitCoordinator commitCoordinator)
{
    private const int CopyBufferSize = 128 * 1024;

    public async Task<DestinationArchiveResult> TransferAsync(
        IDestinationAdapter adapter,
        DestinationAccessConfiguration configuration,
        string effectiveDestinationPath,
        string stagingPath,
        string jobName,
        string topLevelFolder,
        BackupManifest manifest,
        ArchiveOwnership ownership,
        DateTimeOffset archiveInstant,
        Action<BackupTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var expectedLength = new FileInfo(stagingPath).Length;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await adapter.ExecuteAsync(configuration, async () =>
            {
                var partialPath = Path.Combine(effectiveDestinationPath,
                    $".folder-backuper-{ownership.RunId:N}-{Guid.NewGuid():N}.zip.partial");
                var partialCreated = false;
                var commitStarted = false;
                try
                {
                    await using (var input = new FileStream(stagingPath, FileMode.Open, FileAccess.Read,
                                     FileShare.Read, CopyBufferSize,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, CopyBufferSize,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                    {
                        partialCreated = true;
                        var buffer = new byte[CopyBufferSize];
                        long transferred = 0;
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, cancellationToken);
                            if (read == 0) break;
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            transferred += read;
                            progress?.Invoke(new(transferred, expectedLength));
                        }

                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }

                    var actualLength = new FileInfo(partialPath).Length;
                    if (actualLength != expectedLength)
                    {
                        var problems = new List<BackupProblem>
                        {
                            InvalidArchive(partialPath, "The destination partial length does not match the staging archive.")
                        };
                        CleanupPartial(partialPath, partialCreated, problems);
                        return Failed(expectedLength, stopwatch.Elapsed, false, problems);
                    }

                    var validation = await zipArchives.ValidateAsync(partialPath, topLevelFolder, manifest,
                        ownership, RunPhase.Finalizing, cancellationToken);
                    if (validation.Any(problem => problem.Severity == BackupProblemSeverity.Error))
                    {
                        var problems = validation.ToList();
                        CleanupPartial(partialPath, partialCreated, problems);
                        return Failed(expectedLength, stopwatch.Elapsed, false, problems);
                    }

                    var finalFileName = ArchiveFileName.Create(jobName, archiveInstant, ownership.RunId);
                    var finalPath = Path.Combine(effectiveDestinationPath, finalFileName);
                    ValidateContainment(configuration.RootPath, effectiveDestinationPath, finalPath);

                    string? filesystemIdentity = null;
                    try
                    {
                        filesystemIdentity = WindowsFilesystemInterop.GetIdentity(partialPath).ToString();
                    }
                    catch (IOException)
                    {
                        // NAS providers may not expose a stable file identity; length and ZIP ownership remain required.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // NAS providers may not expose a stable file identity; length and ZIP ownership remain required.
                    }
                    catch (Win32Exception)
                    {
                        // NAS providers may not expose a stable file identity; length and ZIP ownership remain required.
                    }

                    await commitCoordinator.BeginCommitAsync(new(
                        ownership.RunId,
                        partialPath,
                        effectiveDestinationPath,
                        finalFileName,
                        expectedLength,
                        archiveInstant,
                        filesystemIdentity), cancellationToken);
                    commitStarted = true;
                    File.Move(partialPath, finalPath, overwrite: false);
                    partialCreated = false;
                    await commitCoordinator.MarkCommittedAsync(ownership.RunId, CancellationToken.None);
                    return new(finalPath, finalFileName, expectedLength, stopwatch.Elapsed, true, validation);
                }
                catch (OperationCanceledException exception) when (!commitStarted)
                {
                    var cleanupProblems = new List<BackupProblem>();
                    CleanupPartial(partialPath, partialCreated, cleanupProblems);
                    if (cleanupProblems.Count > 0)
                    {
                        throw new BackupOperationCanceledException(
                            cleanupProblems.AsReadOnly(), exception, cancellationToken);
                    }
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
                {
                    var problems = new List<BackupProblem> { ClassifyDestinationFailure(exception, partialPath) };
                    CleanupPartial(partialPath, partialCreated, problems);
                    return Failed(expectedLength, stopwatch.Elapsed, commitStarted, problems);
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Failed(expectedLength, stopwatch.Elapsed, false,
                [ClassifyDestinationFailure(exception, effectiveDestinationPath)]);
        }
    }

    private static void ValidateContainment(string rootPath, string effectivePath, string finalPath)
    {
        var resolvedRoot = PathOverlap.ResolveExisting(rootPath);
        var resolvedEffective = PathOverlap.ResolveExisting(effectivePath);
        if (!PathOverlap.IsSameOrDescendant(resolvedEffective, resolvedRoot) ||
            !PathOverlap.IsSameOrDescendant(finalPath, effectivePath))
        {
            throw new ArgumentException("The final archive path resolves outside the destination root.", nameof(finalPath));
        }
    }

    private static DestinationArchiveResult Failed(
        long archiveBytes,
        TimeSpan transferDuration,
        bool commitStarted,
        IReadOnlyList<BackupProblem> problems) =>
        new(null, null, archiveBytes, transferDuration, commitStarted, problems);

    private static BackupProblem ClassifyDestinationFailure(Exception exception, string path)
    {
        var code = exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF;
        var category = code switch
        {
            39 or 112 => BackupProblemCategory.DestinationInsufficientSpace,
            5 or 32 or 33 => BackupProblemCategory.DestinationInaccessible,
            3 or 53 or 64 or 67 or 121 or 1231 => BackupProblemCategory.DestinationUnavailable,
            _ when exception is ArgumentException => BackupProblemCategory.InvalidPath,
            _ => BackupProblemCategory.GeneralIo
        };
        var message = category switch
        {
            BackupProblemCategory.DestinationInsufficientSpace => "The destination has insufficient free space.",
            BackupProblemCategory.DestinationInaccessible => "Access to the destination archive was denied.",
            BackupProblemCategory.DestinationUnavailable => "The destination became unavailable.",
            BackupProblemCategory.InvalidPath => "The destination archive path is invalid or unsafe.",
            _ => "The destination archive operation failed."
        };
        return new(BackupProblemSeverity.Error, category, RunPhase.Transferring,
            "Transfer destination archive", message, path, code);
    }

    private static BackupProblem InvalidArchive(string path, string message) => new(
        BackupProblemSeverity.Error,
        BackupProblemCategory.InvalidArchive,
        RunPhase.Finalizing,
        "Validate destination archive",
        message,
        path);

    private static void CleanupPartial(string path, bool created, List<BackupProblem>? problems)
    {
        if (!created || !File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems?.Add(new(BackupProblemSeverity.Warning, BackupProblemCategory.CleanupFailed,
                RunPhase.Finalizing, "Clean destination partial",
                "The incomplete destination archive could not be removed.", path, exception.HResult & 0xFFFF));
        }
    }
}
