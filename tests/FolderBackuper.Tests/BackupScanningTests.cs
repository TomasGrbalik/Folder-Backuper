using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

public sealed class BackupScanningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));

    public BackupScanningTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ManifestBuilder_IncludesOrdinaryContentAndPreservesMetadata()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source"));
        var empty = Directory.CreateDirectory(Path.Combine(source.FullName, "empty"));
        var hidden = Path.Combine(source.FullName, "hidden.bin");
        await File.WriteAllBytesAsync(hidden, new byte[7]);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden | FileAttributes.System);
        var modified = new DateTime(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(hidden, modified);
        var originalAttributes = File.GetAttributes(hidden);

        var result = await new SourceManifestBuilder().BuildAsync(source.FullName);

        Assert.True(result.CanProceed);
        Assert.NotNull(result.Manifest);
        Assert.Equal(1, result.Manifest.FileCount);
        Assert.Equal(1, result.Manifest.DirectoryCount);
        Assert.Equal(7, result.Manifest.SourceBytes);
        Assert.Contains(result.Manifest.Entries, entry => entry.RelativePath == empty.Name && !entry.IsFile);
        Assert.Contains(result.Manifest.Entries, entry => entry.RelativePath == "hidden.bin" && entry.IsFile);
        Assert.Equal(originalAttributes, File.GetAttributes(hidden));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(hidden));
    }

    [Fact]
    public async Task ManifestBuilder_ReportsAndSkipsReparsePoints()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source"));
        var target = Directory.CreateDirectory(Path.Combine(root, "target"));
        await File.WriteAllTextAsync(Path.Combine(target.FullName, "outside.txt"), "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source.FullName, "link"), target.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var result = await new SourceManifestBuilder().BuildAsync(source.FullName);

        Assert.True(result.CanProceed);
        Assert.Empty(result.Manifest!.Entries);
        var warning = Assert.Single(result.Problems);
        Assert.Equal(BackupProblemSeverity.Warning, warning.Severity);
        Assert.Equal(BackupProblemCategory.SkippedReparsePoint, warning.Category);
        Assert.Equal("link", warning.Path);
    }

    [Fact]
    public void ManifestComparison_DetectsAddedRemovedAndChangedEntries()
    {
        var builder = new SourceManifestBuilder();
        var expected = Manifest(
            Entry("removed.txt", 1),
            Entry("changed.txt", 2));
        var actual = Manifest(
            Entry("changed.txt", 3),
            Entry("added.txt", 4));

        var problems = builder.Compare(expected, actual);

        Assert.Equal(3, problems.Count);
        Assert.All(problems, problem => Assert.Equal(BackupProblemCategory.SourceChanged, problem.Category));
        Assert.Equal(["added.txt", "changed.txt", "removed.txt"],
            problems.Select(problem => problem.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Preflight_VerifiesSafePathsAndExistingOwnershipWithoutWriting()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source"));
        var destinationRoot = Directory.CreateDirectory(Path.Combine(root, "destination"));
        var effective = Directory.CreateDirectory(Path.Combine(destinationRoot.FullName, "job"));
        var paths = ApplicationPaths.Resolve(Path.Combine(root, "app-data"));
        paths.CreateDirectories();
        var jobId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var marker = await new OwnershipMarkerService().ClaimAsync(effective.FullName, installationId, jobId, CancellationToken.None);
        Assert.True(marker.Succeeded);
        var before = Directory.GetFileSystemEntries(effective.FullName).Order(StringComparer.Ordinal).ToArray();
        var destination = Destination(destinationRoot.FullName);
        var job = Job(jobId, source.FullName, destination.Id);

        var result = await Service(paths).ValidateAsync(job, destination, [source.FullName], installationId);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.TrimEndingDirectorySeparator(source.FullName), result.SourcePath);
        Assert.Equal(WindowsFilesystemInterop.GetFinalPath(effective.FullName), result.EffectiveDestinationPath);
        Assert.Equal(before, Directory.GetFileSystemEntries(effective.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Preflight_RejectsStagingOrDestinationOverlapAgainstAnySource()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source"));
        var otherSource = Directory.CreateDirectory(Path.Combine(root, "other-source"));
        var destination = Destination(otherSource.FullName);
        var job = Job(Guid.NewGuid(), source.FullName, destination.Id, "");
        var paths = ApplicationPaths.Resolve(Path.Combine(root, "app-data"));
        paths.CreateDirectories();

        var destinationOverlap = await Service(paths).ValidateAsync(
            job, destination, [source.FullName, otherSource.FullName], Guid.NewGuid());
        Assert.Contains(destinationOverlap.Problems, problem => problem.Category == BackupProblemCategory.InvalidPath);

        var stagingInSource = paths with { Staging = source.FullName };
        var stagingOverlap = await Service(stagingInSource).ValidateAsync(
            job, Destination(Path.Combine(root, "unrelated")), [source.FullName], Guid.NewGuid());
        Assert.Contains(stagingOverlap.Problems,
            problem => problem.Operation == BackupOperation.ValidateStagingOverlap && problem.Category == BackupProblemCategory.InvalidPath);
    }

    private BackupPreflightService Service(ApplicationPaths paths)
    {
        var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], new PlaintextProtector());
        return new(paths, effective, new OwnershipMarkerService(), new NeverLocalDetector());
    }

    private static Destination Destination(string rootPath) => new()
    {
        Name = "Destination",
        Type = DestinationType.Local,
        RootPath = rootPath,
        VerificationResult = DestinationVerificationResult.Succeeded,
        VerificationFingerprint = "verified"
    };

    private static BackupJob Job(Guid id, string sourcePath, Guid destinationId, string subfolder = "job") => new()
    {
        Id = id,
        Name = "Job",
        SourcePath = sourcePath,
        DestinationId = destinationId,
        DestinationSubfolder = subfolder,
        Weekdays = ScheduledWeekdays.Monday,
        ScheduledTime = new TimeOnly(1, 0),
        DestinationOwnershipKey = "test"
    };

    private static BackupManifest Manifest(params BackupManifestEntry[] entries) => new(entries);

    private static BackupManifestEntry Entry(string path, long size) =>
        new(path, BackupManifestEntryType.File, size, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch (FileNotFoundException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PlaintextProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => throw new NotSupportedException();
        public string Unprotect(byte[] protectedData) => throw new NotSupportedException();
    }

    private sealed class NeverLocalDetector : ILocalHostUncDetector
    {
        public bool IsHostedLocally(string uncPath) => false;
    }
}
