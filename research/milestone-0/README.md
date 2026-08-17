# Milestone 0 Diagnostic Harness

This retained, non-shipping .NET 10 harness validates the Windows, NAS, filesystem, DPAPI, and service-hosting assumptions that could change Folder Backuper's architecture. Milestone 1 will create the production application solution separately.

## Safety

- NAS lifecycle mutations are opt-in and confined to a unique `.folder-backuper-m0-<run-id>` directory beneath the configured root.
- When configured, the permission-boundary check attempts one uniquely named file directly beneath `deniedControlPath` and removes it if creation unexpectedly succeeds.
- Source probing opens files read-only, skips reparse-point traversal, and compares captured metadata and the root ACL.
- Passwords are never accepted as command-line values or written to reports.
- Persisted service credentials use machine-scope DPAPI in an ACL-restricted, gitignored directory.
- The deviceless SMB fallback is a separate command and is never selected silently.
- Generated reports and machine-specific configuration are ignored by Git until reviewed and redacted.

## Build And Test

From `research/milestone-0`:

```powershell
dotnet restore FolderBackuper.Milestone0.slnx
dotnet build FolderBackuper.Milestone0.slnx --no-restore
dotnet test FolderBackuper.Milestone0.slnx --no-build
```

Run safe local probes:

```powershell
.\scripts\Invoke-Milestone0.ps1 -Probe Local -ConfigurationPath .\configuration.json
```

Run the primary NAS probe:

```powershell
.\scripts\Invoke-Milestone0.ps1 -Probe Nas -ConfigurationPath .\configuration.json
```

The NAS password is read from a hidden interactive prompt. See `manual-checklist.md` for the `LocalSystem` service procedure and acceptance evidence.

## Result Meanings

- `Passed`: the required behavior was observed.
- `Failed`: a required behavior contradicted the design.
- `Skipped`: an optional or explicitly unconfigured fixture was not exercised.
- `Inconclusive`: the probe could not establish compatibility; it must be resolved before milestone sign-off.

Exit code `0` requires every executed probe to be `Passed` or `Skipped`. Reports are emitted as JSON and Markdown.
