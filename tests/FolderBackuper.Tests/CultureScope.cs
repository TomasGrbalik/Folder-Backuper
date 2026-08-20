using System.Globalization;
using FolderBackuper.Infrastructure.Localization;

namespace FolderBackuper.Tests;

/// <summary>
/// Runs a block of a test in one interface language and restores the previous one afterwards.
/// </summary>
/// <remarks>
/// Applies the language exactly as the application does, through the process-wide culture defaults, so
/// that a test exercises the real mechanism rather than a per-thread stand-in for it. Safe because the
/// suite disables collection parallelization; see AssemblyInfo.cs.
/// </remarks>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo? previousCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? previousUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    private CultureScope(InterfaceLanguage language) => ApplicationCulture.Apply(language);

    public static CultureScope Slovak() => new(InterfaceLanguage.Slovak);

    public static CultureScope English() => new(InterfaceLanguage.English);

    public static CultureScope For(InterfaceLanguage language) => new(language);

    /// <summary>
    /// Re-applies English to the process defaults inside an open scope, for a test that needs to prove a
    /// later call moves them back.
    /// </summary>
    public static void ApplyEnglishDefaults() => ApplicationCulture.Apply(InterfaceLanguage.English);

    public void Dispose()
    {
        // Only the defaults are restored, because only the defaults were changed. Assigning a per-thread
        // culture here would pin one onto this thread and leak it into whatever test runs on it next.
        CultureInfo.DefaultThreadCurrentCulture = previousCulture;
        CultureInfo.DefaultThreadCurrentUICulture = previousUiCulture;
    }
}
