using System.ComponentModel;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using Microsoft.EntityFrameworkCore;

using FolderBackuper.Infrastructure.Localization;
namespace FolderBackuper.Features.Jobs;

public sealed class JobService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ConfigurationMutationGate mutationGate,
    EffectiveDestinationService effectiveDestinations,
    JobDestinationTestService destinationTests,
    InstallationIdentityService installationIdentity,
    TimeProvider timeProvider)
{
    private const ScheduledWeekdays AllWeekdays = ScheduledWeekdays.Monday | ScheduledWeekdays.Tuesday |
        ScheduledWeekdays.Wednesday | ScheduledWeekdays.Thursday | ScheduledWeekdays.Friday |
        ScheduledWeekdays.Saturday | ScheduledWeekdays.Sunday;

    public async Task<IReadOnlyList<JobSummary>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Jobs.AsNoTracking()
            .Where(x => includeArchived || x.Lifecycle != JobLifecycle.Archived)
            .OrderBy(x => x.Name)
            .Select(x => new JobSummary(x.Id, x.Name, x.Lifecycle, x.SourcePath, x.DestinationId,
                x.DestinationSubfolder, x.Weekdays, x.ScheduledTime, x.ScheduleRevision,
                x.ScheduleEffectiveFromUtc, x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<JobDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.AsNoTracking().Include(x => x.Destination)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return job is null ? null : Details(job);
    }

    public Task<JobOperationResult> CreateAsync(
        SaveJobCommand command,
        CancellationToken cancellationToken = default) =>
        MutateAsync(ct => CreateCoreAsync(command, ct), cancellationToken);

    public Task<JobOperationResult> EditAsync(
        Guid id,
        SaveJobCommand command,
        CancellationToken cancellationToken = default) =>
        MutateAsync(ct => EditCoreAsync(id, command, ct), cancellationToken);

    public Task<JobOperationResult> PauseAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(ct => TransitionAsync(id, JobLifecycle.Paused, ct), cancellationToken);

    public Task<JobOperationResult> ReactivateAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(ct => TransitionAsync(id, JobLifecycle.Active, ct), cancellationToken);

    public Task<JobOperationResult> ArchiveAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateAsync(ct => TransitionAsync(id, JobLifecycle.Archived, ct), cancellationToken);

    public Task<JobOperationResult> RestoreAsync(
        Guid id,
        bool restoreActive,
        CancellationToken cancellationToken = default) =>
        MutateAsync(ct => RestoreCoreAsync(id, restoreActive, ct), cancellationToken);

    public Task<JobOperationResult> TestDestinationAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MutateAsync(ct => TestDestinationCoreAsync(id, ct), cancellationToken);

    private async Task<JobOperationResult> CreateCoreAsync(SaveJobCommand command, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(command, null, cancellationToken);
        if (validation.Errors.Count != 0) return JobOperationResult.Validation(validation.Errors);

        var now = timeProvider.GetUtcNow();
        var job = new BackupJob
        {
            Name = validation.Name!,
            SourcePath = validation.SourcePath!,
            DestinationId = validation.Destination!.Id,
            DestinationSubfolder = validation.Subfolder!,
            Weekdays = command.Weekdays,
            ScheduledTime = command.ScheduledTime,
            RetentionCount = command.RetentionCount,
            ScheduleRevision = 1,
            ScheduleEffectiveFromUtc = now,
            DestinationOwnershipKey = validation.OwnershipKey!,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var verification = await ClaimDestinationAsync(job, validation.Destination!,
            requireManagementVerification: command.Activate, cancellationToken);
        if (!verification.Succeeded) return verification.Result!;

        try
        {
            job.DestinationOwnershipKey = verification.Outcome!.OwnershipKey!;
            if (command.Activate)
            {
                job.Activate();
                job.BeginScheduling(now, resetSatisfied: true);
            }
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            context.Jobs.Add(job);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (verification.Outcome!.NewlyClaimed)
            {
                OwnershipMarkerOutcome cleanup;
                try
                {
                    cleanup = await ReleaseClaimAsync(job, validation.Destination!);
                }
                catch (Exception cleanupException)
                {
                    throw new InvalidOperationException(
                        "The job was not saved and its new ownership marker cleanup failed.",
                        new AggregateException(exception, cleanupException));
                }
                if (!cleanup.Succeeded)
                    throw new InvalidOperationException(
                        $"The job was not saved and its new ownership marker could not be released: {cleanup.Message.Key}", exception);
            }
            if (exception is DbUpdateException)
                return new(JobOperationStatus.Conflict, JobMessage.NameOrFolderReserved);
            throw;
        }
        return new(JobOperationStatus.Succeeded, JobMessage.Created, Details(job, validation.Destination!));
    }

    private async Task<JobOperationResult> EditCoreAsync(Guid id, SaveJobCommand command, CancellationToken cancellationToken)
    {
        await using var read = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await read.Jobs.AsNoTracking().Include(x => x.Destination)
            .SingleOrDefaultAsync(x => x.Id == id && x.Lifecycle != JobLifecycle.Archived, cancellationToken);
        if (current is null) return new(JobOperationStatus.NotFound, JobMessage.NotFound);

        var validation = await ValidateAsync(command, id, cancellationToken);
        if (validation.Errors.Count != 0) return JobOperationResult.Validation(validation.Errors);
        var pathChanged = current.DestinationId != command.DestinationId ||
            !string.Equals(current.DestinationSubfolder, validation.Subfolder, StringComparison.OrdinalIgnoreCase);
        if (pathChanged && !command.ConfirmDestinationPathChange)
        {
            return JobOperationResult.Validation([
                new("ConfirmDestinationPathChange", JobValidationMessage.ConfirmDestinationPathChange)]);
        }

        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        JobDestinationTestOutcome? acquired = null;
        if (pathChanged)
        {
            var candidate = new BackupJob
            {
                Id = current.Id,
                Name = validation.Name!,
                SourcePath = validation.SourcePath!,
                DestinationId = validation.Destination!.Id,
                DestinationSubfolder = validation.Subfolder!,
                DestinationOwnershipKey = validation.OwnershipKey!
            };
            var claim = await ClaimDestinationAsync(candidate, validation.Destination,
                current.Lifecycle == JobLifecycle.Active || command.Activate, cancellationToken);
            if (!claim.Succeeded) return claim.Result!;
            acquired = claim.Outcome;
        }

        var oldMarkerReleased = false;
        try
        {
            if (pathChanged && !string.Equals(current.DestinationOwnershipKey, acquired!.OwnershipKey,
                StringComparison.OrdinalIgnoreCase))
            {
                var released = await destinationTests.ReleaseAsync(current.Destination!, current.DestinationSubfolder,
                    installationId, current.Id, cancellationToken);
                if (!released.Succeeded && !(current.Lifecycle == JobLifecycle.Paused &&
                    released.Result == OwnershipMarkerResult.Missing))
                {
                    throw new JobOperationFailureException(new(JobOperationStatus.OwnershipFailed,
                        JobMessage.OldMarkerNotReleased));
                }
                oldMarkerReleased = released.Result == OwnershipMarkerResult.Released;
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var job = await context.Jobs.Include(x => x.Destination)
                .SingleAsync(x => x.Id == id, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var scheduleChanged = job.Weekdays != command.Weekdays || job.ScheduledTime != command.ScheduledTime;
            job.Name = validation.Name!;
            job.SourcePath = validation.SourcePath!;
            if (job.DestinationId != validation.Destination!.Id)
            {
                job.Destination = await context.Destinations.SingleAsync(
                    x => x.Id == validation.Destination.Id, cancellationToken);
                job.DestinationId = validation.Destination.Id;
            }
            job.DestinationSubfolder = validation.Subfolder!;
            job.DestinationOwnershipKey = acquired?.OwnershipKey ?? validation.OwnershipKey!;
            job.Weekdays = command.Weekdays;
            job.ScheduledTime = command.ScheduledTime;
            job.RetentionCount = command.RetentionCount;
            job.UpdatedAtUtc = now;
            if (scheduleChanged)
            {
                job.ScheduleRevision++;
                job.ScheduleEffectiveFromUtc = now;
                if (job.Lifecycle == JobLifecycle.Active || command.Activate)
                    job.BeginScheduling(now, resetSatisfied: true);
                else
                    job.StopScheduling();
            }
            if (command.Activate && job.Lifecycle == JobLifecycle.Paused)
            {
                if (!pathChanged || acquired is null)
                {
                    var verified = await VerifyAndClaimAsync(job, validation.Destination, cancellationToken);
                    if (!verified.Succeeded) throw new JobOperationFailureException(verified.Result!);
                    acquired = verified.Outcome;
                    job.DestinationOwnershipKey = acquired!.OwnershipKey!;
                }
                job.Activate();
                job.BeginScheduling(now, resetSatisfied: scheduleChanged);
            }

            if (pathChanged)
            {
                var artifacts = await context.BackupArtifacts.Include(x => x.Run)
                    .Where(x => x.Run!.JobId == id && x.State == ArtifactState.Retained)
                    .ToListAsync(cancellationToken);
                foreach (var artifact in artifacts) artifact.MarkUnmanaged(now);
                job.ManagedArtifactCount = 0;
                job.ManagedArtifactBytes = 0;
                job.LatestArtifactBytes = null;
                job.StorageConfirmedAtUtc = null;
            }

            await context.SaveChangesAsync(cancellationToken);
            return new(JobOperationStatus.Succeeded, JobMessage.Updated, Details(job));
        }
        catch (Exception exception)
        {
            var failures = new List<string>();
            if (acquired?.NewlyClaimed == true)
            {
                try
                {
                    var released = await destinationTests.ReleaseAsync(validation.Destination!, validation.Subfolder!,
                        installationId, id, CancellationToken.None);
                    if (!released.Succeeded) failures.Add($"new marker release failed: {released.Message}");
                }
                catch (Exception cleanupException)
                {
                    failures.Add($"new marker release failed: {cleanupException.Message}");
                }
            }
            if (oldMarkerReleased)
            {
                try
                {
                    var restored = await destinationTests.TestAndClaimAsync(current.Destination!, current.DestinationSubfolder,
                        current.SourcePath, installationId, id, CancellationToken.None);
                    if (!restored.Succeeded) failures.Add($"old marker restoration failed: {restored.Message.Key}");
                }
                catch (Exception cleanupException)
                {
                    failures.Add($"old marker restoration failed: {cleanupException.Message}");
                }
            }
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    $"The job update failed and ownership compensation was incomplete: {string.Join("; ", failures)}", exception);
            if (exception is JobOperationFailureException failure) return failure.Result;
            if (exception is DbUpdateException)
                return new(JobOperationStatus.Conflict, JobMessage.NameOrFolderReserved);
            throw;
        }
    }

    private async Task<JobOperationResult> TransitionAsync(
        Guid id,
        JobLifecycle target,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.Include(x => x.Destination).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return new(JobOperationStatus.NotFound, JobMessage.NotFound);
        var now = timeProvider.GetUtcNow();
        JobDestinationTestOutcome? claimed = null;
        var markerReleased = false;

        if (target == JobLifecycle.Active)
        {
            if (job.Lifecycle != JobLifecycle.Paused)
                return new(JobOperationStatus.InvalidTransition, JobMessage.OnlyPausedCanBeReactivated);
            var verified = await VerifyAndClaimAsync(job, job.Destination!, cancellationToken);
            if (!verified.Succeeded) return verified.Result!;
            claimed = verified.Outcome;
            job.DestinationOwnershipKey = verified.Outcome!.OwnershipKey!;
            job.Activate();
            job.ScheduleEffectiveFromUtc = now;
            job.BeginScheduling(now);
        }
        else if (target == JobLifecycle.Paused)
        {
            if (job.Lifecycle != JobLifecycle.Active)
                return new(JobOperationStatus.InvalidTransition, JobMessage.OnlyActiveCanBePaused);
            job.Pause();
            job.StopScheduling();
        }
        else
        {
            if (job.Lifecycle == JobLifecycle.Archived)
                return new(JobOperationStatus.InvalidTransition, JobMessage.AlreadyArchived);
            var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
            var released = await destinationTests.ReleaseAsync(job.Destination!, job.DestinationSubfolder,
                installationId, job.Id, cancellationToken);
            if (!released.Succeeded && !(job.Lifecycle == JobLifecycle.Paused &&
                released.Result == OwnershipMarkerResult.Missing))
                return new(JobOperationStatus.OwnershipFailed, JobMessage.MarkerNotReleasedNotArchived);
            markerReleased = released.Result == OwnershipMarkerResult.Released;
            job.Archive();
            job.StopScheduling();
        }

        job.UpdatedAtUtc = now;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (claimed?.NewlyClaimed == true)
            {
                var cleanup = await ReleaseClaimAsync(job, job.Destination!);
                if (!cleanup.Succeeded)
                    throw new InvalidOperationException(
                        $"The lifecycle change was not saved and its new marker could not be released: {cleanup.Message.Key}", exception);
            }
            if (markerReleased)
            {
                var installationId = await installationIdentity.GetInstallationIdAsync(CancellationToken.None);
                var restored = await destinationTests.TestAndClaimAsync(job.Destination!, job.DestinationSubfolder,
                    job.SourcePath, installationId, job.Id, CancellationToken.None);
                if (!restored.Succeeded)
                    throw new InvalidOperationException(
                        $"The archive was not saved and the active database job's marker could not be restored: {restored.Message.Key}", exception);
            }
            throw;
        }
        // The lifecycle used to be interpolated into an English sentence by lowercasing the member
        // name. Each state now has its own message, because no other language forms the sentence that way.
        return new(
            JobOperationStatus.Succeeded,
            job.Lifecycle switch
            {
                JobLifecycle.Active => JobMessage.NowActive,
                JobLifecycle.Paused => JobMessage.NowPaused,
                _ => JobMessage.NowArchived
            },
            Details(job));
    }

    private async Task<JobOperationResult> RestoreCoreAsync(Guid id, bool restoreActive, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.Include(x => x.Destination).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null) return new(JobOperationStatus.NotFound, JobMessage.NotFound);
        if (job.Lifecycle != JobLifecycle.Archived)
            return new(JobOperationStatus.InvalidTransition, JobMessage.OnlyArchivedCanBeRestored);

        var now = timeProvider.GetUtcNow();
        job.Restore();
        var validation = await ValidateExistingAsync(job, cancellationToken);
        if (validation.Count != 0)
            return JobOperationResult.Validation(validation);

        var ownership = await ClaimDestinationAsync(job, job.Destination!,
            requireManagementVerification: false, cancellationToken);
        if (!ownership.Succeeded) return ownership.Result!;
        var claimed = ownership.Outcome;
        job.DestinationOwnershipKey = claimed!.OwnershipKey!;
        if (restoreActive && job.Destination!.Lifecycle == DestinationLifecycle.Active &&
            job.Destination.VerificationResult == DestinationVerificationResult.Succeeded)
        {
            job.Activate();
            job.ScheduleEffectiveFromUtc = now;
            job.BeginScheduling(now);
        }
        job.UpdatedAtUtc = now;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (claimed?.NewlyClaimed == true)
            {
                var installationId = await installationIdentity.GetInstallationIdAsync(CancellationToken.None);
                var released = await destinationTests.ReleaseAsync(job.Destination!, job.DestinationSubfolder,
                    installationId, job.Id, CancellationToken.None);
                if (!released.Succeeded)
                    throw new InvalidOperationException(
                        $"The restore was not saved and its new ownership marker could not be released: {released.Message.Key}",
                        exception);
            }
            if (exception is DbUpdateException)
                return new(JobOperationStatus.Conflict, JobMessage.RestoredNameOrFolderReserved);
            throw;
        }
        var message = job.Lifecycle == JobLifecycle.Active
            ? JobMessage.RestoredAndActivated
            : JobMessage.RestoredPaused;
        return new(JobOperationStatus.Succeeded, message, Details(job));
    }

    private async Task<JobOperationResult> TestDestinationCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.Include(x => x.Destination)
            .SingleOrDefaultAsync(x => x.Id == id && x.Lifecycle != JobLifecycle.Archived, cancellationToken);
        if (job?.Destination is null)
            return new(JobOperationStatus.NotFound, JobMessage.JobOrDestinationNotFound);
        if (job.Destination.VerificationResult != DestinationVerificationResult.Succeeded)
            return new(JobOperationStatus.DestinationVerificationFailed, JobMessage.VerifyDestinationRootFirst);

        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        var outcome = await destinationTests.TestAndClaimAsync(job.Destination, job.DestinationSubfolder,
            job.SourcePath, installationId, job.Id, cancellationToken);
        if (!outcome.Succeeded)
            return new(JobOperationStatus.DestinationVerificationFailed, outcome.Message);

        job.DestinationOwnershipKey = outcome.OwnershipKey!;
        job.UpdatedAtUtc = timeProvider.GetUtcNow();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (outcome.NewlyClaimed)
            {
                var released = await destinationTests.ReleaseAsync(job.Destination, job.DestinationSubfolder,
                    installationId, job.Id, CancellationToken.None);
                if (!released.Succeeded)
                    throw new InvalidOperationException(
                        $"The destination test was not saved and its new marker could not be released: {released.Message.Key}", exception);
            }
            if (exception is DbUpdateException)
                return new(JobOperationStatus.Conflict,
                    JobMessage.EffectiveFolderReserved);
            throw;
        }

        return new(JobOperationStatus.Succeeded, outcome.Message, Details(job));
    }

    private async Task<ValidationState> ValidateAsync(
        SaveJobCommand command,
        Guid? existingId,
        CancellationToken cancellationToken)
    {
        var errors = new List<JobValidationError>();
        var name = command.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200) errors.Add(new("Name", JobValidationMessage.NameRequired));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (name.Length > 0 && await context.Jobs.AsNoTracking().AnyAsync(
            x => x.Id != existingId && x.Name == name, cancellationToken))
            errors.Add(new("Name", JobValidationMessage.NameAlreadyExists));

        var source = ValidateSource(command.SourcePath, errors);
        if (command.Weekdays == ScheduledWeekdays.None || (command.Weekdays & ~AllWeekdays) != 0)
            errors.Add(new("Weekdays", JobValidationMessage.WeekdayRequired));
        if (command.RetentionCount < 1) errors.Add(new("RetentionCount", JobValidationMessage.RetentionAtLeastOne));
        var relative = WindowsPath.Relative(command.DestinationSubfolder);
        if (!relative.IsValid) errors.Add(new("DestinationSubfolder", relative.Error!));

        var destination = await context.Destinations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.DestinationId && x.Lifecycle == DestinationLifecycle.Active,
                cancellationToken);
        if (destination is null) errors.Add(new("DestinationId", JobValidationMessage.ActiveDestinationRequired));

        string? ownershipKey = null;
        if (destination is not null && source is not null && relative.IsValid)
        {
            var effective = await effectiveDestinations.ResolveAsync(destination, relative.Path, source,
                create: false, cancellationToken);
            if (!effective.Succeeded) errors.Add(new("DestinationSubfolder", effective.Message));
            else ownershipKey = effective.OwnershipKey;
        }
        return new(errors, name, source, relative.Path, ownershipKey, destination);
    }

    private async Task<List<JobValidationError>> ValidateExistingAsync(BackupJob job, CancellationToken cancellationToken)
    {
        var command = new SaveJobCommand(job.Name, job.SourcePath, job.DestinationId,
            job.DestinationSubfolder, job.Weekdays, job.ScheduledTime, job.RetentionCount);
        return (await ValidateAsync(command, job.Id, cancellationToken)).Errors;
    }

    private static string? ValidateSource(string sourcePath, List<JobValidationError> errors)
    {
        var local = WindowsPath.Local(sourcePath);
        if (!local.IsValid)
        {
            errors.Add(new("SourcePath", local.Error!));
            return null;
        }
        try
        {
            if (!Directory.Exists(local.Path)) throw new DirectoryNotFoundException();
            var metadata = WindowsFilesystemInterop.GetMetadata(local.Path!);
            if ((metadata.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SourcePathException(UiMessage.For(JobValidationMessage.SourceCannotBeReparsePoint));
            using var entries = Directory.EnumerateFileSystemEntries(local.Path!).GetEnumerator();
            _ = entries.MoveNext();
            return metadata.FinalPath;
        }
        catch (SourcePathException rejected)
        {
            errors.Add(new("SourcePath", rejected.Reason));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            errors.Add(new("SourcePath", JobValidationMessage.SourceMustBeReadableLocalFolder));
        }
        return null;
    }

    private async Task<(bool Succeeded, JobDestinationTestOutcome? Outcome, JobOperationResult? Result)> VerifyAndClaimAsync(
        BackupJob job,
        Destination destination,
        CancellationToken cancellationToken)
    {
        if (destination.Lifecycle != DestinationLifecycle.Active ||
            destination.VerificationResult != DestinationVerificationResult.Succeeded)
        {
            return (false, null, new(JobOperationStatus.DestinationVerificationFailed,
                JobMessage.DestinationNeedsManagementVerification));
        }
        return await ClaimDestinationAsync(job, destination, requireManagementVerification: true, cancellationToken);
    }

    private async Task<(bool Succeeded, JobDestinationTestOutcome? Outcome, JobOperationResult? Result)> ClaimDestinationAsync(
        BackupJob job,
        Destination destination,
        bool requireManagementVerification,
        CancellationToken cancellationToken)
    {
        if (requireManagementVerification && (destination.Lifecycle != DestinationLifecycle.Active ||
            destination.VerificationResult != DestinationVerificationResult.Succeeded))
        {
            return (false, null, new(JobOperationStatus.DestinationVerificationFailed,
                JobMessage.DestinationNeedsManagementVerification));
        }
        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
        var outcome = await destinationTests.TestAndClaimAsync(destination, job.DestinationSubfolder,
            job.SourcePath, installationId, job.Id, cancellationToken);
        return outcome.Succeeded
            ? (true, outcome, null)
            : (false, outcome, new(
                outcome.Result == JobDestinationTestResult.OwnershipConflict
                    ? JobOperationStatus.Conflict
                    : JobOperationStatus.DestinationVerificationFailed,
                outcome.Message));
    }

    private async Task<OwnershipMarkerOutcome> ReleaseClaimAsync(BackupJob job, Destination destination)
    {
        var installationId = await installationIdentity.GetInstallationIdAsync(CancellationToken.None);
        return await destinationTests.ReleaseAsync(destination, job.DestinationSubfolder,
            installationId, job.Id, CancellationToken.None);
    }

    private async Task<JobOperationResult> MutateAsync(
        Func<CancellationToken, Task<JobOperationResult>> action,
        CancellationToken cancellationToken)
    {
        var outcome = await mutationGate.ExecuteAsync(action, cancellationToken);
        return outcome.Succeeded
            ? outcome.Value!
            : new(JobOperationStatus.Busy, outcome.Message);
    }

    private static JobDetails Details(BackupJob job, Destination? destination = null) => new(
        job.Id, job.Name, job.Lifecycle, job.SourcePath, job.DestinationId,
        (destination ?? job.Destination)?.Name ?? "", job.DestinationSubfolder, job.Weekdays,
        job.ScheduledTime, job.ScheduleRevision, job.ScheduleEffectiveFromUtc, job.RetentionCount,
        job.ManagedArtifactCount, job.ManagedArtifactBytes, job.CreatedAtUtc, job.UpdatedAtUtc);

    private sealed record ValidationState(
        List<JobValidationError> Errors,
        string? Name,
        string? SourcePath,
        string? Subfolder,
        string? OwnershipKey,
        Destination? Destination);

    private sealed class JobOperationFailureException(JobOperationResult result) : Exception
    {
        public JobOperationResult Result { get; } = result;
    }
}
