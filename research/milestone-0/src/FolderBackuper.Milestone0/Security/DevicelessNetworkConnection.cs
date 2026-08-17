using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FolderBackuper.Milestone0.Security;

public sealed class DevicelessNetworkConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorNotConnected = 2250;
    private readonly string remoteName;
    private bool connected;

    private DevicelessNetworkConnection(string remoteName)
    {
        this.remoteName = remoteName;
    }

    public static DevicelessNetworkConnection Connect(string remoteName, string username, string password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = remoteName };
        var error = WNetAddConnection2(ref resource, password, username, 0);
        if (error != 0)
        {
            throw new Win32Exception(error, error == 1219
                ? "A conflicting SMB credential session already exists (error 1219)."
                : "The deviceless SMB connection failed.");
        }

        return new DevicelessNetworkConnection(remoteName) { connected = true };
    }

    public static void EnsureNoExistingConnection(string remoteName)
    {
        var length = 0;
        var error = WNetGetUser(remoteName, null, ref length);
        if (error == 0 || error == 234)
        {
            throw new InvalidOperationException("A pre-existing SMB connection targets this NAS root; disconnect it before running the fallback probe.");
        }

        if (error != ErrorNotConnected)
        {
            throw new Win32Exception(error, "Could not establish whether a pre-existing SMB connection targets this NAS root.");
        }
    }

    public void Dispose()
    {
        if (!connected)
        {
            return;
        }

        connected = false;
        var error = WNetCancelConnection2(remoteName, 0, force: false);
        if (error is not 0 and not 2250)
        {
            throw new Win32Exception(error, "The deviceless SMB connection could not be removed.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string password, string username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("mpr.dll", EntryPoint = "WNetGetUserW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetUser(string name, char[]? username, ref int length);
}
