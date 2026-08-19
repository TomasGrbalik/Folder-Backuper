# Milestone 9: Notifications

The selected provider is **Resend over HTTPS**. Choosing it explicitly accepts the two documented
costs: a verified sending domain is required, and the run details carried in an email — job names,
folder paths, and error messages — are processed by an external service. The settings page states
both where the user turns notifications on, not only here.

No automated test sends a real email. Provider responses are scripted through a fake
`HttpMessageHandler`, and outbox tests use a fake `IRunNotificationSender`.

## Automated checks

- The provider-neutral model (`NotificationPayload`, `NotificationProblem`) has no member that can
  hold a credential, username, protected blob, or verification fingerprint, so no formatting path can
  leak one. Asserted by reflection, and separately by serializing an SMB run and confirming the
  destination username is absent.
- The payload snapshots job identity, outcome, source, destination name and effective path, archive
  name and size, scheduled/started/completed times, duration, time zone, retention-warning count,
  total problem count, and the error summary.
- Email carries at most the first 100 problems, states the true total, and directs the user to local
  run details. Errors are ordered before warnings so truncation keeps the actionable entries.
- Success, warning, and failure templates render every accepted field in both an HTML body and a
  plain-text alternative. Every interpolated value is HTML-encoded, so a path containing `<` or `&`
  cannot corrupt the markup or inject elements. The HTML references no remote image, script, or URL.
- Resend responses are classified so that a single attempt is never ambiguous: `2xx` is delivered;
  any `4xx` including `429` is a refusal, because nothing was accepted; `5xx`, a timeout, and a
  mid-flight connection loss are delivery-unknown; a connection that was never established is a
  refusal. A `TaskCanceledException` from shutdown propagates instead of being reported as a result.
- The API key is sent as a bearer token, is passed per call rather than captured, and never appears
  in a result message, a log line, or the persisted error — including when the provider echoes it back
  in an error body, which is redacted. Verbose provider errors are truncated to fit the stored column.
- The API key is protected with DPAPI through the existing `ISecretProtector`. The settings view has
  no member that can carry it, a blank key field keeps the stored key, and a supplied key replaces it.
- Turning notifications on requires a key, a sender address, and at least one recipient; turning them
  off stays possible even when the saved configuration is incomplete. Recipients are validated as
  plain addresses, deduplicated case-insensitively, and capped. Unreadable stored settings degrade to
  "not configured" rather than breaking the settings page.
- Terminal run persistence and outbox insertion happen in one transaction through the single
  `RunPersistenceService.CompleteAsync` choke point that all six completion paths use. The worker is
  signalled only after that transaction commits.
- Successful, successful-with-warnings, and failed outcomes each create exactly one outbox row.
  Cancelled runs create none — enforced in code and independently by the SQLite check constraint
  `CK_NotificationOutbox_NotCancelled`. An unconfigured or disabled installation creates none either,
  leaving the notification state null, which renders as "Not sent" rather than permanently pending.
- Claiming durably marks a record Sending and begins its single attempt; the guarded `UPDATE` makes
  the claim atomic. A record is attempted exactly once, and pending records are attempted oldest
  first.
- A crash before the claim leaves work pending, and startup attempts it. A record left Sending
  becomes delivery-unknown at startup and is never retried; a sender that throws on any call proves
  the provider is not contacted during recovery.
- A delivery failure never changes a backup outcome: the run stays successful while the notification
  is recorded as failed or delivery-unknown, with the result mirrored onto the run for display.
- An unexpected provider fault is recorded as delivery-unknown rather than a clean failure, and never
  propagates out of the sweep. Recovery runs inside the worker, not startup initialization, so no
  notification problem can stop the service.
- Delivered, failed, and delivery-unknown results appear on the dashboard, in permanent history, and
  in run details. The settings page never renders a saved API key, disables the test button until a
  deliverable configuration is saved, and reports an uncertain test as a warning rather than an error.

## Manual checks

- On `/settings`, save a Resend API key, a verified sender address, and one recipient; confirm the
  key is not displayed afterwards, the field offers an optional replacement, and the status reads
  Ready to send.
- Send a test email and confirm it arrives at every configured recipient and that the dialog reports
  the provider result. Confirm the test uses the saved configuration by editing a field without
  saving and observing that the test is refused until the change is saved.
- Run a job to success and confirm one email arrives with the correct job, outcome, times, duration,
  archive size, and destination, and that the dashboard, history, and run details all show Delivered.
- Force warnings (lock a source file) and confirm the warning email lists the problems; force a
  failure (make the destination unavailable) and confirm the failure email names the actionable error.
- Produce a run with more than 100 problems and confirm the email lists 100, states the true total,
  and points to local run details.
- Cancel a run and confirm no email is sent and no notification state appears for it.
- Replace the key with an invalid one, run a job, and confirm the backup still succeeds while the
  dashboard shows an unresolved notification result and run details show the safe provider error.
- Disconnect the internet, run a job, and confirm the notification is recorded without affecting the
  backup outcome.
- Stop the service while a notification is pending and confirm it is sent after restart. Stop it
  during a delivery attempt and confirm the result becomes Delivery unknown with no second email.
- Inspect `logs\folder-backuper-*.log` and confirm no API key and no full problem list appear.
- Verify the settings page and the test dialog at a desktop width and a narrow width.
