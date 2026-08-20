using System.Net;
using Bunit;
using FolderBackuper.Components.Pages;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Features.Settings;
using FolderBackuper.Features.Updates;
using FolderBackuper.Infrastructure.Versioning;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

public sealed class SettingsPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static BunitContext CreateContext(
        TemporaryDatabase database,
        TimeProvider clock,
        IRunNotificationSender? sender = null,
        FakeHttpMessageHandler? releaseFeed = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(database.ContextFactory);
        context.Services.AddSingleton(clock);
        context.Services.AddSingleton(new InstallationIdentityService(database.ContextFactory, clock));
        context.Services.AddSingleton(NotificationTestFactory.Settings(database, clock));
        context.Services.AddSingleton<IRunNotificationSender>(sender ?? new FakeRunNotificationSender());

        // The about card lives on the same page. Its release feed is a fake that answers "nothing
        // published" unless a test scripts something else, so no test here reaches the network.
        var store = new UpdateStatusStore();
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(UpdateTestFactory.Settings(database, clock));
        context.Services.AddSingleton(UpdateTestFactory.Checks(
            releaseFeed ?? FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, """{"message":"Not Found"}"""),
            database,
            store,
            clock));
        return context;
    }

    [Fact]
    public async Task Page_RendersTheNotificationFormAndTheExternalProcessingNotice()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Recipients", StringComparison.Ordinal));

        Assert.Contains("Email notifications", component.Markup, StringComparison.Ordinal);
        Assert.Contains("API key", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Sender address", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Not configured", component.Markup, StringComparison.Ordinal);

        // Resend's external processing and verified-domain requirements must be visible where the
        // user opts in, not only in the documentation.
        Assert.Contains("verified sending domain", component.Markup, StringComparison.Ordinal);
        Assert.Contains("external service", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_NeverRendersTheSavedApiKey()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Ready to send", StringComparison.Ordinal));

        Assert.DoesNotContain(NotificationTestFactory.ApiKey, component.Markup, StringComparison.Ordinal);
        Assert.Contains("never displayed", component.Markup, StringComparison.Ordinal);
        Assert.Contains("optional replacement", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_ShowsTheSavedConfigurationAsReadyToSend()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await NotificationTestFactory.ConfiguredSettingsAsync(database, clock, "operator@example.test");
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Ready to send", StringComparison.Ordinal));

        Assert.Contains("backups@example.test", component.Markup, StringComparison.Ordinal);
        Assert.Contains("operator@example.test", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Last saved", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestEmail_IsUnavailableUntilADeliverableConfigurationIsSaved()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Send test email", StringComparison.Ordinal));

        var button = component.FindAll("button")
            .Single(item => item.TextContent.Contains("Send test email", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));
    }

    [Fact]
    public async Task SendTestEmail_ReportsTheProviderResultFromTheSavedConfiguration()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var sender = new FakeRunNotificationSender(
            new NotificationSendResult(NotificationSendStatus.Delivered, "Accepted by the email provider."));
        await using var context = CreateContext(database, clock, sender);

        context.Render<MudPopoverProvider>();
        var dialogProvider = context.Render<MudDialogProvider>();
        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Ready to send", StringComparison.Ordinal));

        component.FindAll("button")
            .Single(item => item.TextContent.Contains("Send test email", StringComparison.Ordinal))
            .Click();

        dialogProvider.WaitForAssertion(() => Assert.Contains(
            "Accepted by the email provider.", dialogProvider.Markup, StringComparison.Ordinal));
        Assert.Equal(1, sender.TestCount);
    }

    [Fact]
    public async Task SendTestEmail_PresentsAnUncertainResultAsAWarningRatherThanAFailure()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        var sender = new FakeRunNotificationSender(
            new NotificationSendResult(NotificationSendStatus.Uncertain, "The provider did not respond in time."));
        await using var context = CreateContext(database, clock, sender);

        context.Render<MudPopoverProvider>();
        var dialogProvider = context.Render<MudDialogProvider>();
        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Ready to send", StringComparison.Ordinal));

        component.FindAll("button")
            .Single(item => item.TextContent.Contains("Send test email", StringComparison.Ordinal))
            .Click();

        dialogProvider.WaitForAssertion(() => Assert.Contains(
            "did not respond in time", dialogProvider.Markup, StringComparison.Ordinal));
        Assert.Contains("mud-alert-text-warning", dialogProvider.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_ReportsValidationErrorsAgainstTheFieldsThatCausedThem()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Save settings", StringComparison.Ordinal));

        // Turning notifications on with nothing filled in must explain what is missing.
        component.Find("input[type=checkbox]").Change(true);
        component.FindAll("button")
            .Single(item => item.TextContent.Contains("Save settings", StringComparison.Ordinal))
            .Click();

        component.WaitForAssertion(() => Assert.Contains(
            "Correct the highlighted fields", component.Markup, StringComparison.Ordinal));
        Assert.Contains("A Resend API key is required.", component.Markup, StringComparison.Ordinal);
        Assert.Contains("A verified sender address is required.", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Enter at least one recipient address.", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_PersistsTheConfigurationEnteredInTheForm()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Save settings", StringComparison.Ordinal));

        component.Find("input[type=checkbox]").Change(true);
        component.Find("input[type=password]").Change("re_typed_key");
        component.FindAll("input").First(input =>
            input.GetAttribute("placeholder") == "backups@example.com").Change("backups@example.test");
        component.Find("textarea").Change("operator@example.test");

        component.FindAll("button")
            .Single(item => item.TextContent.Contains("Save settings", StringComparison.Ordinal))
            .Click();

        component.WaitForAssertion(() => Assert.Contains(
            "Notification settings saved.", component.Markup, StringComparison.Ordinal));

        var saved = await NotificationTestFactory.Settings(database, clock).GetAsync();
        Assert.True(saved.IsDeliverable);
        Assert.Equal("backups@example.test", saved.FromAddress);
        Assert.Equal(["operator@example.test"], saved.Recipients);
        Assert.True(saved.HasApiKey);
    }

    [Fact]
    public async Task Page_NamesTheInstalledVersionAndWhatTheUpdateCheckSends()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await using var context = CreateContext(database, clock);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("About and updates", StringComparison.Ordinal));

        Assert.Contains($"Version {ProductVersion.Display}", component.Markup, StringComparison.Ordinal);

        // What the check discloses has to be visible where it is switched on and off, not only in the
        // documentation, exactly as the external-processing notice is for email.
        Assert.Contains("anonymous", component.Markup, StringComparison.Ordinal);
        Assert.Contains("no installation identity", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Nothing is ever", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckNow_ReportsANewerVersionAndOffersTheLink()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var feed = FakeHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            UpdateTestFactory.ReleasePayload("v99.0.0", "https://example.test/releases/99"));
        await using var context = CreateContext(database, clock, releaseFeed: feed);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Check now", StringComparison.Ordinal));

        component.FindAll("button")
            .Single(item => item.TextContent.Contains("Check now", StringComparison.Ordinal))
            .Click();

        component.WaitForAssertion(() => Assert.Contains(
            "Version 99.0.0 is available", component.Markup, StringComparison.Ordinal));
        Assert.Contains("The newest release is 99.0.0", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckNow_ReportsAnInconclusiveCheckWithoutClaimingAnythingAboutVersions()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var feed = FakeHttpMessageHandler.Throwing(new HttpRequestException("no route"));
        await using var context = CreateContext(database, clock, releaseFeed: feed);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Check now", StringComparison.Ordinal));

        component.FindAll("button")
            .Single(item => item.TextContent.Contains("Check now", StringComparison.Ordinal))
            .Click();

        // A failed check must not be dressed up as an error, and must not imply the build is current.
        component.WaitForAssertion(() => Assert.Contains(
            "The last check did not get an answer", component.Markup, StringComparison.Ordinal));
        Assert.Contains(
            "This says nothing about whether a newer version exists",
            component.Markup,
            StringComparison.Ordinal);
        Assert.DoesNotContain("is available", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingOffTheUpdateCheck_SavesAtOnceAndStopsAskingGitHub()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var feed = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, UpdateTestFactory.ReleasePayload("v99.0.0"));
        await using var context = CreateContext(database, clock, releaseFeed: feed);

        var component = context.Render<Settings>();
        component.WaitForState(() => component.Markup.Contains("Check GitHub for newer versions", StringComparison.Ordinal));

        // The second checkbox on the page is the update-check switch; the first belongs to the
        // notification form above it.
        component.FindAll("input[type=checkbox]")[1].Change(false);

        component.WaitForAssertion(() => Assert.Contains(
            "Version checking is off", component.Markup, StringComparison.Ordinal));

        // It saves itself, so the notification form's Save button has nothing to do with it.
        Assert.False(await UpdateTestFactory.Settings(database, clock).IsEnabledAsync());
        Assert.Empty(feed.Requests);
    }
}
