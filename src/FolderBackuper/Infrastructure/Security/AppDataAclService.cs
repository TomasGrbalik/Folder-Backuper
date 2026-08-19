using System.Security.AccessControl;
using System.Security.Principal;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Infrastructure.Security;

public sealed class AppDataAclService(ApplicationPaths paths)
{
    public void Apply() => Apply(includeCurrentUser: true);

    /// <param name="includeCurrentUser">
    /// Console development must remain usable without broadening access to other users. The
    /// installer passes <see langword="false"/> so that the installing administrator's personal SID
    /// is not written into the machine-wide data root.
    /// </param>
    public void Apply(bool includeCurrentUser)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        if (includeCurrentUser && WindowsIdentity.GetCurrent().User is { } currentUser)
        {
            AddFullControl(security, currentUser);
        }

        new DirectoryInfo(paths.Root).SetAccessControl(security);
    }

    private static void AddFullControl(DirectorySecurity security, IdentityReference identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
