# Folder Backuper

Folder Backuper is a Windows service and localhost web application for creating scheduled ZIP backups of local folders to local or SMB storage.

The production foundation is a .NET 10 Blazor Interactive Server application with MudBlazor and durable EF Core SQLite persistence. Destination and job management support canonical local and SMB roots, machine-scoped DPAPI credentials, ownership-safe effective folders, read-only source browsing and preview, lifecycle controls, and deterministic local-time scheduling. The backup pipeline performs execution preflight, immutable source scanning, validated ZIP creation, local or impersonated SMB transfer, collision-safe finalization, cleanup, retention, cancellation, and crash recovery. A hosted scheduler adds durable due-time ordering, missed-run catch-up, duplicate prevention, and manual-run coalescing. A monitoring interface adds a health dashboard with live active-run progress, per-job status and managed storage, a permanent run history with structured run and problem details, and a month calendar and agenda covering past and planned runs. An Inno Setup package installs the service, selects and persists the loopback port, verifies readiness, and handles upgrade, port reconfiguration, and uninstall while preserving application data by default. The retained [Milestone 0 diagnostic harness](research/milestone-0/README.md) records the Windows and NAS compatibility evidence and is not part of the shipping application.

## Requirements

- Windows x64
- [.NET 10 SDK](https://dotnet.microsoft.com/) selected by `global.json`
- No Node.js tooling

## Install

Run `FolderBackuper-<version>-setup.exe`. It installs the self-contained application under `C:\Program Files\FolderBackuper`, creates and secures `C:\ProgramData\FolderBackuper`, writes the loopback port you choose, registers the `FolderBackuper` Windows service as `LocalSystem` with delayed automatic start and recovery actions, waits for the web interface to report ready, and adds a start-menu shortcut. Re-running the installer upgrades an existing installation, preserving all application data, and is also how the port is changed. Uninstallation asks before deleting application data and keeps it by default.

See the [installation and first-run guide](docs/installation.md) for the full lifecycle, including how to diagnose a service that does not start.

## Build And Test

From the repository root:

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
```

## Development Run

Use a disposable data root instead of the machine-wide production location:

```powershell
dotnet run --project src/FolderBackuper/FolderBackuper.csproj -- --FolderBackuper:DataRoot=C:\Temp\FolderBackuper-Dev --FolderBackuper:Port=5180
```

Open `http://localhost:5180`. Kestrel always binds to IPv4 and IPv6 loopback; configuration cannot enable remote binding, and the application stops if a non-loopback address is ever bound.

The same settings can be supplied through environment variables as `FolderBackuper__DataRoot` and `FolderBackuper__Port`. Without an override, application data is stored under `C:\ProgramData\FolderBackuper` in `config`, `data`, `staging`, and `logs` directories. The SQLite database is `data\folder-backuper.db`; validated pre-migration backups are retained in `data\migrations`. An installed machine keeps its port in `config\service.json`, which environment and command-line values still override. The data root itself is never read from that file.

Only one process can use a data root. A machine-wide mutex rejects a second service or console process even when it runs in another Windows session. Different development data roots can run independently.

## Publish And Package

```powershell
dotnet publish src/FolderBackuper/FolderBackuper.csproj -c Release -r win-x64 --self-contained true
pwsh installer/Build-Installer.ps1
```

`Build-Installer.ps1` publishes to `artifacts/publish` and then compiles `artifacts/installer/FolderBackuper-<version>-setup.exe` with [Inno Setup](https://jrsoftware.org/isinfo.php) 6.3 or newer. The installer version is read from the built executable, so `setup.exe` cannot drift from the binary it carries. Continuous integration builds the installer and keeps it as a workflow artifact; publishing a release is a separate step. The [release checklist](docs/release-checklist.md) covers signing and the pre-release matrix.

Execution uses the durable queue, cancellation, retention, and startup-recovery workflow described in the [Milestone 6 acceptance checklist](docs/milestone-6-acceptance.md). Unattended scheduling and catch-up follow the [Milestone 7 acceptance checklist](docs/milestone-7-acceptance.md), the operational monitoring and history interface follows the [Milestone 8 acceptance checklist](docs/milestone-8-acceptance.md), and installer and release behavior follows the [Milestone 10 acceptance checklist](docs/milestone-10-acceptance.md). Email notifications are not implemented; Milestone 9 was skipped and deferred.
