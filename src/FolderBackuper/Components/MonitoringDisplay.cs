using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Notifications;
using MudBlazor;

namespace FolderBackuper.Components;

/// <summary>
/// Shared color and label semantics for run status across the dashboard, history, and calendar so past and
/// planned entries read consistently. Kept in the components layer because it maps domain enums to MudBlazor colors.
/// </summary>
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

    public static string OutcomeLabel(RunOutcome? outcome) => outcome switch
    {
        RunOutcome.Successful => "Successful",
        RunOutcome.SuccessfulWithWarnings => "Completed with warnings",
        RunOutcome.Failed => "Failed",
        RunOutcome.Cancelled => "Cancelled",
        _ => "In progress"
    };

    /// <summary>Status label for a run row, falling back to the phase for non-terminal runs.</summary>
    public static string StatusLabel(RunOutcome? outcome, RunPhase phase) =>
        outcome is not null ? OutcomeLabel(outcome) : PhaseLabel(phase);

    public static Color StatusColor(RunOutcome? outcome, RunPhase phase) =>
        outcome is not null ? OutcomeColor(outcome) : (phase == RunPhase.Queued ? Color.Default : Color.Info);

    public static string PhaseLabel(RunPhase phase) => phase switch
    {
        RunPhase.Planned => "Planned",
        RunPhase.Queued => "Queued",
        RunPhase.Scanning => "Scanning",
        RunPhase.Compressing => "Compressing",
        RunPhase.Transferring => "Transferring",
        RunPhase.Finalizing => "Finalizing",
        _ => phase.ToString()
    };

    /// <summary>Transfer verb shown during the transfer phase: local copies vs. SMB uploads.</summary>
    public static string TransferVerb(DestinationType type) =>
        type == DestinationType.Smb ? "Uploading" : "Copying";

    public static string TriggerLabel(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Scheduled => "Scheduled",
        RunTrigger.CatchUp => "Catch-up",
        RunTrigger.Manual => "Manual",
        _ => trigger.ToString()
    };

    public static Color ArtifactColor(ArtifactState? state) => state switch
    {
        ArtifactState.Retained => Color.Success,
        ArtifactState.RemovedByRetention => Color.Default,
        ArtifactState.FoundMissing => Color.Error,
        ArtifactState.Unmanaged => Color.Warning,
        ArtifactState.PendingFinalization => Color.Info,
        _ => Color.Default
    };

    public static string ArtifactLabel(ArtifactState? state) => state switch
    {
        ArtifactState.Retained => "Retained",
        ArtifactState.RemovedByRetention => "Removed by retention",
        ArtifactState.FoundMissing => "Found missing",
        ArtifactState.Unmanaged => "Unmanaged",
        ArtifactState.PendingFinalization => "Pending",
        null => "None",
        _ => state.ToString()!
    };

    public static Color NotificationColor(NotificationDeliveryState? state) => state switch
    {
        NotificationDeliveryState.Delivered => Color.Success,
        NotificationDeliveryState.Failed => Color.Error,
        NotificationDeliveryState.DeliveryUnknown => Color.Warning,
        NotificationDeliveryState.Sending or NotificationDeliveryState.Pending => Color.Info,
        _ => Color.Default
    };

    public static string NotificationLabel(NotificationDeliveryState? state) => state switch
    {
        NotificationDeliveryState.Delivered => "Delivered",
        NotificationDeliveryState.Failed => "Delivery failed",
        NotificationDeliveryState.DeliveryUnknown => "Delivery unknown",
        NotificationDeliveryState.Sending => "Sending",
        NotificationDeliveryState.Pending => "Pending",
        null => "Not sent",
        _ => state.ToString()!
    };
}
