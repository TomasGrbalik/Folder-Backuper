using System.Globalization;
using System.Reflection;
using FolderBackuper.Resources;

namespace FolderBackuper.Tests;

/// <summary>
/// Guards the packaging half of the interface language: the Slovak text has to reach the built output,
/// not only the repository.
/// </summary>
/// <remarks>
/// A narrowed <c>SatelliteResourceLanguages</c>, a resource file dropped from the project, or a build that
/// silently stops emitting satellites would leave every Slovak string falling back to English. None of
/// that breaks the build, and none of it is visible from a test that only reads the .resx files, so the
/// assertion here is against the assembly that was actually produced.
/// </remarks>
public sealed class SatelliteAssemblyTests
{
    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");

    [Fact]
    public void TheSlovakSatelliteIsPresentBesideTheApplicationAssembly()
    {
        var applicationDirectory = Path.GetDirectoryName(typeof(UiStrings).Assembly.Location)!;
        var satellite = Path.Combine(applicationDirectory, "sk", "FolderBackuper.resources.dll");

        Assert.True(
            File.Exists(satellite),
            $"The Slovak satellite assembly was not found at {satellite}. "
            + "Check SatelliteResourceLanguages in Directory.Build.props.");
    }

    [Fact]
    public void TheSlovakSatelliteCanBeLoadedForEveryResourceFile()
    {
        // Reaching a string through the resource manager proves the satellite is loadable and carries the
        // expected neutral resource name, which a file-existence check alone does not.
        Assert.NotNull(typeof(UiStrings).Assembly.GetSatelliteAssembly(CultureInfo.GetCultureInfo("sk")));

        Assert.NotNull(UiStrings.ResourceManager.GetString("NavJobs", Slovak));
        Assert.NotNull(MessageStrings.ResourceManager.GetString("JobMessage_Created", Slovak));
        Assert.NotNull(EmailStrings.ResourceManager.GetString("FactJob", Slovak));
    }

    [Fact]
    public void TheApplicationShipsNoSatelliteLanguageOtherThanSlovak()
    {
        // English is the neutral language and lives in the application assembly itself, so exactly one
        // satellite directory is expected. Anything else means the language list has drifted, which the
        // release checklist asserts against by hand.
        var applicationDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(UiStrings).Assembly.Location)!);
        var satelliteDirectories = applicationDirectory
            .EnumerateDirectories()
            .Where(directory => File.Exists(Path.Combine(directory.FullName, "FolderBackuper.resources.dll")))
            .Select(directory => directory.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(["sk"], satelliteDirectories);
    }

    [Fact]
    public void EnglishResolvesFromTheApplicationAssemblyRatherThanASatellite()
    {
        // The neutral language must stay embedded, or an English installation would depend on a satellite
        // that is deliberately not shipped.
        var neutral = typeof(UiStrings).Assembly.GetName().Name;
        Assert.Equal("FolderBackuper", neutral);

        Assert.Equal("Jobs", UiStrings.ResourceManager.GetString("NavJobs", CultureInfo.InvariantCulture));
    }
}
