using System.Globalization;
using FolderBackuper.Resources;

namespace FolderBackuper.Infrastructure.Localization;

/// <summary>
/// Renders a domain enumeration member as the label the interface shows for it.
/// </summary>
/// <remarks>
/// The key is derived from the type and member name by the same rule <see cref="UiMessage.KeyFor{TEnum}"/>
/// uses, so a label is never reached by a literal and a renamed member breaks the build. Enumerations were
/// previously rendered straight into markup by <c>ToString</c>, which produced English regardless of the
/// selected language; every such site now goes through here instead.
/// </remarks>
public static class EnumText
{
    public static string For<TEnum>(TEnum value) where TEnum : struct, Enum =>
        UiStrings.ResourceManager.GetString(UiMessage.KeyFor(value), CultureInfo.CurrentUICulture)
        // A member with no entry is a defect the completeness tests exist to catch. Falling back to the
        // member name keeps a page readable rather than blank while remaining obviously untranslated.
        ?? value.ToString()!;

    public static string For<TEnum>(TEnum? value, string whenNull) where TEnum : struct, Enum =>
        value is { } present ? For(present) : whenNull;
}
