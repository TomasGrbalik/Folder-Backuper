namespace FolderBackuper.Infrastructure.ServiceHosting;

public enum StartupFailureCategory
{
    DataRoot,
    AccessControl,
    SingleInstance,
    Migration,
    PortBinding,
    NonLoopbackBinding,
    Unexpected
}

/// <summary>
/// A classified startup failure. The event identifier is the durable operator-facing signal; the
/// service process itself always exits with 1 because the service control manager renders a
/// process exit code as a Win32 error string, which would misdescribe the failure.
/// </summary>
public sealed record StartupFailure(StartupFailureCategory Category, int EventId, string OperatorMessage)
{
    public const int ServiceExitCode = 1;

    public static StartupFailure DataRoot { get; } = new(
        StartupFailureCategory.DataRoot,
        1001,
        "The application data root is invalid or could not be created.");

    public static StartupFailure AccessControl { get; } = new(
        StartupFailureCategory.AccessControl,
        1002,
        "Access controls could not be applied to the application data root. The service must run with administrative rights.");

    public static StartupFailure SingleInstance { get; } = new(
        StartupFailureCategory.SingleInstance,
        1003,
        "Another Folder Backuper process is already using this data root. Stop the other process or the service and retry.");

    public static StartupFailure Migration { get; } = new(
        StartupFailureCategory.Migration,
        1004,
        "The database could not be opened or migrated. A validated pre-migration backup is retained under the data directory.");

    public static StartupFailure PortBinding { get; } = new(
        StartupFailureCategory.PortBinding,
        1005,
        "The configured loopback port is already in use. Run setup again and choose a different port.");

    public static StartupFailure NonLoopbackBinding { get; } = new(
        StartupFailureCategory.NonLoopbackBinding,
        1006,
        "The server bound an address that is not loopback. Folder Backuper refuses to serve beyond 127.0.0.1 and [::1].");

    public static StartupFailure Unexpected { get; } = new(
        StartupFailureCategory.Unexpected,
        1099,
        "Folder Backuper failed to start for an unexpected reason.");
}

/// <summary>
/// Raised where the failure category is already known, so the classifier does not have to infer it.
/// </summary>
public sealed class StartupFailureException(StartupFailure failure, Exception innerException)
    : Exception(failure.OperatorMessage, innerException)
{
    public StartupFailure Failure { get; } = failure;
}
