using FolderBackuper.Milestone0.Security;

namespace FolderBackuper.Milestone0.Tests;

public sealed class ProtectedSecretFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-Test-{Guid.NewGuid():N}");

    public ProtectedSecretFileTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task WriteAsync_RoundTripsInHarnessOwnedDirectory()
    {
        var path = Path.Combine(root, "secrets", "nas-password.bin");

        await ProtectedSecretFile.WriteAsync(path, "not-a-real-password", CancellationToken.None);
        var recovered = await ProtectedSecretFile.ReadAsync(path, CancellationToken.None);

        Assert.Equal("not-a-real-password", recovered);
        Assert.True(File.Exists(Path.Combine(root, "secrets", ".folder-backuper-milestone-0-secrets")));
    }

    [Fact]
    public async Task WriteAsync_RefusesToReplaceAclOnUnownedDirectory()
    {
        var unrelated = Directory.CreateDirectory(Path.Combine(root, "unrelated"));
        File.WriteAllText(Path.Combine(unrelated.FullName, "existing.txt"), "keep");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtectedSecretFile.WriteAsync(Path.Combine(unrelated.FullName, "secret.bin"), "secret", CancellationToken.None));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated.FullName, "existing.txt")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
