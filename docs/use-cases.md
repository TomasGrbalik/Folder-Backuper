# Folder Backuper: Use Cases and Product Requirements

Status: Draft for review

## 1. Purpose

Folder Backuper protects important data on an always-on Windows PC by creating scheduled ZIP backups of local folders and storing them on local or SMB storage.

This document defines user needs and expected product behavior. Implementation and technology choices are intentionally deferred to later design documents.

## 2. User and Environment

The initial user is a small accounting company with:

- One always-on Windows PC containing important company data.
- A 64-bit x86 Windows installation for the initial release.
- One person using and administering that PC.
- A closed local network.
- Local storage or an external SMB share available as backup storage.
- An overnight period of several hours when source files are expected to be unused.

Expected operating scale:

- 3-4 backup jobs.
- Approximately 10 GB of source data across all jobs.
- Thousands of files.
- Individual files up to approximately 15 MB.
- Backups running sequentially within an overnight window.

The user should not need command-line knowledge to install, configure, or monitor the application.

## 3. Product Principles

### 3.1 Source data is strictly read-only

The application must never create, modify, rename, delete, restore, or intentionally change metadata or permissions in a configured source folder.

Temporary data, tests, cancellation cleanup, and retention cleanup must operate outside source folders. Restoring data is not a function of this product.

### 3.2 A successful backup is complete

A run is not successful if an ordinary source file cannot be included. Partial archives must not be published as valid backups.

### 3.3 Existing backups are protected

A failed or cancelled run must not damage previous successful backups. Retention cleanup occurs only after a new backup has completed successfully.

### 3.4 Status must be understandable

The dashboard, calendar, run history, and email notifications must agree about what happened. Errors must identify corrective action where possible.

## 4. Scope

The initial product includes:

- Installation and operation as an automatically started Windows background service.
- A localhost web interface for configuration and monitoring.
- Multiple independent backup jobs.
- Reusable local filesystem and SMB destinations.
- ZIP archive creation.
- Scheduled and manual backup execution.
- Sequential job execution.
- Count-based retention.
- Permanent run history.
- Calendar and agenda views.
- Global email notifications.
- Live backup progress and transfer statistics.

## 5. Explicit Non-Goals

The initial product does not include:

- Restoring or writing data to source folders.
- Archive encryption or password protection.
- Include or exclude patterns.
- Network folders as backup sources.
- Automatic retries after failed runs.
- Week or day calendar views.
- Per-job email recipient lists.
- Application authentication while the UI remains localhost-only.
- Remote network access to the web interface.
- Cloud, SFTP, or other remote destination types.
- Linux or macOS support.
- A system tray interface.
- Automatic application updates.
- A raw diagnostic-log viewer or log export workflow.

## 6. Core Concepts

### 6.1 Backup job

Each backup job has:

- A unique, recognizable name.
- Exactly one source folder on a local disk attached to the backup PC.
- One reusable destination.
- An optional subfolder beneath the destination root.
- Its own schedule.
- A `keep latest X backups` retention value, where `X` is at least 1.
- An active, paused, or archived lifecycle state.

Each job run creates exactly one ZIP archive. Multiple jobs operate independently and may use the same reusable destination, but active jobs must not use the same destination subfolder.

### 6.2 Destination

A reusable destination has:

- A unique, recognizable name.
- A type: local filesystem or SMB.
- A root folder.
- Any access information required to use it.
- A verification state and the time and result of its last access test.

Examples:

- Local: `D:\Backups`
- SMB: `\\nas\company-backups`

Each job may specify a relative subfolder, such as `Accounting`. A blank subfolder means the destination root. The effective destination must remain beneath the configured root and must not be the source folder or a descendant of it.

An SMB destination hosted by the backup PC itself is not supported; the equivalent local filesystem path must be configured instead. This prevents a local source from being reached through a UNC alias.

The application may create and manage job subfolders on destination storage. It must never use destination testing as a reason to write to source storage.

### 6.3 Run

A run is one scheduled or manually requested attempt to back up a job. A scheduled run receives one attempt only; failures are not retried automatically.

Non-terminal run states are:

- Planned
- Queued
- Scanning
- Compressing
- Transferring
- Finalizing

Terminal run outcomes are:

- Successful
- Successful with warnings
- Failed
- Cancelled

An interruption before the destination archive receives its final name is recorded as Failed unless the user explicitly cancelled it. If the final rename completed before interruption, startup recovery validates the archive and preserves the successful backup outcome.

## 7. Installation and Lifecycle

### UC-01: Install the application

The administrator runs `setup.exe`. Installation must:

- Install the background service.
- Configure it to start automatically with Windows.
- Start it without requiring a user to remain logged in.
- Make the localhost web interface available.
- Provide a clear link or route to open the interface.
- Clearly report installation or service-start failures.

### UC-02: Upgrade the application

An upgrade must preserve jobs, destinations, notification settings, history, and other application data.

### UC-03: Uninstall the application

Uninstallation must ask whether application data should be retained or removed. Removing all data through uninstall is an explicit administrative action and is separate from normal application behavior.

## 8. Destination Management

### UC-04: Manage a destination

The user can configure a named local or SMB destination. Saving a destination does not require it to be currently available, but it remains marked Unverified until it passes an access test.

Changing a destination's path or access information resets its verification state. Active jobs using that destination become paused until the changed destination passes a new access test and the user reactivates them. A path change requires explicit confirmation because previously managed archives at the old path become unmanaged and are never deleted by retention.

The user can archive a destination only when no active or paused job references it. Archiving a destination:

- Removes it from the normal active-destination list.
- Preserves its configuration and historical references.
- Never deletes folders or backup archives from its storage.
- Does not erase job or run history.

An archived destination can be restored. It must pass a new access test before an active job can use it.

### UC-05: Test destination access

The user can run an explicit access test. The test must use the same effective access as scheduled backups and must verify that the application can:

- Reach the destination.
- Create a temporary test file.
- Write and read that file.
- Delete the test file.
- Create the configured job subfolder when applicable.

The result must distinguish unavailable storage, invalid paths, denied access, and cleanup failures. During destination management or an explicit access test, the application attempts to display available capacity when the destination reports it.

Destination capacity is not monitored outside destination management. The product does not provide capacity thresholds, predictive free-space warnings, or continuous capacity checks.

A paused job cannot be activated until its destination has passed at least one access test.

## 9. Job Management

### UC-06: Create or edit a job

Job configuration uses one full-page form rather than a step-by-step wizard. The form contains sections for:

- General details.
- Source folder.
- Destination and subfolder.
- Schedule.
- Retention.
- Current storage usage when available.
- Validation results.

Focused tasks may open in modals, including source browsing and preview, destination creation or editing, destination testing, and detailed validation results.

The page must provide inline validation and warn about unsaved changes when the user navigates away. Editing a job does not rewrite historical run records.

Path-affecting job and destination changes, archiving, and destination tests are temporarily unavailable while any backup is queued, running, or completing post-commit work. This prevents a run from continuing against configuration that has become unsafe.

### UC-07: Select and preview a source folder

The user can browse local disks attached to the backup PC and select exactly one folder. The preview is strictly read-only and shows:

- The directory tree.
- File names, sizes, and last-modified times.
- Total file count.
- Total folder count.
- Estimated source size.
- Files or directories that cannot currently be inspected.

Large previews load progressively. The interface must explain that preview results are informational and that the source is checked again when a backup runs.

The backup includes all ordinary files recursively, including hidden and system files, and preserves empty directories. Junctions, symbolic links, mount points, and other reparse points are never followed. Each skipped reparse point is reported, and an otherwise complete run is Successful with warnings.

### UC-08: Configure a job destination

The user selects one reusable destination and specifies an optional relative subfolder. The interface suggests a subfolder based on the job name but allows editing it.

The application must prevent path traversal outside the destination root. It must also prevent active jobs from sharing the same effective destination folder because their retention policies could conflict.

The application may place a small ownership marker in each managed job folder to detect two configured paths or SMB aliases that resolve to the same physical folder. This marker exists only on backup storage and is never placed in source data.

Changing a job's destination or subfolder requires explicit confirmation when retained archives exist. Existing archives remain at their original locations, become unmanaged, stop counting toward retention and current managed storage totals, and are never deleted automatically. Their paths and last-known sizes remain permanently visible in history.

### UC-08A: Pause and reactivate a job

Pausing a job stops future scheduling without hiding the job or changing its history. No missed runs accumulate while paused. A paused job can be edited, tested, run manually when its configuration and destination verification are valid, and reactivated. Reactivation schedules only the next future occurrence. Paused jobs continue reserving their effective destination folders against use by other jobs.

Jobs are paused automatically when a referenced destination change invalidates its verification. The user must successfully test the destination and explicitly reactivate affected jobs.

### UC-09: Archive and restore a job

Archiving a job:

- Disables future scheduling.
- Removes it from the normal active-job list.
- Preserves its configuration and permanent history.
- Leaves its existing backup archives untouched.
- Does not accumulate missed runs.

A running job must finish or be cancelled before it can be archived.

Restoring an archived job returns it to active management and never creates catch-up runs for the archived period. It becomes active and schedules only its next future occurrence when its configuration and destination verification remain valid; otherwise it returns paused until corrected and explicitly reactivated.

## 10. Scheduling

### UC-10: Configure a schedule

Each job can run at most once per day. The user selects:

- One or more days of the week.
- One local run time.

Selecting every day creates a daily schedule. All jobs use the Windows PC's current local time zone; there is no per-job time-zone setting. Before saving, the interface displays the selected time, current time zone, and calculated next run.

Each scheduled occurrence has a unique identity so that clock changes, service restarts, and other reevaluation cannot cause it to run twice.

Schedule changes apply immediately after saving, affect only future occurrences, and never create catch-up work from the previous schedule.

### UC-11: Handle a missed schedule

If the PC or service was unavailable at a scheduled time:

- The job runs once as soon as possible after the service starts.
- Multiple missed occurrences collapse into one catch-up run per job.
- A catch-up run must not duplicate a run already completed for that scheduled occurrence.
- Multiple jobs requiring catch-up are queued and executed sequentially.
- A catch-up run does not replace the next regular occurrence.

Clock changes use the same missed-run and duplicate-prevention rules:

- When daylight-saving time advances past a nonexistent scheduled time, the job runs once immediately after the clock advances.
- When daylight-saving time repeats a scheduled time, the job runs only at its first occurrence.
- Moving the system clock or time zone forward past one or more occurrences creates one catch-up run per job.
- Moving the clock backward does not repeat an occurrence that already ran.
- Future planned calendar entries are recalculated using the new local time and time zone.

## 11. Backup Execution

### UC-12: Run a scheduled backup

At the scheduled time, an enabled job is queued. Only one job runs at a time; other jobs remain queued in due-time order. A long-running job does not cause later due jobs to be discarded.

A run proceeds through these user-visible phases:

1. Scan the source and identify the work.
2. Create a temporary ZIP archive outside the source folder.
3. Transfer the completed archive to a temporary destination name.
4. Validate the destination archive.
5. Give it its final name.
6. Apply retention.
7. Record and notify the final outcome.

The archive uses a readable name containing the job name and run timestamp, for example:

`Accounting_2026-08-17_23-00-00_a1b2c3d4.zip`

The short run identifier prevents collisions while keeping the name readable.

The ZIP includes the source folder as its top-level directory:

```text
Accounting_2026-08-17_23-00-00_a1b2c3d4.zip
`-- Accounting/
    `-- ...
```

### UC-13: Validate a completed backup

A run can become successful only after:

- The local ZIP is structurally valid.
- The destination file is fully written.
- Its size matches the source archive.
- The ZIP can be opened from the destination and its entry list can be read.
- The temporary destination file has been finalized under its intended name.

Full byte-for-byte readback verification is not required in the initial product.

### UC-14: Run a backup manually

The user can select Run now for an active job or a valid paused job. A manual run:

- Enters the same sequential queue as scheduled runs.
- Uses the same validation and retention behavior.
- Does not change the regular schedule.
- Does not retry automatically after failure.

The same job cannot execute more than once concurrently. If that job is already queued or running when its scheduled time arrives, the existing execution satisfies that occurrence instead of creating a duplicate run.

### UC-15: Cancel a backup

The user can cancel a queued or running backup. Cancellation must:

- Stop work as soon as safely possible.
- Remove application-owned incomplete output where possible.
- Preserve all previous successful backups.
- Record the attempt as Cancelled.
- Not trigger retention.
- Not send an email notification.

Cancellation is available through scanning, compression, and transfer. It becomes unavailable when the finalization commit sequence begins. After the destination ZIP receives its final name, the backup is committed as successful and retention is recoverable post-commit housekeeping rather than cancellable backup work.

## 12. Progress and Monitoring

### UC-16: Monitor an active backup

The active-run view shows:

- Current job and source folder.
- Current phase.
- A progress bar after scanning establishes total work.
- Current file where appropriate.
- Files and source bytes processed.
- Compression speed and current archive size.
- Transfer bytes and transfer speed, labelled Copying for local destinations and Uploading for SMB destinations.
- Elapsed time and estimated time remaining where meaningful.
- Queued jobs and queue order.
- A Cancel action.

Progress may be indeterminate during initial scanning.

### UC-17: Review overall health

The main dashboard shows:

- The active backup and its progress.
- Queued jobs.
- Each active job's last run outcome.
- Each active job's last successful backup.
- Each active job's next planned run.
- Prominent failures and warnings.
- Notification delivery failures.
- Each destination's last connection-test result and time.
- Quick access to Run now, jobs, destinations, calendar, history, and settings.

Destination status reflects the last explicit test or backup attempt. Continuous destination probing is not required.

### UC-18: Review storage consumption

Each job summary shows:

- Total destination space consumed by its currently retained archives.
- Number of retained archives and configured retention count.
- Size of its latest backup.
- Time at which the storage figure was last confirmed.

For example:

```text
Accounting Data
68.4 GB across 7 of 7 retained backups
Latest backup: 9.7 GB
```

The total excludes temporary, failed, unrelated, and unmanaged files. It is updated after successful backup and retention operations. When a destination is unavailable, the last known value and timestamp remain visible. Archives removed outside the application are flagged when discovered. Archives made unmanaged by a destination or subfolder change remain visible separately in permanent history with their last-known sizes.

Archived jobs retain storage information in their details. Destination-wide capacity is shown only while managing or explicitly testing a destination.

## 13. Calendar and History

### UC-19: Review the calendar

The calendar provides:

- A month view.
- An agenda/list view.
- Past run attempts and future planned runs together.
- Clearly differentiated states and outcomes.
- Filters by job and status.
- Full details when an entry is selected.

Week and day views are not required.

### UC-20: Review permanent run history

Run history is permanent and cannot be cleared through the UI or API. It remains after jobs are edited or archived and after archives are removed by retention.

Each historical record includes:

- Job identity and the relevant configuration at execution time.
- Scheduled time, actual start time, end time, and duration.
- Trigger type: scheduled, catch-up, or manual.
- Final outcome and phase reached.
- Archive name, location, and size when one was completed.
- Whether the archive is still retained, was removed by retention, or was found missing.
- Whether an archive became unmanaged after a destination or subfolder change.
- Problematic files, warnings, and errors.
- Notification delivery result.

Job history cannot be silently erased by job lifecycle actions.
Structured run details and problematic-file lists are the user-facing diagnostic interface. Raw application logs are not exposed through the UI or an export action.

## 14. Retention

### UC-21: Apply count-based retention

After a new backup completes and is validated successfully, the application deletes the job's oldest currently managed completed archives until no more than its configured `X` managed archives remain. Unmanaged historical archives do not count toward this limit.

Retention must:

- Count only completed archives belonging to that job.
- Never delete unrelated files.
- Never run after a failed or cancelled backup.
- Preserve permanent run history for deleted archives.
- Leave the newest successful archive intact.

If the new backup succeeds but an old archive cannot be deleted, the run outcome is Successful with warnings. The warning identifies the affected file and leaves the valid new backup available.

Archived jobs do not perform scheduled retention cleanup because they do not run. Their existing archives remain untouched.

## 15. Failure Handling

### UC-22: Handle inaccessible source content

If an ordinary file or directory cannot be read:

- The run fails.
- No partial archive is published as a valid backup.
- Previous backups remain untouched.
- The application continues checking other source content where practical to collect a useful list of problems.
- Run details and the failure email list discovered problematic paths and their errors.

The product cannot guarantee a list of children beneath a directory that cannot itself be enumerated.

### UC-23: Handle unavailable or insufficient storage

If local working space or destination storage is unavailable, inaccessible, or insufficient:

- The run fails without publishing a partial backup.
- The error distinguishes the affected storage and reason where possible.
- Application-owned temporary output is cleaned up where possible.
- Previous successful backups remain untouched.

### UC-24: Handle interruption

If the service or PC stops during a run:

- Before the final destination rename, previous successful backups remain untouched.
- Incomplete application-owned files are cleaned up on the next startup where possible.
- A run interrupted before the final destination rename is finalized as Failed with an interruption reason.
- If the final rename completed before interruption, startup recovery validates the archive, preserves the successful backup, resumes or reconciles retention, and records any cleanup warning.
- Any required result notification whose delivery attempt had not started is attempted after service recovery. An attempt interrupted in an uncertain provider call is not repeated.
- The interrupted run is not retried automatically.

## 16. Email Notifications

### UC-25: Configure notifications

The application has one global email configuration and one shared list of one or more recipients for all jobs. The user can send a test email.

### UC-26: Receive a run notification

The application makes one persisted email-delivery attempt after a run becomes:

- Successful
- Successful with warnings
- Failed

User-cancelled runs do not send email.

Durably claiming a pending notification is defined as beginning its single delivery attempt. If the application stops before that claim, the pending notification is attempted after startup. If it stops after the claim, including immediately before or during the provider call, that attempt is not repeated; avoiding duplicate email is preferred over recovering the uncertain delivery.

A notification includes, where applicable:

- Job name and outcome.
- Scheduled and actual times.
- Duration.
- Archive size and destination.
- Problematic files and actionable errors.
- Retention warnings.

When more than 100 problems exist, the email includes the total count and first 100 problems. The complete structured list remains available in local run details.

Email delivery failure does not change a completed backup into a failed backup. It is recorded separately and displayed prominently in the UI and run details.

## 17. Local Web Interface

The initial web interface is accessible only from the backup PC through localhost. Because the environment has one local user, application-level authentication and authorization are not required.

Binding the interface to the wider network is deferred. Remote access must not be enabled without revisiting authentication, authorization, transport security, and exposure of local filesystem information.

The interface is available in English and Slovak. A language control in the application bar, mirrored by a
setting on the settings page, selects the interface language, and the choice is stored with the rest of the
application data so that it survives a service restart and an upgrade. An installation that has never been
given a language follows the Windows installed interface language, choosing Slovak only when Windows itself
is Slovak.

The selected language also determines how dates, times, numbers, and file sizes are displayed, so a Slovak
interface never mixes Slovak labels with English weekday names or English decimal separators. Archive
filenames remain in a fixed, locale-independent, sortable timestamp format regardless of the language.

Operator-facing text outside the web interface stays English: Windows event-log entries, installer console
output, and the application log. Those are read by whoever is diagnosing the machine rather than by the
person whose folders are being backed up, and keeping them in one language keeps them searchable.

## 18. Acceptance Summary

The initial product satisfies its core purpose when a user can:

1. Install it and leave it operating without an interactive login.
2. Configure and test reusable local or SMB destinations.
3. Create multiple jobs, each with one local source folder, schedule, destination subfolder, and retention count.
4. Preview source contents without any application write to the source.
5. Receive complete, validated ZIP backups on the selected destination.
6. Monitor live progress and understand queued work.
7. See past and planned activity in month and agenda views.
8. Receive clear email results for successful, warning, and failed runs.
9. Confirm how much destination storage each job consumes.
10. Trust that failures, cancellation, retention, and job archiving do not endanger live files or previous successful backups.
