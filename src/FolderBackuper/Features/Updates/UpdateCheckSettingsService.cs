using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Updates;

/// <summary>
/// Reads and writes whether this installation looks for newer releases.
/// </summary>
/// <remarks>
/// The value lives on the single application settings row next to the notification configuration,
/// because it is the same kind of thing: a machine-wide preference a person set in the web
/// interface, which must survive an upgrade.
/// </remarks>
public sealed class UpdateCheckSettingsService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    InstallationIdentityService installationIdentity,
    TimeProvider timeProvider)
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.ApplicationSettings.AsNoTracking()
            .Select(x => (bool?)x.UpdateCheckEnabled)
            .FirstOrDefaultAsync(cancellationToken);

        // No settings row means nothing has been configured on this installation yet, and the check
        // is on by default.
        return stored ?? true;
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        // The settings row is created on demand elsewhere too; reuse that so a first save converges
        // on the same singleton primary key instead of inserting a competing row.
        await installationIdentity.GetInstallationIdAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.ApplicationSettings.SingleAsync(cancellationToken);
        if (settings.UpdateCheckEnabled == enabled)
        {
            return;
        }

        settings.UpdateCheckEnabled = enabled;
        settings.UpdatedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }
}
