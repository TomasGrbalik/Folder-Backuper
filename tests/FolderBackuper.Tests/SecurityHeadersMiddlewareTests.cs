using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.AspNetCore.Http;

namespace FolderBackuper.Tests;

public sealed class SecurityHeadersMiddlewareTests
{
    [Fact]
    public void Apply_AddsRestrictiveBaselineHeaders()
    {
        var headers = new HeaderDictionary();

        SecurityHeadersMiddleware.Apply(headers);

        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, headers["Content-Security-Policy"]);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("camera=(), microphone=(), geolocation=()", headers["Permissions-Policy"]);
    }
}
