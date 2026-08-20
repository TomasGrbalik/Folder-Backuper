using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Notifications;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Features.Monitoring;
using FolderBackuper.Resources;

namespace FolderBackuper.Tests;

/// <summary>
/// Makes an incomplete translation a test failure rather than something a person discovers in the
/// interface.
/// </summary>
/// <remarks>
/// Three separate things have to stay in step: the neutral resource files, their Slovak counterparts,
/// and the enumerations whose member names are resource keys. Nothing else can catch that drift, because
/// a missing entry does not break the build — it renders as a key or as an English word in a Slovak page.
/// </remarks>
public sealed class ResourceCompletenessTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly string ResourceDirectory =
        Path.Combine(RepositoryRoot, "src", "FolderBackuper", "Resources");

    /// <summary>The three resource files, each with the language that must mirror it.</summary>
    public static TheoryData<string> ResourceFiles => new("UiStrings", "MessageStrings", "EmailStrings");

    /// <summary>Enumerations whose members <c>UiMessage.For</c> resolves against MessageStrings.</summary>
    private static readonly Type[] MessageEnums =
    [
        typeof(PathMessage), typeof(SourceMessage), typeof(EffectiveDestinationMessage),
        typeof(OwnershipMessage), typeof(BackupProblemMessage), typeof(DestinationMessage),
        typeof(JobMessage), typeof(JobValidationMessage), typeof(JobDestinationTestMessage),
        typeof(NotificationResultMessage), typeof(ConfigurationMutationMessage),
        typeof(RunOperationMessage), typeof(Features.Updates.UpdateProblemMessage)
    ];

    /// <summary>Enumerations whose members <c>EnumText.For</c> resolves against UiStrings.</summary>
    private static readonly Type[] LabelEnums =
    [
        typeof(RunOutcome), typeof(RunPhase), typeof(RunTrigger), typeof(ArtifactState),
        typeof(NotificationDeliveryState), typeof(JobLifecycle), typeof(DestinationType),
        typeof(DestinationVerificationResult), typeof(DestinationAccessResult),
        typeof(BackupProblemSeverity), typeof(SourceEntryType), typeof(RunStatusFilter),
        typeof(BackupOperation), typeof(DriveType)
    ];

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void EveryNeutralKeyHasASlovakCounterpart(string name)
    {
        var neutral = Read(name + ".resx");
        var slovak = Read(name + ".sk.resx");

        var untranslated = neutral.Keys.Except(slovak.Keys).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Empty(untranslated);
    }

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void SlovakCarriesNoKeyTheNeutralFileLacks(string name)
    {
        // A Slovak-only key is dead weight that no call site can reach, and usually means a key was
        // renamed on one side only.
        var neutral = Read(name + ".resx");
        var slovak = Read(name + ".sk.resx");

        var orphans = slovak.Keys.Except(neutral.Keys).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Empty(orphans);
    }

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void EveryTranslationSubstitutesTheSamePlaceholders(string name)
    {
        // A translation that drops a placeholder silently loses a value; one that invents a placeholder
        // throws FormatException at render time, which would surface as a broken page rather than a
        // broken build.
        var neutral = Read(name + ".resx");
        var slovak = Read(name + ".sk.resx");

        var mismatched = neutral
            .Where(entry => slovak.ContainsKey(entry.Key))
            .Where(entry => !Placeholders(entry.Value).SetEquals(Placeholders(slovak[entry.Key])))
            .Select(entry => entry.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(mismatched);
    }

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void NoTranslationIsBlank(string name)
    {
        var slovak = Read(name + ".sk.resx");
        var blank = slovak.Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(blank);
    }

    [Fact]
    public void EveryMessageCodeResolvesInBothLanguages()
    {
        var missing = new List<string>();
        foreach (var type in MessageEnums)
        {
            foreach (var member in Enum.GetNames(type))
            {
                var key = type.Name + "_" + member;
                foreach (var culture in Cultures)
                {
                    if (MessageStrings.ResourceManager.GetString(key, culture) is null)
                    {
                        missing.Add($"{key} ({culture.Name})");
                    }
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryEnumerationLabelResolvesInBothLanguages()
    {
        var missing = new List<string>();
        foreach (var type in LabelEnums)
        {
            foreach (var member in Enum.GetNames(type))
            {
                var key = type.Name + "_" + member;
                foreach (var culture in Cultures)
                {
                    if (UiStrings.ResourceManager.GetString(key, culture) is null)
                    {
                        missing.Add($"{key} ({culture.Name})");
                    }
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryMessageCodeRendersDifferentlyEnoughToBeATranslation()
    {
        // Catches an entry copied from English into the Slovak file and never translated. A handful of
        // strings are legitimately identical in both languages — a product name, an em dash, a language
        // name — so they are listed rather than inferred.
        string[] deliberatelyIdentical =
        [
            "AppName", "LanguageEnglish", "LanguageSlovak", "LanguageEnglishShort", "LanguageSlovakShort",
            "ValueUnknownDash", "PageTitleFormat", "DestinationType_Smb", "DriveType_Ram",
            "PagerInfoFormat", "BrandLabel", "ProblemLine", "HeadingRunResult", "SubjectRunResult",
            "DriveType_Unknown", "SourceEntryType_Unknown",
            // Slovak technical usage keeps "build" for a build identifier, so forcing a translation
            // here would read worse than leaving it.
            "SettingsBuild"
        ];

        var untranslated = new List<string>();
        foreach (var name in new[] { "UiStrings", "MessageStrings", "EmailStrings" })
        {
            var neutral = Read(name + ".resx");
            var slovak = Read(name + ".sk.resx");
            untranslated.AddRange(neutral
                .Where(entry => slovak.TryGetValue(entry.Key, out var translated)
                                && string.Equals(entry.Value, translated, StringComparison.Ordinal)
                                && !deliberatelyIdentical.Contains(entry.Key)
                                && entry.Value.Any(char.IsLetter))
                .Select(entry => name + ":" + entry.Key));
        }

        Assert.Empty(untranslated);
    }

    [Fact]
    public void NoResourceKeyIsWrittenAsALiteral()
    {
        // Text is reached through the generated accessor or derived from an enumeration member. A key
        // spelled out as a string would compile and then fail only at render time.
        var offenders = new List<string>();
        var pattern = new Regex(
            @"(?:UiStrings|MessageStrings|EmailStrings)\.ResourceManager\.GetString\(\s*""",
            RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot, "src", "FolderBackuper"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (pattern.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheVersionFileShipsExactlyEnglishAndSlovak()
    {
        // Widening this list ships satellites nobody asked for; narrowing it strips the Slovak interface
        // from publish output, which the release checklist asserts against.
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var languages = Regex.Match(props, "<SatelliteResourceLanguages>([^<]*)</SatelliteResourceLanguages>")
            .Groups[1].Value;

        Assert.Equal("en;sk", languages);
    }

    private static CultureInfo[] Cultures =>
        [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("sk-SK")];

    private static HashSet<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)\}").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> Read(string fileName)
    {
        var document = XDocument.Load(Path.Combine(ResourceDirectory, fileName));
        return document.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
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
