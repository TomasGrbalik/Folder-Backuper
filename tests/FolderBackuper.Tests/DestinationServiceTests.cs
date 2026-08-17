using System.Text;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FolderBackuper.Tests;

public sealed class DestinationServiceTests
{
    [Fact]
    public async Task CreateAndList_NeverReturnPassword_AndAttemptCapacity()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var adapter = new FakeAdapter(DestinationType.Local);
        var service = Service(database, adapter);

        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));
        var listed = Assert.Single(await service.ListAsync());

        Assert.Equal(created, listed);
        Assert.Equal(1234, listed.AvailableBytes);
        Assert.DoesNotContain(typeof(DestinationSummary).GetProperties(), x => x.Name.Contains("Password", StringComparison.Ordinal) && x.PropertyType == typeof(string));
        Assert.True(adapter.CapacityAttempts >= 2);
    }

    [Fact]
    public async Task Edit_KeepsPasswordUnlessReplaced_AndInvalidatesVerification()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var smb = new FakeAdapter(DestinationType.Smb);
        var service = Service(database, smb, new NeverLocalDetector());
        var created = await service.CreateAsync(new("NAS", DestinationType.Smb, @"\\nas\backups", @"NAS\backup", "first"));
        await service.TestAsync(created.Id);

        await service.EditAsync(created.Id, new("NAS", DestinationType.Smb, @"\\nas\backups", @"NAS\changed"));
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            var destination = await context.Destinations.SingleAsync();
            Assert.Equal("first", Encoding.UTF8.GetString(destination.ProtectedPassword!));
            Assert.Equal(DestinationVerificationResult.Unverified, destination.VerificationResult);
        }

        await service.EditAsync(created.Id, new("NAS", DestinationType.Smb, @"\\nas\backups", @"NAS\changed", "second"));
        await using var inspection = await database.ContextFactory.CreateDbContextAsync();
        Assert.Equal("second", Encoding.UTF8.GetString((await inspection.Destinations.SingleAsync()).ProtectedPassword!));
    }

    [Fact]
    public async Task Test_PersistsStructuredVerificationAndLastAccess()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var adapter = new FakeAdapter(DestinationType.Local)
        {
            TestResult = new(false, DestinationAccessResult.CleanupFailed, "Cleanup failed", 5)
        };
        var service = Service(database, adapter);
        var created = await service.CreateAsync(new("Primary", DestinationType.Local, database.Paths.Staging));

        var result = await service.TestAsync(created.Id);
        await using var context = await database.ContextFactory.CreateDbContextAsync();
        var destination = await context.Destinations.SingleAsync();
        Assert.Equal(DestinationAccessResult.CleanupFailed, result.Result);
        Assert.Equal(DestinationVerificationResult.Failed, destination.VerificationResult);
        Assert.Equal(DestinationAccessSource.Management, destination.LastAccessSource);
        Assert.Equal("Cleanup failed", destination.LastAccessErrorSummary);
    }

    [Fact]
    public async Task Create_RejectsLocalHostUnc()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = Service(database, new FakeAdapter(DestinationType.Smb), new AlwaysLocalDetector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new("Local share", DestinationType.Smb, @"\\localhost\share", "user", "secret")));
    }

    [Fact]
    public async Task Create_RejectsLocalDestinationOverlappingAnyConfiguredSource()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var existingDestination = DatabaseInitializationTests.Destination("Existing");
        var job = DatabaseInitializationTests.Job(existingDestination.Id, "Documents");
        job.SourcePath = database.Paths.Staging;
        await using (var context = await database.ContextFactory.CreateDbContextAsync())
        {
            context.AddRange(existingDestination, job);
            await context.SaveChangesAsync();
        }

        var service = Service(database, new FakeAdapter(DestinationType.Local));
        var nestedDestination = Path.Combine(database.Paths.Staging, "Backups");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(new("Unsafe", DestinationType.Local, nestedDestination)));
        Assert.Contains("overlaps configured source", exception.Message, StringComparison.Ordinal);
    }

    private static DestinationService Service(TemporaryDatabase database, IDestinationAdapter adapter, ILocalHostUncDetector? detector = null) =>
        new(database.ContextFactory, new PlainTestProtector(), detector ?? new NeverLocalDetector(), [adapter], TimeProvider.System);

    private sealed class PlainTestProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => Encoding.UTF8.GetString(protectedData);
    }
    private sealed class NeverLocalDetector : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => false; }
    private sealed class AlwaysLocalDetector : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => true; }
    private sealed class FakeAdapter(DestinationType type) : IDestinationAdapter
    {
        public DestinationType Type => type;
        public int CapacityAttempts { get; private set; }
        public DestinationOperationResult TestResult { get; set; } = DestinationOperationResult.Success("Passed", 1234);
        public Task<DestinationOperationResult> TestAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(TestResult);
        public Task<long?> GetAvailableBytesAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken)
        { CapacityAttempts++; return Task.FromResult<long?>(1234); }
        public Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) => action();
    }
}
