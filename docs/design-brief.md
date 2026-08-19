# Design brief: redesign the "Folder Backuper" web UI

## Your task
Redesign the visual design and UI/UX of a localhost web application called **Folder Backuper**. Produce a cohesive, modern, good-looking design system and high-fidelity mockups for every screen listed below. Deliver:
1. A **design language**: color palette (light + dark), typography scale, spacing, elevation/borders, iconography style, component styling (cards, chips/badges, buttons, tables, form fields, dialogs, nav).
2. **High-fidelity mockups** of each screen and each key state (loading, empty, error, active/live, populated).
3. Notes on **layout, hierarchy, and interaction** — especially how status is communicated at a glance.

Present the design as HTML/CSS mockups I can view in a browser. Show light and dark themes.

## What the product is
Folder Backuper is a **single-user Windows utility** that makes scheduled ZIP backups of local folders to local disks or SMB network shares. It runs as an always-on Windows background service and exposes a **localhost-only web UI** (no login, no multi-user, no internet exposure — it binds to loopback only). It is not a SaaS dashboard; it's a personal/small-business tool that lives in a browser tab and is checked occasionally, plus watched live while a backup runs.

**Who uses it:** one non-technical administrator at a small accounting company, on an always-on Windows 11 PC. Scale is small and calm: ~3–4 backup jobs, ~10 GB of data, backups run sequentially overnight. The person should never need a command line. The tone should be **trustworthy, calm, and legible** — this is software guarding important data. Clarity and confidence beat flashiness. It must read well to someone who is not a developer.

**Core promise (reflect this in the design's tone):** source data is strictly read-only and never touched; a backup is either complete or it isn't; existing good backups are never endangered; and **status must always be honest and understandable**. Status shown across dashboard, calendar, and history must feel consistent and unambiguous.

## Technical constraints (the design must be implementable within these)
- **Blazor Interactive Server** app using the **MudBlazor 9** component library, plus a single custom `app.css`. Prefer designs expressible with MudBlazor components (AppBar, Drawer/NavMenu, Paper/Card, Chip, Button, Table, Select, Dialog, ProgressLinear, Alert, Icon) styled via a MudBlazor theme + custom CSS.
- **Material Icons** are the available icon set (MudBlazor ships Material Symbols). Design around that icon vocabulary.
- **No Node build, no external CDNs, no web fonts guaranteed** — assume system fonts (Segoe UI on Windows) are the safe default; if you propose a font, it must degrade gracefully. Everything is self-contained and offline.
- Desktop-first (it's used on a PC in a browser), but it should not break on a narrow window. There is a responsive breakpoint at ~600px today.
- The MudBlazor theme is configured with a palette (Primary, Secondary, Background, Surface, AppbarBackground/Text, DrawerBackground/Text), a default border radius, and a typography config (font families + weights per heading level). Deliver values that map cleanly onto these.

## Current design (what you are replacing)
The current look is a warm, editorial "forest & amber on paper" theme and is fairly plain:
- Colors: primary forest green `#315c4b`, dark appbar `#18352d`, amber accent `#d89045`, paper/cream backgrounds `#f2f0e9` / `#fffdf7`, hairline borders `#d5d0c4`, ink text `#22332d`.
- Type: Segoe UI for body, Georgia (serif) for large headings. Cards are flat (no shadow) with 1px borders and a colored top/left accent stripe. Status uses colored MudBlazor chips (green/amber/red/blue/grey). A small decorative "brand mark" (rotated rounded square) sits in the app bar.
- Layout: left nav drawer + top app bar with a brand lockup and a "Service online" pill. Content pages have a big serif page heading with a lead sentence and a divider.

**Replace this aesthetic with a more modern one.** Move away from the warm editorial/serif "paper" look toward a clean, contemporary UI: a crisp neutral surface system, a refined accent color, subtle depth (soft shadows and/or subtle borders rather than heavy stripes), generous whitespace, a modern sans-serif type system, rounded-but-restrained corners, and clear visual hierarchy. Think polished modern desktop/SaaS utility — but keep it **calm, professional, and trustworthy** (this is software guarding important data, not a consumer social app). Avoid gimmicks, loud gradients, and clutter. You may propose the accent hue; a confident, non-garish color that signals reliability works well.

**Dark mode is required, not optional.** Deliver a fully worked light theme *and* a fully worked dark theme — both first-class, both shown in the mockups. Design the palette as semantic tokens (surface, elevated surface, border, text primary/secondary, accent, and the status colors) so both themes derive from one system and status stays legible in both.

## Information architecture (screens to design)
Left sidebar nav labeled "Workspace" with: **Dashboard, Jobs, Destinations, Calendar, History, Settings**. Top app bar shows the product name, a subtitle ("LOCAL BACKUP CONTROL"), and a service-status indicator. A footer note in the nav says "Loopback access only" (reinforces the private/safe nature). Design the app shell (app bar + nav + content area) plus these pages:

### 1. Dashboard (home)
The at-a-glance health view. Contains, top to bottom:
- **Health alerts** (only when relevant): "N job(s) reported a failed last run" (error), "N completed with warnings" (warning), "N have an unresolved notification result" (warning).
- **Active backup** section: either a rich **live progress panel** (see its own spec below) or a calm idle state ("No backup is running right now." with a check icon).
- **Queued** list (when non-empty): jobs waiting to run, each with trigger type and due time.
- **Jobs** grid: one card per job showing job name, a status chip (Success / Warnings / Failed / No runs yet), a facts row (Last run, Last success, Next run — or "Paused"), a storage summary ("X GB across N of M retained backups", latest archive size, last confirmed time, optional "stale" chip, optional "N archives missing" error, "N unmanaged archives" warning, notification state chip), and actions (Run now, Refresh storage, History).
- **Destinations** list: name, verification-status chip, last-access result + time.
- **Quick actions** row linking to the other pages.

### 2. Jobs
List + create/edit form (the form replaces the list inline when editing).
- **List:** cards per job with name, lifecycle label + chip (**Active / Paused / Archived**), a source→destination route line (monospace paths with an arrow), the schedule summary ("Mon, Wed, Fri at 02:00 · Next: …"), and actions (Edit, Run now, Pause/Reactivate, Archive; Restore for archived). A toggle switches between current and archived jobs. "New job" primary button.
- **Form** ("New job" / "Edit job"), grouped into sections: **Identity and source** (job name; source directory with a "Browse" button and a "Preview source" action that reports size + retention estimate); **Destination** (choose a storage root from existing destinations, a subfolder path, shows verification chip + resolved root path, "Test effective folder"); **Schedule and retention** (weekday checkboxes Mon–Sun, a local time picker, retention count number, a summary line showing timezone + computed next run); **Activation** (checkbox to activate; note that activation verifies destination access/ownership). Save/Cancel footer. Needs clear inline field validation error styling.

### 3. Destinations
Grid of storage-root cards.
- Each card: an icon distinguishing **Local storage** vs **SMB (network) storage**, name, a type label, a verification chip (Succeeded / Failed / Unverified), the root path (monospace, can be long — must wrap), optional SMB account name, available free space, last-test result + time, and actions (Edit, Test access, Archive; Restore for archived). Toggle between active and archived. "Add destination" primary button. Empty state: "No storage roots configured — add a local folder or an SMB share, then verify service access."
- Supporting **dialogs**: Add/Edit destination form (local path or SMB share + credentials), Test-access result dialog.

### 4. Calendar
Past runs and future planned runs together, with a **Month / Agenda** toggle and Job + Status filters.
- **Month view:** a 7-column month grid, weekday header row, out-of-month and "today" cell treatments, and per-day entry chips. Each entry is a small pill with a colored status dot + job name; **planned/future** entries are visually distinct (dashed/outline, not filled). A legend maps dot colors to: Successful (green), Warnings (amber), Failed/missing (red), Cancelled (grey), Planned/running (blue). Month navigation (prev/next/Today) + month label.
- **Agenda view:** entries grouped by day, each row = time + status chip + job name.
- Clicking a real run opens the Run details dialog.

### 5. History
A permanent, unclearable audit log of every run attempt.
- Filters: Job dropdown, Status dropdown (All / Successful / With warnings / Failed / Cancelled).
- A dense, paginated **table**: columns Job, Trigger, Status (chip), Started, Duration, Archive (chip: present/missing/etc. or "—"), Problems (count). Rows are clickable → Run details dialog. Copy note on the page: "Permanent record of every run attempt. This history cannot be cleared."

### Live active-run progress panel (appears on Dashboard; design carefully — it's the emotional core)
Shown while a backup runs; updates live. Contains: job name + source path; a **phase chip** (Queued → Scanning → Compressing → Transferring → Finalizing) and a trigger chip; a progress bar that is **indeterminate** during scanning/prep and **percentage-based** during compress/transfer, with a "% complete" caption; the current file being processed; and a metrics grid: files processed, source read (bytes), archive size (bytes), compression rate, transfer amount + rate (when transferring), elapsed time, estimated time remaining. A **Cancel** button (destructive styling). Design its determinate and indeterminate states, and think about how it should feel reassuring rather than tense.

### Run details dialog
Opened from History and Calendar. Shows a run header with status; a facts grid (job, trigger, timings, duration, etc.); an archive block (archive file name in monospace, size); and a **problems table** (per-file problems with long paths that must wrap). Design the dialog shell and the problems table empty/populated states.

## Domain vocabulary & status system (get these visually consistent everywhere)
This is the most important cross-cutting design job — the same status must look the same on Dashboard, Calendar, and History.
- **Run outcomes:** Successful, Completed with warnings, Failed, Cancelled, plus in-progress phases (Queued, Planned, Scanning, Compressing, Transferring, Finalizing).
- **Suggested status colors:** success = green, warning = amber, error/failed/missing = red, planned/running/info = blue, cancelled/neutral = grey. Design accessible chip/badge and dot treatments for each (must be distinguishable in dark mode and reasonably color-blind-friendly — don't rely on hue alone; pair with icon or label).
- **Job lifecycle:** Active, Paused, Archived.
- **Destination verification:** Succeeded, Failed, Unverified; type Local vs SMB.
- **Trigger types:** Scheduled vs Manual ("Run now").
- **Frequent data types:** file paths (often long, must wrap; consider monospace), byte sizes, durations, rates (MB/s), local date-times, counts.

## Deliverable expectations
- A tight, reusable component/token system — this app reuses the same card, chip, table, and form patterns across every page, so nail those primitives.
- Mockups for: app shell, Dashboard (idle + active-backup + with-alerts), Jobs (list + edit form + validation), Destinations (grid + a dialog), Calendar (month + agenda), History (table), the live progress panel (determinate + indeterminate), and the Run details dialog.
- Light and dark themes.
- Keep it implementable with MudBlazor 9 + Material Icons + a single custom stylesheet. Call out anything that would need custom CSS beyond MudBlazor defaults.
- Prioritize legibility, honest status communication, and a calm, trustworthy feel over decoration.
