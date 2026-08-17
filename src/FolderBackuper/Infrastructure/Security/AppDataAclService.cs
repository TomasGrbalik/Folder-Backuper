using System.Security.AccessControl;
using System.Security.Principal;
using FolderBackuper.Infrastructure.ServiceHosting;

namespace FolderBackuper.Infrastructure.Security;

public sealed class AppDataAclService(ApplicationPaths paths)
{
    public void Apply()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        // Console development must remain usable without broadening access to other users.
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
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
