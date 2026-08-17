using Bunit;
using FolderBackuper.Components.Pages;
using FolderBackuper.Infrastructure.Filesystem;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FolderBackuper.Tests;

public sealed class SourceDialogTests
{
    [Fact]
    public async Task Browser_ShowsOneLevelMetadataAndSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"folder-backuper-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Child"));
        File.WriteAllText(Path.Combine(root, "sample.txt"), "sample");
        try
        {
            await using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddMudServices();
            context.Services.AddSingleton<SourceBrowser>();
            context.Render<MudPopoverProvider>();
            var provider = context.Render<MudDialogProvider>();
            var dialogs = context.Services.GetRequiredService<IDialogService>();
            var parameters = new DialogParameters { [nameof(SourceBrowserDialog.InitialPath)] = root };
            await dialogs.ShowAsync<SourceBrowserDialog>("Browse source", parameters);

            provider.WaitForAssertion(() => Assert.Contains("Child", provider.Markup, StringComparison.Ordinal));
            Assert.Contains("sample.txt", provider.Markup, StringComparison.Ordinal);
            Assert.Contains("Select this directory", provider.Markup, StringComparison.Ordinal);
            Assert.Contains("Reparse points", provider.Markup, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Preview_ExplainsInformationalResultAndBackupRecheck()
    {
        var root = Path.Combine(Path.GetTempPath(), $"folder-backuper-preview-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddMudServices();
            context.Services.AddSingleton<SourcePreview>();
            context.Render<MudPopoverProvider>();
            var provider = context.Render<MudDialogProvider>();
            var dialogs = context.Services.GetRequiredService<IDialogService>();
            var parameters = new DialogParameters { [nameof(SourcePreviewDialog.Path)] = root };
            await dialogs.ShowAsync<SourcePreviewDialog>("Source preview", parameters);

            provider.WaitForAssertion(() =>
            {
                Assert.Contains("informational only", provider.Markup, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("rechecked during backup", provider.Markup, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
