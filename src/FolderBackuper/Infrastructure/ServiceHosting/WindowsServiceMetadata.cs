namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// Single definition of the identifiers shared by the application, the installer script, and
/// operator tooling. <c>InstallerScriptConsistencyTests</c> asserts that
/// <c>installer/FolderBackuper.iss</c> still agrees with these values.
/// </summary>
public static class WindowsServiceMetadata
{
    public const string ServiceName = "FolderBackuper";

    public const string DisplayName = "Folder Backuper";

    public const string Description =
        "Creates scheduled ZIP backups of local folders to local or SMB storage and hosts the localhost web interface.";

    public const string EventLogSource = "Folder Backuper";

    public const string EventLogName = "Application";

    public const string RegistryKey = @"SOFTWARE\FolderBackuper";

    public const string PortValueName = "Port";

    public const int DefaultPort = 5180;

    public const int LastCandidatePort = 5199;

    public const string MachineConfigurationFileName = "service.json";

    public const string PortConfigurationKey = "FolderBackuper:Port";

    public const string ReadinessPath = "/healthz";
}
