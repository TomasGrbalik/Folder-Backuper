# Milestone 5 Acceptance Checklist

Milestone 5 exposes a directly invoked backup engine for integration testing. It does not add **Run now**, scheduling, retention, durable cancellation, or crash recovery; those workflows begin in Milestone 6.

## Automated Verification

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
dotnet publish src/FolderBackuper/FolderBackuper.csproj -c Release -r win-x64 --self-contained true
```

The automated suite covers execution preflight; physical overlap against every configured source; current destination verification; ownership-marker verification; hidden and system files; empty directories; skipped reparse points; locked files; source additions, removals, and detectable changes; the exact archive ownership comment; safe ZIP paths and names; 15 MB content; 2,000-file archives; local transfer and validation; existing-file collisions; cancellation during compression and transfer; staging and partial cleanup; immutable progress; throughput calculation; and destination last-access persistence without changing explicit verification.

## Local Archive Inspection

1. Run `BackupEngineTests.Execute_CreatesValidatedArchiveCleansStagingAndRecordsAccess` under the debugger or from the test runner when an archive needs to be retained for inspection.
2. Confirm the final name follows `Job_yyyy-MM-dd_HH-mm-ss_runid.zip` and contains no locale-dependent text.
3. Open the ZIP in File Explorer and extract it with the standard Windows compressed-folder tools.
4. Confirm one top-level source folder contains every ordinary file and explicit empty directory.
5. Confirm hidden and system files are present and no reparse target content appears.
6. Confirm the application staging folder contains no completed or incomplete archive after the engine returns.
7. Confirm no `.partial` file remains in the effective destination folder.

## Source Read-Only Check

Capture source content and metadata before and after a directly invoked run:

```powershell
$source = 'C:\Temp\FolderBackuper-M5\Source'
function Get-SourceSnapshot {
    Get-ChildItem -LiteralPath $source -Force -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            FullName = $_.FullName
            Length = if ($_.PSIsContainer) { $null } else { $_.Length }
            Attributes = $_.Attributes.ToString()
            CreationTimeUtc = $_.CreationTimeUtc
            LastWriteTimeUtc = $_.LastWriteTimeUtc
            Acl = (Get-Acl -LiteralPath $_.FullName).Sddl
            Hash = if ($_.PSIsContainer) { $null } else { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    }
}
$before = Get-SourceSnapshot | ConvertTo-Json -Depth 4
# Invoke the engine against the disposable source.
$after = Get-SourceSnapshot | ConvertTo-Json -Depth 4
Compare-Object $before $after
```

The comparison must produce no output. Last-access timestamps are intentionally excluded because Windows and storage devices may update them on reads.

## Failure And Cancellation Matrix

Run each case against disposable storage and confirm no new final ZIP appears:

1. Lock an ordinary source file without read sharing.
2. Deny enumeration of an ordinary source directory.
3. Add, remove, resize, or change the timestamp of a source entry after the initial scan.
4. Cancel during compression.
5. Cancel during local or SMB transfer.
6. Cancel immediately before the commit coordinator permits rename.
7. Pre-create the exact intended final filename and confirm its bytes remain unchanged.
8. Disconnect the destination during transfer.
9. Fill or quota-limit the staging volume.
10. Fill or quota-limit the destination volume or share.

The result must distinguish source, staging, and destination failures where applicable. Exact application-owned staging and partial files must be removed when possible. A cleanup failure must remain a structured warning containing the exact owned path; unknown files must remain untouched.

## Intended NAS Validation

1. Use a disposable share and credentials independent of the logged-in Windows user.
2. Configure and explicitly verify the SMB destination through the application.
3. Create and claim a unique effective job folder.
4. Invoke `BackupEngine.ExecuteAsync` with the configured job and a disposable local source.
5. Confirm transfer, flush, ZIP reopen, entry validation, and rename all occur under scoped network-only impersonation.
6. Confirm all destination handles close before impersonation ends.
7. Repeat with wrong credentials, denied access, server unavailability, disconnect during transfer, a final-name collision, and partial cleanup denial.
8. If two aliases reach the same NAS folder, confirm preflight accepts only the job owning the existing marker.

Do not record the SMB password in commands, logs, screenshots, or evidence. Record the NAS model, firmware, SMB dialect where available, alias used, filesystem identity behavior, and native error categories observed.

## Performance Evidence

Prepare an idle representative dataset with approximately 10 GB, thousands of files, ordinary file sizes up to 15 MB, nested folders, hidden/system files, and empty directories. Run once to a local destination and once to the intended NAS.

Record:

- Windows version, CPU, memory, source filesystem, and staging filesystem.
- Source file count, directory count, and bytes.
- Scan, compression, transfer, destination validation, and total durations.
- Final ZIP size and peak working-set observation.
- Local copy or SMB upload throughput.
- Whether staging and destination partial cleanup completed.
- Whether the ZIP opened and extracted with standard Windows tools.

Measured evidence must be attached to the milestone review. No deployment-window target is implied until results from the intended hardware and network are accepted.
