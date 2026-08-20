namespace FolderBackuper.Infrastructure.Filesystem;

/// <summary>
/// Why a path was rejected. Returned instead of a sentence so that the reason is rendered in the
/// language whoever is reading it selected, and so that a caller can react to a specific reason.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>PathMessage_Member</c> rule, so renaming one is a
/// translation change and adding one without translating it fails the resource completeness tests.
/// </remarks>
public enum PathMessage
{
    Required,
    DevicePathUnsupported,
    ParentTraversalUnsupported,
    LocalAbsoluteRequired,
    MappedNetworkDriveUnsupported,
    LocalPathUnavailable,
    UncRequired,
    UncNeedsServerAndShare,
    UncInvalid,
    SubfolderMustBeRelative
}
