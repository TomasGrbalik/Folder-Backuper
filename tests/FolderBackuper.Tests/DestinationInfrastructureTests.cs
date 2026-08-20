using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;

namespace FolderBackuper.Tests;

public sealed class DestinationInfrastructureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FolderBackuper.Tests", Guid.NewGuid().ToString("N"));

    public DestinationInfrastructureTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Dpapi_RoundTripsWithoutStoringPlaintext()
    {
        var protector = new DpapiSecretProtector();
        var protectedData = protector.Protect("correct horse battery staple");
        Assert.Equal("correct horse battery staple", protector.Unprotect(protectedData));
        Assert.DoesNotContain("correct horse", Convert.ToBase64String(protectedData), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalAccessTest_VerifiesBytesCapacityAndExactCleanup()
    {
        var adapter = new LocalDestinationAdapter();
        var result = await adapter.TestAsync(new(DestinationType.Local, root, null, null), default);
        Assert.True(result.Succeeded, MessageAssert.Text(result.Message));
        Assert.NotNull(result.AvailableBytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task OwnershipMarker_PreventsSecondJobAndReleasesOnlyOwner()
    {
        var service = new OwnershipMarkerService();
        var installation = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Assert.Equal(OwnershipMarkerResult.Claimed, (await service.ClaimAsync(root, installation, owner, default)).Result);
        Assert.Equal(OwnershipMarkerResult.OwnedByAnotherJob, (await service.ClaimAsync(root, installation, Guid.NewGuid(), default)).Result);
        Assert.Equal(OwnershipMarkerResult.OwnedByAnotherJob, (await service.ReleaseAsync(root, installation, Guid.NewGuid(), default)).Result);
        Assert.Equal(OwnershipMarkerResult.Released, (await service.ReleaseAsync(root, installation, owner, default)).Result);
        Assert.False(File.Exists(Path.Combine(root, OwnershipMarkerService.MarkerName)));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
