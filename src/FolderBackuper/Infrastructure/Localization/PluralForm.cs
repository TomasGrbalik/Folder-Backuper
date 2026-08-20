using System.Globalization;

namespace FolderBackuper.Infrastructure.Localization;

/// <summary>Which of a language's plural forms a count selects.</summary>
public enum PluralForm
{
    /// <summary>Exactly one.</summary>
    One,

    /// <summary>Two, three, or four. Slovak only; English folds this into <see cref="Many"/>.</summary>
    Few,

    /// <summary>Everything else, including zero.</summary>
    Many
}

/// <summary>
/// Chooses a plural form by rule rather than by an English "(s)" suffix, which cannot express Slovak.
/// </summary>
/// <remarks>
/// Slovak distinguishes one, two-to-four, and five-or-more, and puts zero in the last group. Only two
/// languages ship, so the rules are written out here rather than pulled from a plural-rules library.
/// </remarks>
public static class Plural
{
    public static PluralForm Select(long count, CultureInfo culture)
    {
        var magnitude = Math.Abs(count);
        if (string.Equals(culture.TwoLetterISOLanguageName, "sk", StringComparison.OrdinalIgnoreCase))
        {
            return magnitude switch
            {
                1 => PluralForm.One,
                >= 2 and <= 4 => PluralForm.Few,
                _ => PluralForm.Many
            };
        }

        return magnitude == 1 ? PluralForm.One : PluralForm.Many;
    }

    public static PluralForm Select(long count) => Select(count, CultureInfo.CurrentUICulture);

    /// <summary>Picks the matching form, accepting the Slovak few-form that English never uses.</summary>
    public static string Choose(long count, string one, string few, string many) =>
        Select(count) switch
        {
            PluralForm.One => one,
            PluralForm.Few => few,
            _ => many
        };

    /// <summary>Picks the matching form and substitutes the count into it.</summary>
    public static string Format(long count, string one, string few, string many) =>
        string.Format(CultureInfo.CurrentCulture, Choose(count, one, few, many), count);
}
