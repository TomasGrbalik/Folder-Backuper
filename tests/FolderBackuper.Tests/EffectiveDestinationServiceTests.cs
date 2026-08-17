using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;

namespace FolderBackuper.Tests;

public sealed class EffectiveDestinationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Effective.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> links = [];

    public EffectiveDestinationServiceTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task EffectiveLinkOutsideRoot_IsRejectedBeforeFolderMarkerOrProbeCreation()
    {
        var destinationRoot = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        if (!CreateDirectoryLink(Path.Combine(destinationRoot, "alias"), outside)) return;

        var outcome = await TestService().TestAndClaimAsync(Destination(destinationRoot),
            Path.Combine("alias", "created"), Directory.CreateDirectory(Path.Combine(root, "source")).FullName,
            Guid.NewGuid(), Guid.NewGuid());

        Assert.False(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(outside, "created")));
        Assert.Empty(Directory.EnumerateFiles(outside, ".folder-backuper-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EffectiveLinkIntoSource_IsRejectedBeforeAnySourceWrite()
    {
        var destinationRoot = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        if (!CreateDirectoryLink(Path.Combine(destinationRoot, "alias"), source)) return;

        var outcome = await TestService().TestAndClaimAsync(Destination(destinationRoot),
            Path.Combine("alias", "created"), source, Guid.NewGuid(), Guid.NewGuid());

        Assert.False(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(source, "created")));
        Assert.Empty(Directory.EnumerateFiles(source, ".folder-backuper-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DestinationRootLinkIntoSource_IsRejectedBeforeAnySourceWrite()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var destinationRoot = Path.Combine(root, "destination-link");
        if (!CreateDirectoryLink(destinationRoot, source)) return;

        var outcome = await TestService().TestAndClaimAsync(Destination(destinationRoot),
            "created", source, Guid.NewGuid(), Guid.NewGuid());

        Assert.False(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(source, "created")));
        Assert.Empty(Directory.EnumerateFiles(source, ".folder-backuper-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreCanceledMarkerClaim_DoesNotCreateMarker()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "marker")).FullName;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new OwnershipMarkerService().ClaimAsync(directory, Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));

        Assert.False(File.Exists(Path.Combine(directory, OwnershipMarkerService.MarkerName)));
    }

    private JobDestinationTestService TestService() => new(
        new EffectiveDestinationService([new LocalDestinationAdapter()], new PlainProtector()),
        new OwnershipMarkerService());

    private static Destination Destination(string path) => new()
    {
        Name = "Test",
        Type = DestinationType.Local,
        RootPath = path
    };

    private bool CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            links.Add(link);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            process!.WaitForExit();
            if (process.ExitCode != 0) return false;
            links.Add(link);
            return true;
        }
    }

    public void Dispose()
    {
        foreach (var link in links)
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => System.Text.Encoding.UTF8.GetString(protectedData);
    }
}
