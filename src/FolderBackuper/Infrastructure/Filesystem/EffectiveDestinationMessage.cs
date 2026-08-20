namespace FolderBackuper.Infrastructure.Filesystem;

/// <summary>
/// The outcome of resolving a job's effective destination folder, as a code rather than a sentence.
/// </summary>
/// <remarks>
/// Member names are resource keys by the <c>EffectiveDestinationMessage_Member</c> rule. A rejected
/// subfolder reuses <see cref="PathMessage"/> rather than restating it, so the same malformed path reads
/// identically in the destination form and in the job form.
/// </remarks>
public enum EffectiveDestinationMessage
{
    ReadyExisting,
    ReadyPathValid,
    EffectivePathInvalid,
    MustRemainInsideRoot,
    RootMustExistFirst,
    ResolvesOutsideRoot,
    OverlapsSource,
    AccessFailed
}
