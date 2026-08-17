namespace FolderBackuper.Infrastructure.Security;

public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] protectedData);
}
