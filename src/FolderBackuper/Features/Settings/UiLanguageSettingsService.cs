using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Localization;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Settings;

/// <summary>
/// Reads and writes the interface language.
/// </summary>
/// <remarks>
/// The value lives on the single application settings row for the same reason the update-check
/// preference does: it is a machine-wide preference a person set in the web interface, which must
/// survive a service restart and an upgrade. It is deliberately not a cookie or a browser value,
/// because notification email is written by a background worker that no browser is attached to and has
/// to come out in the same language as the interface that configured it.
/// </remarks>
public sealed class UiLanguageSettingsService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    InstallationIdentityService installationIdentity,
    TimeProvider timeProvider)
{
    public async Task<InterfaceLanguage> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.ApplicationSettings.AsNoTracking()
            .Select(x => x.UiLanguage)
            .FirstOrDefaultAsync(cancellationToken);

        // No settings row, or a row that predates anyone choosing a language, follows the machine.
        return InterfaceLanguages.Parse(stored);
    }

    /// <summary>Stores the language and applies it to the process, in that order.</summary>
    public async Task SetAsync(InterfaceLanguage language, CancellationToken cancellationToken = default)
    {
        // The settings row is created on demand elsewhere too; reuse that so a first save converges on
        // the same singleton primary key instead of inserting a competing row.
        await installationIdentity.GetInstallationIdAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await context.ApplicationSettings.SingleAsync(cancellationToken);
        var stored = language.ToStoredValue();
        if (settings.UiLanguage != stored)
        {
            settings.UiLanguage = stored;
            settings.UpdatedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken);
        }

        // Applied even when the stored value did not change, so that a process whose default drifted
        // from the database — a restart that raced the first request — converges rather than staying wrong.
        ApplicationCulture.Apply(language);
    }

    /// <summary>Applies the stored language to the process. Called once at startup, after migrations.</summary>
    public async Task ApplyStoredAsync(CancellationToken cancellationToken = default)
    {
        ApplicationCulture.Apply(await GetAsync(cancellationToken));
    }
}
