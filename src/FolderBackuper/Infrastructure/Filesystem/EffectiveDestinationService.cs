using System.ComponentModel;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Localization;
using FolderBackuper.Infrastructure.Security;

namespace FolderBackuper.Infrastructure.Filesystem;

public enum EffectiveDestinationResult
{
    Ready,
    InvalidSubfolder,
    OutsideRoot,
    SourceOverlap,
    AccessFailed
}

public sealed record EffectiveDestinationOutcome(
    EffectiveDestinationResult Result,
    UiMessage Message,
    string? EffectivePath = null,
    string? OwnershipKey = null)
{
    public bool Succeeded => Result == EffectiveDestinationResult.Ready;

    public EffectiveDestinationOutcome(
        EffectiveDestinationResult result,
        EffectiveDestinationMessage message,
        string? effectivePath = null,
        string? ownershipKey = null)
        : this(result, UiMessage.For(message), effectivePath, ownershipKey)
    {
    }
}

public sealed class EffectiveDestinationService(
    IEnumerable<IDestinationAdapter> adapters,
    ISecretProtector secretProtector)
{
    public async Task<EffectiveDestinationOutcome> ResolveAsync(
        Destination destination,
        string? subfolder,
        string? localSourcePath = null,
        bool create = false,
        CancellationToken cancellationToken = default) =>
        await ResolveAgainstSourcesAsync(destination, subfolder,
            localSourcePath is null ? [] : [localSourcePath], create, cancellationToken);

    public async Task<EffectiveDestinationOutcome> ResolveAgainstSourcesAsync(
        Destination destination,
        string? subfolder,
        IEnumerable<string> localSourcePaths,
        bool create = false,
        CancellationToken cancellationToken = default)
    {
        var relative = WindowsPath.Relative(subfolder);
        if (!relative.IsValid)
        {
            return new(EffectiveDestinationResult.InvalidSubfolder, relative.Error!);
        }

        string root;
        string effective;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.RootPath));
            effective = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relative.Path!)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return new(EffectiveDestinationResult.InvalidSubfolder, EffectiveDestinationMessage.EffectivePathInvalid);
        }

        if (!PathOverlap.IsSameOrDescendant(effective, root))
        {
            return new(EffectiveDestinationResult.OutsideRoot, EffectiveDestinationMessage.MustRemainInsideRoot);
        }

        var configuration = Configuration(destination);
        try
        {
            return await Adapter(destination.Type).ExecuteAsync(configuration, () => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (create && !Directory.Exists(root))
                {
                    return new EffectiveDestinationOutcome(EffectiveDestinationResult.AccessFailed,
                        EffectiveDestinationMessage.RootMustExistFirst);
                }

                var resolvedRoot = PathOverlap.ResolveProjected(root);
                var projectedEffective = PathOverlap.ResolveProjected(effective);
                var resolvedSources = localSourcePaths.Select(PathOverlap.ResolveProjected).ToArray();
                var safetyFailure = ValidatePhysicalPaths(resolvedRoot, projectedEffective, resolvedSources);
                if (safetyFailure is not null)
                {
                    return safetyFailure;
                }

                if (create)
                {
                    Directory.CreateDirectory(effective);
                    resolvedRoot = PathOverlap.ResolveExisting(root);
                    projectedEffective = PathOverlap.ResolveExisting(effective);
                    safetyFailure = ValidatePhysicalPaths(resolvedRoot, projectedEffective, resolvedSources);
                    if (safetyFailure is not null) return safetyFailure;
                }

                if (Directory.Exists(effective))
                {
                    return new EffectiveDestinationOutcome(EffectiveDestinationResult.Ready,
                        EffectiveDestinationMessage.ReadyExisting, projectedEffective,
                        $"FS:{WindowsFilesystemInterop.GetIdentity(projectedEffective)}");
                }

                return new EffectiveDestinationOutcome(EffectiveDestinationResult.Ready,
                    EffectiveDestinationMessage.ReadyPathValid, projectedEffective,
                    $"PATH:{destination.Type}:{projectedEffective.ToUpperInvariant()}");
            }, cancellationToken));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            return new(EffectiveDestinationResult.AccessFailed, EffectiveDestinationMessage.AccessFailed);
        }
    }

    public DestinationAccessConfiguration Configuration(Destination destination) => new(
        destination.Type,
        destination.RootPath,
        destination.SmbUsername,
        destination.ProtectedPassword is null ? null : secretProtector.Unprotect(destination.ProtectedPassword));

    public IDestinationAdapter Adapter(DestinationType type) => adapters.Single(x => x.Type == type);

    private static EffectiveDestinationOutcome? ValidatePhysicalPaths(
        string resolvedRoot,
        string projectedEffective,
        IReadOnlyCollection<string> resolvedSources)
    {
        if (!PathOverlap.IsSameOrDescendant(projectedEffective, resolvedRoot))
        {
            return new(EffectiveDestinationResult.OutsideRoot,
                EffectiveDestinationMessage.ResolvesOutsideRoot);
        }

        if (resolvedSources.Any(source =>
                PathOverlap.Overlaps(resolvedRoot, source) ||
                PathOverlap.Overlaps(projectedEffective, source)))
        {
            return new(EffectiveDestinationResult.SourceOverlap,
                EffectiveDestinationMessage.OverlapsSource);
        }

        return null;
    }
}
