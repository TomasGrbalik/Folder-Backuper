# Release Checklist

Work through this list in order for every released `setup.exe`. Record the result of each manual section; the clean-VM lifecycle record is a required deliverable.

## Scope of this release

Email notifications are implemented through Resend over HTTPS: a notification settings page, a test email, provider-neutral templates, and the durable single-attempt outbox worker. Sending requires a Resend API key and a verified sending domain; when notifications are not configured, backup outcomes remain visible in the web interface and every run simply reports Not sent.

Before release, confirm that the Resend account used for verification is a test account and that no production key is left in the shipped build or in any captured screenshot.

Versioning and releasing are automated, and the web interface now reports its own version and whether a newer one has been published. Section 1 replaces the former hand-edited version bump. The release check is the first unsolicited outbound request the product makes, so section 7 covers what it does and does not send.

## 1. Source and version

The version is machine-owned. `Directory.Build.props` is written only by `build/Set-ProductVersion.ps1`, which the **Release** workflow calls; do not edit it by hand, or the next release will conflict with the edit.

- Everything intended for the release is merged into `main`, and `main` is green.
- `global.json` still pins the intended SDK, and `Directory.Packages.props` still pins every package version.
- `docs/technical-design.md`, `docs/implementation-plan.md`, and the milestone acceptance documents match the shipped behavior.
- Decide the version. The workflow refuses anything that is not three numeric parts with an optional pre-release suffix, that already has a tag, or that does not come after the highest released version.

Run section 2 locally first. It is the same build the workflow performs, and finding a failure here costs nothing, whereas finding it in the workflow after the push has happened does.

Then dispatch the release from `main`:

```powershell
gh workflow run Release -f version=1.2.0
```

Pass `-f next_version=1.3.0` when the next cycle is not the patch bump the workflow would choose. A pre-release version, for example `1.2.0-rc.1`, is published as a GitHub pre-release and is never offered to installed instances by the update check.

The workflow rewrites the version, commits `Release v<version>`, tags it, builds and tests that commit, commits the next `-dev` version, pushes both commits and the tag atomically, and publishes the release with `setup.exe` attached and generated notes. Nothing reaches the repository until every step that can fail has succeeded. Its run summary records the version, the commit, and the artifact name, which is what the last item of section 8 asks for.

## 2. Build

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
pwsh installer/Build-Installer.ps1
```

- The build produces no warnings.
- Every test passes.
- `artifacts/installer/FolderBackuper-<version>-setup.exe` exists. A local build is named for the development version, for example `FolderBackuper-1.2.1-dev-setup.exe`; only the workflow produces the plain release name.
- The published executable reports the expected version: `(Get-Item artifacts/publish/FolderBackuper.exe).VersionInfo` shows a numeric `FileVersion` and a `ProductVersion` carrying the suffix and the commit hash.
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

Run against the installed build, not a development host. Source: `docs/implementation-plan.md` section 16.

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
- Notification success, rejection, timeout, and uncertain crash boundary. See the [Milestone 9 acceptance checklist](milestone-9-acceptance.md).
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
- The release check sends nothing identifying. Capture the outbound request and confirm it carries no installation identifier, no configuration, no version, and no credential, and that its user agent names only the product. See the [Milestone 11 acceptance checklist](milestone-11-acceptance.md).
- Switching the release check off stops it making any request at all.

## 8. Signing and distribution

Certificate acquisition is outside the application architecture; this section records what a signed release requires.

- Sign the published `FolderBackuper.exe` before packaging.
- Build the installer with a sign tool so that both `setup.exe` and the generated uninstaller are signed:

  ```powershell
  pwsh installer/Build-Installer.ps1 -SignToolCommand 'signtool.exe sign /fd sha256 /tr <timestamp-url> /td sha256 /a $f'
  ```

- Verify the signature and timestamp on `setup.exe`, and confirm the publisher name shown by the elevation prompt.
- **Artifacts published by the Release workflow are unsigned**, because no certificate is configured for continuous integration. Windows SmartScreen will warn about them, and the update notice now points people at them, so this is the most visible remaining gap in the release process. Signing a release means either building it locally with `-SignToolCommand` and attaching that artifact by hand, or adding a certificate to the workflow.
- Continuous integration builds `setup.exe` on every push to `main` and keeps it as a workflow artifact. Publishing a release is a separate, deliberate act: the **Release** workflow, dispatched by hand. See section 1.
- Record the clean-VM lifecycle result alongside the artifact. The release version and commit hash are in the workflow run summary.
