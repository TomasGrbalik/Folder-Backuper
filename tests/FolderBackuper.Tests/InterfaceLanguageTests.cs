using System.Globalization;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Localization;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

/// <summary>
/// The interface language as a stored preference and as a process-wide culture.
/// </summary>
public sealed class InterfaceLanguageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("sk-SK", InterfaceLanguage.Slovak)]
    [InlineData("sk", InterfaceLanguage.Slovak)]
    [InlineData("en-US", InterfaceLanguage.English)]
    [InlineData("en-GB", InterfaceLanguage.English)]
    [InlineData("de-DE", InterfaceLanguage.English)]
    [InlineData("cs-CZ", InterfaceLanguage.English)]
    public void MachineDefault_ChoosesSlovakOnlyWhenWindowsItselfIsSlovak(string installed, InterfaceLanguage expected)
    {
        // Czech is deliberately in this list: it is the closest language to Slovak and must not be
        // mistaken for it, or a Czech machine would come up in the wrong language.
        Assert.Equal(expected, InterfaceLanguages.MachineDefaultFor(CultureInfo.GetCultureInfo(installed)));
    }

    [Fact]
    public void Parse_FallsBackToTheMachineDefaultForAnythingUnrecognized()
    {
        var machine = InterfaceLanguages.MachineDefault();

        Assert.Equal(InterfaceLanguage.Slovak, InterfaceLanguages.Parse("Slovak"));
        Assert.Equal(InterfaceLanguage.English, InterfaceLanguages.Parse("English"));
        Assert.Equal(machine, InterfaceLanguages.Parse(null));
        Assert.Equal(machine, InterfaceLanguages.Parse(""));
        Assert.Equal(machine, InterfaceLanguages.Parse("Klingon"));
        Assert.Equal(machine, InterfaceLanguages.Parse("slovak"));
    }

    [Fact]
    public void ToStoredValue_RoundTrips()
    {
        foreach (var language in InterfaceLanguages.All)
        {
            Assert.Equal(language, InterfaceLanguages.Parse(language.ToStoredValue()));
        }
    }

    [Fact]
    public void Apply_SetsBothTheFormattingAndTheResourceCulture()
    {
        using (CultureScope.Slovak())
        {
            Assert.Equal("sk-SK", CultureInfo.CurrentCulture.Name);
            Assert.Equal("sk-SK", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(InterfaceLanguage.Slovak, InterfaceLanguages.Current);
        }

        using (CultureScope.English())
        {
            Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(InterfaceLanguage.English, InterfaceLanguages.Current);
        }
    }

    [Fact]
    public void ToLanguageTag_IsTheTwoLetterCodeTheDocumentCarries()
    {
        Assert.Equal("en", InterfaceLanguage.English.ToLanguageTag());
        Assert.Equal("sk", InterfaceLanguage.Slovak.ToLanguageTag());
    }

    [Fact]
    public async Task Get_FollowsTheMachineBeforeAnythingIsConfigured()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        Assert.Equal(InterfaceLanguages.MachineDefault(), await Service(database, clock).GetAsync());
    }

    [Fact]
    public async Task Set_RoundTripsBothWaysAndKeepsOneSettingsRow()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = Service(database, clock);

        // Storing a language applies it to the process, so the scope is what restores the defaults for
        // whatever test runs next.
        using var restore = CultureScope.English();

        await service.SetAsync(InterfaceLanguage.Slovak);
        Assert.Equal(InterfaceLanguage.Slovak, await service.GetAsync());

        await service.SetAsync(InterfaceLanguage.English);
        Assert.Equal(InterfaceLanguage.English, await service.GetAsync());

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal(1, await context.ApplicationSettings.CountAsync());
    }

    [Fact]
    public async Task Set_RecordsWhenItChangedAndLeavesTheRowAloneWhenNothingChanged()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = Service(database, clock);
        using var restore = CultureScope.English();

        await service.SetAsync(InterfaceLanguage.Slovak);
        var afterFirst = await UpdatedAtAsync(database);

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.SetAsync(InterfaceLanguage.Slovak);

        Assert.Equal(afterFirst, await UpdatedAtAsync(database));
    }

    [Fact]
    public async Task Set_LeavesTheNotificationAndUpdateCheckPreferencesUntouched()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        await NotificationTestFactory.ConfiguredSettingsAsync(database, clock);
        using var restore = CultureScope.English();

        await Service(database, clock).SetAsync(InterfaceLanguage.Slovak);

        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var settings = await context.ApplicationSettings.AsNoTracking().SingleAsync();
        Assert.True(settings.UpdateCheckEnabled);
        Assert.NotNull(settings.NotificationProvider);
        Assert.NotEmpty(settings.RecipientList);
    }

    [Fact]
    public async Task Set_AppliesTheLanguageToTheProcess()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();

        using var restore = CultureScope.English();
        await Service(database, clock).SetAsync(InterfaceLanguage.Slovak);

        // The process default is what the mechanism guarantees, and what every later thread and circuit
        // reads. The ambient CurrentUICulture is deliberately not asserted here: an assignment made
        // inside an async method does not flow back out to the caller's execution context, so this
        // thread can still hold the value it had before the call. That is why the interface reloads the
        // page after a language change instead of re-rendering in place.
        Assert.Equal("sk-SK", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal("sk-SK", CultureInfo.DefaultThreadCurrentCulture?.Name);
    }

    [Fact]
    public async Task ApplyStored_BringsTheProcessBackToTheStoredLanguage()
    {
        var clock = new TestTimeProvider(Now);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var service = Service(database, clock);

        using var restore = CultureScope.English();
        await service.SetAsync(InterfaceLanguage.Slovak);
        CultureScope.ApplyEnglishDefaults();
        Assert.Equal(InterfaceLanguage.English, InterfaceLanguages.Current);

        await service.ApplyStoredAsync();

        Assert.Equal("sk-SK", CultureInfo.DefaultThreadCurrentUICulture?.Name);
    }

    private static UiLanguageSettingsService Service(TemporaryDatabase database, TimeProvider clock) =>
        new(database.ContextFactory, new InstallationIdentityService(database.ContextFactory, clock), clock);

    private static async Task<DateTimeOffset> UpdatedAtAsync(TemporaryDatabase database)
    {
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        return (await context.ApplicationSettings.AsNoTracking().SingleAsync()).UpdatedAtUtc;
    }
}
