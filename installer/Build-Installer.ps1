#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Folder Backuper and packages it into setup.exe with Inno Setup.

.DESCRIPTION
    The application is published first because the installer script reads the product version from
    the Win32 version resource of the built executable at compile time. That keeps setup.exe and the
    binary it carries on the same version by construction.

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
Write-Host "Publish version: $version"

New-Item -ItemType Directory -Force -Path $installerDirectory | Out-Null

$compiler = Resolve-InnoSetupCompiler
Write-Host "Compiling with $compiler"

$arguments = @(
    "/DPublishDir=$publishDirectory"
    "/DOutputDir=$installerDirectory"
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

$package = Get-ChildItem -LiteralPath $installerDirectory -Filter 'FolderBackuper-*-setup.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-Host "Installer: $($package.FullName)"
