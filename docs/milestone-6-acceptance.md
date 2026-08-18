# Milestone 6: Durable Execution And Recovery

## Automated checks

- Racing manual enqueue requests produce one non-terminal run per job.
- Independent queue claimers use a conditional SQLite update, return a run only once, and queued cancellation performs no filesystem work.
- The finalization transaction records a pending owned artifact before rename, closes cancellation, and records the completed rename afterward.
- Staging and destination-partial intents are durable before file creation, and execution uses the immutable run snapshot with current protected credentials.
- Recovery validates length, ZIP ownership, creation metadata, filesystem identity, physical containment, and destination ownership before adopting or deleting a file.
- Retention begins with a deletion intent, runs inside the destination adapter scope, reconciles interrupted deletion, and persists ownership-refusal warnings.
- Startup recovery preserves a valid renamed archive, refuses same-length replacements, leaves inaccessible finalization pending, and removes only proven run-owned temporary files.
- User cancellation survives the claim/register race; service interruption remains non-terminal for startup recovery.
- Legacy duplicate active runs are reconciled deterministically before the unique execution guard is created.
- Explicit fault points cover staging, transfer, commit intent, rename, and final-commit durability boundaries.
- Run now is available for non-archived jobs; cancellation is exposed through the execution application service.

## Manual checks

- Repeat cancellation during scanning, compression, transfer, and immediately before final commit.
- Stop the service before and after rename and confirm recovery preserves a renamed archive.
- Run the retention and recovery matrix against the intended NAS, including an ownership-mismatched replacement ZIP.
- Record representative 10 GB local-staging and intended-NAS results, including cleanup behavior and throughput.
