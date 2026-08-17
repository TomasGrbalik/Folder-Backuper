using Bunit;
using FolderBackuper.Components.Pages;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Scheduling;
using FolderBackuper.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

public sealed class JobsPageTests
{
    [Fact]
    public async Task Page_ShowsCurrentJobActionsAndOpensSinglePageForm()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var source = Path.Combine(database.Paths.Root, "source");
        var root = Path.Combine(database.Paths.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(root);
        var destinationService = DestinationService(database);
        var destination = await destinationService.CreateAsync(new("Primary", DestinationType.Local, root));
        var jobService = JobService(database);
        Assert.True((await jobService.CreateAsync(new("Documents", source, destination.Id, "Documents",
            ScheduledWeekdays.Monday, new(2, 0), 3))).Succeeded);

        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(jobService);
        context.Services.AddSingleton(destinationService);
        context.Services.AddSingleton(new ScheduleOccurrenceCalculator(TimeProvider.System));
        var component = context.Render<Jobs>();

        component.WaitForAssertion(() => Assert.Contains("Documents", component.Markup, StringComparison.Ordinal));
        Assert.Contains("Reactivate", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Archive", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Run now", component.Markup, StringComparison.OrdinalIgnoreCase);

        component.FindAll("button").Single(x => x.TextContent.Contains("New job", StringComparison.Ordinal)).Click();
        component.WaitForAssertion(() => Assert.Contains("Local scheduled time", component.Markup, StringComparison.Ordinal));
        Assert.Contains("Preview source", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Current timezone", component.Markup, StringComparison.Ordinal);
    }

    private static JobService JobService(TemporaryDatabase database)
    {
        var protector = new PassthroughProtector();
        var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], protector);
        return new(database.ContextFactory, new ConfigurationMutationGate(database.ContextFactory), effective,
            new JobDestinationTestService(effective, new OwnershipMarkerService()),
            new InstallationIdentityService(database.ContextFactory, TimeProvider.System), TimeProvider.System);
    }

    private static DestinationService DestinationService(TemporaryDatabase database)
    {
        var protector = new PassthroughProtector();
        return new(database.ContextFactory, protector, new NoLocalUnc(), [new LocalDestinationAdapter()], TimeProvider.System);
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => System.Text.Encoding.UTF8.GetString(protectedData);
    }
    private sealed class NoLocalUnc : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => false; }
}
