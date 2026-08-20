using System.Globalization;
using System.Runtime.CompilerServices;

namespace FolderBackuper.Tests;

/// <summary>
/// Pins the whole suite to English before any test runs.
/// </summary>
/// <remarks>
/// The component tests assert on rendered text and the service tests assert on formatted dates and
/// numbers, so without this the suite would fail on a machine whose own language is Slovak — the very
/// machine this feature exists for. A module initializer is used rather than a fixture because it is
/// guaranteed to run before any test in the assembly and needs no per-class opt-in.
///
/// A test that wants Slovak uses <see cref="CultureScope"/>, which changes the same process-wide
/// defaults the application changes and restores them afterwards. That is safe because the suite
/// disables collection parallelization; see AssemblyInfo.cs.
/// </remarks>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void PinToEnglish()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
    }
}
