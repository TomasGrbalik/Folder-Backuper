# Milestone 7: Scheduler And Queue Semantics

## Automated checks

- Active jobs persist an evaluation watermark and the next resolved occurrence with local-date and time-zone context.
- Scheduled occurrence identity remains unique by job, schedule revision, and local date.
- Duplicate scheduler evaluation does not duplicate a run or occurrence.
- Multiple missed occurrences collapse to the latest catch-up while preserving the next regular occurrence.
- Manual enqueue and scheduler evaluation races create one execution; a due occurrence is associated with the manual run when it wins the race.
- Queue claims order durable rows by due instant, queue instant, and run identifier.
- Due ordering and occurrence state remain in SQLite and therefore survive process restart.
- Paused and archived jobs are excluded from evaluation, and reactivation or active restoration starts at its new effective boundary.
- Forward clock and time-zone changes use catch-up and occurrence uniqueness; backward changes and repeated daylight-saving times cannot recreate an occurrence key.
- The calendar query uses the production occurrence calculator and excludes inactive jobs.
- The scheduler and queue consumer await the same successful startup-recovery barrier.

## Manual checks

- Stop the service before a scheduled time, restart it afterward, and confirm exactly one catch-up is queued.
- Queue several jobs with different due times, restart the service, and confirm due-time order is retained.
- Queue a manual run immediately before its scheduled occurrence and confirm only one run executes.
- Pause a job across several scheduled dates, reactivate it, and confirm its first planned occurrence is in the future.
- Move the Windows clock and time zone forward and backward and confirm no logical occurrence executes twice.
