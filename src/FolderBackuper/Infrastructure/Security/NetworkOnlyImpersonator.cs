using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FolderBackuper.Infrastructure.Security;

public sealed class NetworkOnlyImpersonator : INetworkImpersonator
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinnt50 = 3;

    public async Task<T> RunAsync<T>(string username, string password, Func<Task<T>> action)
    {
        var domain = (string?)null;
        var account = username;
        var separator = username.IndexOf('\\');
        if (separator > 0)
        {
            domain = username[..separator];
            account = username[(separator + 1)..];
        }

        if (!LogonUser(account, domain, password, Logon32LogonNewCredentials, Logon32ProviderWinnt50, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Network-only logon failed.");
        }

        using (token)
        {
            return await WindowsIdentity.RunImpersonatedAsync(token, action);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string username, string? domain, string password, int logonType, int logonProvider,
        out SafeAccessTokenHandle token);
}
