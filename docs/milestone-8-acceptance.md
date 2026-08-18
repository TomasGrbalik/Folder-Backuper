# Milestone 8: Monitoring And History UI

## Automated checks

- The dashboard, run history, run details, calendar, and agenda derive every field from durable SQLite state through dedicated read services (`DashboardQueryService`, `RunQueryService`, `CalendarEntryService`), so views agree with the database after browser reconnect and service restart.
- The active-run view subscribes to the in-memory `BackupProgressRegistry` and unsubscribes on dispose; progress is never written continuously to SQLite or logs.
- The active-run transfer is labelled Copying for local destinations and Uploading for SMB destinations.
- The progress bar is indeterminate during scanning and becomes determinate once total work is known.
- Run history is paged and filterable by job and status, ordered newest first, and remains read-only (no clear or delete path).
- Run details expose configuration at execution time, scheduled/started/completed times and duration, trigger, outcome, phase reached, archive name/location/size, artifact state (retained, removed by retention, found missing, or unmanaged), and notification result.
- Run-problem lists are server-paged so a run with thousands of problems stays usable.
- The calendar unions durable past run attempts with future planned occurrences from the shared production occurrence calculator; past and planned entries use one consistent status/color mapper.
- Only a month view and an agenda view are exposed; no week or day view exists.
- Explicit storage refresh reconciles retained artifacts: present owned archives stay retained and totals refresh; a deleted or ownership-mismatched archive is marked found-missing and drops from managed totals; an unavailable destination preserves the last-confirmed totals and timestamp untouched.
- Dashboard job cards show managed total, retained count versus configured count, latest size, last-confirmed time, a derived stale indicator, missing-archive count, and unmanaged-archive count separately.
- No password value and no raw-log viewer or export action appear in any monitoring page.
- Dates, times, numbers, and sizes render through the shared `DisplayFormat` helper using the current regional culture and local time; archive file names are shown verbatim in their invariant format.

## Manual checks

- Create a job, trigger Run now, and confirm the dashboard active-run view shows job, source, phase, current file, files/bytes processed, compression speed, archive size, transfer bytes and speed with the correct Copying/Uploading label, elapsed time, estimate, and a working Cancel.
- Reload the browser mid-run and confirm the view reconnects and continues to reflect live progress; restart the service mid-run and confirm the dashboard and history agree with SQLite afterward.
- After a run completes, confirm it appears in History with correct status, that opening it shows structured details and the problem list, and that the calendar shows the past entry plus future planned runs.
- Delete a retained archive on disk, run Refresh storage for that job, and confirm the archive is flagged found-missing and the managed total updates; disconnect the destination and confirm Refresh storage leaves the last-confirmed figures unchanged.
- Confirm destination capacity is shown only in destination management and explicit testing, never on the dashboard or history.
- Verify every primary page and modal on a desktop width and a narrow (mobile) width.
