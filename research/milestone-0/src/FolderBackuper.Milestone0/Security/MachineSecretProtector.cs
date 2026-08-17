using System.Security.Cryptography;
using System.Text;

namespace FolderBackuper.Milestone0.Security;

public static class MachineSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FolderBackuper.Milestone0.v1");

    public static byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.LocalMachine);
}
