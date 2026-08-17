using System.Security.AccessControl;
using FolderBackuper.Infrastructure.Security;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Tests;

public sealed class AppDataAclTests : IDisposable
{
    private readonly ApplicationPaths paths = ApplicationPaths.Resolve(Path.Combine(
        Path.GetTempPath(),
        "FolderBackuper.Tests",
        Guid.NewGuid().ToString("N")));

    [Fact]
    public void Apply_ProtectsRootAndGrantsInheritedChildAccess()
    {
        paths.CreateDirectories();

        new AppDataAclService(paths).Apply();

        var rootSecurity = new DirectoryInfo(paths.Root).GetAccessControl();
        Assert.True(rootSecurity.AreAccessRulesProtected);
        var childRules = new DirectoryInfo(paths.Data).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        Assert.Contains(childRules, rule => rule.IsInherited && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    public void Dispose()
    {
        if (Directory.Exists(paths.Root))
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }
}
