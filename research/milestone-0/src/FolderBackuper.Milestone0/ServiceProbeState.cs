using FolderBackuper.Milestone0.Probes;

namespace FolderBackuper.Milestone0;

public sealed class ServiceProbeState
{
    public ProbeReport? Report { get; private set; }
    public string Status { get; private set; } = "Waiting for probes";

    public event Action? Changed;

    public void SetRunning()
    {
        Status = "Running probes";
        Changed?.Invoke();
    }

    public void SetReport(ProbeReport report)
    {
        Report = report;
        Status = report.Succeeded ? "Probes completed" : "Probe attention required";
        Changed?.Invoke();
    }
}
