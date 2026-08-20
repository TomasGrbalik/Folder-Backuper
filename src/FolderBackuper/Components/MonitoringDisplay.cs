using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Localization;
using FolderBackuper.Resources;
using MudBlazor;

namespace FolderBackuper.Components;

/// <summary>
/// Shared color and label semantics for run status across the dashboard, history, and calendar so past and
/// planned entries read consistently. Kept in the components layer because it maps domain enums to MudBlazor colors.
/// </summary>
/// <remarks>
/// Labels resolve through <see cref="EnumText"/>, which derives its resource key from the enumeration member,
/// so adding a member without translating it fails the resource completeness tests rather than surfacing an
/// English word in a Slovak interface. The states that have no member — no outcome yet, no artifact, nothing
/// sent — carry their own keys, because "none" is a sentence about absence rather than a member name.
/// </remarks>
public static class MonitoringDisplay
{
    public static Color OutcomeColor(RunOutcome? outcome) => outcome switch
    {
        RunOutcome.Successful => Color.Success,
        RunOutcome.SuccessfulWithWarnings => Color.Warning,
        RunOutcome.Failed => Color.Error,
        RunOutcome.Cancelled => Color.Default,
        _ => Color.Info
    };

    public static string OutcomeLabel(RunOutcome? outcome) =>
        outcome is { } present ? EnumText.For(present) : UiStrings.RunOutcomeInProgress;

    /// <summary>Status label for a run row, falling back to the phase for non-terminal runs.</summary>
    public static string StatusLabel(RunOutcome? outcome, RunPhase phase) =>
        outcome is not null ? OutcomeLabel(outcome) : PhaseLabel(phase);

    public static Color StatusColor(RunOutcome? outcome, RunPhase phase) =>
        outcome is not null ? OutcomeColor(outcome) : (phase == RunPhase.Queued ? Color.Default : Color.Info);

    public static string PhaseLabel(RunPhase phase) => EnumText.For(phase);

    /// <summary>Transfer verb shown during the transfer phase: local copies vs. SMB uploads.</summary>
    public static string TransferVerb(DestinationType type) =>
        type == DestinationType.Smb ? UiStrings.TransferVerbUploading : UiStrings.TransferVerbCopying;

    public static string TriggerLabel(RunTrigger trigger) => EnumText.For(trigger);

    public static Color ArtifactColor(ArtifactState? state) => state switch
    {
        ArtifactState.Retained => Color.Success,
        ArtifactState.RemovedByRetention => Color.Default,
        ArtifactState.FoundMissing => Color.Error,
        ArtifactState.Unmanaged => Color.Warning,
        ArtifactState.PendingFinalization => Color.Info,
        _ => Color.Default
    };

    public static string ArtifactLabel(ArtifactState? state) =>
        EnumText.For(state, UiStrings.ArtifactStateNone);

    public static Color NotificationColor(NotificationDeliveryState? state) => state switch
    {
        NotificationDeliveryState.Delivered => Color.Success,
        NotificationDeliveryState.Failed => Color.Error,
        NotificationDeliveryState.DeliveryUnknown => Color.Warning,
        NotificationDeliveryState.Sending or NotificationDeliveryState.Pending => Color.Info,
        _ => Color.Default
    };

    public static string NotificationLabel(NotificationDeliveryState? state) =>
        EnumText.For(state, UiStrings.NotificationDeliveryStateNotSent);

    /// <summary>Lifecycle label for a job, used by the jobs list and the dashboard cards.</summary>
    public static string LifecycleLabel(JobLifecycle lifecycle) => EnumText.For(lifecycle);

    public static Color LifecycleColor(JobLifecycle lifecycle) => lifecycle switch
    {
        JobLifecycle.Active => Color.Success,
        JobLifecycle.Paused => Color.Warning,
        _ => Color.Default
    };

    /// <summary>Verification label for a destination, used wherever a verification chip appears.</summary>
    public static string VerificationLabel(DestinationVerificationResult result) => EnumText.For(result);

    public static Color VerificationColor(DestinationVerificationResult result) => result switch
    {
        DestinationVerificationResult.Succeeded => Color.Success,
        DestinationVerificationResult.Failed => Color.Error,
        _ => Color.Warning
    };

    /// <summary>Access-result label for the last destination test.</summary>
    public static string AccessLabel(DestinationAccessResult result) => EnumText.For(result);

    public static string DestinationTypeLabel(DestinationType type) => EnumText.For(type);

    public static string SeverityLabel(BackupProblemSeverity severity) => EnumText.For(severity);
}
