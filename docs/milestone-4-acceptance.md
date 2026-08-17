# Milestone 4 Acceptance Checklist

Run these checks on Windows with disposable source and destination folders. The job-folder test creates a subfolder, an ownership marker, and one random probe file; the probe is removed exactly, while the ownership marker remains until the job is archived or moved.

## Automated Verification

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
dotnet publish src/FolderBackuper/FolderBackuper.csproj -c Release -r win-x64 --self-contained true
```

The automated suite covers schedule value validation; daylight-saving gaps and repeated times; forward, backward, and time-zone changes; schedule revisions and effective boundaries; lifecycle transitions; configuration-gate serialization; SQLite collisions; physical-path aliases; junction escape rejection; marker claim, verification, release, cancellation, and compensation; destination reference rules; bounded source paging; progressive preview; hidden/system entries; reparse skipping; and source metadata preservation.

## Development Setup

1. Create disposable folders such as `C:\Temp\FolderBackuper-M4\Source` and `C:\Temp\FolderBackuper-M4\Destination`.
2. Place ordinary, hidden, and nested files in the source. Add a directory junction only if you want to exercise reparse reporting manually.
3. Start the application with a disposable data root:

```powershell
dotnet run --project src/FolderBackuper/FolderBackuper.csproj -- --FolderBackuper:DataRoot=C:\Temp\FolderBackuper-M4\AppData --FolderBackuper:Port=5180
```

4. Open `http://localhost:5180`.

## Destination And Job Setup

1. Open **Destinations**, add the disposable destination root, and select **Test access**.
2. Confirm the destination reports `Succeeded` and no `.folder-backuper-access-*.tmp` file remains.
3. Open **Jobs** and select **New job**.
4. Enter a unique name, browse to the source, select the verified destination, enter a relative subfolder, choose weekdays and a local time, and set retention to at least one.
5. Confirm the form shows the current Windows time zone and a future next-run preview.
6. Select **Preview source**. Confirm counts and estimated bytes progress to completion, hidden/system content is counted, inaccessible content is reported, and reparse points are counted but not traversed.
7. Save the job paused. Confirm the destination subfolder and `.folder-backuper-owner` marker exist and no `.folder-backuper-job-test-*.tmp` file remains.
8. Edit the job and select **Test effective folder**. Confirm ownership, write, readback, and cleanup succeed.
9. Reactivate the job. An unverified destination must prevent reactivation.

No **Run now** action is expected in this milestone; job lifecycle actions must not insert backup runs.

## Lifecycle And Reservation

1. Pause and reactivate the job. Confirm both actions preserve its configuration and reactivation shows only a future next run.
2. Attempt to create another active or paused job using the same effective destination folder. Confirm it is rejected as reserved.
3. Archive the first job. Confirm it moves to **Show archived**, history/configuration remains available, and only its verified ownership marker is removed.
4. Restore the job. Confirm it becomes active when its destination and configuration remain valid; otherwise it returns paused with a clear explanation.
5. Confirm restoring or reactivating never presents a missed run from the paused or archived period.

## Destination Mutations

1. Attempt to archive a destination referenced by an active or paused job. Confirm the operation is rejected with the reference count.
2. Change destination credentials or access configuration. Confirm successful verification is invalidated and active referencing jobs become paused.
3. Retest the destination, then explicitly reactivate affected jobs.
4. Change the destination root. Confirm a warning explains that jobs will pause and retained artifacts at old paths become unmanaged.
5. Cancel the warning and confirm nothing changes.
6. Repeat and confirm. Verify the new effective folders are reserved, old owned markers are released, active jobs are paused, and the destination must be tested again.
7. Archive an unreferenced destination, show archived destinations, restore it, and confirm it remains unverified until tested.

## Source Read-Only Check

Capture source data and metadata before browsing and previewing:

```powershell
$source = 'C:\Temp\FolderBackuper-M4\Source'
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
```

Browse every level and run a complete preview, then capture the same projection as `$after` and compare:

```powershell
$after = Get-SourceSnapshot | ConvertTo-Json -Depth 4
Compare-Object $before $after
```

The comparison must produce no output. Last-access timestamps are intentionally excluded because Windows and storage devices may update them on reads.

## Physical Alias Safety

1. Use a disposable local junction whose resolved target is inside the source.
2. Attempt to configure that junction, or a descendant through it, as an effective destination.
3. Confirm the operation is rejected before any destination subfolder, marker, or probe is written into the source.
4. If two SMB or DFS aliases reach the same disposable NAS folder, create a job through the first alias and attempt another job through the second.
5. Confirm the second job cannot claim the folder and cannot release the first job's marker.

## Environment Evidence

Record the Windows version, local filesystem, NAS model/firmware where applicable, path aliases tested, identity API result, source item counts, preview duration, and outcomes for marker collision, denied access, unavailable storage, cancellation, and cleanup. Review application logs and rendered pages to confirm no password or protected secret appears.
