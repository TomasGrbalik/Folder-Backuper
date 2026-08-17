[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigurationPath,
    [Parameter(Mandatory)]
    [string]$ProtectedSecretPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\results\generated'),
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish'),
    [string]$ServiceName = 'FolderBackuper-Milestone0'
)

$ErrorActionPreference = 'Stop'

function Wait-ForServiceStatus {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction Stop
        if ($service.Status -eq $Status) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Service '$Name' did not reach status '$Status' within $TimeoutSeconds seconds."
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Remove it before reinstalling."
}

$configuration = (Resolve-Path -LiteralPath $ConfigurationPath).Path
$secret = (Resolve-Path -LiteralPath $ProtectedSecretPath).Path
$secretArgument = " --secret `"$secret`""

$project = Join-Path $PSScriptRoot '..\src\FolderBackuper.Milestone0\FolderBackuper.Milestone0.csproj'
dotnet publish $project --configuration Release --runtime win-x64 --self-contained false --output $PublishDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Probe publication failed.'
}

$executable = Join-Path (Resolve-Path -LiteralPath $PublishDirectory).Path 'FolderBackuper.Milestone0.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$binaryPath = "`"$executable`" service --config `"$configuration`" --output `"$output`"$secretArgument"

$created = $false
try {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'Folder Backuper Milestone 0 Probe' -StartupType Automatic | Out-Null
    $created = $true
    Start-Service -Name $ServiceName
    Wait-ForServiceStatus -Name $ServiceName -Status Running

    $config = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json
    $url = "http://127.0.0.1:$($config.webPort)/"
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
    if ($response.StatusCode -ne 200) {
        throw "Probe UI readiness failed with HTTP $($response.StatusCode)."
    }

    "Service '$ServiceName' is running as LocalSystem. Open $url to verify MudBlazor rendering and the interactive button."
}
catch {
    if ($created) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    throw
}
