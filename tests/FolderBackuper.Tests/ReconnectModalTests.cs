using Bunit;
using FolderBackuper.Components.Layout;

namespace FolderBackuper.Tests;

public sealed class ReconnectModalTests
{
    [Fact]
    public void ModalPresentsReconnectFailureAndRestartStates()
    {
        using var context = new BunitContext();

        var component = context.Render<ReconnectModal>();

        Assert.NotNull(component.Find("#components-reconnect-modal"));
        Assert.Contains("Reconnecting to the backup service", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Connection could not be restored", component.Markup, StringComparison.Ordinal);
        Assert.Contains("The service restarted", component.Markup, StringComparison.Ordinal);
    }
}
