namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// Confirms that every address Kestrel actually bound is loopback.
/// </summary>
/// <remarks>
/// <c>UseUrls</c> writes the same host setting that <c>ASPNETCORE_URLS</c> maps to, so an operator
/// environment variable could otherwise widen the binding. Remote binding requires a separate
/// design covering authentication, authorization, HTTPS, and firewall rules, so a non-loopback
/// address is treated as a startup failure rather than a warning.
/// </remarks>
public static class LoopbackAddressGuard
{
    public static IReadOnlyList<string> FindNonLoopbackAddresses(IEnumerable<string>? addresses)
    {
        if (addresses is null)
        {
            return [];
        }

        return [.. addresses.Where(address => !IsLoopback(address))];
    }

    private static bool IsLoopback(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        return string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
