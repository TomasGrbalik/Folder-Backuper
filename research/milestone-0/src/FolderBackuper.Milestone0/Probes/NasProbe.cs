using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using FolderBackuper.Milestone0.Configuration;
using FolderBackuper.Milestone0.Filesystem;
using FolderBackuper.Milestone0.Security;

namespace FolderBackuper.Milestone0.Probes;

public static class NasProbe
{
    private const string FallbackSemaphoreName = @"Global\FolderBackuper-Milestone0-SmbFallback";

    public static async Task<IReadOnlyList<ProbeResult>> RunWithImpersonationAsync(
        NasConfiguration configuration,
        string password,
        Guid runId,
        CancellationToken cancellationToken) =>
        await ExecuteWithErrorMappingAsync(
            "NAS network-only credential context",
            "A network-only token was established; the lifecycle result determines NAS compatibility.",
            () => NetworkCredentialImpersonator.RunAsync(
                configuration.Username,
                configuration.Domain,
                password,
                () => RunLifecycleAsync(configuration, runId, cancellationToken)));

    public static async Task<IReadOnlyList<ProbeResult>> RunWithDevicelessConnectionAsync(
        NasConfiguration configuration,
        string password,
        Guid runId,
        CancellationToken cancellationToken) =>
        await RunFallbackSerializedAsync(configuration, password, runId, cancellationToken);

    private static async Task<IReadOnlyList<ProbeResult>> RunFallbackSerializedAsync(
        NasConfiguration configuration,
        string password,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, FallbackSemaphoreName);
        var acquired = false;
        try
        {
            var waitResult = WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle]);
            cancellationToken.ThrowIfCancellationRequested();
            acquired = waitResult == 0;

            if (!acquired)
            {
                throw new InvalidOperationException("The global SMB fallback lock could not be acquired.");
            }

            DevicelessNetworkConnection.EnsureNoExistingConnection(configuration.UncRoot);
            return await ExecuteWithErrorMappingAsync(
                "NAS deviceless connection",
                "A serialized deviceless NAS connection was established.",
                async () =>
                {
                    using var connection = DevicelessNetworkConnection.Connect(configuration.UncRoot, QualifiedUsername(configuration), password);
                    return await RunLifecycleAsync(configuration, runId, cancellationToken);
                });
        }
        finally
        {
            if (acquired)
            {
                semaphore.Release();
            }
        }
    }

    private static async Task<IReadOnlyList<ProbeResult>> ExecuteWithErrorMappingAsync(
        string authenticationProbeName,
        string successSummary,
        Func<Task<IReadOnlyList<ProbeResult>>> action)
    {
        try
        {
            var results = (await action()).ToList();
            results.Insert(0, new ProbeResult(authenticationProbeName, ProbeStatus.Passed, successSummary));
            return results;
        }
        catch (Win32Exception exception)
        {
            return [new ProbeResult(authenticationProbeName, ProbeStatus.Failed, exception.Message, NativeErrorCode: exception.NativeErrorCode)];
        }
        catch (UnauthorizedAccessException exception)
        {
            return [new ProbeResult(authenticationProbeName, ProbeStatus.Failed, exception.Message)];
        }
        catch (IOException exception)
        {
            return [new ProbeResult(authenticationProbeName, ProbeStatus.Failed, exception.Message, NativeErrorCode: exception.HResult & 0xFFFF)];
        }
    }

    private static async Task<IReadOnlyList<ProbeResult>> RunLifecycleAsync(
        NasConfiguration configuration,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>();
        var testDirectoryName = $".folder-backuper-m0-{runId:N}";
        var testDirectory = Path.Combine(configuration.UncRoot, testDirectoryName);
        var partialPath = Path.Combine(testDirectory, "transfer.partial");
        var finalPath = Path.Combine(testDirectory, "transfer.zip");
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        var testDirectoryCreated = false;

        try
        {
            if (Directory.Exists(testDirectory))
            {
                throw new IOException("The generated NAS test directory already exists; no existing content will be reused.");
            }

            Directory.CreateDirectory(testDirectory);
            testDirectoryCreated = true;
            FilesystemIdentity? directoryIdentity = null;
            try
            {
                directoryIdentity = WindowsFilesystemInterop.GetIdentity(testDirectory);
            }
            catch (Win32Exception exception)
            {
                results.Add(new ProbeResult("NAS directory identity", ProbeStatus.Inconclusive, exception.Message, NativeErrorCode: exception.NativeErrorCode));
            }

            await using (var stream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var readBack = await File.ReadAllBytesAsync(partialPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(bytes, readBack))
            {
                results.Add(new ProbeResult("NAS file lifecycle", ProbeStatus.Failed, "Known bytes did not survive close and readback."));
                return results;
            }

            FilesystemIdentity? identityBefore = null;
            try
            {
                identityBefore = WindowsFilesystemInterop.GetIdentity(partialPath);
            }
            catch (Win32Exception exception)
            {
                results.Add(new ProbeResult("NAS filesystem identity", ProbeStatus.Inconclusive, exception.Message, NativeErrorCode: exception.NativeErrorCode));
            }

            if (directoryIdentity is not null && identityBefore is not null)
            {
                results.Add(new ProbeResult(
                    "NAS identity distinctness",
                    directoryIdentity != identityBefore ? ProbeStatus.Passed : ProbeStatus.Inconclusive,
                    directoryIdentity != identityBefore
                        ? "The test directory and file returned distinct identities."
                        : "The test directory and file returned the same identity; identity values are not usable for collision detection."));
            }

            File.Move(partialPath, finalPath, overwrite: false);
            await using (var stream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != bytes.Length)
                {
                    results.Add(new ProbeResult("NAS file lifecycle", ProbeStatus.Failed, "The renamed file length changed."));
                    return results;
                }
            }

            if (identityBefore is not null)
            {
                try
                {
                    var identityAfter = WindowsFilesystemInterop.GetIdentity(finalPath);
                    results.Add(new ProbeResult(
                        "NAS filesystem identity",
                        identityBefore == identityAfter ? ProbeStatus.Passed : ProbeStatus.Inconclusive,
                        identityBefore == identityAfter
                            ? $"File identity remained stable across close, reopen, and rename using {identityBefore.Api}."
                            : "File identity changed across rename; ownership markers must remain authoritative.",
                        new Dictionary<string, string> { ["IdentityApi"] = identityBefore.Api }));
                }
                catch (Win32Exception exception)
                {
                    results.Add(new ProbeResult("NAS filesystem identity", ProbeStatus.Inconclusive, exception.Message, NativeErrorCode: exception.NativeErrorCode));
                }
            }

            if (directoryIdentity is not null)
            {
                try
                {
                    var reopenedDirectoryIdentity = WindowsFilesystemInterop.GetIdentity(testDirectory);
                    results.Add(new ProbeResult(
                        "NAS directory identity",
                        directoryIdentity == reopenedDirectoryIdentity ? ProbeStatus.Passed : ProbeStatus.Inconclusive,
                        directoryIdentity == reopenedDirectoryIdentity
                            ? $"Directory identity remained stable across handle close and reopen using {directoryIdentity.Api}."
                            : "Directory identity changed across reopen; ownership markers must remain authoritative.",
                        new Dictionary<string, string> { ["IdentityApi"] = directoryIdentity.Api }));
                }
                catch (Win32Exception exception)
                {
                    results.Add(new ProbeResult("NAS directory identity", ProbeStatus.Inconclusive, exception.Message, NativeErrorCode: exception.NativeErrorCode));
                }
            }

            results.Add(await ProbeOwnershipMarkerAsync(configuration, testDirectoryName, runId, cancellationToken));
            results.Add(ProbeDeniedControl(configuration.DeniedControlPath, runId));
            results.Add(new ProbeResult("NAS file lifecycle", ProbeStatus.Passed, "Create, write, flush, read, non-overwriting rename, reopen, and delete operations succeeded."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            results.Add(new ProbeResult(
                "NAS file lifecycle",
                ProbeStatus.Failed,
                exception.Message,
                NativeErrorCode: exception is Win32Exception win32 ? win32.NativeErrorCode : exception.HResult & 0xFFFF));
        }
        finally
        {
            try
            {
                if (testDirectoryCreated)
                {
                    File.Delete(partialPath);
                    File.Delete(finalPath);
                    File.Delete(Path.Combine(testDirectory, ".folder-backuper-owner"));
                    Directory.Delete(testDirectory, recursive: false);
                }

                results.Add(new ProbeResult(
                    "NAS exact cleanup",
                    ProbeStatus.Passed,
                    testDirectoryCreated
                        ? "The generated test directory and owned files were removed."
                        : "No generated test directory was created."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(new ProbeResult("NAS exact cleanup", ProbeStatus.Failed, $"A generated test artifact may remain: {exception.Message}"));
            }
        }

        return results;
    }

    private static async Task<ProbeResult> ProbeOwnershipMarkerAsync(
        NasConfiguration configuration,
        string testDirectoryName,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var markerName = ".folder-backuper-owner";
        var markerPath = Path.Combine(configuration.UncRoot, testDirectoryName, markerName);
        var installationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var marker = $"FolderBackuper:v1\ninstallation={installationId:D}\njob={jobId:D}\n";
        await using (var markerStream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(markerStream, Encoding.UTF8))
        {
            await writer.WriteAsync(marker.AsMemory(), cancellationToken);
        }

        var roots = configuration.Aliases.Prepend(configuration.UncRoot);
        foreach (var root in roots)
        {
            var aliasMarker = Path.Combine(root, testDirectoryName, markerName);
            if (!string.Equals(await File.ReadAllTextAsync(aliasMarker, cancellationToken), marker, StringComparison.Ordinal))
            {
                return new ProbeResult("NAS ownership marker aliases", ProbeStatus.Failed, "The marker differed through a configured NAS alias.");
            }
        }

        try
        {
            await using var unexpected = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return new ProbeResult("NAS ownership marker aliases", ProbeStatus.Failed, "Exclusive creation unexpectedly replaced an ownership marker.");
        }
        catch (IOException)
        {
            // Existing markers must prevent another synthetic job from claiming the folder.
        }

        if (!string.Equals(await File.ReadAllTextAsync(markerPath, cancellationToken), marker, StringComparison.Ordinal))
        {
            return new ProbeResult("NAS ownership marker aliases", ProbeStatus.Failed, "Marker ownership changed during the collision check.");
        }

        File.Delete(markerPath);
        return new ProbeResult(
            "NAS ownership marker aliases",
            ProbeStatus.Passed,
            configuration.Aliases.Length == 0
                ? "Exclusive claim and ownership-checked release succeeded; no alternate NAS aliases were configured."
                : $"Exclusive claim and ownership-checked release succeeded through {configuration.Aliases.Length + 1} equivalent paths.",
            new Dictionary<string, string> { ["ProbeRun"] = runId.ToString("D") });
    }

    private static ProbeResult ProbeDeniedControl(string? deniedPath, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(deniedPath))
        {
            return new ProbeResult("NAS permission boundary", ProbeStatus.Skipped, "No expected-denied control path was configured.");
        }

        var path = Path.Combine(deniedPath, $"folder-backuper-denial-{runId:N}.tmp");
        var created = false;
        Exception? operationFailure = null;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            created = true;
            stream.Write("permission boundary probe"u8);
            stream.Flush(flushToDisk: true);
        }
        catch (UnauthorizedAccessException)
        {
            if (!created)
            {
                return new ProbeResult("NAS permission boundary", ProbeStatus.Passed, "The NAS account was denied outside its intended test destination.");
            }

            operationFailure = new UnauthorizedAccessException();
        }
        catch (IOException exception)
        {
            operationFailure = exception;
            if (!created)
            {
                return new ProbeResult(
                    "NAS permission boundary",
                    ProbeStatus.Failed,
                    $"The permission boundary was not proven because creation failed for a non-permission reason: {exception.Message}",
                    NativeErrorCode: exception.HResult & 0xFFFF);
            }
        }

        try
        {
            File.Delete(path);
            return new ProbeResult(
                "NAS permission boundary",
                ProbeStatus.Failed,
                operationFailure is null
                    ? "The NAS account could create content in the expected-denied control path; the probe file was removed."
                    : "The NAS account created a file in the expected-denied control path before the write failed; the probe file was removed.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return new ProbeResult("NAS permission boundary", ProbeStatus.Failed, $"The NAS account created content in the expected-denied control path and cleanup failed: {exception.Message}");
        }
    }

    private static string QualifiedUsername(NasConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration.Domain) || configuration.Username.Contains('\\', StringComparison.Ordinal)
            ? configuration.Username
            : $"{configuration.Domain}\\{configuration.Username}";
}
