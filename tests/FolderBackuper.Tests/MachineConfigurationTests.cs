using FolderBackuper.Infrastructure.ServiceHosting;
using Microsoft.Extensions.Configuration;

namespace FolderBackuper.Tests;

public sealed class MachineConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "FolderBackuper-MachineConfiguration-" + Guid.NewGuid().ToString("N"));

    private ApplicationPaths Paths => ApplicationPaths.Resolve(root);

    [Fact]
    public void Write_RoundTripsThePort()
    {
        var paths = Paths;

        MachineConfiguration.Write(paths, 5191);

        Assert.Equal(5191, MachineConfiguration.TryReadPort(paths));
        Assert.False(File.Exists(MachineConfiguration.GetFilePath(paths) + ".tmp"));
    }

    [Fact]
    public void Write_ReplacesAnExistingValue()
    {
        var paths = Paths;
        MachineConfiguration.Write(paths, 5191);

        MachineConfiguration.Write(paths, 5192);

        Assert.Equal(5192, MachineConfiguration.TryReadPort(paths));
    }

    [Fact]
    public void TryReadPort_ReturnsNullForAMissingOrMalformedFile()
    {
        var paths = Paths;
        Assert.Null(MachineConfiguration.TryReadPort(paths));

        Directory.CreateDirectory(paths.Config);
        File.WriteAllText(MachineConfiguration.GetFilePath(paths), "{ not json");

        Assert.Null(MachineConfiguration.TryReadPort(paths));
    }

    [Fact]
    public void TryReadPort_PropagatesAnUnreadableFile()
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.Config);
        var filePath = MachineConfiguration.GetFilePath(paths);
        File.WriteAllText(filePath, "{}");

        using var exclusive = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        // An existing file that cannot be read must not be reported as an absent one.
        Assert.Throws<IOException>(() => MachineConfiguration.TryReadPort(paths));
    }

    [Fact]
    public void Apply_PrefersTheMachineFileOverTheApplicationDefault()
    {
        var paths = Paths;
        MachineConfiguration.Write(paths, 5191);

        Assert.Equal(5191, ResolvePort(paths, []));
    }

    [Fact]
    public void Apply_LetsTheCommandLineOverrideTheMachineFile()
    {
        var paths = Paths;
        MachineConfiguration.Write(paths, 5191);

        Assert.Equal(5199, ResolvePort(paths, ["--FolderBackuper:Port=5199"]));
    }

    [Fact]
    public void Apply_KeepsTheApplicationDefaultWithoutAMachineFile() =>
        Assert.Equal(WindowsServiceMetadata.DefaultPort, ResolvePort(Paths, []));

    [Fact]
    public void Apply_RejectsADataRootWrittenIntoTheMachineFile()
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.Config);
        File.WriteAllText(
            MachineConfiguration.GetFilePath(paths),
            """{ "FolderBackuper": { "Port": 5191, "DataRoot": "C:/Elsewhere" } }""");

        var exception = Assert.Throws<InvalidOperationException>(() => ResolvePort(paths, []));

        Assert.Contains("FolderBackuper:DataRoot", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Mirrors the source order Program.cs establishes.</summary>
    private static int ResolvePort(ApplicationPaths paths, string[] args)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WindowsServiceMetadata.PortConfigurationKey] = WindowsServiceMetadata.DefaultPort.ToString()
        });

        MachineConfiguration.Apply(builder, paths, args);

        return builder.Build().GetValue(
            WindowsServiceMetadata.PortConfigurationKey,
            WindowsServiceMetadata.DefaultPort);
    }
}
