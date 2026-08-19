using FolderBackuper.Infrastructure.Maintenance;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

/// <summary>
/// Guards the identifiers the application and the installer must agree on. Nothing else can catch
/// that drift without installing the product on a Windows machine.
/// </summary>
public sealed class InstallerScriptConsistencyTests
{
    private static readonly string Script = File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "installer", "FolderBackuper.iss"));

    [Theory]
    [InlineData("ServiceName")]
    [InlineData("EventLogSource")]
    [InlineData("RegistryKey")]
    [InlineData("PortValueName")]
    public void Script_DefinesTheSharedIdentifier(string name)
    {
        var expected = name switch
        {
            "ServiceName" => WindowsServiceMetadata.ServiceName,
            "EventLogSource" => WindowsServiceMetadata.EventLogSource,
            "RegistryKey" => WindowsServiceMetadata.RegistryKey,
            _ => WindowsServiceMetadata.PortValueName
        };

        Assert.Contains($"#define {name} \"{expected}\"", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_DefinesTheDisplayName() =>
        Assert.Contains($"#define AppName \"{WindowsServiceMetadata.DisplayName}\"", Script, StringComparison.Ordinal);

    [Fact]
    public void Script_DefinesTheDefaultPort() =>
        Assert.Contains(
            $"#define DefaultPort \"{WindowsServiceMetadata.DefaultPort}\"",
            Script,
            StringComparison.Ordinal);

    [Fact]
    public void Script_UsesTheMaintenanceVerbsTheApplicationImplements()
    {
        Assert.Contains($"'{MaintenanceCommandLine.ConfigurePortVerb} --port=", Script, StringComparison.Ordinal);
        Assert.Contains($"'{MaintenanceCommandLine.WaitReadyVerb} --timeout-seconds=", Script, StringComparison.Ordinal);
        Assert.Contains("--port=' + SelectedPort", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_MapsEveryMaintenanceExitCodeItCanReceive()
    {
        int[] codes =
        [
            MaintenanceExitCode.InvalidArguments,
            MaintenanceExitCode.DataRootUnavailable,
            MaintenanceExitCode.AccessControlDenied,
            MaintenanceExitCode.PortUnavailable,
            MaintenanceExitCode.NoCandidatePortAvailable,
            MaintenanceExitCode.ConfigurationNotWritten,
            MaintenanceExitCode.ReadinessTimedOut,
            MaintenanceExitCode.ServiceNotRunning,
            MaintenanceExitCode.PortNotConfigured
        ];

        foreach (var code in codes)
        {
            Assert.Contains($"    {code}: Result :=", Script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Script_CarriesAStableApplicationIdentity()
    {
        var line = Script
            .Split('\n')
            .Select(candidate => candidate.Trim())
            .Single(candidate => candidate.StartsWith("AppId=", StringComparison.Ordinal));

        Assert.Matches(@"^AppId=\{\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", line);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_InstallsTheServiceAsLocalSystemWithDelayedAutomaticStart()
    {
        Assert.Contains("start= delayed-auto obj= LocalSystem", Script, StringComparison.Ordinal);
        Assert.Contains("sc.exe', 'failure ", Script, StringComparison.Ordinal);

        // Setting the failure flag would turn a deterministic startup failure, such as a failed
        // migration, into a restart loop. Only the explanatory comment may mention it.
        Assert.DoesNotContain("'failureflag", Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_KeepsApplicationDataUnlessRemovalIsRequested()
    {
        Assert.Contains("MB_DEFBUTTON2", Script, StringComparison.Ordinal);
        Assert.Contains("/REMOVEDATA=1", Script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderBackuper.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root containing FolderBackuper.slnx was not found.");
    }
}
