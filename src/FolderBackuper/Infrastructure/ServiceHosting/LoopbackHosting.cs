namespace FolderBackuper.Infrastructure.ServiceHosting;

public static class LoopbackHosting
{
    public static void ConfigureHostFiltering(Microsoft.AspNetCore.HostFiltering.HostFilteringOptions options) =>
        options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    public static string[] GetUrls(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("FolderBackuper:Port must be between 1 and 65535.");
        }

        return [$"http://127.0.0.1:{port}", $"http://[::1]:{port}"];
    }
}
