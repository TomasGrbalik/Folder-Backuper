[CmdletBinding()]
param(
    [ValidateSet('Local', 'Nas', 'NasFallback')]
    [string]$Probe = 'Local',
    [string]$ConfigurationPath,
    [string]$ProtectedSecretPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\results\generated')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\FolderBackuper.Milestone0\FolderBackuper.Milestone0.csproj'

$command = if ($Probe -eq 'NasFallback') { 'nas-fallback' } else { $Probe.ToLowerInvariant() }
$arguments = @('run', '--project', $project, '--', $command, '--output', $OutputDirectory)
if ($ConfigurationPath) {
    $arguments += @('--config', (Resolve-Path -LiteralPath $ConfigurationPath).Path)
}

if ($ProtectedSecretPath) {
    $arguments += @('--secret', (Resolve-Path -LiteralPath $ProtectedSecretPath).Path)
}

if ($Probe -ne 'Local' -and -not $ConfigurationPath) {
    throw 'ConfigurationPath is required for NAS probes.'
}

& dotnet @arguments
exit $LASTEXITCODE
