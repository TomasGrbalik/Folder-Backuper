# Milestone 10: Installer And Release Hardening

Email notifications are not part of this release. Milestone 9 was skipped, so no notification behavior is verified here.

## Automated checks

- Publishing is deterministic and self-contained for `win-x64`, with trimming, single-file, and ahead-of-time compilation disabled, and the built executable carries a usable file version.
- The installer script compiles into a versioned `setup.exe` whose version is read from the built executable rather than declared separately.
- The installer script and `WindowsServiceMetadata` agree on the service name, display name, event-log source, registry key, port value name, and default port.
- The installer script maps every maintenance exit code the application can return, carries a stable non-placeholder application identity, and registers the service as `LocalSystem` with delayed automatic start and recovery actions but no failure flag.
- The installer script keeps application data unless removal is explicitly requested, both interactively and silently.
- The machine configuration file round-trips the loopback port through an atomic replace and tolerates a missing or malformed file.
- The machine configuration file takes precedence over the application default, while environment and command-line values still take precedence over it.
- A data root written into the machine configuration file is rejected at startup rather than silently ignored.
- Port availability is judged the way Kestrel judges it, including a port held exclusively on all interfaces, and selection skips an occupied candidate and reports exhaustion.
- The maintenance command line accepts both port forms, rejects an out-of-range or non-numeric port, rejects unknown options, and leaves ordinary hosting arguments untouched.
- Startup failures classify a wrapped socket bind conflict, a database failure, a denied access-control change, and an explicitly categorized failure, with distinct event identifiers and a fallback to unclassified.
- The startup barrier releases the queue and the scheduler on success, releases them without work on failure, and propagates cancellation.
- A failed database migration prevents normal hosting, leaves a validated pre-migration backup, and exits with the service failure code.
- Every address Kestrel is configured to bind is loopback, and an address beyond loopback is reported.

## Manual checks

- Install on a clean Windows virtual machine and confirm the service is registered as `FolderBackuper` running as `LocalSystem` with delayed automatic start, the recovery actions reported by `sc.exe qfailure FolderBackuper`, and a start-menu shortcut that opens the web interface on the selected port.
- Occupy the default port before installing, choose a different one, and confirm the resulting installation serves the web interface there.
- Reboot with nobody logged on and confirm the service starts and a scheduled backup runs to completion.
- Power off the intended NAS, terminate the service mid-run so interrupted runs remain, then reboot and confirm the service reaches Running well inside the thirty-second service start window and completes recovery afterward.
- Upgrade with a bumped version and confirm the service stops, the application directory is replaced, pending migrations apply, the service restarts, and every job, destination, history record, and log survives.
- Re-run the installer with a changed port and confirm the hosting configuration, the registry value, the start-menu shortcut, and the running service all move together.
- Uninstall once keeping data and once removing it, then repeat silently with `/REMOVEDATA=1`, confirming that archives already written to a destination are never touched.
- Interrupt setup while files are being copied, and interrupt an upgrade between service stop and restart, confirming both leave a recoverable machine with intact application data.
- Force a migration failure against a disposable data root and confirm the service stops, the validated pre-migration backup remains, and event 1004 appears under source **Folder Backuper**.
- Occupy the configured port before the service starts and confirm event 1005 and the installer's port guidance, then confirm a failure before logging is configured leaves `logs\startup-failure.log`.
- Confirm every startup event identifier renders readable text in Event Viewer rather than a missing-description placeholder.
- Run the pre-release matrix in [the release checklist](release-checklist.md) against the installed build, including the intended NAS.
- Confirm no installer action changed permissions on any source folder, and that `C:\ProgramData\FolderBackuper` grants full control only to the local system account and local administrators with inheritance disabled.
- Observe CPU, memory, database growth, log rolling, and the staging directory over repeated runs including one large backup.
