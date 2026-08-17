using System.Text;
using Bunit;
using FolderBackuper.Components.Pages;
using FolderBackuper.Features.Destinations;
using FolderBackuper.Infrastructure.Filesystem;
using FolderBackuper.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

public sealed class DestinationsPageTests
{
    [Fact]
    public async Task Page_RendersPasswordFreeManagementActionsWithoutArchiveOrBackup()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = new DestinationService(
            database.ContextFactory,
            new TestProtector(),
            new TestDetector(),
            [new TestAdapter()],
            TimeProvider.System);
        await service.CreateAsync(new("Primary storage", DestinationType.Local, database.Paths.Staging));
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(service);

        var component = context.Render<Destinations>();

        component.WaitForAssertion(() => Assert.Contains("Primary storage", component.Markup, StringComparison.Ordinal));
        Assert.Contains("Test access", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Edit", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("password", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("archive", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restore", component.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("back up", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDestination_ClickOpensFormDialog()
    {
        await using var database = new TemporaryDatabase();
        await database.Initializer.InitializeAsync();
        var service = new DestinationService(
            database.ContextFactory,
            new TestProtector(),
            new TestDetector(),
            [new TestAdapter()],
            TimeProvider.System);
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(service);
        context.Render<MudPopoverProvider>();
        var dialogProvider = context.Render<MudDialogProvider>();
        var component = context.Render<Destinations>();
        component.WaitForState(() => component.Markup.Contains("Add destination", StringComparison.Ordinal));

        component.FindAll("button")
            .Single(button => button.TextContent.Contains("Add destination", StringComparison.Ordinal))
            .Click();

        dialogProvider.WaitForAssertion(() =>
        {
            Assert.Contains("Add destination", dialogProvider.Markup, StringComparison.Ordinal);
            Assert.Contains("Root path", dialogProvider.Markup, StringComparison.Ordinal);
            Assert.Contains("Storage type", dialogProvider.Markup, StringComparison.Ordinal);
        });
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] protectedData) => Encoding.UTF8.GetString(protectedData);
    }
    private sealed class TestDetector : ILocalHostUncDetector { public bool IsHostedLocally(string uncPath) => false; }
    private sealed class TestAdapter : IDestinationAdapter
    {
        public DestinationType Type => DestinationType.Local;
        public Task<DestinationOperationResult> TestAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult(DestinationOperationResult.Success("Passed"));
        public Task<long?> GetAvailableBytesAsync(DestinationAccessConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult<long?>(1024);
        public Task<T> ExecuteAsync<T>(DestinationAccessConfiguration configuration, Func<Task<T>> action) => action();
    }
}
