# Milestone 6: Durable Execution And Recovery

## Automated checks

- Racing manual enqueue requests produce one non-terminal run per job.
- Queue claims use SQLite ordering and queued cancellation performs no filesystem work.
- The finalization transaction records a pending owned artifact before rename, closes cancellation, and records the completed rename afterward.
- Retention begins with a deletion intent, verifies archive ownership and size, and does not delete an unproven file.
- Startup recovery removes only recorded pre-commit paths and marks interrupted work failed.
- Run now is available for non-archived jobs; cancellation is exposed through the execution application service.

## Manual checks

- Repeat cancellation during scanning, compression, transfer, and immediately before final commit.
- Stop the service before and after rename and confirm recovery preserves a renamed archive.
- Run the retention and recovery matrix against the intended NAS, including an ownership-mismatched replacement ZIP.
- Record representative 10 GB local-staging and intended-NAS results, including cleanup behavior and throughput.
