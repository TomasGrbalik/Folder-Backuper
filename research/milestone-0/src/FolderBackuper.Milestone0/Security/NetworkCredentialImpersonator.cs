using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FolderBackuper.Milestone0.Security;

public static class NetworkCredentialImpersonator
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinnt50 = 3;

    public static async Task<T> RunAsync<T>(
        string username,
        string? domain,
        string password,
        Func<Task<T>> action)
    {
        SplitQualifiedUsername(username, ref domain, out var accountName);
        if (!LogonUser(accountName, domain, password, Logon32LogonNewCredentials, Logon32ProviderWinnt50, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Network-only logon failed.");
        }

        using (token)
        {
            return await WindowsIdentity.RunImpersonatedAsync(token, action);
        }
    }

    private static void SplitQualifiedUsername(string username, ref string? domain, out string accountName)
    {
        var separator = username.IndexOf('\\');
        if (separator > 0)
        {
            domain ??= username[..separator];
            accountName = username[(separator + 1)..];
            return;
        }

        accountName = username;
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string username,
        string? domain,
        string password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);
}
