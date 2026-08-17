# Milestone 3 Acceptance Checklist

Run these checks on Windows from an elevated PowerShell session. Use disposable local storage and a dedicated test share; the access test creates and removes one random root-level file.

## Automated Verification

```powershell
dotnet restore FolderBackuper.slnx
dotnet build FolderBackuper.slnx -c Release --no-restore
dotnet test FolderBackuper.slnx -c Release --no-build
```

The focused tests cover canonical local, UNC, and relative paths; overlap boundaries; final-path and filesystem identities; local-host UNC aliases; DPAPI round trips; exact local test-file cleanup; best-effort capacity; ownership-marker collision and owner-checked release; password-free DTOs; password replacement; verification invalidation; and persisted structured access results.

## Application Data ACL

1. Start the application with a new disposable data root.
2. Run `Get-Acl C:\Temp\FolderBackuper-M3 | Format-List` against that root.
3. Confirm inheritance is disabled and full control is limited to `SYSTEM`, local administrators, and the current identity used for console development.
4. Confirm the ACL applies to `config`, `data`, `staging`, `logs`, the database, and protected values created beneath the root.

## Local Destination

1. Open **Destinations**, select **Add destination**, and create a local destination using an existing writable directory.
2. Confirm the canonical path and best-effort available capacity appear. Unsupported capacity must display `Not reported` without preventing save.
3. Select **Test access** and confirm write, flush, byte-for-byte readback, and exact cleanup succeed.
4. Confirm no `.folder-backuper-access-*.tmp` file remains at the destination root.
5. Change the root and confirm a prior successful verification becomes `Unverified` until retested.

The form must reject relative paths, UNC paths entered as local storage, device paths, parent traversal, and mapped network drives.

## SMB Destination

Use a dedicated NAS account restricted to the test share.

1. Add the destination using a conventional `\\server\share` path, username, and password.
2. Confirm the list and edit form show the username and only indicate that a password exists; the password must never be returned or rendered.
3. Edit the name without entering a replacement password and confirm access still succeeds.
4. Enter a replacement password and confirm verification becomes `Unverified`.
5. Test valid credentials and confirm byte verification, cleanup, and capacity behavior.
6. Separately test a wrong password, a denied directory, an unreachable host, and a directory where test-file deletion is denied. Confirm the UI reports authentication failure, access denied, unavailable storage, and cleanup failure as distinct outcomes.
7. Review application logs and the `Destinations` table. Confirm logs contain no password and `ProtectedPassword` is not plaintext.

No mapped drive or persistent SMB connection is created. This milestone uses network-only impersonation only; it does not include the `WNetAddConnection2` fallback.

## Local-Host UNC Rejection

Attempt destination creation through each available form of the backup PC:

- `\\localhost\share`
- `\\127.0.0.1\share`
- `\\[::1]\share`
- Short machine name and fully qualified machine name
- Each active local interface address
- Any configured DNS alias resolving to a local interface

Each must be rejected with guidance to configure the underlying local filesystem path.

## Ownership Marker

The automated test proves exclusive claim, collision rejection, ownership verification, and owner-checked release with synthetic installation/job IDs. For NAS alias evidence, use a disposable folder reachable through two share or DFS aliases:

1. Claim `.folder-backuper-owner` through the first alias with synthetic job A.
2. Attempt an exclusive claim through the second alias with synthetic job B and confirm it is rejected.
3. Confirm job B cannot release the marker.
4. Confirm job A can verify and release it and no marker remains.

Ownership primitives are present for Milestone 4 job workflows. This milestone deliberately exposes no archive, restore, backup, or job-aware mutation actions.

## Environment Evidence

Record the Windows version, filesystem or NAS model/firmware, path form, identity API (`FileIdInfo` or fallback), capacity support, and outcomes for valid, wrong-credential, denied, unreachable, and cleanup-failure tests. The intended NAS must demonstrate stable distinct identities through the fallback when `FileIdInfo` fails with Windows errors 1, 50, or 87.
