using System.Diagnostics;
using FolderBackuper.Features.Backups;

namespace FolderBackuper.Tests;

public sealed class BackupPrimitivesTests
{
    [Fact]
    public void OwnershipComment_RoundTripsAndRejectsWrongVersion()
    {
        var value = new ArchiveOwnership(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(ArchiveOwnership.TryParse(value.Format(), out var parsed));
        Assert.Equal(value, parsed);
        Assert.False(ArchiveOwnership.TryParse("FolderBackuper:v2;installation=x;run=x", out _));
    }

    [Fact]
    public void ZipLayout_UsesTopLevelAndDetectsCaseInsensitiveDuplicates()
    {
        var entries = new[]
        {
            new BackupManifestEntry("a.txt", BackupManifestEntryType.File, 2, DateTimeOffset.UnixEpoch, 0),
            new BackupManifestEntry("Folder", BackupManifestEntryType.Directory, 0, DateTimeOffset.UnixEpoch, 0)
        };
        Assert.Equal("Source/a.txt", ArchivePathLayout.CreateEntryName("Source", "a.txt", false));
        Assert.Equal("Source/Folder/", ArchivePathLayout.CreateEntryName("Source", "Folder", true));
        Assert.Throws<ArgumentException>(() => ArchivePathLayout.CreateEntryNames("Source", entries.Append(entries[0] with { RelativePath = "A.TXT" })));
        Assert.Throws<ArgumentException>(() => ArchivePathLayout.CreateEntryName("Source", "../escape", false));
    }

    [Fact]
    public void Manifest_IsSortedAndAggregated()
    {
        var manifest = new BackupManifest(new[]
        {
            new BackupManifestEntry("z", BackupManifestEntryType.Directory, 0, DateTimeOffset.UnixEpoch, 0),
            new BackupManifestEntry("a", BackupManifestEntryType.File, 7, DateTimeOffset.UnixEpoch, FileAttributes.Hidden)
        });
        Assert.Equal(["a", "z"], manifest.Entries.Select(e => e.RelativePath));
        Assert.Equal(1, manifest.FileCount); Assert.Equal(1, manifest.DirectoryCount); Assert.Equal(7, manifest.SourceBytes);
    }

    [Fact]
    public void ArchiveName_SanitizesReservedNamesAndRetainsRunSuffix()
    {
        var run = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var name = ArchiveFileName.Create("CON: report. ", new DateTimeOffset(2026, 8, 17, 1, 2, 3, TimeSpan.FromHours(2)), run);
        Assert.EndsWith("_2026-08-16_23-02-03_00112233.zip", name);
        Assert.True(name.Length <= 180);
        Assert.NotEqual(name, ArchiveFileName.Create("job", DateTimeOffset.UtcNow, Guid.NewGuid()));
        foreach (var reserved in new[] { "CON.txt", "NUL.log", "COM1.foo", "LPT9.anything" })
            Assert.StartsWith("_", ArchiveFileName.Create(reserved, DateTimeOffset.UtcNow, run));
        foreach (var reserved in new[] { "COM¹.txt", "COM².log", "COM³.bin", "LPT¹.txt", "LPT².log", "LPT³.bin" })
            Assert.StartsWith("_", ArchiveFileName.Create(reserved, DateTimeOffset.UtcNow, run));
    }

    [Fact]
    public void ProgressRegistry_UnsubscribesAndPhaseChangeBypassesRateLimit()
    {
        var clock = new TestTimeProvider();
        var registry = new BackupProgressRegistry(clock);
        var calls = 0;
        var runId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        using var subscription = registry.Subscribe(runId, _ => calls++);
        var first = Snapshot(runId, RunPhase.Scanning);
        Assert.True(registry.Publish(first));
        Assert.False(registry.Publish(first with { BytesProcessed = 1 }));
        Assert.True(registry.Publish(first with { Phase = RunPhase.Compressing }));
        Assert.True(registry.Publish(Snapshot(otherRunId, RunPhase.Scanning)));
        Assert.Equal(2, calls);
        Assert.Equal(otherRunId, registry.Current(otherRunId)!.RunId);
        Assert.Equal(runId, registry.Current(runId)!.RunId);
        subscription.Dispose();
        Assert.True(registry.Publish(first with { Phase = RunPhase.Transferring }, force: true));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ZipLayout_RejectsFileAncestorsButAllowsDirectoryAncestors()
    {
        var file = new BackupManifestEntry("A", BackupManifestEntryType.File, 1, DateTimeOffset.UnixEpoch, 0);
        var child = file with { RelativePath = "a/child" };
        Assert.Throws<ArgumentException>(() => ArchivePathLayout.CreateEntryNames("Source", [file, child]));
        Assert.Throws<ArgumentException>(() => ArchivePathLayout.CreateEntryNames("Source", [child, file]));
        var directory = new BackupManifestEntry("dir", BackupManifestEntryType.Directory, 0, DateTimeOffset.UnixEpoch, 0);
        Assert.Equal(2, ArchivePathLayout.CreateEntryNames("Source", [directory, child with { RelativePath = "dir/child" }]).Count);
    }

    [Fact]
    public void RollingThroughput_HandlesClockAndCounterEdgeCases()
    {
        var clock = new TestTimeProvider();
        var throughput = new RollingThroughput(clock);
        Assert.Equal(0, throughput.Add(0));
        Assert.Equal(0, throughput.Add(10));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(20, throughput.Add(20));
        Assert.Equal(0, throughput.Add(5));
        clock.MoveBackward(TimeSpan.FromSeconds(1));
        Assert.Equal(0, throughput.Add(6));
    }

    private static BackupProgressSnapshot Snapshot(Guid runId, RunPhase phase) => new(runId, phase, 0, 0, 0, 1, 1, 1, null, 0, 0, 0, 0, TimeSpan.Zero, null, true);

    private sealed class TestTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan value) => timestamp += (long)(value.TotalSeconds * Stopwatch.Frequency);
        public void MoveBackward(TimeSpan value) => timestamp -= (long)(value.TotalSeconds * Stopwatch.Frequency);
    }
}
