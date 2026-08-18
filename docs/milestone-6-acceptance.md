# Milestone 6: Durable execution state

This first slice defines the durable state needed before queue and recovery services are added.

- Each run snapshots `DestinationId` and records the exact local staging and destination-partial paths when those paths exist.
- SQLite provides filtered indexes for active-run uniqueness, queue-oriented lookup, and recovery lookup.
- The database rejects more than one non-terminal queued or executing run for the same job.
- Existing run transition rules continue to prevent cancellation after final commit and invalid terminal outcomes.

Queue services, the execution coordinator, cancellation orchestration, and crash recovery remain subsequent slices.
