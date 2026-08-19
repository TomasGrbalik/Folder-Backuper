using System.Diagnostics;

namespace FolderBackuper.Infrastructure.ServiceHosting;

/// <summary>
/// Records a classified startup failure where an operator can find it.
/// </summary>
/// <remarks>
/// This deliberately does not use Serilog. The failures it exists for happen before the host is
/// built, while <c>Log.Logger</c> is still a console-only bootstrap logger and the service has no
/// console. Both sinks are individually guarded so a reporting failure can never mask the original
/// exception.
/// </remarks>
public static class StartupFailureReporter
{
    public static void Report(StartupFailure failure, Exception exception, ApplicationPaths? paths)
    {
        var text = Compose(failure, exception);
        WriteEventLog(failure, text);
        WriteFallbackFile(text, paths);
    }

    public static string Compose(StartupFailure failure, Exception exception) =>
        $"""
         Folder Backuper failed to start.

         Category: {failure.Category}
         Event ID: {failure.EventId}
         {failure.OperatorMessage}

         {exception}
         """;

    private static void WriteEventLog(StartupFailure failure, string text)
    {
        try
        {
            EventLog.WriteEntry(
                WindowsServiceMetadata.EventLogSource,
                text,
                EventLogEntryType.Error,
                failure.EventId);
        }
        catch (Exception)
        {
            // The event source is registered by the installer. A console or development run
            // without it must still surface the original failure.
        }
    }

    private static void WriteFallbackFile(string text, ApplicationPaths? paths)
    {
        try
        {
            var logs = paths?.Logs ?? ApplicationPaths.Resolve(configuredRoot: null).Logs;
            Directory.CreateDirectory(logs);
            File.AppendAllText(
                Path.Combine(logs, "startup-failure.log"),
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // A failure to resolve or write the data root is itself one of the reported categories.
        }
    }
}
