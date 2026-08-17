using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FolderBackuper.Milestone0.Security;

public static class ProtectedSecretFile
{
    public static async Task WriteAsync(string path, string secret, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("A secret directory is required.");
        var marker = Path.Combine(directory, ".folder-backuper-milestone-0-secrets");
        if (Directory.Exists(directory) && !File.Exists(marker))
        {
            throw new InvalidOperationException("The secret parent must be a new directory or a directory previously created by this harness.");
        }

        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        if (!File.Exists(marker))
        {
            await File.WriteAllTextAsync(marker, "Folder Backuper Milestone 0 protected secrets", cancellationToken);
            File.SetAttributes(marker, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
        }

        await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(MachineSecretProtector.Protect(Encoding.UTF8.GetBytes(secret)), cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.SetAttributes(fullPath, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
    }

    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var protectedBytes = await File.ReadAllBytesAsync(Path.GetFullPath(path), cancellationToken);
        return Encoding.UTF8.GetString(MachineSecretProtector.Unprotect(protectedBytes));
    }

    private static void RestrictDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
        {
            AddFullControl(security, currentUser);
        }

        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void AddFullControl(DirectorySecurity security, IdentityReference identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
