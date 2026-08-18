using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;

namespace FolderBackuper.Tests;

public sealed class DestinationArchiveServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string staging;
    private readonly string destination;

    public DestinationArchiveServiceTests()
    {
        source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
        destination = Directory.CreateDirectory(Path.Combine(root, "destination", "job")).FullName;
    }

    [Fact]
    public async Task Transfer_ValidatesAndRenamesWithoutLeavingPartial()
    {
        var fixture = await CreateStagingArchiveAsync();
        var progress = new List<BackupTransferProgress>();

        var result = await Service().TransferAsync(
            new LocalDestinationAdapter(),
            Configuration(),
            destination,
            fixture.Path,
            "Accounting",
            "source",
            fixture.Manifest,
            fixture.Ownership,
            new DateTimeOffset(2026, 8, 17, 23, 0, 0, TimeSpan.Zero),
            progress.Add);

        Assert.True(result.Succeeded);
        Assert.True(result.CommitStarted);
        Assert.Equal("Accounting_2026-08-17_23-00-00_" + fixture.Ownership.RunId.ToString("N")[..8] + ".zip",
            result.FinalFileName);
        Assert.True(File.Exists(result.FinalPath));
        Assert.True(File.Exists(fixture.Path));
        Assert.DoesNotContain(Directory.GetFiles(destination), path => path.EndsWith(".partial", StringComparison.Ordinal));
        Assert.Equal(new FileInfo(fixture.Path).Length, progress[^1].BytesTransferred);
        var validation = await new ZipArchiveService().ValidateAsync(result.FinalPath!, "source",
            fixture.Manifest, fixture.Ownership, RunPhase.Finalizing);
        Assert.Empty(validation);
    }

    [Fact]
    public async Task Transfer_ExistingFinalFileIsNotOverwrittenAndPartialIsRemoved()
    {
        var fixture = await CreateStagingArchiveAsync();
        var instant = new DateTimeOffset(2026, 8, 17, 23, 0, 0, TimeSpan.Zero);
        var finalName = ArchiveFileName.Create("Accounting", instant, fixture.Ownership.RunId);
        var existingPath = Path.Combine(destination, finalName);
        await File.WriteAllTextAsync(existingPath, "existing");

        var result = await Service().TransferAsync(
            new LocalDestinationAdapter(), Configuration(), destination, fixture.Path, "Accounting",
            "source", fixture.Manifest, fixture.Ownership, instant);

        Assert.False(result.Succeeded);
        Assert.Equal("existing", await File.ReadAllTextAsync(existingPath));
        Assert.DoesNotContain(Directory.GetFiles(destination), path => path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transfer_CancellationRemovesPartialAndDoesNotPublishFinal()
    {
        var fixture = await CreateStagingArchiveAsync(2 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().TransferAsync(
            new LocalDestinationAdapter(), Configuration(), destination, fixture.Path, "Accounting",
            "source", fixture.Manifest, fixture.Ownership, DateTimeOffset.UtcNow,
            snapshot =>
            {
                if (snapshot.BytesTransferred > 0) cancellation.Cancel();
            }, cancellation.Token));

        Assert.Empty(Directory.GetFiles(destination));
        Assert.True(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task Transfer_CancellationAtCommitBoundaryDoesNotPublishFinal()
    {
        var fixture = await CreateStagingArchiveAsync();
        using var cancellation = new CancellationTokenSource();
        var service = new DestinationArchiveService(new ZipArchiveService(),
            new CancellingCommitCoordinator(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TransferAsync(
            new LocalDestinationAdapter(), Configuration(), destination, fixture.Path, "Accounting",
            "source", fixture.Manifest, fixture.Ownership, DateTimeOffset.UtcNow,
            cancellationToken: cancellation.Token));

        Assert.Empty(Directory.GetFiles(destination));
    }

    [Fact]
    public async Task Transfer_PersistenceFailureAfterRenameLeavesFinalArchiveForRecovery()
    {
        var fixture = await CreateStagingArchiveAsync();
        var instant = DateTimeOffset.UtcNow;
        var coordinator = new FailingCommittedCoordinator();
        var service = new DestinationArchiveService(new ZipArchiveService(), coordinator);

        var exception = await Assert.ThrowsAsync<FinalCommitRecoveryRequiredException>(() => service.TransferAsync(
            new LocalDestinationAdapter(), Configuration(), destination, fixture.Path, "Accounting",
            "source", fixture.Manifest, fixture.Ownership, instant));

        Assert.Equal(fixture.Ownership.RunId, exception.RunId);
        Assert.NotNull(coordinator.Intent);
        Assert.True(File.Exists(Path.Combine(destination, coordinator.Intent!.FinalFileName)));
        Assert.DoesNotContain(Directory.GetFiles(destination), path => path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BackupFaultPoint.AfterPartialIntentPersisted, false, false)]
    [InlineData(BackupFaultPoint.AfterPartialFileCreated, true, false)]
    [InlineData(BackupFaultPoint.AfterCommitIntentPersisted, true, false)]
    [InlineData(BackupFaultPoint.AfterFinalRename, false, true)]
    public async Task TransferFaults_ExposeEveryDurableRecoveryWindow(
        BackupFaultPoint faultPoint,
        bool partialExists,
        bool finalExists)
    {
        var fixture = await CreateStagingArchiveAsync();
        var coordinator = new FailingCommittedCoordinator(failCommitted: false);
        var service = new DestinationArchiveService(new ZipArchiveService(), coordinator,
            new ThrowingFaultInjector(faultPoint));

        await Assert.ThrowsAsync<InjectedBackupFaultException>(() => service.TransferAsync(
            new LocalDestinationAdapter(), Configuration(), destination, fixture.Path, "Accounting",
            "source", fixture.Manifest, fixture.Ownership, DateTimeOffset.UtcNow));

        Assert.Equal(partialExists,
            Directory.GetFiles(destination).Any(path => path.EndsWith(".partial", StringComparison.Ordinal)));
        Assert.Equal(finalExists,
            Directory.GetFiles(destination).Any(path => path.EndsWith(".zip", StringComparison.Ordinal)));
    }

    private DestinationArchiveService Service() =>
        new(new ZipArchiveService(), new DirectBackupCommitCoordinator());

    private DestinationAccessConfiguration Configuration() =>
        new(DestinationType.Local, Directory.GetParent(destination)!.FullName, null, null);

    private async Task<(string Path, BackupManifest Manifest, ArchiveOwnership Ownership)> CreateStagingArchiveAsync(int length = 4096)
    {
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), new byte[length]);
        var scan = await new SourceManifestBuilder().BuildAsync(source);
        Assert.True(scan.CanProceed);
        var ownership = new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid());
        var archive = await new ZipArchiveService().CreateAsync(
            source, staging, "source", scan.Manifest!, ownership);
        Assert.True(archive.Succeeded);
        return (archive.StagingPath!, scan.Manifest!, ownership);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class CancellingCommitCoordinator(CancellationTokenSource cancellation)
        : IBackupCommitCoordinator
    {
        public ValueTask RecordPartialIntentAsync(Guid runId, string partialPath, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask BeginCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkCommittedAsync(Guid runId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask MarkFinalizationFailedAsync(Guid runId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FailingCommittedCoordinator(bool failCommitted = true) : IBackupCommitCoordinator
    {
        public BackupCommitIntent? Intent { get; private set; }
        public ValueTask RecordPartialIntentAsync(Guid runId, string partialPath, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask BeginCommitAsync(BackupCommitIntent intent, CancellationToken cancellationToken)
        {
            Intent = intent;
            return ValueTask.CompletedTask;
        }
        public ValueTask MarkCommittedAsync(Guid runId, CancellationToken cancellationToken) =>
            failCommitted
                ? ValueTask.FromException(new IOException("Injected persistence failure."))
                : ValueTask.CompletedTask;
        public ValueTask MarkFinalizationFailedAsync(Guid runId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingFaultInjector(BackupFaultPoint target) : IBackupFaultInjector
    {
        public ValueTask HitAsync(BackupFaultPoint point, Guid runId, CancellationToken cancellationToken)
        {
            if (point == target) throw new InjectedBackupFaultException(point);
            return ValueTask.CompletedTask;
        }
    }
}
