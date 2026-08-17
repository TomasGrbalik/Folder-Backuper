# Folder Backuper

Folder Backuper is a Windows service and localhost web application for creating scheduled ZIP backups of local folders to local or SMB storage.

The production foundation is a .NET 10 Blazor Interactive Server application with MudBlazor. The retained [Milestone 0 diagnostic harness](research/milestone-0/README.md) records the Windows and NAS compatibility evidence and is not part of the shipping application.

## Requirements

- Windows x64
- [.NET 10 SDK](https://dotnet.microsoft.com/) selected by `global.json`
- No Node.js tooling

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

Open `http://localhost:5180`. Kestrel always binds to IPv4 and IPv6 loopback; configuration cannot enable remote binding.

The same settings can be supplied through environment variables as `FolderBackuper__DataRoot` and `FolderBackuper__Port`. Without an override, application data is stored under `C:\ProgramData\FolderBackuper` in `config`, `data`, `staging`, and `logs` directories.

Only one process can use a data root. A machine-wide mutex rejects a second service or console process even when it runs in another Windows session. Different development data roots can run independently.

## Publish

```powershell
dotnet publish src/FolderBackuper/FolderBackuper.csproj -c Release -r win-x64 --self-contained true
```

Installer packaging is scheduled for Milestone 10. See the [Milestone 1 acceptance checklist](docs/milestone-1-acceptance.md) for temporary service-validation commands.
