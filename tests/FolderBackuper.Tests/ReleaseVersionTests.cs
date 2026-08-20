using FolderBackuper.Infrastructure.Versioning;

namespace FolderBackuper.Tests;

/// <summary>
/// Guards the one comparison the update notice depends on. Getting this wrong would either offer an
/// update that does not exist or hide one that does, and neither is visible in any other test.
/// </summary>
public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("0.9.12", 0, 9, 12, null)]
    [InlineData("1.2.3-dev", 1, 2, 3, "dev")]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1")]
    // Build metadata is provenance, not version, so it is discarded before anything is compared.
    [InlineData("1.0.0+abc123", 1, 0, 0, null)]
    [InlineData("1.2.3-dev+abc123", 1, 2, 3, "dev")]
    [InlineData("  1.2.3  ", 1, 2, 3, null)]
    [InlineData("10.20.30", 10, 20, 30, null)]
    public void TryParse_ReadsAVersion(string text, int major, int minor, int patch, string? preRelease)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    // A tag prefix is stripped where tags are read, so it must not be accepted here as well.
    [InlineData("v1.2.3")]
    // A Win32 file version must never be able to masquerade as a release version.
    [InlineData("1.2.3.0")]
    [InlineData("1.2")]
    [InlineData("1")]
    // A leading zero would make ordering surprising.
    [InlineData("1.02.0")]
    [InlineData("01.2.0")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-")]
    [InlineData("a.b.c")]
    [InlineData("release-1.2.3")]
    // Overflow is unparseable rather than an exception.
    [InlineData("99999999999.0.0")]
    public void TryParse_RejectsAnythingItCannotOrder(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    // Numeric, not lexical: this is the case a text comparison gets wrong.
    [InlineData("1.10.0", "1.2.0", true)]
    [InlineData("1.2.0", "1.10.0", false)]
    // A release supersedes its own pre-release, which is what makes the notice appear on a
    // development build once the version it was working towards is published.
    [InlineData("1.1.0", "1.1.0-dev", true)]
    [InlineData("1.1.0-dev", "1.1.0", false)]
    // The development build that follows a release must not be offered that release.
    [InlineData("1.1.0", "1.1.1-dev", false)]
    [InlineData("1.1.1-dev", "1.1.0", true)]
    [InlineData("1.1.0-dev", "1.1.0-dev", false)]
    // Ordinary text ordering between two pre-releases. Pinned so a change here fails a test rather
    // than surprising someone.
    [InlineData("1.1.0-dev", "1.1.0-alpha", true)]
    [InlineData("1.1.0-alpha", "1.1.0-dev", false)]
    public void IsNewerThan_OrdersTwoVersions(string candidate, string installed, bool expected)
    {
        Assert.True(ReleaseVersion.TryParse(candidate, out var newer));
        Assert.True(ReleaseVersion.TryParse(installed, out var older));
        Assert.Equal(expected, newer.IsNewerThan(older));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-dev", "1.2.3-dev")]
    [InlineData("1.2.3-dev+abcdef", "1.2.3-dev")]
    public void ToString_RoundTripsWithoutBuildMetadata(string text, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Fact]
    public void IsPreRelease_DistinguishesADevelopmentBuildFromARelease()
    {
        Assert.True(ReleaseVersion.TryParse("1.2.3-dev", out var development));
        Assert.True(ReleaseVersion.TryParse("1.2.3", out var release));

        Assert.True(development.IsPreRelease);
        Assert.False(release.IsPreRelease);
    }
}
