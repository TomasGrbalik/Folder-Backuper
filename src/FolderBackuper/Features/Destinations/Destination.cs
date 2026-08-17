namespace FolderBackuper.Features.Destinations;

public enum DestinationType
{
    Local,
    Smb
}

public enum DestinationLifecycle
{
    Active,
    Archived
}

public enum DestinationVerificationResult
{
    Unverified,
    Succeeded,
    Failed
}

public enum DestinationAccessResult
{
    NotAttempted,
    Succeeded,
    Unavailable,
    InvalidPath,
    AccessDenied,
    CleanupFailed,
    Failed
}

public enum DestinationAccessSource
{
    Management,
    Backup,
    Inventory
}

public sealed class Destination
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public DestinationType Type { get; set; }
    public required string RootPath { get; set; }
    public string? SmbUsername { get; set; }
    public byte[]? ProtectedPassword { get; set; }
    public string? VerificationFingerprint { get; set; }
    public DestinationVerificationResult VerificationResult { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public DestinationLifecycle Lifecycle { get; private set; } = DestinationLifecycle.Active;
    public DestinationAccessResult LastAccessResult { get; set; }
    public DestinationAccessSource? LastAccessSource { get; set; }
    public DateTimeOffset? LastAccessedAtUtc { get; set; }
    public string? LastAccessErrorSummary { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public void Archive() => Lifecycle = Lifecycle switch
    {
        DestinationLifecycle.Active => DestinationLifecycle.Archived,
        _ => throw InvalidTransition(nameof(DestinationLifecycle.Archived))
    };

    public void Restore() => Lifecycle = Lifecycle switch
    {
        DestinationLifecycle.Archived => DestinationLifecycle.Active,
        _ => throw InvalidTransition(nameof(DestinationLifecycle.Active))
    };

    private InvalidOperationException InvalidTransition(string target) =>
        new($"Destination {Id} cannot transition from {Lifecycle} to {target}.");
}
