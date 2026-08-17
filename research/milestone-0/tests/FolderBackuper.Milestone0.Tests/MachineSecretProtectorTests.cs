using System.Security.Cryptography;
using FolderBackuper.Milestone0.Security;

namespace FolderBackuper.Milestone0.Tests;

public sealed class MachineSecretProtectorTests
{
    [Fact]
    public void Protect_RoundTripsWithoutStoringPlaintext()
    {
        var plaintext = RandomNumberGenerator.GetBytes(64);

        var protectedData = MachineSecretProtector.Protect(plaintext);
        var recovered = MachineSecretProtector.Unprotect(protectedData);

        Assert.False(plaintext.SequenceEqual(protectedData));
        Assert.Equal(plaintext, recovered);
    }
}
