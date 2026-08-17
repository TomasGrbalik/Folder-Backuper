using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FolderBackuper.Infrastructure.Filesystem;

public interface ILocalHostUncDetector { bool IsHostedLocally(string uncPath); }

public sealed class LocalHostUncDetector : ILocalHostUncDetector
{
    private readonly HashSet<string> names;
    private readonly HashSet<IPAddress> addresses;

    public LocalHostUncDetector(IEnumerable<string>? aliases = null)
    {
        names = new(StringComparer.OrdinalIgnoreCase) { "localhost", Environment.MachineName, Dns.GetHostName() };
        foreach (var alias in aliases ?? []) names.Add(alias.Trim().TrimEnd('.'));
        addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => Normalize(x.Address)).ToHashSet();
    }

    public bool IsHostedLocally(string uncPath)
    {
        var parts = uncPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("A UNC server and share are required.", nameof(uncPath));
        var server = parts[0].Trim('[', ']').TrimEnd('.');
        if (names.Contains(server)) return true;
        if (IPAddress.TryParse(server, out var address)) return IsLocal(address);
        try { return Dns.GetHostAddresses(server).Any(IsLocal); }
        catch (SocketException) { return false; }
    }

    private bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address) || addresses.Contains(Normalize(address));
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
