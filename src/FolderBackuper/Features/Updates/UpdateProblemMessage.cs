namespace FolderBackuper.Features.Updates;

/// <summary>
/// Why a look at the release feed could not answer, as a code rather than a sentence.
/// </summary>
/// <remarks>
/// These reach the settings page, which reports an inconclusive check without claiming anything about
/// versions, so they are carried as codes for the same reason every other displayed message is. Member
/// names are resource keys by the <c>UpdateProblemMessage_Member</c> rule.
/// </remarks>
public enum UpdateProblemMessage
{
    Timeout,
    ConnectionLost,
    RateLimited,
    UnexpectedStatus,
    UnreadableResponse,
    EmptyResponse,
    TagIsNotAVersion,
    Unreachable
}
