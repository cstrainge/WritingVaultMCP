# Writing Vault read-only web viewer

Last updated: 2026-09-30  
Phase 8 status: complete and visually accepted on 2026-09-29
Phase 9 status: Debug record-page implementation passed its zero-finding technical hostile review. Its earlier 169-test clean-checkout gate passed; the later Phase 11 staged-checkout suite passed 208/208. The user accepted the consolidated viewer presentation on 2026-09-30.
Phase 10 status: the graphical timeline and integrated chronology table passed the zero-finding Debug technical hostile review. The user accepted the consolidated viewer presentation on 2026-09-30, and the final Phase 11 hostile review closed with zero unresolved findings. Automated and keyboard accessibility checks passed; the user waived the manual Narrator walkthrough.

The viewer is a local, read-only companion to the MCP server. The Release build binds to the fixed bookmark:

```text
http://127.0.0.1:5284
```

The Debug build uses `http://127.0.0.1:5285` so its tray and launcher cannot occupy or control the Release viewer's address. The viewer never opens the Access database and has no write endpoint or write service reference. Every browser tab owns one interactive read-only v4 MCP connection and a separate read-only change-watcher connection. Claude, ChatGPT, and several viewer tabs still converge on the single elected database backend for their selected database.

The story-time control changes the artificial current time for one browser tab. It accepts a local date and time plus a timezone, handles repeated daylight-saving times explicitly, rejects nonexistent local times, and never writes the continuity clock. After a successful change, the browser saves the selected time in a one-year cookie keyed to that continuity. Opening the continuity again, including after a browser or backend restart, restores that time in the new tab's independent session. Choosing **Use continuity clock** removes its cookie without changing cookies for other continuities. If cookies are blocked or a saved time cannot be restored, the viewer shows a warning and remains usable with the continuity clock. A session time displays using the local offset accepted by the backend, so different browser timezone rules cannot shift the displayed time after restoration.

## Current Debug preview

The current Phase 10 browser review uses only the disposable preview database:

```text
.\artifacts\phase9-preview\WritingVault.Phase9Preview.accdb
```

Start it from the project root with:

```powershell
dotnet .\WritingVault.Web\bin\Debug\net10.0-windows\WritingVault.Web.dll `
  --server .\bin\Debug\net10.0-windows\WritingVaultMcp.dll `
  --database .\artifacts\phase9-preview\WritingVault.Phase9Preview.accdb `
  --backup-root .\artifacts\phase9-preview\backup `
  --listen http://127.0.0.1:5285
```

This command does not modify the production database or Release artifacts.

The Debug preview includes linked pages for all six canon entity kinds, vault-global sources and tags, notes, claims, event associations, images, cached source snapshots, and intentionally opened deleted records. Search includes long-form note and event content, has record-kind and deletion filters, and exposes later pages instead of hiding matches after the first 60. Continuity selection likewise offers later pages after the first 100. A record page has copy-link and copy-semantic-reference actions; project event rows show association role and notes. Entity notes flow through one Markdown-style document box with one openable title per note; remaining note pages load automatically into that same box. A repeated opening title is suppressed. Continuity notes remain individually carded, and the standalone note page keeps the title in its hero and the Markdown in one card. Character pages show one age when all four measures agree, group the measures by displayed value when they differ, and give one explanation when every measure is unknown or story time is unset. Known birth and death dates appear as single readable values; unknown, unset, and empty fields are omitted from entity Details, and an empty Details panel is hidden. Active temporal state is summarized without storage metadata. Details and History occupy separate panels in the record sidebar when both are present. Cached source text, including an intentionally opened deleted snapshot, is read from the verified local cache in bounded pages. The preview fixture is created by `V4Phase9PreviewFixtureTests` and is ignored by Git.

The Debug timeline has exact markers, styled uncertain and open-ended periods, an unset-clock state, separate Character, Location, Organization, and Object event controls, an individual-entity filter, a date-range brush, separate graph pan/zoom, and one continuous chronology table. The graph is horizontally scrollable, with fixed lane labels, keyboard arrow scrolling, and touch panning; its canvas grows with the story span but is capped at 16,000 pixels. Earlier/Later move through the canvas and fetch a new bounded date window at an edge, while zoom stays centered on the date currently in view. Scrolling changes only the graph; the chronology table remains governed by its own date selection. The viewer fetches every matching dated and undated entry in revision-bound 500-item API batches and renders them without page controls. Canon entity pages open the same timeline focused on that record. Story dates and periods appear as readable calendar prose with a separate elapsed-duration line for ranges; stored lower and upper bound fields are combined into one date on record pages. The preview fixture contains exact, range, before, after, and undated examples. The earlier 50,000-event performance measurement covered a paged table and does not measure this continuous-table behavior; a large-continuity performance check is still needed.

The graphical timeline displays a highlighted Now card at the artificial clock's exact local date and time, or a whole-day band when the browser override omits the clock time. The card shows the timezone, remains separate from stored events, and can be centered by mouse or keyboard. If Now falls outside the graph's date window, a highlighted jump control points toward it; an unset clock leaves historical events visible without a positioned Now card.

Record pages show timeline, copy-page-link, and copy-Vault-reference actions as compact icons with tooltips on hover and focus. Their accessible names remain available to assistive technology, and copy actions announce success or failure.

## Freshness states

- **Live** means the dedicated watcher is connected and the displayed revision has caught up.
- **Updating** appears while an affected visible view is being replaced.
- **Updates paused — displayed data may be stale** remains visible after watcher, backend, or catch-up failure. It includes the last successful refresh time and retries automatically.
- Returning to a hidden or disconnected tab performs catch-up before the viewer reports Live again.

A committed MCP write advances the change cursor. Unknown impact, cursor expiry, backend replacement, and browser reconnection trigger a complete refresh of the visible view. Vault-global source and tag changes are included.

## Production launcher after cutover

`Run-WritingVault-Web.bat` launches the Release viewer against the production database. It deliberately fails if `127.0.0.1:5284` is occupied and never chooses another port.

The source tree continues to use Debug builds until the Phase 12 Release cutover.

## Windows startup after cutover

`Configure-WritingVault-Startup.bat` manages three independent current-user Scheduled Tasks for each build: viewer, tunnel, and tray. Release is the default; pass `Debug` as the second argument to inspect or manage only Debug tasks:

```text
Configure-WritingVault-Startup.bat install
Configure-WritingVault-Startup.bat status
Configure-WritingVault-Startup.bat start
Configure-WritingVault-Startup.bat stop
Configure-WritingVault-Startup.bat restart
Configure-WritingVault-Startup.bat remove
Configure-WritingVault-Startup.bat status Debug
```

Installation validates the selected build's applications, database, fixed port, tunnel executable and profile, and the current user's decryptable DPAPI credential before changing its tasks. Task definitions never contain the API key. Viewer and tunnel tasks are hidden, non-interactive, independently restarted, and contained in kill-on-close Windows jobs so a stopped supervisor cannot orphan a database client. The tray has its own hidden logon task and single-instance mutex. A one-minute repeating watchdog trigger recovers a crashed task; `MultipleInstances IgnoreNew` prevents duplicate healthy instances. Exiting the tray intentionally disables its watchdog without stopping the viewer or tunnel.

Background logs are written beneath `%LOCALAPPDATA%\WritingVaultMCP\Logs`, redact credential-like values, rotate at 2 MiB, and retain four files per process.

`stop` and `restart` are coordinated maintenance operations. They wait for the viewer, tunnel, adapters, and elected backend to drain before returning.
