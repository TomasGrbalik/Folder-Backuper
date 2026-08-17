using System.ComponentModel;
using System.Diagnostics;
using FolderBackuper.Milestone0.Filesystem;

namespace FolderBackuper.Milestone0.Probes;

public static class LocalFilesystemProbe
{
    public static async Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>();
        var root = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            var file = Path.Combine(target, "identity.dat");
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4], cancellationToken);

            var directoryIdentity = WindowsFilesystemInterop.GetIdentity(target);
            var fileIdentity = WindowsFilesystemInterop.GetIdentity(file);
            var renamed = Path.Combine(target, "renamed.dat");
            File.Move(file, renamed);
            var renamedIdentity = WindowsFilesystemInterop.GetIdentity(renamed);
            results.Add(new ProbeResult(
                "Local filesystem identity",
                fileIdentity == renamedIdentity ? ProbeStatus.Passed : ProbeStatus.Failed,
                fileIdentity == renamedIdentity ? "File identity remained stable across reopen and rename." : "File identity changed after rename.",
                new Dictionary<string, string> { ["DirectoryIdentity"] = directoryIdentity.ToString() }));

            results.Add(await ProbeLinkAsync(root, target, "symbolic link", createJunction: false, cancellationToken));
            results.Add(await ProbeLinkAsync(root, target, "junction", createJunction: true, cancellationToken));
        }
        catch (Win32Exception exception)
        {
            results.Add(new ProbeResult("Local filesystem interop", ProbeStatus.Failed, exception.Message, NativeErrorCode: exception.NativeErrorCode));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return results;
    }

    private static async Task<ProbeResult> ProbeLinkAsync(
        string root,
        string target,
        string linkKind,
        bool createJunction,
        CancellationToken cancellationToken)
    {
        var link = Path.Combine(root, linkKind.Replace(' ', '-'));
        try
        {
            if (createJunction)
            {
                var startInfo = new ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("mklink");
                startInfo.ArgumentList.Add("/J");
                startInfo.ArgumentList.Add(link);
                startInfo.ArgumentList.Add(target);
                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                {
                    return new ProbeResult("Final path through junction", ProbeStatus.Inconclusive, "Windows could not create the junction fixture.");
                }
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }

            var targetPath = WindowsFilesystemInterop.GetFinalPath(target);
            var linkPath = WindowsFilesystemInterop.GetFinalPath(link);
            var sameIdentity = WindowsFilesystemInterop.GetIdentity(target) == WindowsFilesystemInterop.GetIdentity(link);
            var passed = sameIdentity && string.Equals(targetPath, linkPath, StringComparison.OrdinalIgnoreCase);
            return new ProbeResult(
                $"Final path through {linkKind}",
                passed ? ProbeStatus.Passed : ProbeStatus.Failed,
                passed ? "The alias resolved to the target path and identity." : "The alias did not resolve to the target path and identity.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new ProbeResult($"Final path through {linkKind}", ProbeStatus.Inconclusive, exception.Message);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }
}
