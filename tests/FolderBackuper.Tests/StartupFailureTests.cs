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
    public void StartupFailureLog_IsRolledOnceItReachesItsLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderBackuper-StartupFailureLog-" + Guid.NewGuid().ToString("N"));
        var paths = ApplicationPaths.Resolve(root);
        try
        {
            paths.CreateDirectories();
            var filePath = Path.Combine(paths.Logs, "startup-failure.log");
            var previousPath = Path.Combine(paths.Logs, "startup-failure.previous.log");
            File.WriteAllBytes(filePath, new byte[ApplicationLogging.StartupFailureLogSizeLimitBytes]);

            StartupFailureReporter.Report(StartupFailure.DataRoot, new InvalidOperationException("boom"), paths);

            Assert.True(File.Exists(previousPath));
            Assert.Equal(ApplicationLogging.StartupFailureLogSizeLimitBytes, new FileInfo(previousPath).Length);
            Assert.Contains("boom", File.ReadAllText(filePath), StringComparison.Ordinal);
            Assert.True(new FileInfo(filePath).Length < ApplicationLogging.StartupFailureLogSizeLimitBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LogDirectoryBounds_AreFiniteAndDocumented()
    {
        Assert.True(ApplicationLogging.FileSizeLimitBytes > 0);
        Assert.True(ApplicationLogging.RetainedFileCountLimit > 0);
        Assert.Equal(
            ApplicationLogging.FileSizeLimitBytes * ApplicationLogging.RetainedFileCountLimit,
            ApplicationLogging.MaximumLogDirectoryBytes);
        Assert.Equal(TimeSpan.FromDays(30), ApplicationLogging.RetainedFileTimeLimit);
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
