using System.Security.Principal;
using FolderBackuper.Milestone0.Configuration;
using FolderBackuper.Milestone0.Probes;
using FolderBackuper.Milestone0.Reporting;
using FolderBackuper.Milestone0.Security;

namespace FolderBackuper.Milestone0;

public sealed class ServiceProbeWorker(
    ProbeConfiguration configuration,
    ServiceProbeOptions options,
    ServiceProbeState state,
    ILogger<ServiceProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.SetRunning();
        var started = DateTimeOffset.UtcNow;
        var results = new List<ProbeResult>();
        var identity = WindowsIdentity.GetCurrent();
        var isLocalSystem = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        results.Add(new ProbeResult(
            "LocalSystem service identity",
            isLocalSystem ? ProbeStatus.Passed : ProbeStatus.Failed,
            isLocalSystem ? "The diagnostic web host is running as LocalSystem." : "The diagnostic web host is not running as LocalSystem."));

        try
        {
            results.Add(await SourceReadProbe.RunAsync(configuration.SourceReadPath, configuration.MaximumSourceFiles, stoppingToken));
            var password = await ProtectedSecretFile.ReadAsync(options.ProtectedSecretPath, stoppingToken);
            results.Add(new ProbeResult("DPAPI after service start", ProbeStatus.Passed, "LocalSystem decrypted the persisted machine-scope probe secret."));
            results.AddRange(await NasProbe.RunWithImpersonationAsync(configuration.Nas!, password, options.RunId, stoppingToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "A service probe failed before producing its normal result.");
            results.Add(new ProbeResult("Service probe execution", ProbeStatus.Failed, exception.Message));
        }

        var report = Program.CreateReport(options.RunId, started, results);
        state.SetReport(report);
        try
        {
            await ProbeReportWriter.WriteAsync(report, options.OutputDirectory, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "The service probe report could not be written.");
        }
    }
}

public sealed record ServiceProbeOptions(Guid RunId, string ProtectedSecretPath, string OutputDirectory);
