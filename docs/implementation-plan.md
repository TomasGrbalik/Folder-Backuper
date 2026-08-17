# Folder Backuper: Implementation Plan

Status: Draft for review

Related documents:

- [Use Cases and Product Requirements](use-cases.md)
- [Technical Design](technical-design.md)

## 1. Objective

Implement Folder Backuper as a sequence of testable milestones while addressing Windows, SMB, filesystem-safety, and crash-recovery risks before investing heavily in presentation work.

The plan intentionally avoids estimating calendar time. Scope and exit criteria should be stable before effort estimates are assigned.

## 2. Delivery Principles

### 2.1 Safety before automation

Source read-only guarantees, destination containment, artifact ownership, finalization, cancellation, and recovery must work before unattended scheduling is enabled.

### 2.2 Risk before breadth

SMB authentication against the intended NAS and Windows physical-path behavior are the largest environmental risks. They are tested first rather than discovered after building the complete application.

### 2.3 Vertical milestones

Each milestone should leave an executable, testable result. UI, application service, persistence, and infrastructure for a feature are completed together where practical.

### 2.4 Durable state before live state

Run phases, cancellation, artifact operations, and notifications are designed for restart recovery first. Live UI progress is layered over durable behavior rather than becoming the source of truth.

### 2.5 Real resources over excessive mocking

Tests use temporary directories, real ZIP archives, temporary SQLite databases, DPAPI on Windows, and representative SMB shares. Test doubles are reserved for time, notification transport, and fault injection.

### 2.6 No silent scope expansion

Features outside the accepted use cases are not added opportunistically. New behavior updates the requirements and technical design before implementation.

## 3. Milestone Overview

| Milestone | Outcome |
|---|---|
| 0 | High-risk Windows and SMB assumptions proven |
| 1 | Buildable service and MudBlazor application foundation |
| 2 | Durable SQLite model, migrations, and state transitions |
| 3 | Safe paths, secrets, and reusable destinations |
| 4 | Job management, source browser, preview, and schedule calculation |
| 5 | Backup engine creates and validates archives safely |
| 6 | Durable execution, cancellation, retention, and crash recovery |
| 7 | Scheduler, queue ordering, catch-up, and manual-run coalescing |
| 8 | Dashboard, progress, calendar, history, and storage presentation |
| 9 | Selected email provider and durable notification workflow |
| 10 | Installer, upgrade, service recovery, and release hardening |

## 4. Milestone 0: Technical Risk Validation

### Goal

Prove the environment-specific assumptions that could force architectural changes.

### Work

1. Create a minimal .NET 10 Windows test harness.
2. Protect and unprotect a sample secret through machine-scope DPAPI under the intended service identity.
3. Authenticate to the intended standalone NAS using network-only impersonation and credentials entered independently of the logged-in user.
4. Exercise create, write, flush, read, rename, and delete operations through impersonation.
5. Verify whether directory and file identity APIs return stable values on local storage and the intended NAS.
6. Validate final-path resolution through local junctions and symbolic links. A dedicated volume mount-point fixture is not required for Milestone 0; mount-point handling remains part of the production path implementation and tests in Milestone 3.
7. Verify application ownership-marker behavior through alternate SMB names or aliases available in the test environment.
8. Confirm that a UNC destination hosted by the backup PC can be detected and rejected.
9. Verify `ZipArchive.Comment` round-trips the installation and run identifiers.
10. Start a minimal ASP.NET Core application as `LocalSystem` and confirm loopback Kestrel and MudBlazor assets load without an interactive login.
11. Read representative local source data as `LocalSystem` and confirm the probe does not change source permissions or metadata intentionally.

If network-only impersonation fails against the intended NAS, validate deviceless `WNetAddConnection2` as the fallback and update the technical design before continuing.

### Deliverables

- Repeatable Windows-only integration tests or a retained diagnostic harness.
- Recorded NAS compatibility results.
- Confirmed SMB access mechanism.
- Confirmed physical-identity and ownership-marker strategy.
- Any required architecture amendment.

### Exit Criteria

- The service identity can read representative local source data without changing its permissions.
- Explicit credentials can access and mutate only the intended SMB test destination.
- Machine-scope secrets survive process and service restarts on the backup PC.
- Local physical-path aliases are detected.
- The intended NAS supports the selected transfer and finalization operations.
- No unresolved finding requires replacing the selected filesystem or SMB approach.

## 5. Milestone 1: Application Foundation

### Goal

Establish a production-shaped executable, project conventions, and automated build before feature implementation.

### Work

1. Create the solution with one application project and one test project.
2. Pin the .NET SDK and NuGet package versions.
3. Enable nullable reference types and the agreed compiler and analyzer settings.
4. Configure release builds to treat selected warnings as errors.
5. Add ASP.NET Core Windows service hosting with a console-development mode.
6. Add the machine-wide single-instance mutex.
7. Add application path resolution with a development data-root override.
8. Add bootstrap and normal Serilog configuration.
9. Add Blazor Interactive Server and MudBlazor.
10. Build the initial application shell, navigation, error boundary, and reconnect presentation.
11. Configure loopback Kestrel, host filtering, antiforgery, and baseline response headers.
12. Add automated Windows build and test execution.

### Deliverables

- Service-capable executable.
- MudBlazor application shell accessible on loopback.
- Deterministic package and SDK configuration.
- Initial CI workflow or equivalent repeatable build command.

### Exit Criteria

- A clean checkout restores, builds, and tests with documented commands.
- The application runs interactively for development and as a Windows service.
- A second process using the same data root is rejected across Windows sessions.
- The UI reconnects after a deliberate service restart.
- No Node.js tooling is required.

## 6. Milestone 2: Persistence and Durable State

### Goal

Implement the complete initial persistence model and prove migration and recovery primitives before filesystem workflows depend on them.

### Work

1. Add EF Core SQLite and context creation per operation.
2. Implement destination, job, run, run-problem, artifact, outbox, and settings records.
3. Implement enums and explicit transition rules for job, run, artifact, cancellation, retention, and notification states.
4. Add immutable run configuration snapshots.
5. Add schedule revision and occurrence identity records.
6. Add normalized destination-ownership data.
7. Configure foreign keys, WAL, and busy timeout.
8. Create the initial EF Core migration.
9. Implement online SQLite backup and validation before pending migrations.
10. Implement startup database initialization and migration failure handling.
11. Add repositories or focused persistence services only where needed by use cases.

### Deliverables

- Initial SQLite schema and migration.
- Tested transition services.
- Migration backup and restore evidence.
- Database inspection fixtures for later integration tests.

### Exit Criteria

- Every accepted permanent history field has a durable representation.
- Invalid state transitions are rejected.
- A scheduled occurrence cannot be inserted twice.
- Migration backup remains valid under WAL mode.
- A deliberately failing migration prevents normal service startup and leaves a validated pre-migration backup available; the working database is not assumed unchanged unless the migration mechanism proves transactional behavior.
- Tests use real temporary SQLite files rather than the EF in-memory provider.

## 7. Milestone 3: Safe Filesystem and Destinations

### Goal

Deliver reusable local and SMB destination management with service-side validation and safety boundaries.

### Work

1. Implement canonical Windows path parsing and normalization.
2. Implement narrow safe-handle Win32 interop for final path, reparse data, volume identity, and file/directory identity.
3. Implement reusable source/destination overlap primitives, including validation against a supplied set of sources.
4. Reject unsupported device paths, mapped SMB drives, parent traversal, and local-host UNC destinations.
5. Implement DPAPI secret protection and application-data ACL setup.
6. Implement network-only SMB impersonation using scoped safe handles.
7. Implement the fallback connection mechanism only if Milestone 0 required it.
8. Implement local and SMB destination adapters using common application operations.
9. Implement random exclusive root-level destination test files with byte verification and exact cleanup.
10. Implement best-effort capacity lookup during destination management and testing; unsupported capacity is non-fatal.
11. Implement ownership-marker creation, validation, and safe release primitives using synthetic job identities in tests.
12. Implement destination create, edit, verification invalidation, and last-access state. Job-reference-aware archive and mutation behavior is completed in Milestone 4.
13. Build the MudBlazor destination list, form, credential handling, and test dialog. Archive and restore actions remain unavailable until Milestone 4 implements reference-aware behavior.

### Deliverables

- Reusable destination application service.
- Local and SMB destination adapters.
- Destination management UI.
- Windows and NAS integration-test results.

### Exit Criteria

- Passwords never return from the server after saving and never appear in diagnostics.
- Wrong SMB credentials, denied access, unreachable hosts, and cleanup failures produce distinct structured results.
- Local-host UNC aliases are rejected.
- Two aliases reaching the same synthetic test folder cannot claim different ownership markers.
- Capacity is attempted in the required management and test contexts without making unsupported capacity a failure.

## 8. Milestone 4: Jobs, Source Preview, and Schedule Calculation

### Goal

Allow the user to configure valid jobs completely, without yet enabling unattended execution.

### Work

1. Implement job create, edit, pause, reactivate, archive, and restore services.
2. Implement the global configuration-mutation gate across queued, executing, and post-commit work for path changes, job/destination archive actions, and destination tests.
3. Implement effective destination-folder reservation for active and paused jobs.
4. Implement explicit unmanaged-artifact transitions after confirmed destination or subfolder changes.
5. Complete destination archive/restore and mutation workflows with active/paused reference checks, transactional job pausing, collision checks, and verification invalidation.
6. Implement separate job-effective-folder tests, including validated subfolder creation, marker ownership verification, known-byte checking, and exact cleanup.
7. Enforce authoritative ownership-marker claim or verification during activation and reactivation, and verified marker release during archive or confirmed path changes.
8. Build destination archive and restore actions backed by the reference-aware transactional workflow.
9. Implement local source browsing one directory level at a time using cancellable bounded pages.
10. Implement cancellable, progressive source preview aggregation.
11. Explicitly include hidden and system content in previews.
12. Report but never traverse reparse points.
13. Verify preview operations do not change source metadata intentionally.
14. Implement weekday and local-time schedule value objects.
15. Implement occurrence calculation with `TimeProvider` and `TimeZoneInfo`.
16. Cover daylight-saving, clock-change, schedule-revision, and catch-up rules.
17. Build the single-page MudBlazor job form with focused modals.
18. Add inline validation, unsaved-change warning, next-run preview, and retention estimate presentation.

### Deliverables

- Complete job-management application service and UI.
- Source browser and progressive preview.
- Pure, deterministic schedule calculator.

### Exit Criteria

- A user can configure all accepted job fields on one page.
- An unverified destination cannot activate a job.
- Active and paused jobs cannot share an effective destination folder.
- Activation, reactivation, and effective-folder testing enforce ownership-marker claim or verification; archive and confirmed path changes safely release only the job's verified marker.
- Destination changes pause affected jobs and mark old artifacts unmanaged in one transaction.
- Destination archive cannot invalidate an active or paused job reference.
- Source browsing returns bounded directory-tree pages with names, types, sizes, modified times, access problems, and cancellation.
- Preview progressively reports file count, folder count, estimated size, inaccessible content, and skipped reparse points, explains that results are informational, and does not write to source data.
- Every agreed daylight-saving and clock-change example has a deterministic test.
- Pausing, archiving, restoring, and reactivating update lifecycle and schedule watermark/revision state without immediately inserting runs.

## 9. Milestone 5: Backup Engine

### Goal

Produce a complete validated archive through a directly invoked engine before adding durable orchestration and scheduling.

### Work

1. Implement controlled source enumeration with explicit options.
2. Add execution preflight that revalidates source locality and readability, staging containment, destination containment and physical overlap against every source, local-host UNC rejection, destination verification, and effective-folder ownership before any write.
3. Build the immutable source manifest and aggregate counts.
4. Record skipped reparse points as warnings.
5. Collect inaccessible-file and inaccessible-directory problems where practical.
6. Implement ZIP entry path validation and top-level source folder layout.
7. Implement cancellable copy loops with byte-level progress.
8. Add the archive ownership comment.
9. Implement empty-directory preservation and ZIP64 behavior.
10. Re-enumerate and compare the source after compression.
11. Validate the local archive against the manifest.
12. Transfer to a random destination partial name through local and SMB adapters.
13. Update destination last-access result independently from explicit verification.
14. Classify local staging and destination unavailable, inaccessible, insufficient-space, and general I/O failures as structured problems.
15. Validate the destination archive and expected length.
16. Generate sanitized, bounded, collision-resistant final names.
17. Rename without overwrite.
18. Attempt staging and partial cleanup through normal and exceptional paths and preserve ownership information when cleanup cannot complete.
19. Implement rate-limited immutable progress snapshots and throughput calculations.

### Deliverables

- Backup engine callable from integration tests.
- Local and SMB end-to-end archive tests.
- Progress model suitable for later UI subscription.

### Exit Criteria

- Performance evidence is recorded for a representative 10 GB dataset, thousands of files, local staging, and the intended NAS; any deployment-window target is accepted separately using measured hardware and network results.
- Produced ZIP files open in standard Windows tools and have the agreed folder layout.
- Hidden files, system files, empty directories, thousands of files, and 15 MB files behave correctly.
- Any unreadable ordinary file or inaccessible ordinary directory prevents publication, with continued safe problem discovery where practical.
- Source additions, removals, and detectable changes during compression prevent publication.
- Cancellation before commit leaves no valid new archive.
- Existing destination files are never overwritten.
- Local staging and destination partials are cleaned where possible after success, failure, and cancellation; remaining owned files produce structured cleanup warnings and are recoverable at startup.
- Local and destination insufficient-space failures are distinguished and never publish a valid backup.

## 10. Milestone 6: Durable Execution and Recovery

### Goal

Make backup execution safe across cancellation, service shutdown, process crash, and database/filesystem commit boundaries.

### Work

1. Implement durable queued-run creation and atomic claim.
2. Add the single queue consumer and in-process wake signal.
3. Implement the durable one-queued-or-running-run-per-job guard and race-safe manual enqueue.
4. Implement durable cancellation requests and executor token ownership.
5. Implement the atomic cancellation gate and pending-finalization intent.
6. Reconcile crashes before and after destination rename.
7. Implement artifact ownership fingerprints.
8. Implement count-based retention with pending deletion intent.
9. Refuse deletion when archive ownership cannot be proven.
10. Implement managed, missing, removed, and unmanaged artifact transitions.
11. Persist managed count, total bytes, latest size, and last-confirmed time after successful finalization and retention.
12. Implement post-commit recovery and warning classification.
13. Implement startup queue recovery and startup cleanup tied to known run identifiers.
14. Add a startup coordinator barrier: database initialization and complete recovery must finish before the queue consumer starts or receives a wake signal.
15. Implement graceful service-shutdown interruption separately from user cancellation.
16. Implement explicit fault-injection hooks for integration tests.
17. Expose Run now and Cancel through application services, including ownership-marker verification before queueing.

### Deliverables

- Durable execution coordinator.
- Recovery service.
- Retention service.
- Fault-injection integration suite.

### Exit Criteria

- One and only one backup pipeline executes at a time.
- Repeated or racing manual requests cannot create multiple queued/running runs for one job.
- Queued cancellation never starts filesystem work.
- Running cancellation works through transfer and is rejected after commit starts.
- A crash before final rename produces a failed run and attempts partial cleanup, retaining safe ownership data and a warning if cleanup is impossible.
- A crash after final rename preserves and reconciles the successful backup.
- A crash around retention deletion produces a deterministic artifact state after recovery.
- An unrelated replacement file is never deleted when ownership verification fails.
- Unknown and unregistered files are never deleted.
- Previous successful backups survive every pre-commit failure test.
- Successful backup and retention work update managed storage aggregates without waiting for UI refresh.
- Queued work and interrupted execution recover correctly before the scheduler exists.
- The queue consumer cannot claim work until startup recovery has completed successfully.

## 11. Milestone 7: Scheduler and Queue Semantics

### Goal

Enable unattended operation only after a safe durable execution path exists.

### Work

1. Implement the hosted scheduler loop.
2. Require the scheduler to await the same successful startup-recovery barrier as the queue consumer.
3. Persist schedule watermarks and next occurrences.
4. Insert due and catch-up runs transactionally.
5. Claim durable queue rows by due instant, queue time, and run identifier.
6. Collapse missed occurrences per the accepted policy.
7. Coalesce a due occurrence into an existing manual queued or running job execution through the durable guard.
8. Prevent catch-up for paused and archived periods.
9. Add deterministic race and restart tests.

### Deliverables

- Hosted scheduler.
- Durable due-time queue behavior.
- Calendar occurrence query service shared with the UI.

### Exit Criteria

- Due-time ordering survives service restart.
- Duplicate scheduler evaluation cannot duplicate an occurrence.
- Manual-run and scheduled-run races produce one execution.
- Forward clock changes create one catch-up per job.
- Backward clock changes and fall-back daylight-saving time do not duplicate runs.
- Catch-up does not replace the next regular occurrence.
- A long-running job does not discard later queued work.
- Pausing, archiving, restoring, and reactivating do not create catch-up work for inactive periods.

## 12. Milestone 8: Monitoring and History UI

### Goal

Expose complete operational visibility after execution semantics are stable.

### Work

1. Connect the in-memory progress registry to Blazor components.
2. Implement active-run job, source folder, current phase, current file, files/bytes processed, archive size, compression speed, Copying/Uploading transfer label and speed, progress, elapsed time, estimate, and Cancel presentation.
3. Implement queued-job ordering presentation.
4. Build the health-focused dashboard with current run, queue, per-job last outcome, last success, next run, failures, warnings, notification status, destination last-access state, and all accepted quick actions.
5. Build structured run details and problem-list views.
6. Build permanent history filtering and detail navigation.
7. Build the custom MudBlazor month calendar with job/status filters and selectable details.
8. Build the agenda/list view with the same filters and details.
9. Use the same occurrence service for planned calendar entries.
10. Implement managed artifact reconciliation on explicit storage refresh.
11. Display current managed totals, stale timestamps, missing artifacts, and unmanaged historical artifacts.
12. Apply Windows regional formatting and invariant archive-name presentation.
13. Verify desktop and narrow-screen behavior.

### Deliverables

- Dashboard.
- Live progress view.
- Calendar and agenda.
- Permanent history and structured diagnostics, pending notification-result integration in Milestone 9.
- Managed storage presentation.

### Exit Criteria

- UI state agrees with SQLite after browser reconnect and service restart.
- Progress updates do not write continuously to SQLite or logs.
- Past and planned entries use consistent state and color semantics.
- Every accepted active-run and dashboard field is present and derives from the correct live or durable source.
- Month and agenda views show past and planned entries, job/status filters, and selected-entry details; no week/day view is exposed.
- Full problem lists remain usable with thousands of entries.
- Job storage shows managed total, retained count versus configured count, latest size, last-confirmed time, stale state, missing artifacts, and separate unmanaged historical artifacts.
- Destination capacity appears only during destination management or explicit testing.
- No raw-log viewer or export is exposed.
- Desktop and narrow-screen acceptance checks cover every primary page and modal.

## 13. Milestone 9: Notifications

### Prerequisite Decision

Choose exactly one initial provider before starting this milestone:

- MailKit over SMTP, or
- Resend over HTTPS.

If Resend is selected, explicitly accept its verified-domain and external-processing requirements. If MailKit is selected, define the supported SMTP security modes.

### Goal

Deliver global test and run-result email without coupling backup correctness to provider availability.

### Work

1. Implement the provider-neutral notification model.
2. Implement the selected provider only.
3. Protect SMTP password or Resend API key with DPAPI.
4. Implement global recipients and provider settings UI.
5. Implement Send test email.
6. Insert notification outbox work with terminal run outcomes.
7. Implement the single-attempt claim and Delivery unknown recovery rule.
8. Build success, warning, and failure templates.
9. Include job, outcome, scheduled/actual times, duration, archive size and destination, retention warnings, total problem count, and at most the first 100 problems.
10. Exclude cancelled runs.
11. Surface delivery failures and unknown outcomes on dashboard, permanent history, and run details.

### Deliverables

- Selected email transport.
- Notification settings and test UI.
- Durable single-attempt notification worker.
- Provider-neutral message templates.

### Exit Criteria

- Test email verifies the exact saved service-side configuration.
- Backup outcome is unchanged by delivery failure.
- A crash before notification claim leaves work pending for startup.
- A crash after claim does not create an application retry.
- Cancelled runs never send email.
- Secrets and excessive problem details do not appear in logs.
- Global recipients and every eligible outcome are covered by provider-neutral payload tests.
- Every accepted message field is rendered and redacted correctly.
- Permanent history records delivered, failed, and delivery-unknown results.

## 14. Milestone 10: Installer and Release Hardening

### Goal

Deliver a reliable `setup.exe` and validate the complete unattended product lifecycle.

### Work

1. Add deterministic self-contained `win-x64` publishing.
2. Build the Inno Setup package.
3. Install under `Program Files` and create protected `ProgramData` directories.
4. Select and persist the loopback port.
5. Install `LocalSystem` service startup and recovery settings.
6. Create the start-menu shortcut.
7. Wait for service readiness and verify the localhost URL.
8. Implement clear startup, migration, and port-binding failure reporting.
9. Implement repair-time port reconfiguration.
10. Implement stop, replace, migrate, and restart upgrade flow.
11. Preserve application data by default.
12. Implement explicit uninstall data-removal choice.
13. Test interrupted installation and upgrade failure behavior.
14. Run full-scale and failure-injection tests on installed builds.
15. Validate CPU, memory, database, log, and staging behavior over repeated runs.
16. Complete security, privacy, and source-write reviews.
17. Prepare code-signing integration for release artifacts.

### Deliverables

- Versioned `setup.exe`.
- Clean Windows VM lifecycle test record.
- Release checklist.
- Operator installation and first-run documentation.

### Exit Criteria

- Fresh install, service startup, UI launch, upgrade, repair, and uninstall pass in a clean VM.
- Upgrade preserves all application data and permanent history.
- Migration failure leaves a valid pre-migration database backup and visible diagnostics.
- Backups run without an interactive login.
- Installed SMB backup succeeds against the intended NAS.
- Service crash recovery works in every durable pipeline phase.
- No installer action changes source-folder permissions.
- Released binaries are ready for signing and distribution.

## 15. Cross-Cutting Verification

Every milestone must maintain:

- `dotnet build` success with the pinned SDK.
- `dotnet test` success on Windows.
- No compiler warnings newly introduced without explicit disposition.
- No secrets in repository files, logs, snapshots, test output, or rendered edit models.
- No source-folder writes in integration tests.
- Updated design documents when behavior changes.
- Focused tests for every corrected defect.

Before release, execute a matrix covering:

- Local and SMB destinations.
- Correct and incorrect SMB credentials.
- Destination disconnect before and during transfer.
- Insufficient local staging space and insufficient local/SMB destination space.
- Locked, inaccessible, added, removed, and changed source files.
- Hidden and system files.
- Reparse points and physical path aliases.
- Empty directories and long paths.
- Cancellation in each pre-commit phase.
- Process termination around every durable filesystem intent.
- Retention success, ownership mismatch, deletion denial, and missing artifacts.
- Daylight-saving and system-clock changes.
- Browser disconnect and service restart.
- Notification success, rejection, timeout, and uncertain crash boundary.
- Install, upgrade, repair, and uninstall.

## 16. Implementation Dependencies

```text
Milestone 0: risk validation
        |
Milestone 1: foundation
        |
Milestone 2: persistence
        |
Milestone 3: destination and path primitives
        |
Milestone 4: jobs, coupled destination workflows, and schedules
        |
Milestone 5: backup engine
                  |
Milestone 6: durable execution and recovery
                  |
Milestone 7: scheduler and queue
                  |
Milestone 8: monitoring UI
                  |
Milestone 9: notifications
                  |
Milestone 10: installer and release hardening
```

Milestone 9 provider selection can occur earlier, but provider implementation should not distract from backup correctness and recovery.

## 17. Definition of Complete

The initial implementation is complete only when:

1. All acceptance items in the use-case document are demonstrably satisfied.
2. All technical-design invariants have automated tests where practical.
3. The intended NAS passes credential, transfer, finalization, retention, and failure tests.
4. Every durable pipeline boundary has a tested startup-recovery result.
5. The UI accurately reflects durable state after reconnect and restart.
6. The installer passes clean install, upgrade, repair, and uninstall testing.
7. Permanent history survives normal upgrades and artifact retention.
8. Source read-only behavior has been reviewed and tested independently.
9. The notification provider decision and privacy implications are documented.
10. No known critical or high-severity correctness issue remains open.
