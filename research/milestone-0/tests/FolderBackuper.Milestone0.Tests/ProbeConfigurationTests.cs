using FolderBackuper.Milestone0.Configuration;

namespace FolderBackuper.Milestone0.Tests;

public sealed class ProbeConfigurationTests
{
    [Fact]
    public async Task LoadAsync_ReadsCamelCaseConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"FolderBackuper-M0-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "sourceReadPath": "C:\\source",
                  "nas": {
                    "uncRoot": "\\\\192.0.2.10\\probe",
                    "username": "probe-user"
                  }
                }
                """);

            var configuration = await ProbeConfiguration.LoadAsync(path, CancellationToken.None);

            Assert.Equal(@"C:\source", configuration.SourceReadPath);
            Assert.NotNull(configuration.Nas);
            Assert.Equal(@"\\192.0.2.10\probe", configuration.Nas.UncRoot);
            Assert.Equal("probe-user", configuration.Nas.Username);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_RejectsLocalHostNas()
    {
        var configuration = new ProbeConfiguration
        {
            Nas = new NasConfiguration { UncRoot = @"\\localhost\probe", Username = "user" }
        };

        var exception = Assert.Throws<InvalidDataException>(() => configuration.Validate(requireNas: true));
        Assert.Contains("backup PC", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\nas\share\..\other")]
    [InlineData(@"\\nas\share/../other")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\?\UNC\nas\share")]
    public void Validate_RejectsUnsafeNasRoots(string path)
    {
        var configuration = new NasConfiguration { UncRoot = path, Username = "user" };

        Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Theory]
    [InlineData("DOMAIN\\user")]
    [InlineData("user@example.test")]
    public void Validate_RejectsDomainWithAlreadyQualifiedUsername(string username)
    {
        var configuration = new NasConfiguration
        {
            UncRoot = @"\\192.0.2.10\probe",
            Username = username,
            Domain = "OTHER"
        };

        Assert.Throws<InvalidDataException>(configuration.Validate);
    }
}
