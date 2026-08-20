#Requires -Version 7.0
<#
.SYNOPSIS
    Sets the product version in Directory.Build.props.

.DESCRIPTION
    Directory.Build.props is the single source of truth for the product version, and this script is
    its only writer. The release workflow calls it twice: once with an empty suffix to produce the
    released commit, and once with 'dev' to reopen development on the next version.

    The file is rewritten through ReadAllText and WriteAllText rather than Get-Content and
    Set-Content, because the repository has no .gitattributes while the working tree uses CRLF and
    the index uses LF. A round trip through the line-based cmdlets would rewrite every line and turn
    a two-line change into a whole-file commit, which would also defeat the release workflow's guard
    that nothing but those two lines changed.

.PARAMETER Version
    The three-part numeric version, for example 1.2.0.

.PARAMETER Suffix
    The prerelease suffix, or an empty string for a release build. Defaults to 'dev'.

.EXAMPLE
    pwsh build/Set-ProductVersion.ps1 -Version 1.2.0 -Suffix ''
    Produces the version a release carries.

.EXAMPLE
    pwsh build/Set-ProductVersion.ps1 -Version 1.2.1
    Reopens development, so every build reports 1.2.1-dev.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$', ErrorMessage = "'{0}' is not a three-part numeric version, for example 1.2.0.")]
    [string]$Version,

    [AllowEmptyString()]
    [ValidatePattern('^([0-9A-Za-z][0-9A-Za-z.-]*)?$', ErrorMessage = "'{0}' is not a usable prerelease suffix.")]
    [string]$Suffix = 'dev',

    [string]$PropsPath = (Join-Path $PSScriptRoot '..\Directory.Build.props')
)

$ErrorActionPreference = 'Stop'

# A leading zero would make version ordering surprising, and the release workflow orders versions.
foreach ($part in $Version.Split('.')) {
    if ($part.Length -gt 1 -and $part.StartsWith('0')) {
        throw "The version part '$part' in '$Version' has a leading zero. Write it without one."
    }
}

$path = [IO.Path]::GetFullPath($PropsPath)
if (-not (Test-Path -LiteralPath $path)) {
    throw "The properties file was not found: $path"
}

# Lookarounds keep the element tags out of the match, so the replacement cannot damage them, and
# [^<] cannot run past the closing tag into the rest of the file.
$prefixPattern = '(?<=<VersionPrefix>)[^<]*(?=</VersionPrefix>)'
$suffixPattern = '(?<=<VersionSuffix>)[^<]*(?=</VersionSuffix>)'

$text = [IO.File]::ReadAllText($path)

foreach ($pattern in @($prefixPattern, $suffixPattern)) {
    $found = [regex]::Matches($text, $pattern).Count
    if ($found -ne 1) {
        throw "Expected exactly one match for '$pattern' in $path but found $found."
    }
}

$updated = [regex]::Replace($text, $prefixPattern, $Version)
$updated = [regex]::Replace($updated, $suffixPattern, $Suffix)

# UTF8Encoding($false) writes no byte order mark. The file has none, and adding one would show up
# as a change to its first line.
[IO.File]::WriteAllText($path, $updated, (New-Object Text.UTF8Encoding $false))

# Read the file back rather than trusting the replacement, because it decides what a release is.
$verify = [IO.File]::ReadAllText($path)
$writtenPrefix = [regex]::Match($verify, $prefixPattern).Value
$writtenSuffix = [regex]::Match($verify, $suffixPattern).Value
if ($writtenPrefix -ne $Version -or $writtenSuffix -ne $Suffix) {
    throw "The rewrite did not take effect: $path now holds prefix '$writtenPrefix' and suffix '$writtenSuffix'."
}

$display = if ($Suffix) { "$Version-$Suffix" } else { $Version }
Write-Host "Product version set to $display in $path"
