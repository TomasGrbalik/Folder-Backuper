# Installation And First Run

This guide covers installing, upgrading, reconfiguring, and removing Folder Backuper on a Windows machine.

## Requirements

- 64-bit Windows 10 version 1809 or newer, or Windows Server 2019 or newer.
- Local administrator rights to run the installer.
- No .NET installation. The application ships self-contained.

## Install

1. Run `FolderBackuper-<version>-setup.exe` and accept the elevation prompt.
2. Confirm the installation directory. The default is `C:\Program Files\FolderBackuper`.
3. Choose the loopback port for the web interface. The default is `5180`. Any value between 1024 and 65535 is accepted; pick a different one if another program already uses the default.
4. Finish the wizard. The installer writes the hosting configuration, registers the `FolderBackuper` service, starts it, and waits until the web interface reports ready.
5. Leave **Open the Folder Backuper web interface** selected, or open **Folder Backuper** from the start menu afterwards.

The web interface is reachable only from this computer, at `http://localhost:<port>`. It is never exposed to the network; the application refuses to serve a non-loopback address.

### What the installer creates

| Location | Contents |
| --- | --- |
| `C:\Program Files\FolderBackuper` | The application. Replaced entirely on upgrade. |
| `C:\ProgramData\FolderBackuper\config` | `service.json`, the loopback port. |
| `C:\ProgramData\FolderBackuper\data` | The SQLite database and validated pre-migration backups. |
| `C:\ProgramData\FolderBackuper\staging` | Temporary archives during a backup run. |
| `C:\ProgramData\FolderBackuper\logs` | Daily rolling log files, retained for thirty days. |
| Start menu, **Folder Backuper** | A shortcut to the web interface and a shortcut to the log folder. |

`C:\ProgramData\FolderBackuper` is restricted to the local system account and local administrators. Inheritance is disabled so that other local accounts receive no access.

The installer never changes permissions on the folders you back up, and never creates mapped drives, destination credentials, firewall rules, or certificates.

### The service

The service is registered as `FolderBackuper`, displayed as **Folder Backuper**, and runs as `LocalSystem` with delayed automatic start. Backups therefore run with nobody logged on. Windows service recovery restarts it after an unexpected termination.

Inspect it with:

```powershell
sc.exe qc FolderBackuper
sc.exe qfailure FolderBackuper
Get-Service FolderBackuper
```

## First run

1. Open the web interface from the start menu.
2. Add a destination on the **Destinations** page and run its access test. A destination stays *Unverified* until it passes.
3. Add a job on the **Jobs** page, choosing a source folder, a destination, a weekly schedule, and how many archives to retain.
4. Use **Run now** to confirm the whole path works before relying on the schedule.
5. Watch progress on the dashboard, and review outcomes in the run history.

Email notifications are not part of this release. Backup outcomes are visible only in the web interface.

## Change the port

Run the same `setup.exe` again. The port page is pre-filled with the current value; enter a new one and finish the wizard. The installer rewrites the hosting configuration, updates the start-menu shortcut, and restarts the service on the new port.

This is also the recovery path when the web interface will not start because another program has taken the port.

## Upgrade

Run a newer `setup.exe`. It stops the service, replaces the application directory, and restarts the service, which applies any pending database migration on startup. Jobs, destinations, run history, logs, and archives are preserved.

Before applying a pending migration the application takes a validated backup of the database into `C:\ProgramData\FolderBackuper\data\migrations` and keeps the three most recent. If a migration fails, the service stops rather than running against unknown state, and that backup is your recovery point.

There is no automatic update check. Upgrades happen only when you run a newer installer.

## Uninstall

Uninstall from **Settings, Apps** or from the start-menu entry. The uninstaller stops and removes the service and deletes the application directory, then asks whether to delete `C:\ProgramData\FolderBackuper`.

- **No**, the default, keeps every job, destination, history record, and log for a future installation.
- **Yes** permanently deletes them.

Archives already written to a backup destination are never touched by uninstallation.

For an unattended removal that also deletes the data, pass `/REMOVEDATA=1`:

```powershell
& "C:\Program Files\FolderBackuper\unins000.exe" /VERYSILENT /REMOVEDATA=1
```

Without that switch, a silent uninstall keeps the data.

## When the service does not start

The installer reports a failed start and names where to look. Two places record the reason:

- `C:\ProgramData\FolderBackuper\logs` — the daily log file, plus `startup-failure.log` for failures that happen before logging is fully configured.
- **Event Viewer**, **Windows Logs**, **Application**, source **Folder Backuper**.

| Event ID | Meaning | What to do |
| --- | --- | --- |
| 1001 | The application data root is invalid or could not be created. | Confirm `C:\ProgramData` is writable and not redirected. |
| 1002 | Access controls could not be applied. | Confirm the service account is `LocalSystem`. |
| 1003 | Another process is already using this data root. | Stop the other Folder Backuper process or service instance. |
| 1004 | The database could not be opened or migrated. | Use the validated backup in `data\migrations`. |
| 1005 | The configured port is already in use. | Run setup again and choose a different port. |
| 1006 | A non-loopback address was bound. | Remove any `ASPNETCORE_URLS` override from the service environment. |
| 1099 | An unclassified startup failure. | Read the log file for the full exception. |

Service recovery actions restart the service after an unexpected termination, but Windows does not apply them to start failures. A service that fails to start stays stopped until the cause is fixed, which is why these diagnostics are the intended path.

## Build the installer yourself

From the repository root, with [Inno Setup 6.3 or newer](https://jrsoftware.org/isinfo.php) installed:

```powershell
pwsh installer/Build-Installer.ps1
```

The script publishes a self-contained `win-x64` build to `artifacts/publish`, then compiles `artifacts/installer/FolderBackuper-<version>-setup.exe`. The version comes from the built executable, so the installer and the binary it carries can never disagree.
