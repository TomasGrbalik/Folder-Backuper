using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace FolderBackuper.Infrastructure.ServiceHosting;

public sealed class ApplicationInstanceLock : IDisposable
{
    private readonly Mutex mutex;
    private bool ownsMutex;

    private ApplicationInstanceLock(Mutex mutex)
    {
        this.mutex = mutex;
        ownsMutex = true;
    }

    public static ApplicationInstanceLock Acquire(string dataRoot)
    {
        var name = GetMutexName(dataRoot);
        var security = CreateSecurity();
        var mutex = MutexAcl.Create(initiallyOwned: true, name, out var createdNew, security);

        if (!createdNew)
        {
            mutex.Dispose();
            throw new InvalidOperationException("Another Folder Backuper process is already using this data root.");
        }

        return new ApplicationInstanceLock(mutex);
    }

    public static string GetMutexName(string dataRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $@"Global\FolderBackuper-{hash}";
    }

    public void Dispose()
    {
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }

        mutex.Dispose();
        GC.SuppressFinalize(this);
    }

    private static MutexSecurity CreateSecurity()
    {
        var security = new MutexSecurity();
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            AddFullControl(security, currentUser);
        }

        return security;
    }

    private static void AddFullControl(MutexSecurity security, IdentityReference identity) =>
        security.AddAccessRule(new MutexAccessRule(identity, MutexRights.FullControl, AccessControlType.Allow));
}
