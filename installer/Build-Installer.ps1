#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Folder Backuper and packages it into setup.exe with Inno Setup.

.DESCRIPTION
    The application is published first because the installer script reads the product version from
    the Win32 version resource of the built executable at compile time. That keeps setup.exe and the
    binary it carries on the same version by construction.

.PARAMETER ExpectedVersion
    The version the built executable must report, for example 1.2.0. The release workflow supplies
    it so that a version which was not applied before publishing fails here, where the cause is
    obvious, instead of producing a plausibly named installer carrying the wrong build.

.PARAMETER SignToolCommand
    An Inno Setup sign-tool command line, for example:
        signtool.exe sign /fd sha256 /tr http://timestamp.example/rfc3161 /td sha256 /a $f
    When omitted the installer is built unsigned. Signing a released artifact is a release-checklist
    step; see docs/release-checklist.md.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [string]$SignToolCommand,
    [string]$ExpectedVersion,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

function Resolve-InnoSetupCompiler {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    # Inno Setup can also be installed per user, for example through winget.
    $uninstallKeys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $uninstallKeys) {
        $location = (Get-ItemProperty -Path $key -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
        if ($location) {
            $candidates += (Join-Path $location 'ISCC.exe')
        }
    }

    $found = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $found) {
        throw 'ISCC.exe was not found. Install Inno Setup 6.3 or newer, for example: winget install JRSoftware.InnoSetup'
    }

    return $found
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$publishDirectory = Join-Path $artifacts 'publish'
$installerDirectory = Join-Path $artifacts 'installer'
$project = Join-Path $repositoryRoot 'src\FolderBackuper\FolderBackuper.csproj'
$script = Join-Path $PSScriptRoot 'FolderBackuper.iss'

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    Write-Host "Publishing $project to $publishDirectory"
    dotnet publish $project `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

$executable = Join-Path $publishDirectory 'FolderBackuper.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "The publish output does not contain FolderBackuper.exe: $publishDirectory"
}

$version = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
if (-not $version -or $version -eq '0.0.0.0') {
    throw "FolderBackuper.exe carries no usable FileVersion. Inno Setup reads the version from this resource."
}
# A numeric version cannot express a prerelease suffix, so the display label comes from the Win32
# ProductVersion field, which carries InformationalVersion. Everything from the '+' onwards is build
# provenance, namely the commit hash, and does not belong in a file name.
$productVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
$versionLabel = if ($productVersion) { ($productVersion -split '\+', 2)[0].Trim() } else { '' }
if (-not $versionLabel) {
    # A build without source-control metadata is still a legitimate build, so fall back to the
    # numeric version rather than refusing to package.
    $versionLabel = ($version -split '\.')[0..2] -join '.'
}
if ($versionLabel -notmatch '^[0-9A-Za-z][0-9A-Za-z.\-]*$') {
    throw "The version label '$versionLabel' contains characters that cannot appear in a file name."
}
if ($ExpectedVersion -and $versionLabel -ne $ExpectedVersion) {
    throw "The built executable reports version '$versionLabel' but '$ExpectedVersion' was expected. The intended version was not applied before publishing."
}

Write-Host "Publish version: $version (label $versionLabel)"

New-Item -ItemType Directory -Force -Path $installerDirectory | Out-Null

$compiler = Resolve-InnoSetupCompiler
Write-Host "Compiling with $compiler"

$arguments = @(
    "/DPublishDir=$publishDirectory"
    "/DOutputDir=$installerDirectory"
    "/DVersionLabel=$versionLabel"
)
if ($SignToolCommand) {
    $arguments += '/DSIGN'
    $arguments += "/Sfbsign=$SignToolCommand"
}
$arguments += $script

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

# This directory is deliberately not cleaned, so earlier installers stay available for comparison.
# Matching the exact expected name is therefore safer than taking the most recently written file.
$expectedName = "FolderBackuper-$versionLabel-setup.exe"
$package = @(Get-ChildItem -LiteralPath $installerDirectory -Filter $expectedName)
if ($package.Count -ne 1) {
    throw "Expected exactly one $expectedName in $installerDirectory but found $($package.Count)."
}

Write-Host "Installer: $($package[0].FullName)"
