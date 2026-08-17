namespace FolderBackuper.Features.Backups;

public enum ArtifactState
{
    PendingFinalization,
    Retained,
    RemovedByRetention,
    FoundMissing,
    Unmanaged
}

public enum FinalizationOperationState
{
    Pending,
    Completed,
    Failed
}

public enum RetentionOperationState
{
    None,
    PendingDeletion,
    Completed,
    Failed,
    OwnershipRefused
}

public sealed class BackupArtifact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RunId { get; init; }
    public BackupRun? Run { get; set; }
    public required string DestinationName { get; init; }
    public required string DestinationRootPath { get; init; }
    public required string EffectivePath { get; init; }
    public required string FinalFileName { get; init; }
    public long Size { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public ArtifactState State { get; private set; } = ArtifactState.PendingFinalization;
    public Guid OwnershipRunId { get; init; }
    public long OwnershipExpectedLength { get; init; }
    public DateTimeOffset? OwnershipCreatedAtUtc { get; init; }
    public string? OwnershipFileSystemIdentity { get; init; }
    public FinalizationOperationState FinalizationState { get; private set; } = FinalizationOperationState.Pending;
    public RetentionOperationState RetentionState { get; private set; }
    public DateTimeOffset StateChangedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void MarkRetained(DateTimeOffset now)
    {
        RequireState(ArtifactState.PendingFinalization);
        RequireFinalizationState(FinalizationOperationState.Pending);
        State = ArtifactState.Retained;
        FinalizationState = FinalizationOperationState.Completed;
        StateChangedAtUtc = now;
    }

    public void MarkFinalizationFailed(DateTimeOffset now)
    {
        RequireState(ArtifactState.PendingFinalization);
        RequireFinalizationState(FinalizationOperationState.Pending);
        FinalizationState = FinalizationOperationState.Failed;
        StateChangedAtUtc = now;
    }

    public void BeginRetentionDeletion(DateTimeOffset now)
    {
        RequireState(ArtifactState.Retained);
        if (RetentionState != RetentionOperationState.None)
        {
            throw InvalidTransition("pending retention deletion");
        }

        RetentionState = RetentionOperationState.PendingDeletion;
        StateChangedAtUtc = now;
    }

    public void MarkRemovedByRetention(DateTimeOffset now)
    {
        RequireState(ArtifactState.Retained);
        if (RetentionState != RetentionOperationState.PendingDeletion)
        {
            throw InvalidTransition(nameof(ArtifactState.RemovedByRetention));
        }

        State = ArtifactState.RemovedByRetention;
        RetentionState = RetentionOperationState.Completed;
        StateChangedAtUtc = now;
    }

    public void MarkRetentionFailed(bool ownershipRefused, DateTimeOffset now)
    {
        RequireState(ArtifactState.Retained);
        if (RetentionState != RetentionOperationState.PendingDeletion)
        {
            throw InvalidTransition("failed retention");
        }

        RetentionState = ownershipRefused
            ? RetentionOperationState.OwnershipRefused
            : RetentionOperationState.Failed;
        StateChangedAtUtc = now;
    }

    public void MarkMissing(DateTimeOffset now)
    {
        RequireState(ArtifactState.Retained);
        RequireNoPendingRetention();
        State = ArtifactState.FoundMissing;
        StateChangedAtUtc = now;
    }

    public void MarkUnmanaged(DateTimeOffset now)
    {
        RequireState(ArtifactState.Retained);
        RequireNoPendingRetention();
        State = ArtifactState.Unmanaged;
        StateChangedAtUtc = now;
    }

    private void RequireState(ArtifactState required)
    {
        if (State != required)
        {
            throw InvalidTransition(required.ToString());
        }
    }

    private void RequireFinalizationState(FinalizationOperationState required)
    {
        if (FinalizationState != required)
        {
            throw InvalidTransition(required.ToString());
        }
    }

    private void RequireNoPendingRetention()
    {
        if (RetentionState == RetentionOperationState.PendingDeletion)
        {
            throw InvalidTransition("inventory state");
        }
    }

    private InvalidOperationException InvalidTransition(string target) =>
        new($"Artifact {Id} cannot transition from {State}/{RetentionState} to {target}.");
}
