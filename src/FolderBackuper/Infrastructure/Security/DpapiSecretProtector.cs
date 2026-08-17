using System.Security.Cryptography;
using System.Text;

namespace FolderBackuper.Infrastructure.Security;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "FolderBackuper.Secrets.v1"u8.ToArray();

    public byte[] Protect(string plaintext) => ProtectedData.Protect(
        Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);

    public string Unprotect(byte[] protectedData) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.LocalMachine));
}
