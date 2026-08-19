using System.Net;
using System.Net.Sockets;
using FolderBackuper.Infrastructure.Maintenance;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

public sealed class LoopbackPortSelectorTests
{
    [Fact]
    public void IsAvailable_ReportsAnOccupiedPortAsUnavailable()
    {
        var port = ReserveFreePort();
        using var listener = Listen(IPAddress.Loopback, port);

        Assert.False(LoopbackPortSelector.IsAvailable(port));
    }

    [Fact]
    public void IsAvailable_ReportsAPortHeldExclusivelyOnAllInterfacesAsUnavailable()
    {
        var port = ReserveFreePort();
        using var listener = Listen(IPAddress.Any, port, exclusive: true);

        Assert.False(LoopbackPortSelector.IsAvailable(port));
    }

    [Fact]
    public void IsAvailable_ReportsAFreePortAsAvailable() =>
        Assert.True(LoopbackPortSelector.IsAvailable(ReserveFreePort()));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void IsAvailable_RejectsAPortOutsideTheValidRange(int port) =>
        Assert.False(LoopbackPortSelector.IsAvailable(port));

    [Fact]
    public void FindAvailable_SkipsAnOccupiedCandidate()
    {
        var occupied = ReserveFreePort();
        var free = ReserveFreePort();
        using var listener = Listen(IPAddress.Loopback, occupied);

        Assert.Equal(free, LoopbackPortSelector.FindAvailable([occupied, free]));
    }

    [Fact]
    public void FindAvailable_ReturnsNullWhenEveryCandidateIsTaken()
    {
        var port = ReserveFreePort();
        using var listener = Listen(IPAddress.Loopback, port);

        Assert.Null(LoopbackPortSelector.FindAvailable([port]));
    }

    [Fact]
    public void CandidatePorts_StartAtTheDocumentedDefault()
    {
        Assert.Equal(WindowsServiceMetadata.DefaultPort, LoopbackPortSelector.CandidatePorts[0]);
        Assert.Equal(WindowsServiceMetadata.LastCandidatePort, LoopbackPortSelector.CandidatePorts[^1]);
    }

    private static Socket Listen(IPAddress address, int port, bool exclusive = false)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = exclusive
        };
        socket.Bind(new IPEndPoint(address, port));
        socket.Listen(1);
        return socket;
    }

    private static int ReserveFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
