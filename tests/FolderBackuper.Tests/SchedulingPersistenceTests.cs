using FolderBackuper.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class SchedulingPersistenceTests
{
    [Fact]
    public async Task ScheduleEffectiveFromUtc_IsRequiredAndRoundTrips()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var destination = DatabaseInitializationTests.Destination("Primary");
        var job = DatabaseInitializationTests.Job(destination.Id, "Documents");
        job.ScheduleEffectiveFromUtc = new DateTimeOffset(2026, 8, 17, 12, 34, 56, TimeSpan.Zero);

        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(destination, job);
            await context.SaveChangesAsync();
        }

        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(job.ScheduleEffectiveFromUtc, (await inspection.Jobs.SingleAsync()).ScheduleEffectiveFromUtc);
        Assert.False(inspection.Model.FindEntityType(typeof(Features.Jobs.BackupJob))!
            .FindProperty(nameof(Features.Jobs.BackupJob.ScheduleEffectiveFromUtc))!
            .IsNullable);
    }
}
