using System.Diagnostics;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Backups;

public sealed record BackupEngineRequest(Guid RunId, Guid JobId, DateTimeOffset ArchiveInstant);

public sealed record BackupEngineResult(
    Guid RunId,
    RunOutcome Outcome,
    string? FinalPath,
    string? FinalFileName,
    long FileCount,
    long DirectoryCount,
    long SourceBytes,
    long ArchiveBytes,
    TimeSpan CompressionDuration,
    TimeSpan TransferDuration,
    IReadOnlyList<BackupProblem> Problems);

public sealed class BackupEngine(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    InstallationIdentityService installationIdentity,
    BackupPreflightService preflight,
    SourceManifestBuilder manifests,
    ZipArchiveService zipArchives,
    DestinationArchiveService destinationArchives,
    EffectiveDestinationService effectiveDestinations,
    DestinationAccessRecorder accessRecorder,
    BackupProgressRegistry progressRegistry,
    ApplicationPaths applicationPaths,
    TimeProvider timeProvider,
    RunPersistenceService? runPersistence = null,
    IBackupFaultInjector? faultInjector = null)
{
    public async Task<BackupEngineResult> ExecuteAsync(
        BackupEngineRequest request,
        CancellationToken userCancellationToken = default,
        CancellationToken interruptionToken = default)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            userCancellationToken, interruptionToken);
        var cancellationToken = operationCancellation.Token;
        var stopwatch = Stopwatch.StartNew();
        var problems = new List<BackupProblem>();
        BackupManifest? manifest = null;
        Destination? destination = null;
        string? stagingPath = null;
        string? finalPath = null;
        string? finalFileName = null;
        long archiveBytes = 0;
        var compressionDuration = TimeSpan.Zero;
        var transferDuration = TimeSpan.Zero;
        var outcome = RunOutcome.Failed;
        var destinationAccessed = false;
        var crashInjected = false;
        var destinationAccessResult = DestinationAccessResult.Failed;
        string? destinationAccessError = null;

        try
        {
            BackupJob? job;
            string[] configuredSources;
            await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                if (runPersistence is not null)
                {
                    var run = await context.Runs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.RunId, cancellationToken);
                    var currentDestination = run is null ? null : await context.Destinations.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.Id == run.DestinationId, cancellationToken);
                    if (run is not null && currentDestination is not null)
                    {
                        job = new BackupJob
                        {
                            Id = run.JobId,
                            Name = run.JobName,
                            SourcePath = run.SourcePath,
                            DestinationId = run.DestinationId,
                            DestinationSubfolder = run.DestinationSubfolder,
                            Weekdays = run.ScheduledWeekdays,
                            ScheduledTime = run.ScheduledTime,
                            RetentionCount = run.RetentionCount,
                            DestinationOwnershipKey = "snapshot"
                        };
                        destination = new Destination
                        {
                            Id = run.DestinationId,
                            Name = run.DestinationName,
                            Type = run.DestinationType,
                            RootPath = run.DestinationRootPath,
                            SmbUsername = run.DestinationUsername,
                            ProtectedPassword = currentDestination.ProtectedPassword,
                            VerificationResult = string.IsNullOrWhiteSpace(run.DestinationVerificationFingerprint)
                                ? DestinationVerificationResult.Unverified
                                : DestinationVerificationResult.Succeeded,
                            VerificationFingerprint = run.DestinationVerificationFingerprint
                        };
                    }
                    else
                    {
                        job = null;
                    }
                }
                else
                {
                    job = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.JobId, cancellationToken);
                    if (job is not null)
                    {
                        destination = await context.Destinations.AsNoTracking()
                            .SingleOrDefaultAsync(item => item.Id == job.DestinationId, cancellationToken);
                    }
                }
                configuredSources = await context.Jobs.AsNoTracking()
                    .Select(item => item.SourcePath).ToArrayAsync(cancellationToken);
            }

            if (job is null || destination is null)
            {
                problems.Add(Error(BackupProblemCategory.SourceUnavailable, RunPhase.Scanning,
                    "Load backup configuration", "The job or destination no longer exists."));
                return Result();
            }

            Publish(request.RunId, RunPhase.Scanning, stopwatch.Elapsed, cancellationAvailable: true);
            var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
            var preflightResult = await preflight.ValidateAsync(job, destination, configuredSources,
                installationId, cancellationToken);
            problems.AddRange(preflightResult.Problems);
            if (!preflightResult.Succeeded)
            {
                destinationAccessed = preflightResult.Problems.Any(problem =>
                    problem.Operation is "Validate effective destination" or "Verify destination ownership");
                if (destinationAccessed)
                {
                    destinationAccessResult = MapAccessResult(preflightResult.Problems);
                    destinationAccessError = preflightResult.Problems.FirstOrDefault(problem =>
                        problem.Category is BackupProblemCategory.DestinationUnavailable or
                            BackupProblemCategory.DestinationInaccessible)?.Message;
                }
                return Result();
            }

            destinationAccessed = true;
            destinationAccessResult = DestinationAccessResult.Succeeded;
            var sourcePath = preflightResult.SourcePath!;
            var effectivePath = preflightResult.EffectiveDestinationPath!;
            var topLevelFolder = TopLevelFolder(sourcePath);
            var ownership = new ArchiveOwnership(installationId, request.RunId);

            var initialScan = await manifests.BuildAsync(sourcePath, cancellationToken);
            problems.AddRange(initialScan.Problems);
            if (!initialScan.CanProceed) return Result();
            manifest = initialScan.Manifest!;
            Publish(request.RunId, RunPhase.Scanning, stopwatch.Elapsed, true, manifest);

            var compressionThroughput = new RollingThroughput(timeProvider);
            await PersistPhaseAsync(RunPhase.Compressing, cancellationToken);
            Publish(request.RunId, RunPhase.Compressing, stopwatch.Elapsed, true, manifest);
            stagingPath = Path.Combine(applicationPaths.Staging,
                $".folder-backuper-{request.RunId:N}-{Guid.NewGuid():N}.zip.tmp");
            if (runPersistence is not null)
            {
                await runPersistence.RecordStagingPathAsync(request.RunId, stagingPath, cancellationToken);
                if (faultInjector is not null)
                    await faultInjector.HitAsync(BackupFaultPoint.AfterStagingIntentPersisted, request.RunId, cancellationToken);
            }
            var localArchive = await zipArchives.CreateAsync(
                sourcePath,
                applicationPaths.Staging,
                topLevelFolder,
                manifest,
                ownership,
                copy => PublishCompression(request.RunId, stopwatch.Elapsed, manifest, copy, compressionThroughput),
                cancellationToken,
                stagingPath);
            problems.AddRange(localArchive.Problems);
            compressionDuration = localArchive.CompressionDuration;
            if (!localArchive.Succeeded) return Result();
            if (faultInjector is not null)
                await faultInjector.HitAsync(BackupFaultPoint.AfterStagingFileCreated, request.RunId, cancellationToken);
            archiveBytes = localArchive.ArchiveBytes;

            var finalScan = await manifests.BuildAsync(sourcePath, cancellationToken);
            problems.AddRange(finalScan.Problems);
            if (!finalScan.CanProceed) return Result();
            var changes = manifests.Compare(manifest, finalScan.Manifest!);
            problems.AddRange(changes);
            if (changes.Count > 0) return Result();

            problems.AddRange(await zipArchives.ValidateAsync(stagingPath!, topLevelFolder, manifest,
                ownership, RunPhase.Compressing, cancellationToken));
            if (HasErrors()) return Result();

            Publish(request.RunId, RunPhase.Transferring, stopwatch.Elapsed, true, manifest,
                archiveBytes: archiveBytes);
            var transferThroughput = new RollingThroughput(timeProvider);
            await PersistPhaseAsync(RunPhase.Transferring, cancellationToken);
            var destinationResult = await destinationArchives.TransferAsync(
                effectiveDestinations.Adapter(destination.Type),
                effectiveDestinations.Configuration(destination),
                effectivePath,
                stagingPath!,
                job.Name,
                topLevelFolder,
                manifest,
                ownership,
                request.ArchiveInstant,
                transfer => PublishTransfer(request.RunId, stopwatch.Elapsed, manifest, archiveBytes,
                    transfer, transferThroughput),
                cancellationToken);
            problems.AddRange(destinationResult.Problems);
            transferDuration = destinationResult.TransferDuration;
            if (!destinationResult.Succeeded)
            {
                destinationAccessResult = MapAccessResult(destinationResult.Problems);
                destinationAccessError = destinationResult.Problems.FirstOrDefault(problem =>
                    problem.Severity == BackupProblemSeverity.Error)?.Message;
                return Result();
            }

            finalPath = destinationResult.FinalPath;
            finalFileName = destinationResult.FinalFileName;
            archiveBytes = destinationResult.ArchiveBytes;
            Publish(request.RunId, RunPhase.Finalizing, stopwatch.Elapsed, cancellationAvailable: false,
                manifest, archiveBytes, archiveBytes);
            outcome = problems.Any(problem => problem.Severity == BackupProblemSeverity.Warning)
                ? RunOutcome.SuccessfulWithWarnings
                : RunOutcome.Successful;
        }
        catch (BackupOperationCanceledException exception) when (userCancellationToken.IsCancellationRequested)
        {
            problems.AddRange(exception.CleanupProblems);
            problems.Add(new(BackupProblemSeverity.Error, BackupProblemCategory.Cancelled,
                CurrentPhase(request.RunId), "Cancel backup", "The backup was cancelled."));
            outcome = RunOutcome.Cancelled;
        }
        catch (OperationCanceledException) when (userCancellationToken.IsCancellationRequested)
        {
            problems.Add(new(BackupProblemSeverity.Error, BackupProblemCategory.Cancelled,
                CurrentPhase(request.RunId), "Cancel backup", "The backup was cancelled."));
            outcome = RunOutcome.Cancelled;
        }
        catch (DurableCancellationRequestedException)
        {
            problems.Add(new(BackupProblemSeverity.Error, BackupProblemCategory.Cancelled,
                CurrentPhase(request.RunId), "Cancel backup", "The backup was cancelled."));
            outcome = RunOutcome.Cancelled;
        }
        catch (OperationCanceledException) when (interruptionToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FinalCommitRecoveryRequiredException)
        {
            throw;
        }
        catch (InjectedBackupFaultException)
        {
            crashInjected = true;
            throw;
        }
        catch (Exception exception)
        {
            problems.Add(Error(BackupProblemCategory.GeneralIo, CurrentPhase(request.RunId),
                "Execute backup", "The backup failed unexpectedly.", exception.HResult & 0xFFFF));
        }
        finally
        {
            if (!crashInjected && stagingPath is not null && File.Exists(stagingPath))
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new(BackupProblemSeverity.Warning, BackupProblemCategory.CleanupFailed,
                        RunPhase.Finalizing, "Clean staging archive",
                        "The completed staging archive could not be removed.", stagingPath,
                        exception.HResult & 0xFFFF));
                }
            }

            if (destination is not null && destinationAccessed)
            {
                try
                {
                    await accessRecorder.RecordAsync(destination.Id, destinationAccessResult,
                        destinationAccessError, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    problems.Add(new(BackupProblemSeverity.Warning, BackupProblemCategory.GeneralIo,
                        RunPhase.Finalizing, "Record destination access",
                        "The destination access result could not be recorded.", NativeErrorCode: exception.HResult & 0xFFFF));
                }
            }
        }

        if (outcome == RunOutcome.Successful && problems.Any(problem => problem.Severity == BackupProblemSeverity.Warning))
        {
            outcome = RunOutcome.SuccessfulWithWarnings;
        }

        return Result();

        BackupEngineResult Result()
        {
            Publish(request.RunId, CurrentPhase(request.RunId), stopwatch.Elapsed,
                cancellationAvailable: false, manifest, archiveBytes, archiveBytes, force: true);
            return new(
                request.RunId,
                outcome,
                finalPath,
                finalFileName,
                manifest?.FileCount ?? 0,
                manifest?.DirectoryCount ?? 0,
                manifest?.SourceBytes ?? 0,
                archiveBytes,
                compressionDuration,
                transferDuration,
                problems.AsReadOnly());
        }

        bool HasErrors() => problems.Any(problem => problem.Severity == BackupProblemSeverity.Error);

        Task PersistPhaseAsync(RunPhase phase, CancellationToken token) =>
            runPersistence?.AdvancePhaseAsync(request.RunId, phase, token) ?? Task.CompletedTask;
    }

    private void PublishCompression(
        Guid runId,
        TimeSpan elapsed,
        BackupManifest manifest,
        BackupCopyProgress copy,
        RollingThroughput throughput)
    {
        var rate = throughput.Add(copy.BytesProcessed);
        Publish(runId, RunPhase.Compressing, elapsed, true, manifest, copy.ArchiveBytes, 0,
            copy.FilesProcessed, copy.BytesProcessed, copy.CurrentRelativePath, rate);
    }

    private void PublishTransfer(
        Guid runId,
        TimeSpan elapsed,
        BackupManifest manifest,
        long archiveBytes,
        BackupTransferProgress transfer,
        RollingThroughput throughput)
    {
        var rate = throughput.Add(transfer.BytesTransferred);
        Publish(runId, RunPhase.Transferring, elapsed, true, manifest, archiveBytes,
            transfer.BytesTransferred, manifest.FileCount, manifest.SourceBytes, null, rate,
            Remaining(transfer.TotalBytes, transfer.BytesTransferred, rate));
    }

    private void Publish(
        Guid runId,
        RunPhase phase,
        TimeSpan elapsed,
        bool cancellationAvailable,
        BackupManifest? manifest = null,
        long archiveBytes = 0,
        long transferBytes = 0,
        long filesProcessed = 0,
        long bytesProcessed = 0,
        string? currentPath = null,
        double throughput = 0,
        TimeSpan? remaining = null,
        bool force = false) =>
        progressRegistry.Publish(new(
            runId,
            phase,
            filesProcessed,
            phase == RunPhase.Scanning && manifest is not null ? manifest.DirectoryCount : 0,
            bytesProcessed,
            manifest?.FileCount ?? 0,
            manifest?.DirectoryCount ?? 0,
            manifest?.SourceBytes ?? 0,
            currentPath,
            bytesProcessed,
            archiveBytes,
            transferBytes,
            throughput,
            elapsed,
            remaining,
            cancellationAvailable), force);

    private RunPhase CurrentPhase(Guid runId) => progressRegistry.Current(runId)?.Phase ?? RunPhase.Scanning;

    private static TimeSpan? Remaining(long total, long completed, double rate) =>
        rate > 0 && total > completed ? TimeSpan.FromSeconds((total - completed) / rate) : null;

    private static DestinationAccessResult MapAccessResult(IEnumerable<BackupProblem> problems)
    {
        var categories = problems.Select(problem => problem.Category).ToHashSet();
        if (categories.Contains(BackupProblemCategory.DestinationUnavailable)) return DestinationAccessResult.Unavailable;
        if (categories.Contains(BackupProblemCategory.DestinationInaccessible)) return DestinationAccessResult.AccessDenied;
        if (categories.Contains(BackupProblemCategory.InvalidPath)) return DestinationAccessResult.InvalidPath;
        return DestinationAccessResult.Failed;
    }

    private static string TopLevelFolder(string sourcePath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(sourcePath);
        var name = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        var root = Path.GetPathRoot(sourcePath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .TrimEnd(':');
        return string.IsNullOrWhiteSpace(root) ? "Source" : $"Drive_{root}";
    }

    private static BackupProblem Error(
        BackupProblemCategory category,
        RunPhase phase,
        string operation,
        string message,
        int? nativeErrorCode = null) =>
        new(BackupProblemSeverity.Error, category, phase, operation, message, NativeErrorCode: nativeErrorCode);
}
