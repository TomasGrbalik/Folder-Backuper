using System.Globalization;
using Bunit;
using FolderBackuper.Components;
using FolderBackuper.Components.Layout;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Formatting;
using FolderBackuper.Infrastructure.Localization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

/// <summary>
/// What a person actually sees with Slovak selected.
/// </summary>
/// <remarks>
/// The rest of the suite runs pinned to English so that its assertions on wording stay stable. These
/// tests are the other half of that: they switch the language the way the application does and check that
/// the interface follows, including the formatting that used to come from the Windows regional settings.
/// </remarks>
public sealed class SlovakInterfaceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 8, 20, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Navigation_ReadsSlovak()
    {
        using var slovak = CultureScope.Slovak();
        using var context = new BunitContext();
        context.Services.AddMudServices();

        var markup = context.Render<NavMenu>().Markup;

        Assert.Contains("Prehľad", markup, StringComparison.Ordinal);
        Assert.Contains("Úlohy", markup, StringComparison.Ordinal);
        Assert.Contains("Cieľové umiestnenia", markup, StringComparison.Ordinal);
        Assert.Contains("Kalendár", markup, StringComparison.Ordinal);
        Assert.Contains("História", markup, StringComparison.Ordinal);
        Assert.Contains("Nastavenia", markup, StringComparison.Ordinal);
        Assert.Contains("Pracovná plocha", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_LeavesNoEnglishLabelBehind()
    {
        // A missing resource entry falls back to a key or to an English word, and neither is visible
        // from a passing assertion about the Slovak text alone.
        using var slovak = CultureScope.Slovak();
        using var context = new BunitContext();
        context.Services.AddMudServices();

        var markup = context.Render<NavMenu>().Markup;

        foreach (var english in new[] { "Dashboard", "Workspace", "Destinations", "Calendar", "History", "Settings" })
        {
            Assert.DoesNotContain(english, markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReconnectBanner_ReadsSlovakEvenThoughItRendersOutsideACircuit()
    {
        using var slovak = CultureScope.Slovak();
        using var context = new BunitContext();

        var markup = context.Render<ReconnectModal>().Markup;

        Assert.Contains("Znovu sa pripájam", markup, StringComparison.Ordinal);
        Assert.Contains("Znovu načítať", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Reconnecting", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageToggle_MarksTheStoredLanguageAsSelected()
    {
        var clock = new TestTimeProvider(Instant);
        await using var database = new TemporaryDatabase(clock);
        await database.Initializer.InitializeAsync();
        var languages = new UiLanguageSettingsService(
            database.ContextFactory, new InstallationIdentityService(database.ContextFactory, clock), clock);

        // The scope is entered first because storing a language applies it to the process, and only the
        // scope restores the defaults afterwards.
        using var slovak = CultureScope.Slovak();
        await languages.SetAsync(InterfaceLanguage.Slovak);
        using var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddSingleton(languages);

        var component = context.Render<LanguageToggle>();
        component.WaitForAssertion(() => Assert.Contains("active", component.Markup, StringComparison.Ordinal));

        // Both languages are always offered, and each button is labelled in its own language so that a
        // person who cannot read the current one can still find theirs.
        var buttons = component.FindAll("button");
        Assert.Equal(2, buttons.Count);
        Assert.Equal("EN", buttons[0].TextContent.Trim());
        Assert.Equal("SK", buttons[1].TextContent.Trim());
        Assert.Equal("false", buttons[0].GetAttribute("aria-pressed"));
        Assert.Equal("true", buttons[1].GetAttribute("aria-pressed"));
    }

    [Fact]
    public void StatusVocabulary_ReadsSlovak()
    {
        using var slovak = CultureScope.Slovak();

        Assert.Equal("Úspešné", MonitoringDisplay.OutcomeLabel(RunOutcome.Successful));
        Assert.Equal("Dokončené s upozorneniami", MonitoringDisplay.OutcomeLabel(RunOutcome.SuccessfulWithWarnings));
        Assert.Equal("Zlyhalo", MonitoringDisplay.OutcomeLabel(RunOutcome.Failed));
        Assert.Equal("Komprimovanie", MonitoringDisplay.PhaseLabel(RunPhase.Compressing));
        Assert.Equal("Aktívna", MonitoringDisplay.LifecycleLabel(JobLifecycle.Active));
        Assert.Equal("Nahrávanie", MonitoringDisplay.TransferVerb(DestinationType.Smb));
        Assert.Equal("Doručené", MonitoringDisplay.NotificationLabel(
            Features.Notifications.NotificationDeliveryState.Delivered));
    }

    [Fact]
    public void ServiceMessages_ReadSlovak()
    {
        using var slovak = CultureScope.Slovak();

        Assert.Equal("Úloha bola vytvorená.", MessageText.Resolve(UiMessage.For(JobMessage.Created)));
        Assert.Equal(
            "Cieľové umiestnenie bolo archivované.",
            MessageText.Resolve(UiMessage.For(DestinationMessage.Archived)));
    }

    [Fact]
    public void ANestedMessageIsRenderedInTheReadingLanguageToo()
    {
        // The composed cleanup messages carry another message as an argument rather than embedding its
        // text, so the inner reason has to follow the language as well.
        using var slovak = CultureScope.Slovak();

        var composed = UiMessage.For(
            JobDestinationTestMessage.NewlyClaimedMarkerNotReleased,
            UiMessageArgument.FromMessage(UiMessage.For(Infrastructure.Filesystem.OwnershipMessage.MarkerMissing)));

        var text = MessageText.Resolve(composed);

        Assert.Contains("Značka vlastníctva chýba.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("marker", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMessageArgumentIsFormattedWithTheReadingCulture()
    {
        var message = UiMessage.For(
            DestinationMessage.ReferencedByJobs, UiMessageArgument.FromNumber(1234));

        using (CultureScope.English())
        {
            Assert.Contains("1,234", MessageText.Resolve(message), StringComparison.Ordinal);
        }

        using (CultureScope.Slovak())
        {
            // Slovak groups with a non-breaking space rather than a comma.
            Assert.DoesNotContain("1,234", MessageText.Resolve(message), StringComparison.Ordinal);
            Assert.Contains("234", MessageText.Resolve(message), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Formatting_FollowsTheSelectedLanguageRatherThanTheMachine()
    {
        using (CultureScope.English())
        {
            Assert.Equal("9.7 GB", DisplayFormat.Bytes(10_402_070_631));
            Assert.Equal("Not reported", DisplayFormat.Bytes(null));
        }

        using (CultureScope.Slovak())
        {
            // Slovak uses a comma as the decimal separator, which is the visible half of the promise
            // that formatting follows the interface language.
            Assert.Equal("9,7 GB", DisplayFormat.Bytes(10_402_070_631));
            Assert.Equal("Neuvedené", DisplayFormat.Bytes(null));
        }
    }

    [Fact]
    public void TheNextOccurrenceCarriesADateRatherThanAnEra()
    {
        // "ddd, g" looks like a weekday followed by the general short date and time, but it is a custom
        // pattern in which g is the era, so the job card read "Mon, AD" and "po, po Kr." with no date at
        // all. Both halves are asserted: the era is gone, and the date it was standing in for is back.
        var local = Instant.ToLocalTime();

        using (CultureScope.English())
        {
            var text = DisplayFormat.LocalDayAndTime(Instant);

            Assert.StartsWith(
                CultureInfo.GetCultureInfo("en-US").DateTimeFormat.AbbreviatedDayNames[(int)local.DayOfWeek] + ", ",
                text,
                StringComparison.Ordinal);
            Assert.Contains(local.Year.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
            Assert.DoesNotContain("AD", text, StringComparison.Ordinal);
        }

        using (CultureScope.Slovak())
        {
            var text = DisplayFormat.LocalDayAndTime(Instant);

            Assert.StartsWith(
                CultureInfo.GetCultureInfo("sk-SK").DateTimeFormat.AbbreviatedDayNames[(int)local.DayOfWeek] + ", ",
                text,
                StringComparison.Ordinal);
            Assert.Contains(local.Year.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
            Assert.DoesNotContain("po Kr.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Weekdays_ComeFromTheCultureAndStartOnItsFirstDayOfWeek()
    {
        // These used to be built from the enumeration's member names, so they read "Mon, Wed, Fri" in
        // every language and always started on Monday regardless of the culture.
        using (CultureScope.Slovak())
        {
            var options = WeekdayDisplay.Options();
            Assert.Equal(7, options.Count);
            Assert.Equal(ScheduledWeekdays.Monday, options[0].Value);
            Assert.Equal(
                CultureInfo.GetCultureInfo("sk-SK").DateTimeFormat.AbbreviatedDayNames[(int)DayOfWeek.Monday],
                options[0].Label);
            Assert.DoesNotContain("Mon", WeekdayDisplay.Summarize(ScheduledWeekdays.Monday), StringComparison.Ordinal);
        }

        using (CultureScope.English())
        {
            var options = WeekdayDisplay.Options();
            Assert.Equal(ScheduledWeekdays.Sunday, options[0].Value);
            Assert.Equal("Mon, Wed, Fri", WeekdayDisplay.Summarize(
                ScheduledWeekdays.Monday | ScheduledWeekdays.Wednesday | ScheduledWeekdays.Friday));
        }
    }

    [Fact]
    public void TheRootDocumentTakesItsLanguageFromTheCultureRatherThanALiteral()
    {
        // Asserted against the source because the root document is not a component a test can render.
        var app = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "FolderBackuper", "Components", "App.razor"));

        Assert.DoesNotContain("<html lang=\"en\"", app, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentUICulture.TwoLetterISOLanguageName", app, StringComparison.Ordinal);
    }

    [Fact]
    public void OneStoredRunRendersInWhicheverLanguageItIsReadIn()
    {
        // The point of storing a code rather than a sentence. The same row, read twice, in two languages.
        var stored = StoredMessage.Decode(
            UiMessage.KeyFor(BackupProblemMessage.SourceFileAccessDenied),
            null);

        string english;
        using (CultureScope.English())
        {
            english = MessageText.Resolve(stored);
        }

        string slovak;
        using (CultureScope.Slovak())
        {
            slovak = MessageText.Resolve(stored);
        }

        Assert.Equal("The source file could not be read because access was denied.", english);
        Assert.Equal("Zdrojový súbor sa nepodarilo prečítať, pretože prístup bol odmietnutý.", slovak);
    }

    [Fact]
    public void AStoredOperationRendersInWhicheverLanguageItIsReadIn()
    {
        using (CultureScope.English())
        {
            Assert.Equal("Read source file", EnumText.For(BackupOperation.ReadSourceFile));
        }

        using (CultureScope.Slovak())
        {
            Assert.Equal("Čítanie zdrojového súboru", EnumText.For(BackupOperation.ReadSourceFile));
        }
    }

    [Fact]
    public void NotificationEmailIsWrittenInTheSelectedLanguage()
    {
        // Rendered with no browser anywhere near it, which is the case that matters: a scheduled run
        // produces its email from a background worker.
        var payload = EmailPayload();

        using var slovak = CultureScope.Slovak();
        var message = Features.Notifications.NotificationTemplates.RunResult(payload);

        Assert.Contains("zálohovanie zlyhalo", message.Subject, StringComparison.Ordinal);
        Assert.Contains("Úloha", message.Text, StringComparison.Ordinal);
        Assert.Contains("Veľkosť archívu", message.Text, StringComparison.Ordinal);
        Assert.Contains("Závažnosť", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive size", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("backup failed", message.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmailsTimestampsAndTheArchiveNameStayInvariantUnderEitherLanguage()
    {
        // Email is read away from the machine that produced it, so its timestamps name the run's own
        // time zone and stay in a fixed format. The archive name is locale-independent by design.
        var payload = EmailPayload();

        var english = string.Empty;
        using (CultureScope.English())
        {
            english = Features.Notifications.NotificationTemplates.RunResult(payload).Text;
        }

        string slovak;
        using (CultureScope.Slovak())
        {
            slovak = Features.Notifications.NotificationTemplates.RunResult(payload).Text;
        }

        const string stamp = "2026-08-20 23:00:00 (UTC)";
        Assert.Contains(stamp, english, StringComparison.Ordinal);
        Assert.Contains(stamp, slovak, StringComparison.Ordinal);
        Assert.Contains(payload.ArchiveFileName!, english, StringComparison.Ordinal);
        Assert.Contains(payload.ArchiveFileName!, slovak, StringComparison.Ordinal);
    }

    private static Features.Notifications.NotificationPayload EmailPayload() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Účtovníctvo",
        RunOutcome.Failed,
        @"C:\Source",
        "NAS",
        @"\\nas\backups\Finance",
        "Finance_2026-08-20_23-00-00.zip",
        4096,
        Instant,
        Instant,
        Instant,
        TimeSpan.FromMinutes(3),
        "UTC",
        1,
        0,
        [
            // One problem, so the problems table renders and its headers can be checked too.
            new Features.Notifications.NotificationProblem(
                BackupProblemSeverity.Error,
                RunPhase.Transferring,
                BackupOperation.TransferDestinationArchive,
                nameof(BackupProblemCategory.DestinationUnavailable),
                @"\\nas\backups\Finance",
                UiMessage.For(BackupProblemMessage.DestinationUnavailable))
        ],
        UiMessage.For(BackupProblemMessage.DestinationUnavailable));

    private static string RepositoryRoot()
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
