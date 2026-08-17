# Milestone 0 NAS Compatibility Report

Status: Passed

## Environment

- Harness run: `9b010648-d0cb-496b-97ec-a270019e6b79`
- LocalSystem service run: `3deffdf2-5202-4d60-8e85-d60fceae82f8`
- Elevated local run: `34b38938-0490-41c1-b066-bb1e19df06be`
- Post-reboot LocalSystem run: `65362e9e-beb6-4462-bc11-9f4cc1bf7cb5`
- Execution date: 2026-08-17
- Backup PC: `TITAN`
- Windows build: `10.0.26200`
- .NET runtime: `10.0.11`
- NAS endpoint: `Andromeda` dedicated backup sandbox
- NAS vendor/model: Not recorded
- NAS firmware: Not recorded
- Alternate NAS aliases: None available for this run

Credentials, usernames, share names, protected blobs, and sensitive local paths are intentionally omitted.

## Results

| Requirement | Result | Evidence or finding |
|---|---|---|
| Network-only credential context | Passed | Explicit credentials established a scoped outbound token and completed authenticated NAS operations. |
| NAS create/write/flush/read | Passed | Random known bytes survived OS flush, close, reopen, and exact comparison. |
| NAS non-overwriting rename | Passed | Same-directory finalization rename and reopen succeeded. |
| NAS delete and cleanup | Passed | The generated test directory and owned files were removed. |
| NAS permission boundary | Passed | The account was denied in the configured expected-denied control location. |
| Ownership marker | Passed | Exclusive claim, collision refusal, ownership verification, and release succeeded. No alternate aliases were available. |
| `FileIdInfo` identity | Unsupported | `GetFileInformationByHandleEx(FileIdInfo)` returned Windows error 87. |
| Fallback file identity | Passed | `GetFileInformationByHandle` returned a stable file identity across close, reopen, and rename. |
| Fallback directory identity | Passed | `GetFileInformationByHandle` returned a stable directory identity across close and reopen. |
| Identity distinctness | Passed | The generated directory and file returned different fallback identities. |
| LocalSystem service identity | Passed | The installed automatic service reported the `LocalSystem` identity. |
| LocalSystem source read | Passed | One representative source file was read without changing captured metadata or the root ACL. |
| DPAPI after service start | Passed | `LocalSystem` decrypted the persisted machine-scope secret. |
| Loopback Kestrel | Passed | HTTP returned 200; listeners existed only on `127.0.0.1:5180` and `[::1]:5180`. |
| Local web assets | Passed | The rendered page referenced local MudBlazor and Blazor framework assets. |
| Interactive browser UI | Passed | MudBlazor styling rendered and the Interactive Server test counter incremented. |
| Symbolic-link final path | Passed | Elevated local probe resolved the alias to the target path and identity. |
| Junction final path | Passed | Elevated local probe resolved the alias to the target path and identity. |
| Automatic startup without login | Passed | Windows booted at `12:52:58Z`; the automatic `LocalSystem` run completed successfully at `12:53:16Z` while the PC remained at the sign-in screen. |

## Decisions

- Selected SMB mechanism: Windows network-only impersonation.
- Deviceless `WNetAddConnection2` fallback: Not required.
- Physical-identity strategy: Try `FileIdInfo`, then use `GetFileInformationByHandle` for SMB servers reporting unsupported-function errors.
- Ownership strategy: Directory identity is an early collision check; the ownership marker remains authoritative.
- NAS blocker: None found for transfer or finalization.
- Mount-point fixture: Not required for Milestone 0; production support remains scheduled for Milestone 3.

## Conclusion

No finding requires replacing the selected Windows filesystem, SMB authentication, transfer, finalization, physical-identity, or ownership-marker approach. Milestone 0 is complete.
