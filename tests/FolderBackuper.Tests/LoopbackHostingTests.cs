using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.AspNetCore.HostFiltering;

namespace FolderBackuper.Tests;

public sealed class LoopbackHostingTests
{
    [Fact]
    public void ConfigureHostFiltering_ReplacesReadOnlyConfiguredHosts()
    {
        var options = new HostFilteringOptions { AllowedHosts = new[] { "*" } };

        LoopbackHosting.ConfigureHostFiltering(options);

        Assert.Equal(["localhost", "127.0.0.1", "[::1]"], options.AllowedHosts);
    }

    [Fact]
    public void GetUrls_ReturnsOnlyIpv4AndIpv6Loopback()
    {
        var urls = LoopbackHosting.GetUrls(5180);

        Assert.Equal(["http://127.0.0.1:5180", "http://[::1]:5180"], urls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void GetUrls_RejectsInvalidPort(int port) =>
        Assert.Throws<InvalidOperationException>(() => LoopbackHosting.GetUrls(port));
}
