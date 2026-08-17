using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Settings;

public sealed class InstallationIdentityService(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    TimeProvider timeProvider)
{
    // A stable primary key makes concurrent first-use insertion converge on one row.
    private static readonly Guid SingletonId = Guid.Parse("6b4502fd-b6bc-43b3-847b-a51b1c4f1948");

    public async Task<Guid> GetInstallationIdAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ApplicationSettings.AsNoTracking()
            .Select(x => (Guid?)x.InstallationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var now = timeProvider.GetUtcNow();
        var settings = new ApplicationSettings
        {
            Id = SingletonId,
            InstallationId = Guid.NewGuid(),
            RecipientList = "[]",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.ApplicationSettings.Add(settings);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return settings.InstallationId;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            return await context.ApplicationSettings.AsNoTracking()
                .Where(x => x.Id == SingletonId)
                .Select(x => x.InstallationId)
                .SingleAsync(cancellationToken);
        }
    }
}
