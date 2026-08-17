# Folder Backuper: Technical Design

Status: Draft for review

Related requirements: [Use Cases and Product Requirements](use-cases.md)

## 1. Purpose

This document translates the agreed product requirements into a technical architecture. It defines component boundaries, data ownership, execution behavior, deployment, security, and verification strategy.

The notification transport remains open. All other major stack choices in this document have been accepted for the initial implementation.

## 2. Architecture Summary

Folder Backuper is one self-contained Windows application running as one Windows service process. The process hosts:

- The localhost web interface.
- Interactive Blazor circuits.
- Application services.
- The scheduler.
- The sequential backup queue.
- Backup execution.
- SQLite persistence.
- Notification delivery.

```text
Browser on backup PC
        |
        | HTTP + Blazor Interactive Server
        v
ASP.NET Core Windows service
        |
        +-- MudBlazor UI
        +-- Job and destination services
        +-- Scheduler and execution queue
        +-- Backup pipeline and progress registry
        +-- Notification sender
        +-- EF Core
                |
                v
             SQLite

Backup pipeline
        |
        +-- Local source folders (read-only)
        +-- Local staging directory
        +-- Local or SMB destinations
```

There is no separate worker process, frontend deployment, public API, message broker, or external database.

## 3. Technology Stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 LTS, `win-x64` |
| Host | ASP.NET Core `WebApplication` with Windows service lifetime |
| UI | Blazor Web App, Interactive Server rendering |
| Component library | MudBlazor 9.x, version pinned |
| Web server | Kestrel, loopback binding only |
| Persistence | SQLite |
| Data access | EF Core SQLite |
| Scheduling | Custom weekday/time scheduler using `TimeProvider` and `TimeZoneInfo` |
| Queue | SQLite-backed durable queue with an in-process wake signal and one consumer |
| Compression | `System.IO.Compression.ZipArchive` |
| Filesystem | BCL `System.IO` APIs plus narrow Windows safe-handle interop for physical identity and final-path resolution |
| SMB authentication | Windows network-only impersonation with per-destination credentials |
| Secret protection | Windows DPAPI, machine scope |
| Email | TBD: MailKit SMTP or Resend HTTPS API |
| Logging | Serilog rolling files |
| Installer | Inno Setup |
| Tests | xUnit, bUnit, real temporary files, and real temporary SQLite databases |

Explicitly excluded dependencies include Quartz, Hangfire, NCrontab, VSS libraries, SMB client libraries, alternate ZIP libraries, Node.js tooling, and filesystem abstraction libraries.

## 4. Deployment Model

### 4.1 Installed layout

The application is published as a self-contained `win-x64` directory and packaged into `setup.exe` by Inno Setup.

The installed application is not required to be a literal single file. A normal publish directory avoids runtime extraction and static-asset complications involving ASP.NET Core, MudBlazor, and native SQLite components.

```text
C:\Program Files\FolderBackuper\
    FolderBackuper.exe
    application assemblies
    framework assemblies
    static assets
    native SQLite components
```

The application runs as one process despite containing multiple installed files.

### 4.2 Writable application data

```text
C:\ProgramData\FolderBackuper\
    config\
    data\
        folder-backuper.db
        migrations\
    staging\
    logs\
```

The service and local administrators have access to this tree. Normal users do not receive direct filesystem access through installer-created permissions.

The staging directory must never be located inside a configured source folder.

### 4.3 Service identity

The service runs as `LocalSystem` and starts automatically with Windows. Windows service recovery restarts it after unexpected termination.

`LocalSystem` is chosen because the service must run without an interactive login, read locally configured sources, protect machine-level credentials, and perform scoped outbound impersonation. Source validation must still confirm that the service can read a selected folder. The application never changes source permissions automatically.

### 4.4 Publishing

- Self-contained `win-x64` release.
- Trimming disabled.
- Native AOT disabled.
- ReadyToRun may be evaluated only after measuring publish size and startup impact.
- Package versions are centrally pinned.
- The .NET SDK is pinned for reproducible builds.

Supporting Windows on ARM or 32-bit Windows requires a separate future deployment decision.

## 5. Solution Structure

```text
src/
    FolderBackuper/
tests/
    FolderBackuper.Tests/
installer/
docs/
research/
```

There is one application project and one test project. The application project uses feature-oriented organization instead of one class library per architectural layer.

```text
src/FolderBackuper/
    Features/
        Dashboard/
        Jobs/
        Destinations/
        Backups/
        Calendar/
        History/
        Notifications/
    Infrastructure/
        Database/
        Filesystem/
        Scheduling/
        Security/
        ServiceHosting/
    Components/
    wwwroot/
    Program.cs
```

Interfaces are introduced at concrete boundaries that require substitution or isolation, such as time, notification delivery, protected secrets, SMB impersonation, and backup progress. Domain code is not split into layers solely to follow a template.

## 6. Hosting and Startup

Startup order is deterministic:

1. Resolve and validate application paths.
2. Configure structured bootstrap logging.
3. Acquire a named single-instance mutex.
4. Apply application-data access controls.
5. Open SQLite and enable required connection settings.
6. Back up the database if a schema migration is pending.
7. Apply EF Core migrations.
8. Recover interrupted runs and orphaned temporary files.
9. Start the backup queue consumer.
10. Start the scheduler.
11. Start Kestrel and accept UI connections.

If database initialization or migration fails, the scheduler and backup executor must not start. The service exits with a diagnostic rather than running against unknown state.

The application uses a machine-wide `Global\FolderBackuper-<data-root-hash>` mutex with an explicit security descriptor granting the service and local administrators access. This prevents service and interactive diagnostic instances in different Windows sessions from operating on the same data directory concurrently.

## 7. Web Interface

### 7.1 Hosting

- Blazor Web App with global Interactive Server rendering.
- MudBlazor 9.x.
- Kestrel bound to `127.0.0.1` and `::1` on the configured port.
- HTTP only while loopback-only.
- No CORS policy allowing external origins.
- No public Minimal API in the initial version.
- Static assets served locally; runtime CDN dependencies are prohibited.

The installer selects an available default port, writes immutable hosting configuration, and creates a start-menu shortcut for the correct localhost URL.

### 7.2 Security controls

The accepted localhost-only threat model does not require application login. The application still uses:

- Loopback binding.
- Host filtering.
- Antiforgery protection for state changes.
- Restrictive response headers and a content security policy compatible with Blazor and MudBlazor.
- Strict server-side path validation.
- No secret values in rendered models after initial submission.

Binding to `0.0.0.0` is not a configuration toggle in the initial version. Remote binding requires a separate design covering authentication, authorization, HTTPS, firewall rules, and local filesystem exposure.

### 7.3 Components

MudBlazor supplies forms, dialogs, validation presentation, tables, tree views, progress indicators, navigation, status chips, alerts, snackbars, and responsive layout primitives.

The application supplies:

- A custom health-focused dashboard layout.
- A custom month calendar.
- A custom agenda/list view.
- A lazy server-side source browser.
- Backup progress and statistics presentation.
- A restrained custom MudBlazor theme.

No additional calendar package is planned. Narrow JavaScript interop is allowed for browser-only behavior such as unsaved-change warnings.

The source browser requests one directory level at a time under a service-validated local root. Requests are cancellable and return bounded pages of names, types, sizes, modified times, and access problems. Aggregate preview scanning runs separately with cancellation and progressive count/size updates. Preview results are not persisted as execution truth; backup scanning always starts again. Integration tests snapshot source metadata before and after browsing to enforce the no-write invariant.

### 7.4 Circuit behavior

Live progress is delivered through the existing Blazor Interactive Server circuit. The UI displays a reconnect state if the circuit is interrupted. Durable state is reloaded from SQLite after reconnection; current progress is reloaded from the in-memory progress registry while the process remains alive.

The UI must not treat an open browser connection as necessary for backup execution.

## 8. Persistence Design

### 8.1 SQLite operation

EF Core SQLite owns application persistence and schema migrations.

Connection initialization enables:

- Foreign keys.
- Write-ahead logging where compatible with the installed filesystem.
- A bounded busy timeout.

Transactions are short. Backup file I/O, SMB access, ZIP creation, and email delivery never occur inside a database transaction.

EF Core contexts are created per application operation. Entities are not retained as long-lived tracked objects in hosted services or Blazor circuits.

### 8.2 Core records

#### Destination

- Identifier.
- Name.
- Type: local or SMB.
- Canonical root path.
- SMB username when applicable.
- DPAPI-protected password blob when applicable.
- Verification fingerprint, result, and timestamp.
- Active or archived state.
- Last access-attempt result, source, and timestamp, separate from verification.
- Created and updated timestamps.

The verification fingerprint is derived from configuration fields that affect access. Changing any such field invalidates prior verification without comparing or exposing the password.

#### Job

- Identifier.
- Unique name.
- Canonical local source path.
- Destination identifier.
- Normalized relative destination subfolder.
- Weekday bit set.
- Local scheduled time.
- Schedule revision.
- Retention count.
- Active, paused, or archived state.
- Normalized effective-destination ownership key for active-job collision checks.
- Created and updated timestamps.

Unique database constraints protect job names. Effective destination ownership depends on both destination and job data, so configuration mutations are serialized in an immediate transaction and maintain a normalized ownership key on each active or paused job. Local ownership keys include resolved volume and directory identity where available; SMB ownership uses server/share identity obtained through the authenticated directory handle where supported, with normalized UNC identity as a conservative fallback. Destination-root changes transactionally recalculate all referencing jobs, reject collisions or source overlap, pause affected jobs, invalidate verification, and mark artifacts at the old path unmanaged after explicit user confirmation.

#### Run

- Identifier.
- Job identifier and immutable execution snapshot containing job name, source path, destination name/type/root, destination subfolder, schedule, retention count, and relevant regional/time-zone context. Secrets are never snapshotted.
- Trigger: scheduled, catch-up, or manual.
- Schedule revision and occurrence identity when scheduled.
- Nullable scheduled local date and time; manual runs leave these absent unless they later satisfy a due occurrence.
- Time-zone identity and UTC offset relevant to the occurrence.
- Queued, started, and completed UTC timestamps.
- Durable phase.
- Terminal outcome.
- Durable cancellation-request timestamp and final-commit timestamp.
- File, directory, and byte statistics.
- Compression and transfer duration.
- Error summary.
- Notification status and error summary.

A unique constraint on scheduled occurrence identity prevents duplicate scheduled and catch-up runs. If a manual run for the job is already queued or running when an occurrence becomes due, the due occurrence is transactionally associated with that run instead of creating another run.

#### Run problem

- Run identifier.
- Source or destination path, as appropriate.
- Pipeline phase and operation.
- Stable application error category.
- Native or provider error code where available.
- User-facing message.
- Diagnostic detail safe for local storage.

Problems are stored as structured rows, not parsed from log text.

#### Backup artifact

- Run identifier.
- Destination and effective path snapshot.
- Final filename.
- Size.
- Creation timestamp.
- State: pending finalization, retained, removed by retention, found missing, or unmanaged.
- Expected ownership fingerprint, including run identifier, expected length, creation metadata, and filesystem identity where the destination supports it.
- Durable finalization and retention-operation state used by recovery.
- State-change timestamp.

Only registered retained artifacts are candidates for automated retention deletion. A destination or subfolder change marks prior retained artifacts unmanaged; unmanaged artifacts remain in history but are never deleted or included in current managed-storage totals.

#### Notification outbox

- Run identifier and terminal outcome.
- Provider-neutral notification payload snapshot.
- Pending, sending, delivered, failed, or delivery-unknown state.
- Attempt count, timestamps, and last safe error.

Terminal run persistence and outbox insertion occur in one transaction. Claiming a pending record durably marks it Sending and defines the beginning of its single attempt before contacting the provider. Startup attempts records that remain Pending, but converts records left Sending to Delivery unknown without retry. A crash in the small interval after claim and before the provider call may therefore lose the notification; this is the accepted cost of preventing application-created duplicates.

#### Application settings

- Notification provider configuration.
- Global recipient list.
- Protected notification secret.
- Other mutable product settings that do not affect early host startup.

The Kestrel port and application-data root remain host configuration outside SQLite because they are needed before the application database and UI are available.

### 8.3 Time representation

- Actual event timestamps are stored as UTC instants.
- Scheduled wall-clock values are stored as local date/time plus time-zone context.
- UI dates, times, numbers, and file sizes use the Windows PC's current regional culture.
- Archive timestamps use a fixed, locale-independent format.

### 8.4 Migration safety

Before a pending migration, the service creates a consistent backup through SQLite's online backup API while no application writers are active, then opens and validates the backup database. A small fixed number of recent migration backups is retained. Migration failure prevents normal startup and preserves diagnostics and the pre-migration backup.

## 9. Scheduler and Queue

### 9.1 Schedule calculation

The scheduler stores weekdays and a local `TimeOnly`; it does not parse cron expressions.

An occurrence calculator based on `TimeProvider` and `TimeZoneInfo` implements:

- Normal future occurrence calculation.
- Invalid local times during spring daylight-saving transitions.
- Ambiguous local times during autumn transitions.
- Forward and backward clock changes.
- Time-zone changes.
- Schedule revisions.
- Collapsing multiple missed occurrences.

The calculator is a pure service with exhaustive deterministic tests.

A scheduled occurrence key consists of job identifier, schedule revision, and scheduled local date. Because schedules run at most once per selected day, this remains stable across repeated local times and time-zone changes.

- For an invalid spring-forward local time, the due instant is the first valid instant after the gap.
- For an ambiguous fall-back local time, the earlier UTC instant is selected.
- A forward clock or time-zone change collapses all newly missed keys into the latest catch-up occurrence.
- A backward change cannot recreate a key already recorded.
- A schedule edit increments the revision and initializes its watermark from the save instant, so the old revision cannot create catch-up work.

### 9.2 Scheduler loop

A hosted service periodically:

1. Reads active jobs and durable schedule state.
2. Determines due or missed occurrences using the current `TimeProvider` value.
3. Creates run records transactionally.
4. Relies on the occurrence uniqueness constraint to reject duplicates.
5. Signals the queue consumer that durable work is available.
6. Calculates and persists the next planned occurrence.

Calendar queries use the same occurrence calculator so displayed future events agree with execution behavior.

### 9.3 Durable execution queue

Scheduled, catch-up, and manual runs are durable queued rows. One hosted consumer executes them sequentially.

The consumer always claims the next queued row from SQLite ordered by effective due instant, queued timestamp, and run identifier. This preserves due-time ordering across service restarts and supplies deterministic tie-breaking.

An in-process bounded channel carries only a coalesced wake signal; it never defines durable order. Signals are harmless because the consumer atomically claims queued rows. Startup signals the consumer after recovery.

A transactionally maintained job-execution guard prevents more than one queued or running run for a job. When a schedule becomes due while a manual run is queued or running, the occurrence key is attached to that existing run as satisfied. Race tests cover simultaneous manual enqueue, scheduler evaluation, cancellation, and restart.

### 9.4 User cancellation

Cancellation is a durable request, not only an in-memory token.

- Cancelling a queued run transitions it directly to Cancelled.
- Cancelling a claimed run records the request and signals the executor's owned cancellation token.
- Scanning, compression, and transfer check cancellation frequently.
- One SQLite transaction conditionally rejects any existing cancellation request, records pending finalization, and closes the cancellation window before destination rename.
- The UI disables Cancel once the commit-start transaction begins.
- A cancellation request that loses the race with commit start is rejected; finalization proceeds.
- Cancelled runs remove incomplete output, do not run retention, and do not create a notification outbox record.

### 9.5 Shutdown and interruption

Graceful service shutdown requests internal interruption and allows a short cleanup period. This is distinguished from user cancellation. If work cannot finish, the run remains durably non-terminal for startup recovery.

Runs interrupted before the final destination rename become Failed and are not retried. If the final rename completed, recovery validates the final archive and preserves the successful outcome, then reconciles or resumes post-commit retention. Required notifications are delivered through the durable outbox after recovery.

## 10. Backup Pipeline

### 10.1 Overview

```text
Queued
  -> Scanning
  -> Compressing to local staging
  -> Validating local ZIP
  -> Transferring to destination temporary file
  -> Validating destination ZIP
  -> Finalizing destination filename
  -> Applying retention
  -> Recording outcome
  -> Sending notification
```

Each durable phase transition is persisted before the next externally visible phase begins.

Local ZIP validation is displayed as Compressing. Destination validation, final commit, and retention are displayed as Finalizing. These internal substates do not add user-visible run phases beyond the accepted model.

### 10.2 Source safety

All source operations use read-only filesystem APIs. The application never:

- Creates source files or directories.
- Opens source files for write access.
- Changes source metadata, attributes, ACLs, or timestamps intentionally.
- Deletes or renames source entries.
- Places staging or marker files in the source tree.

Source and effective destination paths are canonicalized before overlap validation. For existing local paths, the service opens directory handles and resolves final physical paths so junction aliases and volume mount points cannot hide overlap. For a not-yet-created job subfolder, it resolves the deepest existing ancestor, creates the subfolder only after validation, and then resolves and validates the result again.

Validation compares a local destination against every configured source, not only the current job's source. It is repeated immediately before creating destination or staging content. Device paths and unsupported path forms are rejected. The trusted-local-host threat model does not attempt to defend against a local administrator deliberately swapping filesystem objects between validation and use.

### 10.3 Scanning

Scanning performs controlled recursive enumeration and builds an immutable manifest containing:

- Relative path.
- Entry type.
- Size.
- Last-write timestamp.
- Relevant attributes.

Enumeration explicitly includes hidden and system entries, does not ignore inaccessible content, and does not follow any reparse point. Junctions, symbolic links, mount points, and other reparse entries are recorded as warnings and skipped. If ordinary content cannot be enumerated, the run does not proceed to compression.

The manifest supplies total counts and bytes for progress reporting. It exists only for the current run and is not stored as permanent per-file history unless an entry becomes a problem.

### 10.4 Compression

Compression uses `ZipArchive` directly rather than `ZipFile.CreateFromDirectory`.

- The staging filename contains the run identifier and a temporary suffix.
- ZIP entry paths are generated from validated relative paths only.
- The source directory name is the top-level ZIP entry.
- Empty directory entries are emitted explicitly.
- ZIP64 is used automatically when required.
- A fixed application compression level is used initially.
- Copy loops report bytes and honor cancellation.
- Source metadata is checked around reads to detect obvious concurrent changes.
- Any failed ordinary entry invalidates the entire staging archive.
- The archive comment stores an application installation identifier and run identifier for later ownership verification without adding a file to restored content.

After a compression error, the service continues non-destructive accessibility checks where practical to produce a useful problem list, then deletes the staging archive.

After compression, the source is enumerated again and compared with the original manifest. Added, removed, or detectably changed ordinary entries fail the run before publication. Without VSS this is not an atomic multi-file snapshot, and same-size modifications that preserve timestamps may remain undetectable; the operating requirement remains that source applications are idle.

### 10.5 Local ZIP validation

The staging ZIP is reopened read-only. Validation confirms:

- The central directory can be read.
- Expected manifest entries exist.
- Entry names are safe and match expected relative paths.
- Entry lengths match expected uncompressed lengths.

The initial product does not read and CRC-check every decompressed byte as a separate validation pass.

### 10.6 Transfer

The destination filename is first created with an application-specific partial suffix in the final destination directory. Transfer uses asynchronous buffered streams with explicit progress reporting and cancellation.

- Local destinations use standard filesystem access.
- SMB destinations execute inside scoped Windows network impersonation.
- Existing files are never overwritten.
- Transfer errors close handles and attempt partial-file cleanup.
- A cleanup failure is recorded without treating the partial file as a valid artifact.

### 10.7 Destination validation and finalization

The destination temporary file must:

- Match the local archive length.
- Open as a ZIP from the destination.
- Expose the expected entry list and lengths.

The final name contains a sanitized, length-bounded job name, invariant timestamp, and short run identifier. Invalid Windows filename characters, reserved names, trailing periods or spaces, repeated wall-clock times, and same-second runs therefore cannot cause ambiguous names.

Before rename, one database transaction verifies that cancellation has not been requested, records a pending-finalization artifact containing expected paths, length, archive comment, and available filesystem identity, and marks commit started. The application then renames the file within the same destination directory without overwriting an existing file and marks the run final-committed. Startup recovery can reconcile a crash on either side of the rename by validating the recorded ownership fingerprint.

The backup artifact becomes retained only after finalization succeeds. From this commit point onward the backup cannot become Cancelled or Failed because of service interruption; retention and notification remain recoverable post-commit work.

### 10.8 Retention

Retention queries currently managed retained artifact records for the job, ordered oldest first. Artifacts made unmanaged by a destination or subfolder change are excluded and never deleted automatically.

For each deletion:

1. Persist a pending-retention-deletion intent.
2. Resolve and revalidate physical destination containment.
3. Open the candidate and verify its run-specific archive comment, expected length, creation metadata, and filesystem identity where available.
4. Refuse deletion and record a warning if ownership cannot be proven.
5. Delete the file.
6. Mark the artifact as removed by retention.
7. Preserve its run and artifact metadata permanently.

Failure to delete an old artifact changes an otherwise successful run to Successful with warnings. It does not delete the newly created backup.

### 10.9 Orphan recovery

On startup, the recovery service reconciles every durable filesystem intent:

- Pre-commit staging and partial files are validated against their run and removed.
- A pending finalization with a valid final path is completed durably; one with only a partial path is cleaned and failed as interrupted. Recovery never resumes a rename that did not occur before interruption.
- Final-committed runs resume pending retention.
- Pending retention deletions are reconciled by ownership and existence.
- Final outcomes create or recover notification outbox work.

The run-level executor also deletes local staging in a `finally` path after success, failure, or cancellation. Startup recovery handles leftovers that could not be removed.

Unknown files and unregistered ZIP files are never deleted automatically.

## 11. Progress Model

Current progress is held in a singleton in-memory registry keyed by run identifier. It contains immutable snapshots with:

- Current phase.
- File and directory counts.
- Source and archive bytes.
- Current relative path.
- Compression or transfer throughput.
- Elapsed duration.
- Estimated remaining duration when stable enough to calculate.
- Cancellation availability.

Copy loops may produce frequent internal measurements, but UI publication is rate-limited to a small number of updates per second. Throughput uses a rolling time window rather than a lifetime average.

Blazor components subscribe to registry events and marshal updates onto their render context. Components always unsubscribe when disposed.

Progress samples are not written continuously to SQLite or Serilog. Durable phase changes and final aggregate statistics are persisted.

## 12. Filesystem and Path Rules

### 12.1 Source paths

- Must resolve to an attached local filesystem directory.
- Must not be UNC or a mapped network drive.
- Are normalized to canonical absolute paths.
- Are accessed by the service for preview and backup validation.

### 12.2 Destination paths

- Local destinations use canonical absolute directory paths.
- SMB destinations use UNC roots only.
- Mapped drive letters are rejected for SMB destinations.
- Job subfolders are normalized relative paths.
- Rooted subfolders and parent traversal are rejected.
- SMB server names and resolved endpoints are checked against the backup PC's names, loopback addresses, and local network-interface addresses. A UNC share hosted by the backup PC is rejected; users must configure its local filesystem path.

### 12.3 Overlap

The effective local destination must not equal or exist physically beneath any configured source. The staging directory must not exist beneath any source. Active and paused jobs cannot own the same effective destination folder.

Containment combines conservative case-insensitive lexical comparison with handle-resolved final physical paths for existing local directories. Treating case variants as equivalent is an intentional safety restriction even on a case-sensitive Windows directory. Validation is repeated at execution time rather than trusted only from configuration time.

### 12.4 Windows filesystem interop

`System.IO` remains the data-transfer API. A small isolated Windows interop component uses safe handles around `CreateFileW`, `GetFinalPathNameByHandleW`, and `GetFileInformationByHandleEx` to obtain final paths, volume identity, directory/file identity, and reparse information that public `System.IO` APIs do not expose completely.

Interop accepts only validated absolute paths, owns every native handle through `SafeFileHandle`, maps native errors to structured categories, and has Windows-specific tests. No general native filesystem abstraction is introduced.

## 13. SMB Authentication

### 13.1 Credential storage

Each SMB destination stores a username and DPAPI-protected password. Usernames may use accepted Windows forms such as `DOMAIN\user`, `NAS\user`, or a user principal name.

Passwords:

- Are protected using DPAPI machine scope before persistence.
- Are never returned to the browser after saving.
- Remain unchanged during editing unless a replacement is submitted.
- Are never included in logs, run history, exception messages, or email.
- Cannot be decrypted after moving the database to another Windows machine.

Application-data ACLs are the primary control preventing other local processes from obtaining the encrypted values. Machine entropy may provide context separation but is not treated as an independent secret when stored on the same machine.

### 13.2 Scoped access

SMB operations use Windows network-only logon credentials and execute through scoped impersonation. Local operations continue under the service identity.

The scope covers only destination testing, directory creation, transfer, validation, retention, inventory checks, and cleanup for that destination. Handles are closed before impersonation ends.

This avoids mapped drives and persistent session-wide SMB connections. Integration tests must validate the approach against representative Windows shares and the intended standalone NAS.

If a target NAS is incompatible, a deviceless `WNetAddConnection2` connection is the documented fallback. That fallback requires serialized connection management and explicit handling of Windows error 1219 credential conflicts.

### 13.3 Destination verification

Destination testing occurs in the service, not the browser process. A root-level test and a job-effective-folder test are distinguished. The latter may create the configured subfolder.

The test uses a cryptographically random temporary name and exclusive create semantics, writes random known bytes, flushes, reopens and compares those bytes, then deletes only that exact file. Cleanup failure is a distinct result. Native failures are mapped to unavailable storage, invalid path, denied access, and other safe categories. Capacity is queried only during destination management or explicit testing and is not monitored afterward.

Changing the root, username, or password invalidates the destination's verification fingerprint.

Every backup access updates a separate last-access result and timestamp used by the dashboard. A successful backup access does not silently replace the explicit verification fingerprint.

### 13.4 Destination and job mutations

Destination and job configuration mutations use serialized immediate transactions.

Path-affecting mutations, job or destination archiving, and destination tests first verify that no run is queued, executing, or completing post-commit work. They are rejected with a clear temporary-busy result otherwise. This global configuration gate is intentionally conservative for a small sequential system and prevents in-flight snapshots from reading or writing paths that have changed ownership.

- A destination cannot be archived while an active or paused job references it.
- Restoring a destination leaves it unverified.
- Changing destination access configuration pauses referencing jobs until explicit retest and reactivation.
- Changing a root or job subfolder recalculates overlap and active ownership keys before commit.
- A path change requires explicit confirmation and marks artifacts at old effective paths unmanaged.
- Paused and archived periods do not accrue catch-up occurrences.
- Job restore or reactivation advances its schedule watermark to the action time and schedules only a future occurrence.

### 13.5 Effective-folder ownership

Each active or paused job folder contains a small application ownership marker created with exclusive semantics. The marker contains the installation and job identifiers and is never placed inside the ZIP or any source folder.

Activation, reactivation, destination testing for a job, and manual queueing verify this marker. If another job owns it, the operation is rejected even when different UNC aliases or DFS paths reached the same folder. When a job releases a folder through archive or confirmed path change, the application removes only its own verified marker. Failure to remove it is reported and may require manual destination cleanup before reuse.

Directory identity remains an early collision check, while the marker is the authoritative alias-resistant ownership check at the destination.

### 13.6 Artifact inventory

Managed artifact inventory is reconciled after successful backup and retention work and when the user explicitly refreshes job storage details. The application checks only registered artifact paths and never deletes during inventory.

Confirmed retained counts, total bytes, latest size, and confirmation timestamp are persisted. Unavailable destinations leave the previous values marked with their last-confirmed time. A registered artifact absent during reconciliation becomes found missing. Unmanaged artifacts remain historical records with last-known size and do not contribute to current managed totals.

## 14. Notifications

Backup execution depends on an internal notification sender boundary, not directly on SMTP or Resend types.

```text
IRunNotificationSender
    SendTestAsync(...)
    SendRunResultAsync(...)
```

Only one provider will be implemented initially.

Provider-neutral eligibility and content rules are fixed:

- Successful, Successful with warnings, and Failed outcomes create outbox work.
- Cancelled outcomes never create notification work.
- The payload snapshots job identity, outcome, scheduled and actual times, duration, archive size and location, retention warnings, and safe problem details.
- Credentials and secret values are excluded before provider formatting.
- The full structured problem list remains in SQLite. Email includes at most the first 100 problems, states the total count, and directs the user to local run details.

### Option A: MailKit

- Direct SMTP delivery.
- SMTP host, port, transport security, username, and protected password.
- No third-party email API dependency.
- More configuration and more variation between mail servers.

### Option B: Resend

- HTTPS request through a typed `HttpClient`.
- Protected API key and verified sender identity.
- Simpler transport configuration.
- Requires internet access and a verified sending domain.
- Sends email content, including selected run diagnostics, through an external processor.

The provider decision must be made before implementing the notification milestone. The architecture does not include runtime provider switching or simultaneous provider support.

Notification delivery occurs through the durable outbox after the run outcome is durable. Startup begins records for which no attempt started. Failure updates notification status and creates a dashboard warning; it never changes the backup outcome.

The application makes at most one provider call per notification. It marks the attempt started before calling the provider. A crash during an uncertain provider call records Delivery unknown after restart and is not retried, preventing application-created duplicates at the cost of possibly losing that notification.

## 15. Logging and Diagnostics

Serilog writes structured rolling files under `ProgramData`.

- Daily rolling files.
- Thirty-day retention initially.
- Stable event identifiers for important operations.
- Reduced ASP.NET and SignalR request noise.
- Credentials and protected values excluded by construction.
- Startup logging available before normal dependency injection is complete.
- Final flush attempted during graceful shutdown.

SQLite structured run records are the source for user-visible diagnostics. Raw log files are not exposed through the UI or exported by the product.

High-frequency progress is neither logged nor persisted. Meaningful phase transitions, warnings, cleanup failures, and unexpected exceptions are logged.

## 16. Failure and Consistency Rules

- A run is claimed atomically before execution.
- One queue consumer and a durable job-execution guard prevent concurrent backup pipelines.
- Every externally visible pipeline phase has a preceding durable state transition.
- Finalization intent is durable before destination rename.
- Commit start closes cancellation before destination rename; recovery determines success from a valid finalized destination archive and otherwise fails the interrupted run.
- Retention deletion intent is durable and archive ownership is verified before deletion.
- Notification outbox work is committed with the final outcome.
- Database transactions never span filesystem or network operations.
- Recovery reconciles every durable filesystem and notification intent after interruption.
- Previous retained backups are never rollback targets for a failed new run.

The database and filesystem cannot participate in one atomic transaction. The design therefore uses explicit intermediate states, idempotent recovery, unique run identifiers, temporary filenames, and safe ordering.

## 17. Testing Strategy

### 17.1 Unit tests

- Schedule calculation, including daylight-saving transitions.
- Catch-up collapsing and duplicate occurrence identity.
- Path normalization, containment, and overlap.
- Archive naming and ZIP entry path generation.
- Retention selection.
- Run outcome classification.
- Progress and throughput calculations.
- Notification model construction and redaction.

`Microsoft.Extensions.TimeProvider.Testing` supplies deterministic time. Standard xUnit assertions are sufficient.

### 17.2 Integration tests

- EF Core migrations against real temporary SQLite databases.
- Startup recovery from every durable pipeline phase.
- Source scanning against real temporary trees.
- ZIP creation, empty directories, hidden files, cancellation, and validation.
- Local transfer, finalization, collision handling, and retention.
- Locked and inaccessible file behavior on Windows.
- DPAPI protection and decryption.
- Blazor component behavior with bUnit for critical forms and status components.
- Manual-run and scheduled-occurrence coalescing under races.
- Durable queue ordering and tie-breaking after restart.
- Cancellation during every pre-commit phase and rejection after commit begins.
- Crash recovery on both sides of final rename and retention deletion.
- Destination-root mutation, job pausing, collision checks, and unmanaged-artifact transitions.
- Archive ownership mismatch, external replacement, and retention refusal.
- Source and destination physical aliases through reparse points.
- Windows final-path and filesystem-identity interop, including handle cleanup and native error mapping.
- Rejection of UNC destinations hosted by the backup PC through names, aliases, and local addresses.
- Notification outbox crash windows and possible duplicate delivery.
- Progressive source preview cancellation and no-write verification.
- Managed-storage reconciliation, stale totals, and externally missing artifacts.

### 17.3 Environment tests

- SMB impersonation against a Windows share.
- SMB impersonation against the intended NAS.
- Wrong credentials, unavailable server, permission denial, disconnect during transfer, and cleanup.
- Windows service startup without an interactive user.
- Installer install, upgrade, repair, and uninstall in a clean Windows VM.
- Database migration during an installed upgrade.
- Service crash and recovery during each backup phase.
- Service-versus-console global mutex contention across Windows sessions.
- Port conflict between installer selection and service startup.

Environment tests may be manual or run in a dedicated Windows test environment; they are not expected to run in every local unit-test pass.

## 18. Installer Design

Inno Setup is responsible for:

- Installing the self-contained publish output under `Program Files`.
- Creating and securing `ProgramData` directories.
- Selecting and writing the loopback port.
- Installing the `LocalSystem` Windows service.
- Configuring automatic startup and recovery.
- Stopping and restarting the service during upgrade.
- Creating the start-menu shortcut.
- Preserving application data by default.
- Asking explicitly before deleting application data during uninstall.
- Waiting for service readiness and verifying the localhost URL after install or upgrade.
- Reporting service, migration, and Kestrel binding failures with a path to local diagnostics.
- Providing a repair action that can select and write a new loopback port when the current port prevents UI startup.

The installer does not create mapped drives, source ACLs, destination credentials, firewall rules, or TLS certificates.

Schema migration is performed by the application during startup, not by custom installer SQL.

## 19. Update Policy

Automatic update and release polling are not included because no accepted use case requires them. Upgrades are performed by running a newer signed `setup.exe`.

Code signing is strongly recommended for released installer and executable artifacts, but certificate acquisition and release infrastructure are outside the application architecture.

## 20. Accepted Tradeoffs

- Blazor Interactive Server is appropriate because the UI is localhost-only and the service is always running.
- MudBlazor reduces UI implementation time at the cost of a pinned third-party component dependency.
- EF Core adds binary size but supplies typed persistence and migrations.
- Local staging requires temporary disk space but enables separate compression and transfer progress and safer destination publication.
- No VSS means backup consistency depends on source files being unused; detected access or change problems fail the run.
- SQLite and an in-process queue fit one service instance and the expected workload.
- DPAPI machine scope protects secrets at rest but does not defend against local administrators.
- Destination validation checks ZIP structure and expected entries but does not perform a complete decompression and CRC readback.

## 21. Open Technical Decision

The initial notification provider remains unresolved:

- MailKit over SMTP, or
- Resend over HTTPS.

This choice does not block implementation of the persistence, scheduler, backup, destination, or UI foundations.
