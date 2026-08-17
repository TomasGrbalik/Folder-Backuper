using System.Text.Json;

namespace FolderBackuper.Infrastructure.Filesystem;

public sealed class SourceBrowser
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;
    public const int MaxOffset = 10_000;

    public IReadOnlyList<SourceRoot> GetRoots() => SourceInspection.GetEligibleRoots();

    public Task<SourceBrowseResult> BrowseAsync(SourceBrowseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Browse(request, cancellationToken), cancellationToken);
    }

    private static SourceBrowseResult Browse(SourceBrowseRequest request, CancellationToken cancellationToken)
    {
        var path = SourceInspection.ValidateBrowsableDirectory(request.Path);
        if (request.PageSize <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Page size must be positive.");

        var pageSize = Math.Min(request.PageSize, MaxPageSize);
        var continuation = ResolveContinuation(request);
        var offset = continuation is null ? request.Offset : 0;
        var retainedLimit = checked(offset + pageSize + 1);
        var entries = new SortedSet<SourceEntry>(SourceEntryComparer.Instance);
        string? accessProblem = null;

        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = GetEntryType(info);
                if (continuation is not null && CompareKey(type, info.Name, continuation.Type, continuation.Name) <= 0)
                {
                    continue;
                }

                entries.Add(ReadEntry(info, type));
                if (entries.Count > retainedLimit)
                {
                    entries.Remove(entries.Max!);
                }
            }
        }
        catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
        {
            accessProblem = SourceInspection.Problem(exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var page = entries.Skip(offset).Take(pageSize).ToArray();
        var hasMore = entries.Count > offset + page.Length;
        var nextOffset = hasMore ? request.Offset + page.Length : (int?)null;
        var nextContinuation = hasMore && page.Length != 0 ? EncodeContinuation(page[^1]) : null;

        return new(path, request.Offset, pageSize, page, nextOffset, nextContinuation, accessProblem);
    }

    private static ContinuationKey? ResolveContinuation(SourceBrowseRequest request)
    {
        if (request.Offset < 0) throw new ArgumentOutOfRangeException(nameof(request), "Offset cannot be negative.");
        if (request.Offset > MaxOffset) throw new ArgumentOutOfRangeException(nameof(request), $"Offset cannot exceed {MaxOffset}.");
        if (request.Continuation is null) return null;
        if (request.Offset != 0) throw new ArgumentException("Specify either an offset or continuation, not both.", nameof(request));

        try
        {
            var bytes = Convert.FromBase64String(request.Continuation.Replace('-', '+').Replace('_', '/').PadRight((request.Continuation.Length + 3) / 4 * 4, '='));
            var key = JsonSerializer.Deserialize<ContinuationKey>(bytes);
            if (key is null || key.Version != 1 || !Enum.IsDefined(key.Type) || string.IsNullOrEmpty(key.Name))
            {
                throw new JsonException();
            }

            return key;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(request), exception);
        }
    }

    private static string EncodeContinuation(SourceEntry entry)
    {
        var value = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ContinuationKey(1, entry.EntryType, entry.Name)));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static SourceEntryType GetEntryType(FileSystemInfo info) =>
        info is DirectoryInfo ? SourceEntryType.Directory : info is FileInfo ? SourceEntryType.File : SourceEntryType.Unknown;

    private static SourceEntry ReadEntry(FileSystemInfo info, SourceEntryType type)
    {
        try
        {
            info.Refresh();
            var attributes = info.Attributes;
            var isReparse = attributes.HasFlag(FileAttributes.ReparsePoint);
            long? size = type == SourceEntryType.File && !isReparse ? ((FileInfo)info).Length : null;
            return new(info.Name, info.FullName, type, size, info.LastWriteTimeUtc, null,
                attributes.HasFlag(FileAttributes.Hidden), attributes.HasFlag(FileAttributes.System), isReparse);
        }
        catch (Exception exception) when (SourceInspection.IsFilesystemException(exception))
        {
            return new(info.Name, info.FullName, type, null, null, SourceInspection.Problem(exception), false, false, false);
        }
    }

    private static int CompareKey(SourceEntryType leftType, string leftName, SourceEntryType rightType, string rightName)
    {
        var comparison = TypeOrder(leftType).CompareTo(TypeOrder(rightType));
        if (comparison != 0) return comparison;
        comparison = StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(leftName, rightName);
    }

    private static int TypeOrder(SourceEntryType type) => type switch
    {
        SourceEntryType.Directory => 0,
        SourceEntryType.File => 1,
        _ => 2
    };

    private sealed record ContinuationKey(int Version, SourceEntryType Type, string Name);

    private sealed class SourceEntryComparer : IComparer<SourceEntry>
    {
        public static SourceEntryComparer Instance { get; } = new();

        public int Compare(SourceEntry? left, SourceEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var comparison = CompareKey(left.EntryType, left.Name, right.EntryType, right.Name);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.FullPath, right.FullPath);
        }
    }
}
