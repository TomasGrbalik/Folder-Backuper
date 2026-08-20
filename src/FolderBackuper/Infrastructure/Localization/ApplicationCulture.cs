using System.Globalization;

namespace FolderBackuper.Infrastructure.Localization;

/// <summary>
/// Applies the selected interface language as the process-wide culture default.
/// </summary>
/// <remarks>
/// The language is one machine-wide preference rather than a per-request or per-browser one, so it is
/// applied once here instead of through request localization middleware. Nothing in this application
/// sets a per-thread culture, so every thread resolves through these defaults: the static render of the
/// root document, every interactive circuit, the scheduler, and the notification outbox worker. Request
/// localization middleware would instead derive a culture from each request's Accept-Language header,
/// overriding the stored preference and leaving work that has no request on a different language.
/// </remarks>
public static class ApplicationCulture
{
    /// <summary>Sets both the formatting and the resource-lookup culture for the whole process.</summary>
    public static void Apply(InterfaceLanguage language)
    {
        var culture = language.ToCulture();
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // Deliberately nothing else. Assigning CultureInfo.CurrentCulture here as well would pin an
        // explicit culture onto whichever thread happened to run this call, and a thread that holds an
        // explicit culture stops following the defaults above — so a pooled thread would keep serving the
        // old language after a change. Leaving every thread without one is what makes a single pair of
        // defaults govern the whole process.
        //
        // The consequence is that the caller does not see the new culture in its own continuation. That is
        // why changing the language reloads the page instead of re-rendering it: the reload establishes a
        // new circuit, which reads these defaults.
    }
}
