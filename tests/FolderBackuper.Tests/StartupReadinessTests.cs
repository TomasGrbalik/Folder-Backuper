using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

public sealed class StartupReadinessTests
{
    [Fact]
    public async Task Barrier_ReleasesWaitersWhenInitializationCompletes()
    {
        var barrier = new StartupRecoveryBarrier();
        var waiter = barrier.WaitAsync();

        barrier.Complete();

        Assert.True(await waiter);
        Assert.True(barrier.IsCompleted);
        Assert.False(barrier.IsFaulted);
    }

    [Fact]
    public async Task Barrier_ReleasesWaitersWhenInitializationFails()
    {
        var barrier = new StartupRecoveryBarrier();
        var waiter = barrier.WaitAsync();

        barrier.Fault(new InvalidOperationException("migration failed"));

        Assert.False(await waiter);
        Assert.True(barrier.IsFaulted);
        Assert.False(barrier.IsCompleted);
    }

    [Fact]
    public async Task Barrier_PropagatesCancellation()
    {
        var barrier = new StartupRecoveryBarrier();
        using var cancellation = new CancellationTokenSource();
        var waiter = barrier.WaitAsync(cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public void LoopbackAddressGuard_AcceptsTheAddressesKestrelIsConfiguredWith() =>
        Assert.Empty(LoopbackAddressGuard.FindNonLoopbackAddresses(
            LoopbackHosting.GetUrls(WindowsServiceMetadata.DefaultPort)));

    [Fact]
    public void LoopbackAddressGuard_AcceptsNoAddresses() =>
        Assert.Empty(LoopbackAddressGuard.FindNonLoopbackAddresses(null));

    [Theory]
    [InlineData("http://0.0.0.0:80")]
    [InlineData("http://[::]:80")]
    [InlineData("http://192.168.1.10:5180")]
    [InlineData("not a url")]
    public void LoopbackAddressGuard_RejectsAnAddressBeyondLoopback(string address) =>
        Assert.Equal([address], LoopbackAddressGuard.FindNonLoopbackAddresses([address]));

    [Fact]
    public void LoopbackAddressGuard_ReportsOnlyTheOffendingAddress()
    {
        var addresses = LoopbackAddressGuard.FindNonLoopbackAddresses(
            ["http://127.0.0.1:5180", "http://0.0.0.0:80", "http://[::1]:5180"]);

        Assert.Equal(["http://0.0.0.0:80"], addresses);
    }
}
