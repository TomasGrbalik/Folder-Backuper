using System.Net;
using FolderBackuper.Features.Updates;
using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Tests;

/// <summary>
/// The decision the whole feature exists to make: is a newer version available, and what does a
/// check that could not answer leave behind.
/// </summary>
public sealed class UpdateCheckServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckNow_ReportsAVersionAboveThisBuild()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var store = new UpdateStatusStore();
        var handler = Feed(Above());

        var outcome = await UpdateTestFactory.Checks(handler, database, store, clock).CheckNowAsync();

        Assert.Equal(LatestReleaseStatus.Read, outcome.Status);
        Assert.True(store.Current.UpdateAvailable);
        Assert.Equal(Now, store.Current.LastCheckedUtc);
        Assert.Null(store.Current.LastProblem);
        Assert.Equal("https://example.test/release", store.Current.DownloadUrl);
    }

    [Fact]
    public async Task CheckNow_DoesNotOfferTheVersionThisBuildAlreadyIs()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var store = new UpdateStatusStore();
        var handler = Feed(Installed.ToString());

        await UpdateTestFactory.Checks(handler, database, store, new TestTimeProvider(Now)).CheckNowAsync();

        Assert.False(store.Current.UpdateAvailable);
        Assert.True(store.Current.HasBeenChecked);
    }

    [Fact]
    public async Task CheckNow_DoesNotOfferAReleaseOlderThanThisBuild()
    {
        // The releases/latest endpoint answers by publication date, so a backported patch published
        // after a newer version really can look like this.
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var store = new UpdateStatusStore();

        await UpdateTestFactory.Checks(Feed("v0.0.1"), database, store, new TestTimeProvider(Now)).CheckNowAsync();

        Assert.False(store.Current.UpdateAvailable);
        Assert.Equal("0.0.1", store.Current.LatestDisplay);
    }

    [Fact]
    public async Task CheckNow_ReportsNoReleaseWithoutClaimingAnUpdate()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var store = new UpdateStatusStore();
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var outcome = await UpdateTestFactory.Checks(handler, database, store, new TestTimeProvider(Now))
            .CheckNowAsync();

        Assert.Equal(LatestReleaseStatus.NoRelease, outcome.Status);
        Assert.False(store.Current.UpdateAvailable);
        Assert.Null(store.Current.LatestDisplay);
        Assert.True(store.Current.HasBeenChecked);
    }

    [Fact]
    public async Task CheckNow_MakesNoRequestWhenTheCheckIsSwitchedOff()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var store = new UpdateStatusStore();
        await UpdateTestFactory.Settings(database, clock).SetEnabledAsync(false);

        var handler = Feed("v99.0.0");
        var outcome = await UpdateTestFactory.Checks(handler, database, store, clock).CheckNowAsync();

        Assert.Null(outcome.Status);
        Assert.Empty(handler.Requests);
        Assert.False(store.Current.UpdateAvailable);
    }

    [Fact]
    public async Task CheckNow_ClearsAStandingNoticeWhenTheCheckIsSwitchedOff()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var store = new UpdateStatusStore();
        var handler = Feed(Above());

        await UpdateTestFactory.Checks(handler, database, store, clock).CheckNowAsync();
        Assert.True(store.Current.UpdateAvailable);

        // Switching it off must take effect at once rather than leaving the notice in the app bar.
        await UpdateTestFactory.Settings(database, clock).SetEnabledAsync(false);
        await UpdateTestFactory.Checks(handler, database, store, clock).CheckNowAsync();

        Assert.False(store.Current.UpdateAvailable);
        Assert.Null(store.Current.LatestDisplay);
    }

    [Fact]
    public async Task CheckNow_KeepsWhatWasKnownWhenACheckCannotAnswer()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var store = new UpdateStatusStore();
        var newer = Above();

        await UpdateTestFactory.Checks(Feed(newer), database, store, clock).CheckNowAsync();

        var offline = FakeHttpMessageHandler.Throwing(new HttpRequestException("no route"));
        var outcome = await UpdateTestFactory.Checks(offline, database, store, clock).CheckNowAsync();

        Assert.Equal(LatestReleaseStatus.Unavailable, outcome.Status);
        // The failure records itself without withdrawing an update that genuinely exists.
        Assert.True(store.Current.UpdateAvailable);
        Assert.Equal(newer.TrimStart('v'), store.Current.LatestDisplay);
        Assert.NotNull(store.Current.LastProblem);
    }

    [Fact]
    public async Task CheckNow_NeverReportsUpToDateAfterAFailedFirstCheck()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var store = new UpdateStatusStore();
        var offline = FakeHttpMessageHandler.Throwing(new HttpRequestException("no route"));

        await UpdateTestFactory.Checks(offline, database, store, new TestTimeProvider(Now)).CheckNowAsync();

        Assert.False(store.Current.UpdateAvailable);
        Assert.Null(store.Current.LatestDisplay);
        // Nothing was established, so the snapshot must not claim that a check completed.
        Assert.False(store.Current.HasBeenChecked);
        Assert.NotNull(store.Current.LastProblem);
    }

    [Fact]
    public async Task CheckNow_BacksOffAfterRepeatedInconclusiveChecks()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var store = new UpdateStatusStore();
        var offline = FakeHttpMessageHandler.Throwing(new HttpRequestException("no route"));
        var checks = UpdateTestFactory.Checks(offline, database, store, new TestTimeProvider(Now));

        var delays = new List<TimeSpan>();
        for (var attempt = 0; attempt <= UpdateCheckService.MaxConsecutiveRetries; attempt++)
        {
            delays.Add((await checks.CheckNowAsync()).NextDelay);
        }

        // The early attempts retry soon; a machine with no route out settles on the daily cadence
        // rather than writing about it every hour forever.
        Assert.Equal(UpdateCheckService.RetryInterval, delays[0]);
        Assert.Equal(UpdateCheckService.AnsweredInterval, delays[^1]);
    }

    [Fact]
    public async Task CheckNow_ForgetsEarlierFailuresOnceItGetsAnAnswer()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var clock = new TestTimeProvider(Now);
        var store = new UpdateStatusStore();
        var offline = FakeHttpMessageHandler.Throwing(new HttpRequestException("no route"));

        for (var attempt = 0; attempt <= UpdateCheckService.MaxConsecutiveRetries; attempt++)
        {
            await UpdateTestFactory.Checks(offline, database, store, clock).CheckNowAsync();
        }

        var recovered = await UpdateTestFactory.Checks(Feed(Above()), database, store, clock).CheckNowAsync();

        Assert.Equal(UpdateCheckService.AnsweredInterval, recovered.NextDelay);
        // A successful check clears the recorded problem rather than leaving it on screen forever.
        Assert.Null(store.Current.LastProblem);
    }

    [Theory]
    [InlineData(LatestReleaseStatus.Read, 0, 24)]
    [InlineData(LatestReleaseStatus.NoRelease, 0, 24)]
    [InlineData(LatestReleaseStatus.Unavailable, 1, 1)]
    [InlineData(LatestReleaseStatus.Unavailable, 2, 1)]
    [InlineData(LatestReleaseStatus.Unavailable, 3, 24)]
    [InlineData(LatestReleaseStatus.Unavailable, 9, 24)]
    public void NextDelay_PicksTheCadenceFromTheOutcome(
        LatestReleaseStatus status,
        int consecutiveFailures,
        int expectedHours)
    {
        Assert.Equal(
            TimeSpan.FromHours(expectedHours),
            UpdateCheckService.NextDelay(status, consecutiveFailures, rateLimitResetIn: null));
    }

    [Fact]
    public void NextDelay_WaitsExactlyAsLongAsARateLimitSays()
    {
        var delay = UpdateCheckService.NextDelay(
            LatestReleaseStatus.Unavailable,
            consecutiveFailures: 1,
            rateLimitResetIn: TimeSpan.FromMinutes(20));

        // A minute past the stated reset, so the retry does not land on the boundary.
        Assert.Equal(TimeSpan.FromMinutes(21), delay);
    }

    [Theory]
    // A limit that has already lifted, or one further out than the ordinary cadence, must not move
    // the wait away from what the ordinary rules give.
    [InlineData(-5)]
    [InlineData(60 * 48)]
    public void NextDelay_IgnoresAnUnusableRateLimitReset(int resetMinutes)
    {
        var delay = UpdateCheckService.NextDelay(
            LatestReleaseStatus.Unavailable,
            consecutiveFailures: 1,
            rateLimitResetIn: TimeSpan.FromMinutes(resetMinutes));

        Assert.Equal(UpdateCheckService.RetryInterval, delay);
    }

    [Fact]
    public void NextDelay_KeepsTheOrdinaryCadenceForAnAnsweredCheckEvenWhileRateLimited()
    {
        Assert.Equal(
            UpdateCheckService.AnsweredInterval,
            UpdateCheckService.NextDelay(LatestReleaseStatus.Read, 0, TimeSpan.FromMinutes(5)));
    }

    private static ReleaseVersion Installed => ProductVersion.Version!.Value;

    /// <summary>A version one patch above this build, so no test hard-codes the current version.</summary>
    private static string Above() =>
        $"v{Installed.Major}.{Installed.Minor}.{Installed.Patch + 1}";

    private static FakeHttpMessageHandler Feed(string tag) =>
        FakeHttpMessageHandler.Returning(HttpStatusCode.OK, UpdateTestFactory.ReleasePayload(tag));
}
