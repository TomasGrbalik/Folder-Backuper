namespace FolderBackuper.Infrastructure.Database;

/// <summary>
/// The outcome of queueing a manual run or requesting a cancellation, as a code rather than a sentence.
/// </summary>
/// <remarks>Member names are resource keys by the <c>RunOperationMessage_Member</c> rule.</remarks>
public enum RunOperationMessage
{
    JobOrDestinationUnavailable,
    WorkAlreadyPending,
    Queued,
    RunNotFound,
    RunAlreadyFinished,
    FinalizationStarted,
    QueuedRunCancelled,
    CancellationRequested
}
