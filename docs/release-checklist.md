# Release Checklist

Work through this list in order for every released `setup.exe`. Record the result of each manual section; the clean-VM lifecycle record is a required deliverable.

## Scope of this release

Email notifications are not implemented. Milestone 9 was skipped, so no notification provider, settings page, template, or outbox exists. Backup outcomes are visible only in the web interface, and every notification row in the pre-release matrix below is marked not applicable.

## 1. Source and version

- The working tree is clean and the release commit is on `main`.
- `VersionPrefix` in `Directory.Build.props` is bumped and matches the intended release.
- `global.json` still pins the intended SDK, and `Directory.Packages.props` still pins every package version.
- `docs/technical-design.md`, `docs/implementation-plan.md`, and the milestone acceptance documents match the shipped behavior.

## 2. Build

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
pwsh installer/Build-Installer.ps1
```

- The build produces no warnings.
- Every test passes.
- `artifacts/installer/FolderBackuper-<version>-setup.exe` exists and its version matches `Directory.Build.props`.
- The published application directory contains no `web.config`, no satellite resource directories for languages other than English, and `FolderBackuper.pdb`.
- `FolderBackuper.exe` and `setup.exe` both carry the application icon.

## 3. Clean Windows VM lifecycle

Use a snapshot of a clean 64-bit Windows installation with no prior Folder Backuper install. Revert to the snapshot between the fresh-install and upgrade passes.

- Fresh install completes, the service is registered as `FolderBackuper` running as `LocalSystem` with delayed automatic start, and `sc.exe qfailure FolderBackuper` shows the configured restart actions.
- The start-menu shortcut opens the web interface on the selected port.
- Occupying the default port before installing and choosing a different one produces a working installation on that port.
- The service starts after a reboot with nobody logged on, and a scheduled backup runs.
- Upgrading with a bumped version preserves every job, destination, history record, and log, applies pending migrations, and restarts the service.
- Re-running the installer with a changed port moves the configuration, the registry value, the shortcut, and the running service.
- Uninstalling with the default answer keeps `C:\ProgramData\FolderBackuper`; uninstalling and choosing removal deletes it; `/VERYSILENT /REMOVEDATA=1` deletes it without prompting.
- Interrupting setup while files are being copied, and interrupting an upgrade between service stop and restart, both leave a recoverable machine with intact application data.
- No installer action changed permissions on any source folder.

## 4. Startup diagnostics

- A migration failure stops the service, leaves a validated pre-migration backup in `data\migrations`, and records event 1004.
- Occupying the configured port before the service starts records event 1005 and produces the installer's port guidance.
- A failure before logging is configured leaves `logs\startup-failure.log`.
- Every event identifier renders readable text in Event Viewer under source **Folder Backuper**.
- The service reaches Running well inside the thirty-second service start window with an unreachable destination and interrupted runs pending. Power off the NAS, terminate the service mid-run, then reboot.

## 5. Pre-release matrix

Run against the installed build, not a development host. Source: `docs/implementation-plan.md` section 15.

- Local and SMB destinations.
- Correct and incorrect SMB credentials.
- Destination disconnect before and during transfer.
- Insufficient local staging space and insufficient local and SMB destination space.
- Locked, inaccessible, added, removed, and changed source files.
- Hidden and system files.
- Reparse points and physical path aliases.
- Empty directories and long paths.
- Cancellation in each pre-commit phase.
- Process termination around every durable filesystem intent.
- Retention success, ownership mismatch, deletion denial, and missing artifacts.
- Daylight-saving and system-clock changes.
- Browser disconnect and service restart.
- Notification success, rejection, timeout, and uncertain crash boundary. **Not applicable**; see Milestone 9.
- Install, upgrade, repair, and uninstall.

## 6. Resource behavior

Observe an installed build over repeated runs, including at least one large backup.

- CPU and memory return to idle levels after a run completes.
- The database does not grow unbounded, and WAL files are checkpointed.
- Log files roll daily and at the size limit, older files are removed after thirty days, and the directory stays within its documented cap.
- The startup-failure log rolls into a single previous copy rather than growing.
- The staging directory is empty between runs, and interrupted runs leave nothing behind after startup recovery.

## 7. Security and privacy review

- No credential, protected value, or secret appears in the repository, log files, run records, rendered edit models, test output, or installer script.
- `C:\ProgramData\FolderBackuper` has inheritance disabled and grants full control only to the local system account and local administrators.
- The application binds only `127.0.0.1` and `::1`, and refuses to start when a non-loopback address is bound.
- No source folder is written to, and no source permission is changed, by the application or the installer.
- The readiness endpoint exposes no application data.

## 8. Signing and distribution

Certificate acquisition is outside the application architecture; this section records what a signed release requires.

- Sign the published `FolderBackuper.exe` before packaging.
- Build the installer with a sign tool so that both `setup.exe` and the generated uninstaller are signed:

  ```powershell
  pwsh installer/Build-Installer.ps1 -SignToolCommand 'signtool.exe sign /fd sha256 /tr <timestamp-url> /td sha256 /a $f'
  ```

- Verify the signature and timestamp on `setup.exe`, and confirm the publisher name shown by the elevation prompt.
- Continuous integration builds `setup.exe` and keeps it as a workflow artifact. Publishing a GitHub release is a separate, deliberate step and is not automated.
- Record the release version, commit hash, and the clean-VM lifecycle result alongside the artifact.
