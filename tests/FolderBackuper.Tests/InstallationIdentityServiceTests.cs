using FolderBackuper.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class InstallationIdentityServiceTests
{
    [Fact]
    public async Task GetInstallationId_IsDurableSingleton_WithRequiredRecipientList()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = new InstallationIdentityService(database.ContextFactory, TimeProvider.System);

        var identities = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => service.GetInstallationIdAsync()));

        Assert.All(identities, identity => Assert.Equal(identities[0], identity));
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var settings = await context.ApplicationSettings.SingleAsync();
        Assert.Equal("[]", settings.RecipientList);
        Assert.Equal(identities[0], settings.InstallationId);
    }
}
