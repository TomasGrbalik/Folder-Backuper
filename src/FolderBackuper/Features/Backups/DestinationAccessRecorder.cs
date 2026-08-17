using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Features.Backups;

public sealed class DestinationAccessRecorder(
    IDbContextFactory<FolderBackuperDbContext> contextFactory,
    TimeProvider timeProvider)
{
    public async Task RecordAsync(
        Guid destinationId,
        DestinationAccessResult result,
        string? safeErrorSummary,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var destination = await context.Destinations.SingleAsync(item => item.Id == destinationId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        destination.LastAccessResult = result;
        destination.LastAccessSource = DestinationAccessSource.Backup;
        destination.LastAccessedAtUtc = now;
        destination.LastAccessErrorSummary = result == DestinationAccessResult.Succeeded ? null : safeErrorSummary;
        destination.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
    }
}
