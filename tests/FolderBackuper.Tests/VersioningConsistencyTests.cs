using System.Text.RegularExpressions;
using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Tests;

/// <summary>
/// Guards the contract between the version file, the script that rewrites it, the installer script,
/// and the version the built assembly reports. Nothing else can catch that drift without running a
/// release.
/// </summary>
public sealed class VersioningConsistencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly string PropsFile =
        File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));

    private static readonly string InstallerScript =
        File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "FolderBackuper.iss"));

    [Fact]
    public void Props_HoldsExactlyOneVersionPrefixAndOneVersionSuffix()
    {
        // build/Set-ProductVersion.ps1 rewrites exactly these two elements and refuses to run if it
        // finds any other number of them.
        Assert.Single(Regex.Matches(PropsFile, "<VersionPrefix>[^<]*</VersionPrefix>"));
        Assert.Single(Regex.Matches(PropsFile, "<VersionSuffix>[^<]*</VersionSuffix>"));
    }

    [Fact]
    public void Props_HoldsAThreePartNumericVersionPrefix()
    {
        var prefix = Regex.Match(PropsFile, "<VersionPrefix>([^<]*)</VersionPrefix>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", prefix);
    }

    [Fact]
    public void Props_KeepsTheWin32VersionsNumeric()
    {
        // Inno Setup reads VersionInfoVersion out of the Win32 resource and rejects anything that is
        // not numeric, so neither of these may ever pick up the suffix.
        Assert.Contains("<AssemblyVersion>$(VersionPrefix).0</AssemblyVersion>", PropsFile, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>$(VersionPrefix).0</FileVersion>", PropsFile, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningAssembly_ReportsTheVersionTheFileDeclares()
    {
        // This catches a rewrite that produced a version the compiler accepted but nobody intended.
        var prefix = Regex.Match(PropsFile, "<VersionPrefix>([^<]*)</VersionPrefix>").Groups[1].Value;
        var suffix = Regex.Match(PropsFile, "<VersionSuffix>([^<]*)</VersionSuffix>").Groups[1].Value;
        var expected = suffix.Length == 0 ? prefix : $"{prefix}-{suffix}";

        Assert.Equal(expected, ProductVersion.Display);
    }

    [Fact]
    public void RunningAssembly_ExposesAParsedVersion()
    {
        Assert.NotNull(ProductVersion.Version);
    }

    [Fact]
    public void InstallerScript_KeepsVersionInfoVersionOnTheNumericVersion()
    {
        Assert.Contains(
            "#define AppVersion GetVersionNumbersString(AddBackslash(PublishDir) + AppExeName)",
            InstallerScript,
            StringComparison.Ordinal);
        Assert.Contains("VersionInfoVersion={#AppVersion}", InstallerScript, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScript_UsesTheDisplayLabelForTheNameAndFallsBackWithoutIt()
    {
        // Build-Installer.ps1 passes /DVersionLabel. A direct ISCC invocation, which the script's own
        // header documents, must still compile.
        Assert.Contains("#ifndef VersionLabel", InstallerScript, StringComparison.Ordinal);
        Assert.Contains("#define VersionLabel AppVersion", InstallerScript, StringComparison.Ordinal);
        Assert.Contains("AppVersion={#VersionLabel}", InstallerScript, StringComparison.Ordinal);
        Assert.Contains(
            "OutputBaseFilename=FolderBackuper-{#VersionLabel}-setup",
            InstallerScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VersionScript_Exists()
    {
        // The release workflow calls it by this path, and a rename would otherwise only be noticed
        // in the middle of a release.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "build", "Set-ProductVersion.ps1")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderBackuper.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root containing FolderBackuper.slnx was not found.");
    }
}
