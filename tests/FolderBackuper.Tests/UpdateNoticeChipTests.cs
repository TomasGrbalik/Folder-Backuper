using Bunit;
using FolderBackuper.Components.Layout;
using FolderBackuper.Features.Updates;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

/// <summary>
/// The notice is the only place the product tells someone a newer version exists, so both of its
/// states matter: it must appear when there is something to say and take no space when there is not.
/// </summary>
public sealed class UpdateNoticeChipTests
{
    private const string ReleaseUrl = "https://example.test/releases/1.9.0";

    [Fact]
    public void Chip_SaysNothingBeforeAnyCheckHasRun()
    {
        var component = Render(UpdateStatus.ForInstalledBuild());

        Assert.Empty(component.Markup.Trim());
    }

    [Fact]
    public void Chip_SaysNothingWhenTheInstallationIsCurrent()
    {
        var component = Render(new UpdateStatus(
            "1.9.0", "abc1234", "1.9.0", ReleaseUrl, false, DateTimeOffset.UtcNow, null));

        Assert.Empty(component.Markup.Trim());
    }

    [Fact]
    public void Chip_SaysNothingWhenACheckCouldNotAnswer()
    {
        // An unreachable release feed says nothing about whether an update exists, so it must not
        // produce a notice, and it must not produce an error either.
        var component = Render(UpdateStatus.ForInstalledBuild() with { LastProblem = UiMessage.For(UpdateProblemMessage.Unreachable) });

        Assert.Empty(component.Markup.Trim());
    }

    [Fact]
    public void Chip_NamesTheNewVersionAndLinksToIt()
    {
        var component = Render(new UpdateStatus(
            "1.8.0", "abc1234", "1.9.0", ReleaseUrl, true, DateTimeOffset.UtcNow, null));

        Assert.Contains("Version 1.9.0 available", component.Markup, StringComparison.Ordinal);

        var link = component.Find("a.update-notice");
        Assert.Equal(ReleaseUrl, link.GetAttribute("href"));
        // The web interface is loopback-only, so the release page has to open outside it, and the
        // opener must not be reachable from the new tab.
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"), StringComparison.Ordinal);
    }

    [Fact]
    public void Chip_FallsBackToTheReleasesPageWithoutASpecificUrl()
    {
        var component = Render(new UpdateStatus(
            "1.8.0", null, "1.9.0", null, true, DateTimeOffset.UtcNow, null));

        Assert.Equal(
            UpdateCheckMetadata.ReleasesPageUrl,
            component.Find("a.update-notice").GetAttribute("href"));
    }

    [Fact]
    public void Chip_AppearsAsSoonAsACheckFindsSomething()
    {
        // The Check now button is on the settings page while the notice is in the app bar, so the
        // notice has to react to the store rather than to its own initialisation.
        using var context = new BunitContext();
        context.Services.AddMudServices();
        var store = new UpdateStatusStore();
        context.Services.AddSingleton(store);

        var component = context.Render<UpdateNoticeChip>();
        Assert.Empty(component.Markup.Trim());

        store.Publish(new UpdateStatus("1.8.0", null, "1.9.0", ReleaseUrl, true, DateTimeOffset.UtcNow, null));

        component.WaitForAssertion(() =>
            Assert.Contains("Version 1.9.0 available", component.Markup, StringComparison.Ordinal));
    }

    private static IRenderedComponent<UpdateNoticeChip> Render(UpdateStatus status)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        var store = new UpdateStatusStore();
        store.Publish(status);
        context.Services.AddSingleton(store);
        return context.Render<UpdateNoticeChip>();
    }
}
