using System.Security.Cryptography;
using System.Text;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Destinations;

public sealed class DestinationService
{
    private readonly IDbContextFactory<FolderBackuperDbContext> contextFactory;
    private readonly ISecretProtector secretProtector;
    private readonly ILocalHostUncDetector localHostDetector;
    private readonly IReadOnlyList<IDestinationAdapter> adapters;
    private readonly TimeProvider timeProvider;
    private readonly ConfigurationMutationGate mutationGate;
    private readonly EffectiveDestinationService effectiveDestinations;
    private readonly JobDestinationTestService jobDestinationTests;
    private readonly InstallationIdentityService installationIdentity;

    public DestinationService(
        IDbContextFactory<FolderBackuperDbContext> contextFactory,
        ISecretProtector secretProtector,
        ILocalHostUncDetector localHostDetector,
        IEnumerable<IDestinationAdapter> adapters,
        TimeProvider timeProvider,
        ConfigurationMutationGate mutationGate,
        EffectiveDestinationService effectiveDestinations,
        JobDestinationTestService jobDestinationTests,
        InstallationIdentityService installationIdentity)
    {
        this.contextFactory = contextFactory;
        this.secretProtector = secretProtector;
        this.localHostDetector = localHostDetector;
        this.adapters = adapters.ToList();
        this.timeProvider = timeProvider;
        this.mutationGate = mutationGate;
        this.effectiveDestinations = effectiveDestinations;
        this.jobDestinationTests = jobDestinationTests;
        this.installationIdentity = installationIdentity;
    }

    public DestinationService(
        IDbContextFactory<FolderBackuperDbContext> contextFactory,
        ISecretProtector secretProtector,
        ILocalHostUncDetector localHostDetector,
        IEnumerable<IDestinationAdapter> adapters,
        TimeProvider timeProvider)
    {
        var adapterList = adapters.ToList();
        this.contextFactory = contextFactory;
        this.secretProtector = secretProtector;
        this.localHostDetector = localHostDetector;
        this.adapters = adapterList;
        this.timeProvider = timeProvider;
        mutationGate = new ConfigurationMutationGate(contextFactory);
        effectiveDestinations = new EffectiveDestinationService(adapterList, secretProtector);
        jobDestinationTests = new JobDestinationTestService(effectiveDestinations, new OwnershipMarkerService());
        installationIdentity = new InstallationIdentityService(contextFactory, timeProvider);
    }

    public async Task<IReadOnlyList<DestinationSummary>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destinations = await context.Destinations.AsNoTracking()
            .Where(x => includeArchived || x.Lifecycle == DestinationLifecycle.Active)
            .OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var results = new List<DestinationSummary>(destinations.Count);
        foreach (var destination in destinations)
        {
            var configuration = Configuration(destination);
            var capacity = await Adapter(destination.Type).GetAvailableBytesAsync(configuration, cancellationToken);
            results.Add(ToSummary(destination, capacity));
        }
        return results;
    }

    public Task<IReadOnlyList<DestinationSummary>> ListAsync(CancellationToken cancellationToken) =>
        ListAsync(false, cancellationToken);

    public async Task<DestinationSummary> CreateAsync(SaveDestinationCommand command, CancellationToken cancellationToken = default)
    {
        var normalized = Validate(command, passwordRequired: command.Type == DestinationType.Smb);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateSourceOverlapAsync(context, normalized, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var destination = new Destination
        {
            Name = normalized.Name,
            Type = normalized.Type,
            RootPath = normalized.RootPath,
            SmbUsername = normalized.SmbUsername,
            ProtectedPassword = normalized.Password is null ? null : secretProtector.Protect(normalized.Password),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        destination.VerificationFingerprint = Fingerprint(destination);
        context.Destinations.Add(destination);
        await context.SaveChangesAsync(cancellationToken);
        var capacity = await Adapter(destination.Type).GetAvailableBytesAsync(Configuration(destination), cancellationToken);
        return ToSummary(destination, capacity);
    }

    public async Task<DestinationOperationResult> EditAsync(
        Guid id,
        SaveDestinationCommand command,
        CancellationToken cancellationToken = default)
    {
        var gated = await mutationGate.ExecuteAsync(ct => EditCoreAsync(id, command, ct), cancellationToken);
        return gated.Succeeded ? gated.Value! : DestinationOperationResult.Failure(
            DestinationOperationStatus.Busy, gated.Message);
    }

    public async Task<DestinationOperationResult> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var gated = await mutationGate.ExecuteAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var destination = await context.Destinations.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (destination is null)
                return DestinationOperationResult.Failure(DestinationOperationStatus.NotFound, "The destination was not found.");
            if (destination.Lifecycle == DestinationLifecycle.Archived)
                return DestinationOperationResult.Failure(DestinationOperationStatus.InvalidTransition, "The destination is already archived.");
            var references = await context.Jobs.AsNoTracking().CountAsync(x => x.DestinationId == id &&
                (x.Lifecycle == JobLifecycle.Active || x.Lifecycle == JobLifecycle.Paused), ct);
            if (references != 0)
                return DestinationOperationResult.Failure(DestinationOperationStatus.Referenced,
                    $"The destination is referenced by {references} active or paused job(s) and cannot be archived.");
            destination.Archive();
            destination.UpdatedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(ct);
            return DestinationOperationResult.Completed("The destination was archived.", ToSummary(destination, null));
        }, cancellationToken);
        return gated.Succeeded ? gated.Value! : DestinationOperationResult.Failure(
            DestinationOperationStatus.Busy, gated.Message);
    }

    public async Task<DestinationOperationResult> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var gated = await mutationGate.ExecuteAsync(async ct =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var destination = await context.Destinations.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (destination is null)
                return DestinationOperationResult.Failure(DestinationOperationStatus.NotFound, "The destination was not found.");
            if (destination.Lifecycle != DestinationLifecycle.Archived)
                return DestinationOperationResult.Failure(DestinationOperationStatus.InvalidTransition, "Only an archived destination can be restored.");
            destination.Restore();
            destination.VerificationResult = DestinationVerificationResult.Unverified;
            destination.VerifiedAtUtc = null;
            destination.UpdatedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(ct);
            return DestinationOperationResult.Completed("The destination was restored and must be verified.", ToSummary(destination, null));
        }, cancellationToken);
        return gated.Succeeded ? gated.Value! : DestinationOperationResult.Failure(
            DestinationOperationStatus.Busy, gated.Message);
    }

    private async Task<DestinationOperationResult> EditCoreAsync(
        Guid id,
        SaveDestinationCommand command,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.Destinations.SingleOrDefaultAsync(
            x => x.Id == id && x.Lifecycle == DestinationLifecycle.Active, cancellationToken);
        if (destination is null)
            return DestinationOperationResult.Failure(DestinationOperationStatus.NotFound, "The active destination was not found.");
        var replacingPassword = !string.IsNullOrEmpty(command.Password);
        var normalized = Validate(command, passwordRequired: command.Type == DestinationType.Smb && destination.ProtectedPassword is null && !replacingPassword);
        await ValidateSourceOverlapAsync(context, normalized, cancellationToken);
        var rootChanged = destination.Type != normalized.Type ||
            !string.Equals(destination.RootPath, normalized.RootPath, StringComparison.OrdinalIgnoreCase);
        if (rootChanged && !command.ConfirmRootPathChange)
            return DestinationOperationResult.Failure(DestinationOperationStatus.ValidationFailed,
                "Changing the destination root path or type requires explicit confirmation.");
        var accessChanged = rootChanged ||
            !string.Equals(destination.SmbUsername, normalized.SmbUsername, StringComparison.Ordinal) ||
            replacingPassword;

        var jobs = accessChanged
            ? await context.Jobs.Where(x => x.DestinationId == id &&
                (x.Lifecycle == JobLifecycle.Active || x.Lifecycle == JobLifecycle.Paused)).ToListAsync(cancellationToken)
            : [];
        var original = Copy(destination);
        var replacementPassword = normalized.Type == DestinationType.Local ? null :
            replacingPassword ? secretProtector.Protect(normalized.Password!) : destination.ProtectedPassword;
        var replacement = Copy(destination);
        replacement.Type = normalized.Type;
        replacement.RootPath = normalized.RootPath;
        replacement.SmbUsername = normalized.Type == DestinationType.Smb ? normalized.SmbUsername : null;
        replacement.ProtectedPassword = replacementPassword;

        var replacementKeys = new Dictionary<Guid, string>();
        var newClaims = new List<(BackupJob Job, JobDestinationTestOutcome Outcome)>();
        var releasedJobs = new List<BackupJob>();
        var artifacts = new List<BackupArtifact>();
        Guid installationId = default;
        var pausedCount = 0;
        try
        {
            if (rootChanged)
            {
                installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken);
                foreach (var job in jobs)
                {
                    var claimed = await jobDestinationTests.TestAndClaimAsync(replacement, job.DestinationSubfolder,
                        job.SourcePath, installationId, job.Id, cancellationToken);
                    if (!claimed.Succeeded)
                    {
                        throw new DestinationOperationFailureException(DestinationOperationResult.Failure(
                            claimed.Result == JobDestinationTestResult.OwnershipConflict
                                ? DestinationOperationStatus.Conflict
                                : DestinationOperationStatus.OwnershipFailed,
                            $"The new destination folder for job '{job.Name}' could not be claimed: {claimed.Message}"));
                    }
                    newClaims.Add((job, claimed));
                    replacementKeys[job.Id] = claimed.OwnershipKey!;
                }

                var otherKeys = await context.Jobs.AsNoTracking().Where(x => x.DestinationId != id &&
                        (x.Lifecycle == JobLifecycle.Active || x.Lifecycle == JobLifecycle.Paused))
                    .Select(x => x.DestinationOwnershipKey).ToListAsync(cancellationToken);
                if (replacementKeys.Values.Any(key => otherKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new DestinationOperationFailureException(DestinationOperationResult.Failure(
                        DestinationOperationStatus.Conflict,
                        "A folder under the new destination configuration is already reserved by another job."));
                }

                foreach (var job in jobs)
                {
                    if (string.Equals(job.DestinationOwnershipKey, replacementKeys[job.Id],
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    var released = await jobDestinationTests.ReleaseAsync(original, job.DestinationSubfolder,
                        installationId, job.Id, cancellationToken);
                    var missingAllowed = job.Lifecycle == JobLifecycle.Paused &&
                        released.Result == OwnershipMarkerResult.Missing;
                    if (!released.Succeeded && !missingAllowed)
                    {
                        throw new DestinationOperationFailureException(DestinationOperationResult.Failure(
                            DestinationOperationStatus.OwnershipFailed,
                            $"The old ownership marker for job '{job.Name}' could not be released: {released.Message}"));
                    }
                    if (released.Result == OwnershipMarkerResult.Released) releasedJobs.Add(job);
                }
            }

            destination.Name = normalized.Name;
            destination.Type = normalized.Type;
            destination.RootPath = normalized.RootPath;
            destination.SmbUsername = normalized.Type == DestinationType.Smb ? normalized.SmbUsername : null;
            destination.ProtectedPassword = replacementPassword;
            var fingerprint = Fingerprint(destination);
            if (accessChanged)
            {
                destination.VerificationResult = DestinationVerificationResult.Unverified;
                destination.VerifiedAtUtc = null;
            }
            destination.VerificationFingerprint = fingerprint;
            var now = timeProvider.GetUtcNow();
            destination.UpdatedAtUtc = now;
            foreach (var job in jobs)
            {
                if (job.Lifecycle == JobLifecycle.Active)
                {
                    job.Pause();
                    pausedCount++;
                }
                if (rootChanged) job.DestinationOwnershipKey = replacementKeys[job.Id];
                job.UpdatedAtUtc = now;
            }
            if (rootChanged && jobs.Count != 0)
            {
                artifacts = await context.BackupArtifacts.Include(x => x.Run)
                    .Where(x => x.Run != null && x.Run.Job != null &&
                        x.Run.Job.DestinationId == id && x.State == ArtifactState.Retained)
                    .ToListAsync(cancellationToken);
            }
            foreach (var artifact in artifacts) artifact.MarkUnmanaged(now);
            if (rootChanged)
            {
                foreach (var job in jobs)
                {
                    job.ManagedArtifactCount = 0;
                    job.ManagedArtifactBytes = 0;
                    job.LatestArtifactBytes = null;
                    job.StorageConfirmedAtUtc = null;
                }
            }

            await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
            await context.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (rootChanged)
                await CompensateRootClaimsOrThrowAsync(newClaims, releasedJobs, replacement, original,
                    installationId, exception);
            if (exception is DestinationOperationFailureException failure) return failure.Result;
            if (exception is DbUpdateException)
                return DestinationOperationResult.Failure(DestinationOperationStatus.Conflict,
                    "The destination update conflicts with an existing name or folder reservation.");
            throw;
        }
        // The mutation is committed; caller cancellation must not make a successful change look failed.
        var capacity = await Adapter(destination.Type).GetAvailableBytesAsync(Configuration(destination), CancellationToken.None);
        var message = accessChanged
            ? $"The destination was updated and verification invalidated. Paused {pausedCount} job(s); marked {artifacts.Count} retained artifact(s) unmanaged."
            : "The destination was updated.";
        return DestinationOperationResult.Completed(message, ToSummary(destination, capacity), pausedCount, artifacts.Count);
    }

    public async Task<DestinationOperationResult> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var gated = await mutationGate.ExecuteAsync(ct => TestCoreAsync(id, ct), cancellationToken);
        return gated.Succeeded ? gated.Value! : DestinationOperationResult.Failure(
            DestinationOperationStatus.Busy, gated.Message);
    }

    private async Task<DestinationOperationResult> TestCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.Destinations.SingleOrDefaultAsync(
            x => x.Id == id && x.Lifecycle == DestinationLifecycle.Active, cancellationToken);
        if (destination is null)
            return DestinationOperationResult.Failure(DestinationOperationStatus.NotFound, "The active destination was not found.");
        var result = await Adapter(destination.Type).TestAsync(Configuration(destination), cancellationToken);
        var now = timeProvider.GetUtcNow();
        destination.VerificationResult = result.Succeeded ? DestinationVerificationResult.Succeeded : DestinationVerificationResult.Failed;
        destination.VerifiedAtUtc = now;
        destination.LastAccessResult = result.Result;
        destination.LastAccessSource = DestinationAccessSource.Management;
        destination.LastAccessedAtUtc = now;
        destination.LastAccessErrorSummary = result.Succeeded ? null : result.Message;
        destination.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task RecordAccessAsync(
        Guid id,
        DestinationAccessResult result,
        DestinationAccessSource source,
        string? safeErrorSummary = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.Destinations.SingleAsync(x => x.Id == id, cancellationToken);
        destination.LastAccessResult = result;
        destination.LastAccessSource = source;
        destination.LastAccessedAtUtc = timeProvider.GetUtcNow();
        destination.LastAccessErrorSummary = result == DestinationAccessResult.Succeeded ? null : safeErrorSummary;
        destination.UpdatedAtUtc = destination.LastAccessedAtUtc.Value;
        await context.SaveChangesAsync(cancellationToken);
    }

    private SaveDestinationCommand Validate(SaveDestinationCommand command, bool passwordRequired)
    {
        var name = command.Name.Trim();
        if (name.Length is 0 or > 200) throw new ArgumentException("A destination name of at most 200 characters is required.");
        var path = command.Type == DestinationType.Local ? WindowsPath.Local(command.RootPath) : WindowsPath.Unc(command.RootPath);
        if (!path.IsValid) throw new ArgumentException(path.Error);
        if (command.Type == DestinationType.Smb)
        {
            if (localHostDetector.IsHostedLocally(path.Path!)) throw new ArgumentException("An SMB destination hosted by this computer must be configured as a local path.");
            if (string.IsNullOrWhiteSpace(command.SmbUsername)) throw new ArgumentException("An SMB username is required.");
            if (passwordRequired && string.IsNullOrEmpty(command.Password)) throw new ArgumentException("An SMB password is required.");
        }
        return command with { Name = name, RootPath = path.Path!, SmbUsername = command.SmbUsername?.Trim() };
    }

    private DestinationAccessConfiguration Configuration(Destination destination) => new(
        destination.Type, destination.RootPath, destination.SmbUsername,
        destination.ProtectedPassword is null ? null : secretProtector.Unprotect(destination.ProtectedPassword));

    private IDestinationAdapter Adapter(DestinationType type) => adapters.Single(x => x.Type == type);

    private static Destination Copy(Destination destination) => new()
    {
        Name = destination.Name,
        Type = destination.Type,
        RootPath = destination.RootPath,
        SmbUsername = destination.SmbUsername,
        ProtectedPassword = destination.ProtectedPassword,
        VerificationFingerprint = destination.VerificationFingerprint,
        VerificationResult = destination.VerificationResult,
        VerifiedAtUtc = destination.VerifiedAtUtc
    };

    private async Task CompensateRootClaimsOrThrowAsync(
        IEnumerable<(BackupJob Job, JobDestinationTestOutcome Outcome)> newClaims,
        IEnumerable<BackupJob> releasedJobs,
        Destination replacement,
        Destination original,
        Guid installationId,
        Exception? cause = null)
    {
        var failures = new List<string>();
        foreach (var job in releasedJobs)
        {
            try
            {
                var restored = await jobDestinationTests.TestAndClaimAsync(original, job.DestinationSubfolder,
                    job.SourcePath, installationId, job.Id, CancellationToken.None);
                if (!restored.Succeeded) failures.Add($"old marker for '{job.Name}' was not restored: {restored.Message}");
            }
            catch (Exception exception)
            {
                failures.Add($"old marker for '{job.Name}' was not restored: {exception.Message}");
            }
        }
        foreach (var claim in newClaims.Where(x => x.Outcome.NewlyClaimed))
        {
            try
            {
                var released = await jobDestinationTests.ReleaseAsync(replacement, claim.Job.DestinationSubfolder,
                    installationId, claim.Job.Id, CancellationToken.None);
                if (!released.Succeeded) failures.Add($"new marker for '{claim.Job.Name}' was not released: {released.Message}");
            }
            catch (Exception exception)
            {
                failures.Add($"new marker for '{claim.Job.Name}' was not released: {exception.Message}");
            }
        }
        if (failures.Count != 0)
            throw new InvalidOperationException(
                $"The destination update failed and ownership compensation was incomplete: {string.Join("; ", failures)}",
                cause);
    }

    private sealed class DestinationOperationFailureException(DestinationOperationResult result) : Exception
    {
        public DestinationOperationResult Result { get; } = result;
    }

    private static async Task ValidateSourceOverlapAsync(
        FolderBackuperDbContext context,
        SaveDestinationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Type != DestinationType.Local)
        {
            return;
        }

        var sources = await context.Jobs.AsNoTracking()
            .Select(x => x.SourcePath)
            .ToListAsync(cancellationToken);
        if (PathOverlap.FindDestinationOverlap(command.RootPath, sources) is { } source)
        {
            throw new ArgumentException($"The destination overlaps configured source '{source}'.");
        }
    }

    private static string Fingerprint(Destination destination)
    {
        var secretHash = destination.ProtectedPassword is null ? "" : Convert.ToHexString(SHA256.HashData(destination.ProtectedPassword));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{destination.Type}\n{destination.RootPath.ToUpperInvariant()}\n{destination.SmbUsername?.ToUpperInvariant()}\n{secretHash}")));
    }

    private static DestinationSummary ToSummary(Destination destination, long? capacity) => new(
        destination.Id, destination.Name, destination.Type, destination.RootPath, destination.SmbUsername,
        destination.ProtectedPassword is not null, destination.VerificationResult, destination.VerifiedAtUtc,
        destination.LastAccessResult, destination.LastAccessedAtUtc, capacity, destination.Lifecycle);
}
