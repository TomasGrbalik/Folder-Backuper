using FolderBackuper.Infrastructure.Maintenance;

namespace FolderBackuper.Tests;

public sealed class MaintenanceCommandLineTests
{
    [Fact]
    public void Parse_IgnoresNormalHostingArguments()
    {
        var result = MaintenanceCommandLine.Parse(["--FolderBackuper:Port=5180"]);

        Assert.False(result.IsMaintenance);
        Assert.Null(result.Command);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Parse_IgnoresAnEmptyCommandLine() =>
        Assert.False(MaintenanceCommandLine.Parse([]).IsMaintenance);

    [Theory]
    [InlineData("--port=5191")]
    [InlineData("--port")]
    public void Parse_AcceptsBothPortForms(string first)
    {
        string[] args = first == "--port"
            ? [MaintenanceCommandLine.ConfigurePortVerb, "--port", "5191"]
            : [MaintenanceCommandLine.ConfigurePortVerb, first];

        var command = Assert.IsType<ConfigurePortCommand>(MaintenanceCommandLine.Parse(args).Command);

        Assert.Equal(5191, command.Port);
    }

    [Fact]
    public void Parse_TreatsAutoAsNoExplicitPort()
    {
        var command = Assert.IsType<ConfigurePortCommand>(
            MaintenanceCommandLine.Parse([MaintenanceCommandLine.ConfigurePortVerb, "--port=auto"]).Command);

        Assert.Null(command.Port);
    }

    [Fact]
    public void Parse_ReadsTheDataRoot()
    {
        var command = Assert.IsType<ConfigurePortCommand>(
            MaintenanceCommandLine.Parse(
                [MaintenanceCommandLine.ConfigurePortVerb, "--port=5191", @"--data-root=C:\Temp\FolderBackuper-M10"])
                .Command);

        Assert.Equal(@"C:\Temp\FolderBackuper-M10", command.DataRoot);
    }

    [Fact]
    public void Parse_RequiresAPortForConfigurePort()
    {
        var result = MaintenanceCommandLine.Parse([MaintenanceCommandLine.ConfigurePortVerb]);

        Assert.True(result.IsMaintenance);
        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("five")]
    public void Parse_RejectsAnInvalidPort(string port)
    {
        var result = MaintenanceCommandLine.Parse([MaintenanceCommandLine.ConfigurePortVerb, $"--port={port}"]);

        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_RejectsAnUnknownOption()
    {
        var result = MaintenanceCommandLine.Parse(
            [MaintenanceCommandLine.ConfigurePortVerb, "--port=5191", "--unexpected=1"]);

        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_ReadsTheReadinessTimeout()
    {
        var command = Assert.IsType<WaitReadyCommand>(
            MaintenanceCommandLine.Parse([MaintenanceCommandLine.WaitReadyVerb, "--timeout-seconds=30"]).Command);

        Assert.Equal(30, command.TimeoutSeconds);
    }

    [Fact]
    public void Parse_DefaultsTheReadinessTimeout()
    {
        var command = Assert.IsType<WaitReadyCommand>(
            MaintenanceCommandLine.Parse([MaintenanceCommandLine.WaitReadyVerb]).Command);

        Assert.Equal(90, command.TimeoutSeconds);
        Assert.Null(command.Port);
    }

    [Fact]
    public void Parse_ReadsAnExplicitReadinessPort()
    {
        var command = Assert.IsType<WaitReadyCommand>(
            MaintenanceCommandLine.Parse([MaintenanceCommandLine.WaitReadyVerb, "--port=5191"]).Command);

        Assert.Equal(5191, command.Port);
    }

    [Fact]
    public void Parse_RejectsAnInvalidReadinessPort()
    {
        var result = MaintenanceCommandLine.Parse([MaintenanceCommandLine.WaitReadyVerb, "--port=0"]);

        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }
}
