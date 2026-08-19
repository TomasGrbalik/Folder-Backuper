using System.Net.Sockets;
using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.Data.Sqlite;

namespace FolderBackuper.Tests;

public sealed class StartupFailureTests
{
    [Fact]
    public void Classify_RecognizesAKestrelBindConflict()
    {
        var exception = new IOException(
            "Failed to bind to address http://127.0.0.1:5180.",
            new SocketException((int)SocketError.AddressAlreadyInUse));

        Assert.Equal(StartupFailure.PortBinding, StartupFailureClassifier.Classify(exception));
    }

    [Fact]
    public void Classify_RecognizesADeniedBind()
    {
        var exception = new IOException(
            "Failed to bind to address http://127.0.0.1:5180.",
            new SocketException((int)SocketError.AccessDenied));

        Assert.Equal(StartupFailure.PortBinding, StartupFailureClassifier.Classify(exception));
    }

    [Fact]
    public void Classify_RecognizesADatabaseFailure() =>
        Assert.Equal(
            StartupFailure.Migration,
            StartupFailureClassifier.Classify(new SqliteException("no such column", 1)));

    [Fact]
    public void Classify_RecognizesADeniedAccessControlChange() =>
        Assert.Equal(
            StartupFailure.AccessControl,
            StartupFailureClassifier.Classify(new UnauthorizedAccessException()));

    [Fact]
    public void Classify_PrefersAnExplicitlyClassifiedFailure()
    {
        var exception = new StartupFailureException(
            StartupFailure.SingleInstance,
            new InvalidOperationException("Another Folder Backuper process is already using this data root."));

        Assert.Equal(StartupFailure.SingleInstance, StartupFailureClassifier.Classify(exception));
    }

    [Fact]
    public void Classify_UnwrapsAnAggregateException()
    {
        var exception = new AggregateException(
            new InvalidOperationException(),
            new IOException("bind", new SocketException((int)SocketError.AddressAlreadyInUse)));

        Assert.Equal(StartupFailure.PortBinding, StartupFailureClassifier.Classify(exception));
    }

    [Fact]
    public void Classify_FallsBackToUnexpected() =>
        Assert.Equal(StartupFailure.Unexpected, StartupFailureClassifier.Classify(new InvalidOperationException()));

    [Fact]
    public void EventIds_AreDistinct()
    {
        StartupFailure[] failures =
        [
            StartupFailure.DataRoot,
            StartupFailure.AccessControl,
            StartupFailure.SingleInstance,
            StartupFailure.Migration,
            StartupFailure.PortBinding,
            StartupFailure.NonLoopbackBinding,
            StartupFailure.Unexpected
        ];

        Assert.Equal(failures.Length, failures.Select(failure => failure.EventId).Distinct().Count());
        Assert.Equal(failures.Length, failures.Select(failure => failure.Category).Distinct().Count());
    }

    [Fact]
    public void Compose_NamesTheCategoryAndTheEventId()
    {
        var text = StartupFailureReporter.Compose(StartupFailure.PortBinding, new InvalidOperationException("boom"));

        Assert.Contains("PortBinding", text, StringComparison.Ordinal);
        Assert.Contains("1005", text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }
}
