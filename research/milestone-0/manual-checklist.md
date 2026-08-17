# Milestone 0 Manual Acceptance Checklist

Run these checks from an elevated PowerShell session on the intended backup PC.

1. Copy `configuration.example.json` to the ignored `configuration.json` and replace every placeholder with a disposable test location.
2. Confirm the NAS root is dedicated to this probe. The harness creates only `.folder-backuper-m0-<run-id>` beneath it.
3. Configure `deniedControlPath` only when it is safe to attempt one expected-denied file creation there.
4. Run `dotnet test FolderBackuper.Milestone0.slnx` from this directory.
5. Run `scripts\Invoke-Milestone0.ps1 -Probe Local -ConfigurationPath .\configuration.json` elevated. Confirm symbolic-link and junction results pass.
6. Run `scripts\Invoke-Milestone0.ps1 -Probe Nas -ConfigurationPath .\configuration.json`. Enter the NAS password at the hidden prompt.
7. Use `NasFallback` only if network-only impersonation fails after availability, path, and permissions have been ruled out.
8. Set `$secretPath = Join-Path $PWD 'secrets\nas-password.bin'`, then create a persisted service credential with `dotnet run --project src\FolderBackuper.Milestone0 -- protect-secret --output $secretPath`.
9. Install the temporary service with `scripts\Install-ProbeService.ps1 -ConfigurationPath .\configuration.json -ProtectedSecretPath $secretPath`.
10. Reboot the PC, leave it at the Windows sign-in screen long enough for the automatic service probe to complete, then log in and open the loopback URL reported by the install script.
11. Confirm MudBlazor styles load, the interactive counter increments, and no listener is reachable through a non-loopback interface.
12. Confirm the post-reboot service report records `LocalSystem`, DPAPI recovery, representative source reads, NAS lifecycle operations, and exact cleanup without an interactive login.
13. Restart the service once more and confirm it decrypts the same protected secret and produces another report.
14. Remove the service with `scripts\Remove-ProbeService.ps1`.
15. Delete the ignored protected-secret directory with `Remove-Item -LiteralPath .\secrets -Recurse -Force`, then inspect the NAS for any reported leftover generated directory.
16. Copy `results\compatibility-report-template.md` to a dated, redacted report and record the final SMB, identity, and marker decisions.

An `Inconclusive` result does not satisfy the milestone. Resolve the fixture or privilege issue and rerun it.
