using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FolderBackuper.Milestone0.Filesystem;

public sealed class LocalHostUncDetector(IEnumerable<string>? configuredAliases = null)
{
    private readonly HashSet<string> localNames = BuildLocalNames(configuredAliases ?? []);
    private readonly HashSet<IPAddress> localAddresses = BuildLocalAddresses();

    public bool IsHostedLocally(string uncPath)
    {
        if (!TryGetServer(uncPath, out var server))
        {
            throw new ArgumentException("A UNC path with a server and share is required.", nameof(uncPath));
        }

        var normalizedServer = server.Trim('[', ']').TrimEnd('.');
        if (localNames.Contains(normalizedServer) || IPAddress.TryParse(normalizedServer, out var address) && IsLocalAddress(address))
        {
            return true;
        }

        try
        {
            return Dns.GetHostAddresses(normalizedServer).Any(IsLocalAddress);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || localAddresses.Contains(NormalizeAddress(address));

    private static bool TryGetServer(string path, out string server)
    {
        server = string.Empty;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = path.IndexOf('\\', 2);
        if (separator <= 2 || separator == path.Length - 1)
        {
            return false;
        }

        server = path[2..separator];
        return true;
    }

    private static HashSet<string> BuildLocalNames(IEnumerable<string> aliases)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            Environment.MachineName,
            Dns.GetHostName()
        };

        try
        {
            names.Add(Dns.GetHostEntry(string.Empty).HostName);
        }
        catch (SocketException)
        {
            // Short host names and interface addresses still provide conservative coverage.
        }

        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)))
        {
            names.Add(alias.Trim().TrimEnd('.'));
        }

        return names;
    }

    private static HashSet<IPAddress> BuildLocalAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => NormalizeAddress(unicast.Address))
            .ToHashSet();
        addresses.Add(IPAddress.Loopback);
        addresses.Add(IPAddress.IPv6Loopback);
        return addresses;
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : new IPAddress(address.GetAddressBytes());
}
