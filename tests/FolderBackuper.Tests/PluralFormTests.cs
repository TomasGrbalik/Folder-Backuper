using System.Globalization;
using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Tests;

/// <summary>
/// Plural selection, which an English "(s)" suffix cannot express.
/// </summary>
public sealed class PluralFormTests
{
    private static readonly CultureInfo Slovak = CultureInfo.GetCultureInfo("sk-SK");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0, PluralForm.Many)]
    [InlineData(1, PluralForm.One)]
    [InlineData(2, PluralForm.Few)]
    [InlineData(3, PluralForm.Few)]
    [InlineData(4, PluralForm.Few)]
    [InlineData(5, PluralForm.Many)]
    [InlineData(11, PluralForm.Many)]
    [InlineData(21, PluralForm.Many)]
    [InlineData(102, PluralForm.Many)]
    public void Slovak_DistinguishesOneFewAndMany(long count, PluralForm expected) =>
        Assert.Equal(expected, Plural.Select(count, Slovak));

    [Theory]
    [InlineData(0, PluralForm.Many)]
    [InlineData(1, PluralForm.One)]
    [InlineData(2, PluralForm.Many)]
    [InlineData(4, PluralForm.Many)]
    [InlineData(5, PluralForm.Many)]
    [InlineData(21, PluralForm.Many)]
    public void English_DistinguishesOnlyOneFromTheRest(long count, PluralForm expected) =>
        Assert.Equal(expected, Plural.Select(count, English));

    [Fact]
    public void ANegativeCountUsesTheFormItsMagnitudeSelects()
    {
        // Nothing in the interface counts downwards, but a corrupt figure must not pick a form by
        // falling through the rules.
        Assert.Equal(PluralForm.One, Plural.Select(-1, Slovak));
        Assert.Equal(PluralForm.Few, Plural.Select(-3, Slovak));
        Assert.Equal(PluralForm.Many, Plural.Select(-7, Slovak));
    }

    [Fact]
    public void Choose_PicksTheFormTheReadingLanguageNeeds()
    {
        using (CultureScope.Slovak())
        {
            Assert.Equal("few", Plural.Choose(3, "one", "few", "many"));
            Assert.Equal("one", Plural.Choose(1, "one", "few", "many"));
            Assert.Equal("many", Plural.Choose(9, "one", "few", "many"));
        }

        using (CultureScope.English())
        {
            // English never reaches the few form, which is why every English entry repeats the many one.
            Assert.Equal("many", Plural.Choose(3, "one", "few", "many"));
            Assert.Equal("one", Plural.Choose(1, "one", "few", "many"));
        }
    }

    [Fact]
    public void Format_SubstitutesTheCountWithTheReadingCulturesSeparators()
    {
        using (CultureScope.English())
        {
            Assert.Equal("5 items", Plural.Format(5, "{0} item", "{0} items", "{0} items"));
        }

        using (CultureScope.Slovak())
        {
            Assert.Equal("3 veci", Plural.Format(3, "{0} vec", "{0} veci", "{0} vecí"));
            Assert.Equal("7 vecí", Plural.Format(7, "{0} vec", "{0} veci", "{0} vecí"));
        }
    }
}
