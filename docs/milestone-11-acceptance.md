# Milestone 11: Versioning, Release Automation, And Update Notification

Installer and upgrade behavior is verified in the [Milestone 10 acceptance checklist](milestone-10-acceptance.md) and is not re-verified here. Confirm only that an upgrade preserves the update-check preference along with the rest of the application data.

## Automated checks

- The version file holds exactly one `VersionPrefix` and one `VersionSuffix`, the prefix is three numeric parts, and `AssemblyVersion` and `FileVersion` stay derived from the prefix alone so that they remain numeric for Inno Setup.
- The running assembly reports the version the file declares, including its suffix.
- The installer script keeps `VersionInfoVersion` on the numeric version, uses the display label for `AppVersion` and the output file name, and still compiles when the label is not supplied.
- A version is parsed from three numeric parts with an optional pre-release suffix, and build metadata after a `+` is discarded before anything is compared.
- A leading `v`, a four-part version, a leading zero, a numeric overflow, and text that is not a version are all rejected, so neither a tag name nor a Win32 file version can masquerade as a release version.
- Versions order numerically rather than lexically, a release supersedes its own pre-release, and the development build that follows a release is not superseded by it.
- The release feed's answers are each classified: a published release, a repository with no release, a draft or pre-release payload, a rate limit with the time it lifts, a server error, a timeout, an unreachable host, an unreadable body, and a tag that is not a version.
- A tag's leading `v` is stripped where tags are read and nowhere else.
- The request carries no credential, no body, and nothing naming the version or the machine.
- Shutdown propagates out of the check rather than being recorded as a result.
- A newer published version is reported as available; the running version and an older published version are not.
- A check that cannot answer keeps what was last known, records why, and never reports that the installation is up to date.
- Repeated inconclusive checks fall back to the ordinary daily cadence instead of retrying hourly without end, a rate limit is waited out for exactly as long as it states, and an answered check restores the ordinary cadence.
- Switching the check off makes no request at all and clears a standing notice.
- The update-check preference defaults to on before anything is configured, round-trips both ways, keeps one settings row, records when it changed, does not touch the row when nothing changed, and leaves the notification configuration untouched.
- The navigation drawer names the running version.
- The update notice renders nothing before a check has run, nothing when the installation is current, and nothing when a check could not answer; it names the new version and links to its release page when one exists, falls back to the releases page without a specific URL, and appears as soon as a check finds something rather than waiting for a navigation.
- The settings page names the running version, states what the request discloses, reports an inconclusive check without claiming anything about versions, and saves the preference without the notification form's save button.

## Manual checks

- Build locally and confirm the published executable reports a numeric `FileVersion` and a `ProductVersion` carrying both the `dev` suffix and the commit hash, and that the installer is named for the development version.
- Run `installer/Build-Installer.ps1 -ExpectedVersion` with a version the build does not carry and confirm it refuses to package.
- Run `build/Set-ProductVersion.ps1`, confirm that exactly the two version lines change and that no other line in the file is rewritten, then set it back and confirm the file is byte-identical to where it started.
- Dispatch the Release workflow from `main` and confirm it produces the tag, a published release with the installer attached and generated notes, a repository left on the next `-dev` version, and a run summary naming the version, the commit, and the artifact.
- Dispatch it with a version that is already tagged, with a version below the highest released one, with a `dev` suffix, and with something that is not a version, and confirm each is refused before anything is pushed.
- Dispatch it from a branch other than `main` and confirm it refuses.
- Confirm that the commits the workflow pushes do not start another build, and that the release commit is the commit the published binary was stamped with.
- Open the web interface on an installed build and confirm the drawer and the settings page both name the installed version, and that the settings page names the build.
- With a release published that is newer than the installed build, confirm the notice appears in the title bar, links to that release, and opens it in a new tab.
- With the installed build newer than the newest release, confirm no notice appears.
- Press **Check now** on a machine with no route to the internet and confirm the page reports that the check did not get an answer, that no error is raised, that no backup is affected, and that the daily log records the first failure once rather than repeatedly.
- Switch the check off, confirm the notice disappears at once and that no further request is made, then upgrade and confirm the preference survived.
- Upgrade an installation created before this milestone and confirm the check is on afterwards, matching a fresh installation.
- Confirm the published installer is unsigned and that the release checklist says so, since the update notice now sends people to download it.
