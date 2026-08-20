using System.Globalization;

namespace FolderBackuper.Infrastructure.Localization;

/// <summary>
/// The interface languages the product ships. The member names are what the settings row stores, so
/// renaming one is a persistence change, not a rename.
/// </summary>
public enum InterfaceLanguage
{
    English,
    Slovak
}

/// <summary>
/// Maps interface languages to the cultures that format their dates, numbers, and file sizes, and
/// resolves the language an installation should start in before anyone has chosen one.
/// </summary>
/// <remarks>
/// The neutral resource language is English, so <see cref="InterfaceLanguage.English"/> deliberately
/// maps to a specific culture rather than the invariant one: the invariant culture would format dates
/// and numbers in a way that matches no country.
/// </remarks>
public static class InterfaceLanguages
{
    /// <summary>Every shipped language, in the order the interface offers them.</summary>
    public static readonly IReadOnlyList<InterfaceLanguage> All = [InterfaceLanguage.English, InterfaceLanguage.Slovak];

    public static CultureInfo ToCulture(this InterfaceLanguage language) => language switch
    {
        InterfaceLanguage.Slovak => CultureInfo.GetCultureInfo("sk-SK"),
        _ => CultureInfo.GetCultureInfo("en-US")
    };

    /// <summary>The two-letter code the root document's language attribute carries.</summary>
    public static string ToLanguageTag(this InterfaceLanguage language) =>
        language.ToCulture().TwoLetterISOLanguageName;

    /// <summary>Round-trips the stored value, falling back to the machine default for anything unrecognized.</summary>
    public static InterfaceLanguage Parse(string? stored) =>
        Enum.TryParse<InterfaceLanguage>(stored, ignoreCase: false, out var language) && All.Contains(language)
            ? language
            : MachineDefault();

    public static string ToStoredValue(this InterfaceLanguage language) => language.ToString();

    /// <summary>
    /// The language an installation that has never chosen one runs in. Slovak only when Windows itself
    /// is Slovak, which keeps the pre-Milestone-12 promise that the interface followed the PC.
    /// </summary>
    public static InterfaceLanguage MachineDefault() => MachineDefaultFor(CultureInfo.InstalledUICulture);

    internal static InterfaceLanguage MachineDefaultFor(CultureInfo installed) =>
        string.Equals(installed.TwoLetterISOLanguageName, "sk", StringComparison.OrdinalIgnoreCase)
            ? InterfaceLanguage.Slovak
            : InterfaceLanguage.English;

    /// <summary>The language whose culture is currently in effect, for code that renders rather than decides.</summary>
    public static InterfaceLanguage Current =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "sk", StringComparison.OrdinalIgnoreCase)
            ? InterfaceLanguage.Slovak
            : InterfaceLanguage.English;
}
