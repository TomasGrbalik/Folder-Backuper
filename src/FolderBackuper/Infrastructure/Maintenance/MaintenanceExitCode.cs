namespace FolderBackuper.Infrastructure.Maintenance;

/// <summary>
/// Exit codes read by the installer through Inno Setup's <c>Exec</c> result code.
/// </summary>
/// <remarks>
/// These apply only to the maintenance commands. The service process itself always exits with
/// <see cref="FolderBackuper.Infrastructure.ServiceHosting.StartupFailure.ServiceExitCode"/>,
/// because the service control manager renders a service exit code as a Win32 error string.
/// </remarks>
public static class MaintenanceExitCode
{
    public const int Success = 0;
    public const int InvalidArguments = 10;
    public const int DataRootUnavailable = 11;
    public const int AccessControlDenied = 12;
    public const int PortUnavailable = 13;
    public const int NoCandidatePortAvailable = 14;
    public const int ConfigurationNotWritten = 15;
    public const int ReadinessTimedOut = 20;
    public const int ServiceNotRunning = 21;
    public const int PortNotConfigured = 22;
}
