using System.Security.Cryptography;
using System.Text;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Destinations;

public sealed class DestinationService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    ISecretProtector secretProtector,
    ILocalHostUncDetector localHostDetector,
    IEnumerable<IDestinationAdapter> adapters,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<DestinationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destinations = await context.Destinations.AsNoTracking()
            .Where(x => x.Lifecycle == DestinationLifecycle.Active).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var results = new List<DestinationSummary>(destinations.Count);
        foreach (var destination in destinations)
        {
            var configuration = Configuration(destination);
            var capacity = await Adapter(destination.Type).GetAvailableBytesAsync(configuration, cancellationToken);
            results.Add(ToSummary(destination, capacity));
        }
        return results;
    }

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

    public async Task<DestinationSummary> EditAsync(Guid id, SaveDestinationCommand command, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.Destinations.SingleAsync(x => x.Id == id && x.Lifecycle == DestinationLifecycle.Active, cancellationToken);
        var replacingPassword = !string.IsNullOrEmpty(command.Password);
        var normalized = Validate(command, passwordRequired: command.Type == DestinationType.Smb && destination.ProtectedPassword is null && !replacingPassword);
        await ValidateSourceOverlapAsync(context, normalized, cancellationToken);
        destination.Name = normalized.Name;
        destination.Type = normalized.Type;
        destination.RootPath = normalized.RootPath;
        destination.SmbUsername = normalized.Type == DestinationType.Smb ? normalized.SmbUsername : null;
        destination.ProtectedPassword = normalized.Type == DestinationType.Local ? null :
            replacingPassword ? secretProtector.Protect(normalized.Password!) : destination.ProtectedPassword;
        var fingerprint = Fingerprint(destination);
        if (!string.Equals(fingerprint, destination.VerificationFingerprint, StringComparison.Ordinal))
        {
            destination.VerificationResult = DestinationVerificationResult.Unverified;
            destination.VerifiedAtUtc = null;
        }
        destination.VerificationFingerprint = fingerprint;
        destination.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
        var capacity = await Adapter(destination.Type).GetAvailableBytesAsync(Configuration(destination), cancellationToken);
        return ToSummary(destination, capacity);
    }

    public async Task<DestinationOperationResult> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.Destinations.SingleAsync(x => x.Id == id && x.Lifecycle == DestinationLifecycle.Active, cancellationToken);
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
        destination.LastAccessResult, destination.LastAccessedAtUtc, capacity);
}
