using Bunit;
using FolderBackuper.Components.Pages;
using FolderBackuper.Features.Backups;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Features.Jobs;
using FolderBackuper.Features.Monitoring;
using FolderBackuper.Features.Settings;
using FolderBackuper.Infrastructure.Database;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Scheduling;
using FolderBackuper.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

public sealed class MonitoringPageTests
{
    private static DateTimeOffset Utc(int day, int hour) => new(2026, 8, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveRunPanel_LabelsSmbTransferAsUploading()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        var runId = Guid.NewGuid();
        var run = new ActiveRunView(runId, Guid.NewGuid(), "Docs", @"C:\Source", "NAS",
            DestinationType.Smb, RunPhase.Transferring, RunTrigger.Manual, Utc(1, 1), false);
        PublishTransfer(context, runId);

        var component = context.Render<ActiveRunPanel>(ps => ps.Add(p => p.Run, run));

        Assert.Contains("uploading", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copying", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveRunPanel_LabelsLocalTransferAsCopying()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        var runId = Guid.NewGuid();
        var run = new ActiveRunView(runId, Guid.NewGuid(), "Docs", @"C:\Source", "Disk",
            DestinationType.Local, RunPhase.Transferring, RunTrigger.Manual, Utc(1, 1), false);
        PublishTransfer(context, runId);

        var component = context.Render<ActiveRunPanel>(ps => ps.Add(p => p.Run, run));

        Assert.Contains("copying", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uploading", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveRunPanel_IsIndeterminateDuringScanning()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        var registry = context.Services.GetRequiredService<BackupProgressRegistry>();
        var runId = Guid.NewGuid();
        var run = new ActiveRunView(runId, Guid.NewGuid(), "Docs", @"C:\Source", "Disk",
            DestinationType.Local, RunPhase.Scanning, RunTrigger.Manual, Utc(1, 1), false);
        registry.Publish(new BackupProgressSnapshot(runId, RunPhase.Scanning, 0, 0, 0, 0, 0, 0,
            null, 0, 0, 0, 0, TimeSpan.FromSeconds(1), null, true), force: true);

        var component = context.Render<ActiveRunPanel>(ps => ps.Add(p => p.Run, run));

        // Scanning shows the indeterminate caption and no determinate percentage.
        Assert.Contains("Scanning source", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("% complete", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_ShowsActiveRunJobStatusAndNoSecretsOrLogs()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        var destination = await SeedDestinationAsync(database);
        var job = DatabaseInitializationTests.Job(destination.Id, "Accounting");
        job.Activate();
        job.ManagedArtifactCount = 3;
        job.ManagedArtifactBytes = 300;
        job.LatestArtifactBytes = 120;
        job.StorageConfirmedAtUtc = Utc(4, 1);
        var running = MonitoringTestSeed.Running(job, destination, RunPhase.Compressing, Utc(5, 1));
        await using (var db = await database.ContextFactory.CreateDbContextAsync())
        {
            db.AddRange(job, running);
            await db.SaveChangesAsync();
        }

        var component = context.Render<Dashboard>();
        component.WaitForAssertion(() => Assert.Contains("Accounting", component.Markup, StringComparison.Ordinal));

        Assert.Contains("Active backup", component.Markup, StringComparison.Ordinal);
        Assert.Contains("retained backup", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Run now", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw log", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("export log", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task History_ListsRunsWithStatusAndNoLogExport()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        var destination = await SeedDestinationAsync(database);
        var job = DatabaseInitializationTests.Job(destination.Id, "Nightly");
        var success = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, Utc(1, 1));
        var failed = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Failed, Utc(2, 1));
        await using (var db = await database.ContextFactory.CreateDbContextAsync())
        {
            db.AddRange(job, success, failed);
            await db.SaveChangesAsync();
        }

        context.Render<MudPopoverProvider>();
        var component = context.Render<History>();
        component.WaitForAssertion(() => Assert.Contains("Nightly", component.Markup, StringComparison.Ordinal));

        Assert.Contains("cannot be cleared", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Failed", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("raw log", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Export", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calendar_ShowsMonthAndAgendaButHasNoWeekOrDayView()
    {
        await using var database = await CreateDatabaseAsync();
        await using var context = CreateContext(database);
        var destination = await SeedDestinationAsync(database);
        var job = DatabaseInitializationTests.Job(destination.Id, "Weekly");
        var run = MonitoringTestSeed.Terminal(job, destination, RunOutcome.Successful, DateTimeOffset.UtcNow.AddDays(-1));
        await using (var db = await database.ContextFactory.CreateDbContextAsync())
        {
            db.AddRange(job, run);
            await db.SaveChangesAsync();
        }

        context.Render<MudPopoverProvider>();
        var component = context.Render<Calendar>();
        component.WaitForAssertion(() => Assert.Contains("calendar-grid", component.Markup, StringComparison.Ordinal));

        var buttons = component.FindAll("button").Select(x => x.TextContent).ToList();
        Assert.Contains(buttons, x => x.Contains("Month", StringComparison.Ordinal));
        Assert.Contains(buttons, x => x.Contains("Agenda", StringComparison.Ordinal));
        Assert.DoesNotContain(buttons, x => x.Equals("Week", StringComparison.Ordinal));
        Assert.DoesNotContain(buttons, x => x.Equals("Day", StringComparison.Ordinal));
    }

    private static void PublishTransfer(BunitContext context, Guid runId)
    {
        var registry = context.Services.GetRequiredService<BackupProgressRegistry>();
        registry.Publish(new BackupProgressSnapshot(runId, RunPhase.Transferring, 10, 2, 500, 10, 2, 1000,
            "file.txt", 500, 1000, 500, 100, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), true), force: true);
    }

    private static async Task<TemporaryDatabase> CreateDatabaseAsync()
    {
        var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        return database;
    }

    private static async Task<Destination> SeedDestinationAsync(TemporaryDatabase database)
    {
        var root = Directory.CreateDirectory(Path.Combine(database.Paths.Root, "destination")).FullName;
        var destination = new Destination
        {
            Name = "Primary",
            Type = DestinationType.Local,
            RootPath = root,
            VerificationResult = DestinationVerificationResult.Succeeded,
            VerificationFingerprint = "verified"
        };
        await using var db = await database.ContextFactory.CreateDbContextAsync();
        db.Add(destination);
        await db.SaveChangesAsync();
        return destination;
    }

    private static BunitContext CreateContext(TemporaryDatabase database)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(database.ContextFactory);
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        context.Services.AddSingleton(database.MutationGate);
        context.Services.AddSingleton(database.RunPersistence);
        context.Services.AddSingleton(new InstallationIdentityService(database.ContextFactory, TimeProvider.System));
        context.Services.AddSingleton(new EffectiveDestinationService([new LocalDestinationAdapter()], new PassthroughProtector()));
        context.Services.AddSingleton<OwnershipMarkerService>();
        context.Services.AddSingleton<BackupArtifactOwnershipVerifier>();
        context.Services.AddSingleton<BackupProgressRegistry>();
        context.Services.AddSingleton<BackupCancellationRegistry>();
        context.Services.AddSingleton<BackupExecutionQueue>();
        context.Services.AddSingleton<BackupExecutionService>();
        context.Services.AddSingleton<BackupRetentionService>();
        context.Services.AddSingleton<RunQueryService>();
        context.Services.AddSingleton<DashboardQueryService>();
        context.Services.AddSingleton(new ScheduleOccurrenceCalculator(TimeProvider.System));
        context.Services.AddSingleton<IMachineTimeZoneProvider>(new MachineTimeZoneProvider());
        context.Services.AddSingleton<CalendarOccurrenceService>();
        context.Services.AddSingleton<CalendarEntryService>();
        context.Services.AddSingleton(BuildDestinationService(database));
        context.Services.AddSingleton(BuildJobService(database));
        return context;
    }

    private static JobService BuildJobService(TemporaryDatabase database)
    {
        var effective = new EffectiveDestinationService([new LocalDestinationAdapter()], new PassthroughProtector());
        return new(database.ContextFactory, new ConfigurationMutationGate(database.ContextFactory), effective,
            new JobDestinationTestService(effective, new OwnershipMarkerService()),
            new InstallationIdentityService(database.ContextFactory, TimeProvider.System), TimeProvider.System);
    }

    private static DestinationService BuildDestinationService(TemporaryDatabase database) =>
        new(database.ContextFactory, new PassthroughProtector(), new NoLocalUnc(),
            [new LocalDestinationAdapter()], TimeProvider.System);

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => System.Text.Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => System.Text.Encoding.UTF8.GetString(protectedData);
    }

    private sealed class NoLocalUnc : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => false; }
}
