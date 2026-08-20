using System.Globalization;

namespace FolderBackuper.Infrastructure.Versioning;

/// <summary>
/// A product version as this application compares them: three numeric parts and an optional
/// pre-release suffix.
/// </summary>
/// <remarks>
/// This is deliberately not a complete semantic-version implementation. The specification orders
/// pre-release identifiers field by field and compares dot-separated numeric fields numerically.
/// That machinery is unnecessary here because the only question ever asked is whether one published
/// release supersedes the running build, and the only suffix this product ships is <c>dev</c>. A
/// release wins against a pre-release of the same three numbers, and two pre-releases of the same
/// three numbers are ordered by plain text comparison.
/// <para>
/// The type deliberately does not implement <see cref="IComparable{T}"/>: a single
/// <see cref="IsNewerThan"/> question is all the application asks, and a partial comparison surface
/// would invite callers to assume ordering guarantees that are not implemented.
/// </para>
/// </remarks>
public readonly record struct ReleaseVersion
{
    private ReleaseVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The pre-release suffix without its leading hyphen, or null for a release.</summary>
    public string? PreRelease { get; }

    public bool IsPreRelease => PreRelease is not null;

    /// <summary>
    /// Parses a three-part version with an optional pre-release suffix. Build metadata after a
    /// <c>+</c> is discarded, so an informational version stamped with a commit hash parses.
    /// </summary>
    /// <remarks>
    /// A leading <c>v</c> is rejected. Tag names carry one, and stripping it belongs at the boundary
    /// that reads tags, so that no other caller can accidentally accept a malformed value. Four-part
    /// input is rejected too, so a Win32 file version can never masquerade as a release version.
    /// </remarks>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = text.Trim();

        // Build metadata never participates in ordering, so it goes before anything else is read.
        var metadata = candidate.IndexOf('+', StringComparison.Ordinal);
        if (metadata >= 0)
        {
            candidate = candidate[..metadata];
        }

        string? preRelease = null;
        var hyphen = candidate.IndexOf('-', StringComparison.Ordinal);
        if (hyphen >= 0)
        {
            preRelease = candidate[(hyphen + 1)..];
            candidate = candidate[..hyphen];
            if (preRelease.Length == 0)
            {
                return false;
            }
        }

        var parts = candidate.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!TryParseNumber(parts[index], out numbers[index]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>True when this version supersedes <paramref name="other"/>.</summary>
    public bool IsNewerThan(ReleaseVersion other) => Compare(this, other) > 0;

    public override string ToString() =>
        PreRelease is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}");

    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;

        // A leading zero would make ordering surprising, and nothing this product publishes has one.
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        // NumberStyles.None rejects signs and whitespace, and overflow returns false rather than
        // throwing, so an absurd version number is simply unparseable.
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static int Compare(ReleaseVersion left, ReleaseVersion right)
    {
        var numeric = left.Major.CompareTo(right.Major);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = left.Minor.CompareTo(right.Minor);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = left.Patch.CompareTo(right.Patch);
        if (numeric != 0)
        {
            return numeric;
        }

        return (left.PreRelease, right.PreRelease) switch
        {
            (null, null) => 0,
            // A release supersedes its own pre-release, which is what keeps a freshly published
            // version from being offered to the development build that follows it.
            (null, not null) => 1,
            (not null, null) => -1,
            var (mine, theirs) => string.CompareOrdinal(mine, theirs)
        };
    }
}
