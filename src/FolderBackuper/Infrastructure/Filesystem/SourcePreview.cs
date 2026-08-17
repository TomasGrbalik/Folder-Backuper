using System.Runtime.CompilerServices;

namespace FolderBackuper.Infrastructure.Filesystem;

public sealed class SourcePreview
{
    public const int DefaultSnapshotInterval = 100;
    public const int DefaultInaccessibleSampleLimit = 20;

    public async IAsyncEnumerable<SourcePreviewSnapshot> InspectAsync(
        string path,
        int snapshotInterval = DefaultSnapshotInterval,
        int inaccessibleSampleLimit = DefaultInaccessibleSampleLimit,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshotInterval <= 0) throw new ArgumentOutOfRangeException(nameof(snapshotInterval));
        if (inaccessibleSampleLimit < 0) throw new ArgumentOutOfRangeException(nameof(inaccessibleSampleLimit));

        var sourcePath = SourceInspection.ValidateBrowsableDirectory(path);
        var directories = new Stack<string>();
        var inaccessibleSamples = new List<SourceAccessProblem>();
        directories.Push(sourcePath);
        long files = 0, folders = 0, bytes = 0, inaccessible = 0, skippedReparse = 0, inspected = 0;

        yield return Snapshot(isComplete: false);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            IEnumerator<FileSystemInfo>? enumerator = null;
            try
            {
                enumerator = new DirectoryInfo(directory).EnumerateFileSystemInfos().GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileSystemInfo info;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        info = enumerator.Current;
                    }
                    catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
                    {
                        AddProblem(directory, exception);
                        break;
                    }

                    try
                    {
                        info.Refresh();
                        var attributes = info.Attributes;
                        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                        var isReparse = attributes.HasFlag(FileAttributes.ReparsePoint);
                        if (isDirectory) folders++;
                        else files++;

                        if (isReparse)
                        {
                            skippedReparse++;
                        }
                        else if (isDirectory)
                        {
                            directories.Push(info.FullName);
                        }
                        else
                        {
                            bytes = checked(bytes + ((FileInfo)info).Length);
                        }
                    }
                    catch (Exception exception) when (SourceInspection.IsFilesystemException(exception) || exception is OverflowException)
                    {
                        AddProblem(info.FullName, exception);
                    }

                    inspected++;
                    if (inspected % snapshotInterval == 0)
                    {
                        yield return Snapshot(isComplete: false);
                        await Task.Yield();
                    }
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return Snapshot(isComplete: true);

        void AddProblem(string problemPath, Exception exception)
        {
            inaccessible++;
            if (inaccessibleSamples.Count < inaccessibleSampleLimit)
            {
                inaccessibleSamples.Add(new(problemPath, SourceInspection.Problem(exception)));
            }
        }

        SourcePreviewSnapshot Snapshot(bool isComplete) => new(
            sourcePath,
            files,
            folders,
            bytes,
            inaccessible,
            Array.AsReadOnly(inaccessibleSamples.ToArray()),
            skippedReparse,
            isComplete);
    }
}
