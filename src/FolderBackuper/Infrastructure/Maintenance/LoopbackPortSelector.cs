using System.Net;
using System.Net.Sockets;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Infrastructure.Maintenance;

public static class LoopbackPortSelector
{
    public static IReadOnlyList<int> CandidatePorts { get; } =
        [.. Enumerable.Range(
            WindowsServiceMetadata.DefaultPort,
            WindowsServiceMetadata.LastCandidatePort - WindowsServiceMetadata.DefaultPort + 1)];

    /// <summary>
    /// Reports whether Kestrel could bind both loopback addresses on the given port.
    /// </summary>
    /// <remarks>
    /// <c>ExclusiveAddressUse</c> matches Kestrel, so the answer reflects what the server itself
    /// would be able to do, including when another process holds the port exclusively on all
    /// interfaces.
    /// </remarks>
    public static bool IsAvailable(int port)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        return CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.IPv6Loopback, port);
    }

    public static int? FindAvailable(IEnumerable<int>? candidates = null) =>
        (candidates ?? CandidatePorts).Cast<int?>().FirstOrDefault(port => IsAvailable(port!.Value));

    private static bool CanBind(IPAddress address, int port)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            return true;
        }

        Socket? socket = null;
        try
        {
            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
