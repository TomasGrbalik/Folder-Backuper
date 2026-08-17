using System.IO.Compression;
using FolderBackuper.Features.Backups;

namespace FolderBackuper.Tests;

public sealed class ZipArchiveServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string staging;

    public ZipArchiveServiceTests()
    {
        source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
    }

    [Fact]
    public async Task CreateAndValidate_ProducesOwnedArchiveWithTopLevelLayoutAndEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var file = Path.Combine(source, "nested", "data.bin");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden | FileAttributes.System);
        var manifest = AssertManifest(await new SourceManifestBuilder().BuildAsync(source));
        var ownership = new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid());
        var progress = new List<BackupCopyProgress>();

        var result = await new ZipArchiveService().CreateAsync(
            source, staging, "source", manifest, ownership, progress.Add);

        Assert.True(result.Succeeded);
        Assert.True(result.ArchiveBytes > 0);
        Assert.NotEmpty(progress);
        Assert.Equal(manifest.SourceBytes, progress[^1].BytesProcessed);
        await using var stream = File.OpenRead(result.StagingPath!);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(ownership.Format(), archive.Comment);
        Assert.Contains(archive.Entries, entry => entry.FullName == "source/");
        Assert.Contains(archive.Entries, entry => entry.FullName == "source/empty/");
        var data = Assert.Single(archive.Entries, entry => entry.FullName == "source/nested/data.bin");
        Assert.Equal(4096, data.Length);

        var validation = await new ZipArchiveService().ValidateAsync(
            result.StagingPath!, "source", manifest, ownership, RunPhase.Compressing);
        Assert.Empty(validation);
    }

    [Fact]
    public async Task Create_CancellationRemovesIncompleteStagingArchive()
    {
        var file = Path.Combine(source, "large.bin");
        await File.WriteAllBytesAsync(file, new byte[2 * 1024 * 1024]);
        var manifest = AssertManifest(await new SourceManifestBuilder().BuildAsync(source));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ZipArchiveService().CreateAsync(
            source,
            staging,
            "source",
            manifest,
            new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid()),
            progress: snapshot =>
            {
                if (snapshot.BytesProcessed > 0) cancellation.Cancel();
            },
            cancellationToken: cancellation.Token));

        Assert.Empty(Directory.GetFiles(staging));
    }

    [Fact]
    public async Task Validate_RejectsCorruptionAndManifestMismatch()
    {
        var file = Path.Combine(source, "data.bin");
        await File.WriteAllBytesAsync(file, new byte[16]);
        var manifest = AssertManifest(await new SourceManifestBuilder().BuildAsync(source));
        var ownership = new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid());
        var service = new ZipArchiveService();
        var result = await service.CreateAsync(source, staging, "source", manifest, ownership);
        Assert.True(result.Succeeded);

        var changedManifest = new BackupManifest(manifest.Entries.Select(entry =>
            entry.IsFile ? entry with { Size = entry.Size + 1 } : entry));
        var mismatch = await service.ValidateAsync(
            result.StagingPath!, "source", changedManifest, ownership, RunPhase.Compressing);
        Assert.Contains(mismatch, problem => problem.Category == BackupProblemCategory.InvalidArchive);

        await File.WriteAllBytesAsync(result.StagingPath!, [1, 2, 3]);
        var corrupt = await service.ValidateAsync(
            result.StagingPath!, "source", manifest, ownership, RunPhase.Compressing);
        Assert.Contains(corrupt, problem => problem.Category == BackupProblemCategory.InvalidArchive);
    }

    [Fact]
    public async Task Create_RejectsSourceMetadataChangeAndPublishesNothing()
    {
        var file = Path.Combine(source, "data.bin");
        await File.WriteAllBytesAsync(file, new byte[32]);
        var manifest = AssertManifest(await new SourceManifestBuilder().BuildAsync(source));
        await File.AppendAllTextAsync(file, "changed");

        var result = await new ZipArchiveService().CreateAsync(
            source, staging, "source", manifest, new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.Category == BackupProblemCategory.SourceChanged);
        Assert.Empty(Directory.GetFiles(staging));
    }

    [Fact]
    public async Task Create_ReportsLockedSourceAndPublishesNothing()
    {
        var file = Path.Combine(source, "locked.bin");
        await File.WriteAllBytesAsync(file, new byte[32]);
        var manifest = AssertManifest(await new SourceManifestBuilder().BuildAsync(source));
        await using var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await new ZipArchiveService().CreateAsync(
            source, staging, "source", manifest, new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.Category == BackupProblemCategory.SourceInaccessible);
        Assert.Empty(Directory.GetFiles(staging));
    }

    private static BackupManifest AssertManifest(SourceManifestScanResult result)
    {
        Assert.True(result.CanProceed);
        return Assert.IsType<BackupManifest>(result.Manifest);
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch (FileNotFoundException) { }
        }
        Directory.Delete(root, recursive: true);
    }
}
