# Milestone 1 Acceptance Checklist

Run these checks from an elevated PowerShell session on Windows. Use a disposable data root so validation never touches production data.

## Automated Verification

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
dotnet publish src/FolderBackuper/FolderBackuper.csproj -c Release -r win-x64 --self-contained true --no-build
```

## Console Hosting

1. Run `dotnet run --project src/FolderBackuper/FolderBackuper.csproj -- --FolderBackuper:DataRoot=C:\Temp\FolderBackuper-M1 --FolderBackuper:Port=5180`.
2. Open `http://localhost:5180` and confirm the application shell and MudBlazor styling load.
3. Run `Get-NetTCPConnection -State Listen -LocalPort 5180` and confirm listeners exist only on `127.0.0.1` and `::1`.
4. Inspect the response and confirm the CSP, content-type, frame, referrer, and permissions headers are present.
5. Confirm browser developer tools report no CDN or other external asset requests.

## Single Instance

1. Keep the console instance running.
2. Start a second process with the same data root and a different port. Confirm it exits with code 1 and reports that the data root is already in use.
3. Start another process with a different disposable data root and port. Confirm it starts independently.

## Windows Service

Publish before creating this temporary service. Replace `<publish-path>` with the absolute publish directory.

```powershell
sc.exe create FolderBackuper-M1 binPath= "<publish-path>\FolderBackuper.exe --FolderBackuper:DataRoot=C:\ProgramData\FolderBackuper-M1 --FolderBackuper:Port=5180" start= demand obj= LocalSystem
sc.exe start FolderBackuper-M1
```

1. Confirm the service reaches `Running` and the loopback UI loads.
2. From another Windows session, start an elevated console process with the same data root and confirm the global mutex rejects it.
3. Keep the browser open and restart the service. Confirm the reconnect presentation appears and offers reload after the service creates a new circuit.
4. Stop and remove the temporary service with `sc.exe stop FolderBackuper-M1` and `sc.exe delete FolderBackuper-M1`.
5. Remove only the disposable `C:\ProgramData\FolderBackuper-M1` test tree after reviewing its logs.
