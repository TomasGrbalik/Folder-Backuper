# Milestone 12: Interface Language

Formatting through the shared `DisplayFormat` helper is verified in the [Milestone 8 acceptance checklist](milestone-8-acceptance.md) and is not re-verified here. Confirm only that the culture the helper resolves now follows the selected language. Installer and upgrade behavior is verified in the [Milestone 10 acceptance checklist](milestone-10-acceptance.md); confirm only that an upgrade preserves the language preference along with the rest of the application data, and that the package carries the Slovak satellite assembly.

## Automated checks

- Every key in each neutral resource file has a Slovak counterpart, neither file carries a key the other lacks, and a key whose English text contains a placeholder has the same placeholders in Slovak. A missing or mismatched entry fails the suite rather than surfacing as English text in a Slovak interface.
- Every member of every message code enumeration resolves to an entry in both languages, so a code can never reach a page without text behind it.
- No resource key is written as a string literal in presentation or service code; text is reached only through the generated accessor or through a message code.
- The language preference defaults to the Windows installed interface language before anything is configured, choosing Slovak only when Windows itself is Slovak, round-trips both ways, keeps one settings row, records when it changed, and does not touch the row when nothing changed.
- Applying a language sets both the culture and the interface culture, and applying it leaves the notification configuration and the update-check preference untouched.
- The root document's language attribute follows the interface culture.
- The navigation drawer, the application bar, and the settings page all name the selected language, and the application bar and the settings page cannot disagree because both read and write the one preference.
- Plural forms are chosen by rule: Slovak distinguishes one, few, and many across zero, one, two, four, five, eleven, and twenty-one; English distinguishes only one from the rest.
- A run recorded in one language renders its problems, its error summary, and its pipeline operations in the other after the language changes, because what was stored is a code and its arguments rather than a sentence.
- The permanent history's problem rows carry a code and arguments; no row stores display text.
- Weekday and month names, the calendar's first day of week, and the schedule summary all derive from the culture. Slovak starts the week on Monday; the weekday abbreviations are not derived from enumeration member names in either language.
- Archive file names stay in the invariant timestamp format under both languages, and the notification email's timestamps stay invariant and in the run's own time zone.
- Notification email subject and body are produced in the selected language by a code path with no browser attached.
- The table pager's row-count and range text comes from the application's own resources rather than from the component library, so it is Slovak with Slovak selected. No component library localizer is registered: the components this application renders were checked against a recording localizer and ask one for nothing, because MudBlazor routes only its data grid and pickers that way and this application uses neither.
- Every page renders under Slovak without an English word from a curated list appearing in the markup, and the checks that assert no password value and no raw-log or export action appear continue to hold in both languages.
- The version file's satellite language list names exactly English and Slovak.

## Manual checks

- Open the interface, press **SK**, and confirm the page reloads into Slovak: the application bar, the navigation drawer, and the dashboard. Restart the service and confirm it comes back Slovak.
- Walk every page and dialog in Slovak — jobs list, job form, all five confirmation dialogs, destinations grid, add and edit destination, test access, source browser, source preview, calendar in month and agenda views, history table and its pager, run details with a populated problems table, and settings — and confirm no English text survives.
- Save a job with no name, no weekday, and an invalid destination subfolder, and confirm every inline field error and the outcome alert are Slovak.
- Run a backup against a destination that has been disconnected, then open the failed run's details and confirm the problem rows, the operation column, and the error summary are Slovak. Switch to English and confirm the same run now reads English.
- Send a test email in Slovak and confirm the subject and body are Slovak, then let a scheduled run finish and confirm its email is Slovak too, with the run's own time zone and an invariant timestamp.
- Confirm Slovak formatting throughout: a date as `20. 8. 2026 23:00`, a size as `9,7 GB`, a Monday-first calendar, and Slovak month names. Confirm the archive file name in run details is unchanged.
- Narrow the browser window below the responsive breakpoint and confirm the longer Slovak strings do not break the application bar, the job cards, or the table headers.
- Press **EN** and confirm a clean return, then open the settings page and confirm it agrees with the application bar.
- Publish the application and confirm exactly one satellite directory, `sk`, is present; build the installer and confirm it packages that directory.
- Upgrade an installation created before this milestone and confirm the interface comes up in the Windows installed interface language, matching a fresh installation, and that setting Slovak then upgrading again preserves Slovak.
- Confirm that Windows event-log entries, installer console output, and the daily log file stay English with Slovak selected.
