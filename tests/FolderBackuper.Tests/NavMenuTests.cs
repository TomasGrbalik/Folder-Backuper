using Bunit;
using FolderBackuper.Components.Layout;
using FolderBackuper.Infrastructure.Versioning;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

public sealed class NavMenuTests
{
    [Fact]
    public void NavigationContainsFoundationDestinations()
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();

        var component = context.Render<NavMenu>();

        Assert.Contains("Dashboard", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Jobs", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Destinations", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Calendar", component.Markup, StringComparison.Ordinal);
        Assert.Contains("History", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Settings", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Loopback access only", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationNamesTheInstalledVersion()
    {
        // Someone looking at an installed instance has to be able to tell what it is without opening
        // a settings page or a file.
        using var context = new BunitContext();
        context.Services.AddMudServices();

        var component = context.Render<NavMenu>();

        Assert.Contains($"Version {ProductVersion.Display}", component.Markup, StringComparison.Ordinal);
    }
}
