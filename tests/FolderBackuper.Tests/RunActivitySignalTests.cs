using FolderBackuper.Features.Monitoring;

namespace FolderBackuper.Tests;

public sealed class RunActivitySignalTests
{
    [Fact]
    public void SignalReachesEverySubscriberUntilItIsDisposed()
    {
        var signal = new RunActivitySignal();
        var first = 0;
        var second = 0;
        using var firstSubscription = signal.Subscribe(() => { first++; return Task.CompletedTask; });
        var secondSubscription = signal.Subscribe(() => { second++; return Task.CompletedTask; });

        signal.Signal();
        Assert.Equal(1, first);
        Assert.Equal(1, second);

        secondSubscription.Dispose();
        signal.Signal();

        Assert.Equal(2, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void AFailingSubscriberNeverFaultsTheWriterThatRaisedTheSignal()
    {
        var signal = new RunActivitySignal();
        var reached = 0;
        using var failing = signal.Subscribe(() => throw new ObjectDisposedException("circuit"));
        using var healthy = signal.Subscribe(() => { reached++; return Task.CompletedTask; });

        signal.Signal();

        // The writer is a backup in progress: a page torn down between the change and the callback must
        // never surface as an execution failure.
        Assert.Equal(1, reached);
    }
}
