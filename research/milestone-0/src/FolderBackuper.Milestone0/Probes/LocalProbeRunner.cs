using System.Security.Cryptography;
using FolderBackuper.Milestone0.Configuration;
using FolderBackuper.Milestone0.Filesystem;
using FolderBackuper.Milestone0.Security;

namespace FolderBackuper.Milestone0.Probes;

public static class LocalProbeRunner
{
    public static async Task<IReadOnlyList<ProbeResult>> RunAsync(ProbeConfiguration configuration, CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>();
        var secret = RandomNumberGenerator.GetBytes(32);
        var protectedSecret = MachineSecretProtector.Protect(secret);
        var recoveredSecret = MachineSecretProtector.Unprotect(protectedSecret);
        results.Add(new ProbeResult(
            "Machine-scope DPAPI",
            CryptographicOperations.FixedTimeEquals(secret, recoveredSecret) ? ProbeStatus.Passed : ProbeStatus.Failed,
            "A sample secret was protected and recovered using LocalMachine scope."));

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-Zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            results.Add(ZipCommentProbe.Run(workingDirectory, Guid.NewGuid(), Guid.NewGuid()));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }

        results.AddRange(await LocalFilesystemProbe.RunAsync(cancellationToken));

        var detector = new LocalHostUncDetector(configuration.LocalHostAliases);
        var localPaths = new[] { @"\\localhost\probe", $@"\\{Environment.MachineName}\probe", @"\\127.0.0.1\probe", @"\\[::1]\probe" };
        var rejected = localPaths.All(detector.IsHostedLocally);
        results.Add(new ProbeResult(
            "Local-host UNC detection",
            rejected ? ProbeStatus.Passed : ProbeStatus.Failed,
            rejected ? "Loopback and machine-name UNC destinations were detected as local." : "At least one local UNC form was not detected."));

        return results;
    }
}
