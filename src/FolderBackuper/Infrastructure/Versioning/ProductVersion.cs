using System.Reflection;

namespace FolderBackuper.Infrastructure.Versioning;

/// <summary>
/// The version of this build, as stamped into the assembly when it was compiled.
/// </summary>
/// <remarks>
/// The version is read from <see cref="AssemblyInformationalVersionAttribute"/>, because that is the
/// only attribute carrying the pre-release suffix: <c>FileVersion</c> and <c>AssemblyVersion</c>
/// cannot express it and must stay numeric for Inno Setup. The SDK also appends the commit hash
/// after a <c>+</c>, which is build provenance rather than part of the version.
/// <para>
/// The assembly is located through a type inside it rather than through
/// <see cref="Assembly.GetEntryAssembly"/>, because under a test runner the entry assembly is the
/// runner, whose version has nothing to do with this product.
/// </para>
/// </remarks>
public static class ProductVersion
{
    private const string DevelopmentSuffix = "dev";
    private const int ShortShaLength = 7;

    private static readonly Stamp Current = Stamp.Read();

    /// <summary>The version shown to a person, for example <c>1.2.0</c> or <c>1.2.1-dev</c>.</summary>
    public static string Display => Current.Display;

    /// <summary>The commit this build was made from, or null when it carries no provenance.</summary>
    public static string? CommitSha => Current.CommitSha;

    /// <summary>The first characters of <see cref="CommitSha"/>, which is what a person reads.</summary>
    public static string? ShortCommitSha =>
        Current.CommitSha is { Length: >= ShortShaLength } sha ? sha[..ShortShaLength] : Current.CommitSha;

    /// <summary>
    /// The parsed version, or null when the stamped version could not be parsed. Null disables the
    /// update comparison rather than guessing, because offering an update on a version nobody can
    /// interpret would be worse than offering none.
    /// </summary>
    public static ReleaseVersion? Version => Current.Version;

    /// <summary>True for any build the release workflow did not produce.</summary>
    public static bool IsDevelopmentBuild =>
        Current.Version?.PreRelease?.StartsWith(DevelopmentSuffix, StringComparison.Ordinal) ?? false;

    private sealed record Stamp(string Display, string? CommitSha, ReleaseVersion? Version)
    {
        public static Stamp Read()
        {
            var assembly = typeof(ProductVersion).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
            {
                // Building without the attribute is not expected, but a version that cannot be read
                // must never stop the service from running backups.
                var assemblyVersion = assembly.GetName().Version;
                var fallback = assemblyVersion is null
                    ? "unknown"
                    : FormattableString.Invariant(
                        $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
                return new Stamp(fallback, null, Parse(fallback));
            }

            var separator = informational.IndexOf('+', StringComparison.Ordinal);
            if (separator < 0)
            {
                return new Stamp(informational, null, Parse(informational));
            }

            var display = informational[..separator];
            var sha = separator == informational.Length - 1 ? null : informational[(separator + 1)..];
            return new Stamp(display, sha, Parse(display));
        }

        private static ReleaseVersion? Parse(string display) =>
            ReleaseVersion.TryParse(display, out var parsed) ? parsed : null;
    }
}
