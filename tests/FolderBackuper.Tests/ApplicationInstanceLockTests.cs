using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

public sealed class ApplicationInstanceLockTests
{
    [Fact]
    public void GetMutexName_IsStableAcrossEquivalentPathsAndDoesNotExposePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FolderBackuper-{Guid.NewGuid():N}");

        var first = ApplicationInstanceLock.GetMutexName(root);
        var second = ApplicationInstanceLock.GetMutexName(root.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.StartsWith(@"Global\FolderBackuper-", first, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(root), first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acquire_RejectsSecondOwnerForSameDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FolderBackuper-{Guid.NewGuid():N}");
        using var first = ApplicationInstanceLock.Acquire(root);

        var exception = Assert.Throws<InvalidOperationException>(() => ApplicationInstanceLock.Acquire(root));

        Assert.Contains("already using this data root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Acquire_AllowsDifferentDataRoots()
    {
        using var first = ApplicationInstanceLock.Acquire(Path.Combine(Path.GetTempPath(), $"FolderBackuper-{Guid.NewGuid():N}"));
        using var second = ApplicationInstanceLock.Acquire(Path.Combine(Path.GetTempPath(), $"FolderBackuper-{Guid.NewGuid():N}"));
    }
}
