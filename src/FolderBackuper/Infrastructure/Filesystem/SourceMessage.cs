using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Infrastructure.Filesystem;

/// <summary>
/// Why a source folder or entry could not be read or browsed.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>SourceMessage_Member</c> rule. Reasons that a path is
/// malformed rather than unreachable belong to <see cref="PathMessage"/> and are reused from there
/// rather than duplicated, so the same rejection reads identically wherever it surfaces.
/// </remarks>
public enum SourceMessage
{
    AccessDenied,
    EntryUnavailable,
    PathTooLong,
    MetadataUnreadable,
    DriveNotFixedOrRemovable,
    DirectoryMissing,
    DirectoryInvalid,
    ReparsePointNotTraversable
}

/// <summary>
/// A source path was rejected, carrying the reason as a message code rather than as a sentence.
/// </summary>
/// <remarks>
/// Exists so that the browse and preview dialogs can render a rejection in the reading language. They
/// previously displayed <c>Exception.Message</c> directly, which was both untranslatable and a way for
/// incidental diagnostic detail to reach the interface.
/// </remarks>
public sealed class SourcePathException(UiMessage reason, string? parameterName = null, Exception? innerException = null)
    : ArgumentException(reason.Key, parameterName, innerException)
{
    public UiMessage Reason { get; } = reason;
}
