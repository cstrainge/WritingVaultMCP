# v4 representative fixtures

Status: Phase 0 decision record  
Accepted: 2026-09-28

All fixtures use disposable databases and generated companion-asset roots. None use the production database or production backup directory. Seeds and expected semantic outcomes are deterministic; private storage IDs are never asserted in public contract fixtures.

## 1. Empty vault

- One unset-clock continuity and one continuity with a persisted clock.
- No canon entities, sources, tags, or images.
- Verifies onboarding, empty states, clock provenance, unset-clock timeline behavior, and the absence of invented data.

## 2. Lostville editorial fixture

- A realistic continuity with projects, locations, characters, organizations, objects, world events, local events, notes, sources, snapshots, claims, tags, images, variants, and deleted records.
- Kirsty is born 2012-08-11 and has `The Lost Year` from 2023-08-14 through 2024-09-23 at biological and experienced rates `0.0`.
- Contains exact, month, year, circa, before, after, closed-range, open-range, original-text-only, undated, and narrative-only dates.
- Contains directed and undirected character relationships, recurring non-overlapping periods, residences, memberships, organization locations, co-ownership shares, custody, and object locations.
- Contains unassigned world and entity-specific events, each event kind assigned to one project, and each event kind shared across multiple projects. Project links carry representative role/notes metadata.
- Provides the representative content for continuity, character, location, organization, object, world-event, project, source, tag, claim, Markdown, image, deleted-record, and timeline visual reviews.

## 3. Ambiguity and isolation fixture

- At least three records named `Aurora` across different kinds and two characters with the same preferred name in one continuity.
- Repeated aliases owned by different records, differently cased and Unicode-normalized names, and a uniquely resolvable exact alias.
- A second continuity repeats names, tags, and concepts but has independent mutable canon.
- Vault-global sources and tags link into both continuities.
- Verifies candidate responses, no-guess writes, continuity isolation, global-record reads, and change-feed invalidation.
- Includes writes committed immediately before, during, and after page reads to prove the observed-revision/watch handshake has no missed interval.
- Includes attempted cross-continuity event/project associations, duplicate active associations, and restore conflicts; every invalid mutation must fail atomically without leaking the other continuity's records.

## 4. Browser-threat and content fixture

- Markdown containing raw HTML, scripts, event attributes, unsafe schemes, remote images, oversized headings, tables, nested lists, code, and long unbroken text.
- URLs with Unicode hosts, credentials, fragments, malformed schemes, and very long values.
- Names, notes, and captions at accepted limits plus rejected over-limit forms.
- Images with misleading extensions/media declarations, EXIF orientation, ICC profiles, transparency, decompression-bomb dimensions, truncated data, and metadata payloads.
- Verifies sanitization, escaping, link presentation, image validation, responsive overflow, and the no-remote-fetch rule.

## 5. Representative personal-vault scale

One dense continuity contains approximately:

| Record group | Count |
| --- | ---: |
| Characters | 500 |
| Locations | 750 |
| Organizations | 200 |
| Objects | 2,000 |
| Projects | 100 |
| World events | 5,000 |
| Entity events | 20,000 |
| Temporal relationships and periods | 30,000 |
| Notes | 15,000 |
| Sources and snapshots | 5,000 |
| Claims/evidence links | 15,000 |
| Tag and project links | 50,000 |
| Image metadata rows | 5,000 |

Small generated renditions keep the disposable fixture practical while retaining realistic row counts. A separate bounded image fixture exercises actual JPEG bytes and capacity checks. This scale is a test target, not an assertion about current production content.

## 6. Timeline density fixture

- At least 50,000 projected timeline items across all lane kinds.
- Dense clusters on one day, one month, and one year; long overlapping ranges; open bounds; undated items; and identical narrative positions.
- Queries cover wide aggregate views, narrow detailed views, rapid cancellation during zoom, and clock changes while a viewport is visible.
- Verifies bounded responses, stable cursors, aggregation, accessible ordering, and the performance budgets in the v4 plan.

## 7. Migration and recovery fixtures

- Empty current schema.
- Populated current schema with every table represented.
- Soft-deleted and restored rows.
- A copy interrupted after each independently resumable migration step.
- Deliberately missing indexes, foreign keys, migration rows, external assets, and hash mismatches.
- A pre-migration verified backup and companion-asset manifest used for full recovery.

## 8. Image ingress contract fixtures

The original Phase 1 fixture design used a small known PNG and a larger but permitted PNG through both ChatGPT and Claude against a Debug-only, non-persisting probe. The probe:

- Accepts each candidate host representation without relying on a server-local path.
- Validates and hashes the decoded bytes in memory, returns media type, dimensions, byte count, and SHA-256, then discards them.
- Never writes the bytes to Access or the companion asset root and never records base64 or content in logs.
- Records practical request/response ceilings and the client-visible failure behavior for an over-limit probe.
- Establishes one canonical application input. If host transports differ, adapter code performs the normalization.

The user moved both real-client PNG tests to Phase 12 usability validation against the v4 Release deployment on 2026-09-30; they do not block switch-over. Their results must still be recorded, and any adapter bug fixed, before Phase 12 closes. The Debug probe remains a diagnostic fixture, but a copied base64 payload does not prove actual client attachment transport.

## 9. Human visual review set

- **Phase 8:** Empty and populated navigation shells at desktop, tablet, and narrow widths in light and dark themes.
- **Phase 9 technical fixture:** One representative page for every canon type plus continuity, source, tag, claim, deleted record, Markdown threat content, and image gallery.
- **Phase 10 technical fixture:** Sparse, dense, fuzzy, undated, narrative-order, focused-character, and unset-clock timelines plus their keyboard/list alternatives.
- **Phase 11 consolidated visual review:** Review the complete viewer, including all Phase 9 and 10 states, and incorporate refinements before a Release candidate.

The user deferred the separate Phase 9 and 10 visual inspections on 2026-09-29. Screenshots and automated visual diffs support the final review but do not replace it.

For a reproducible Debug layout check, first run the
`V4Phase9PreviewFixtureTests` test to generate the ignored
`artifacts/phase9-preview` database and asset directory. Then run:

```powershell
.\tools\Test-WritingVaultVisualRegression.ps1 `
  -Database .\artifacts\phase9-preview\WritingVault.Phase9Preview.accdb `
  -BackupRoot .\artifacts\phase9-preview\backup
```

The script refuses an occupied fixed port, starts and stops its own Debug viewer,
and checks loaded desktop overview, graph/table geometry, a real first-Tab
skip link and Enter activation of a timeline marker, and a narrow dark
character page with image and note. Its PNGs remain under ignored `artifacts/`.
The geometry assertions catch layout regressions; the Phase 11 human review
still decides whether the pages look and feel right.

## 10. Windows startup and supervision fixture

- A disposable current-user task namespace and test configuration use a non-production database, tunnel profile, and port.
- Installation is tested with missing artifacts, occupied port, missing profile, undecryptable credential, and failure while registering the second task; every failure leaves no partial new configuration and preserves a prior valid configuration.
- Web-first and tunnel-first logon simulations converge on one backend. Simultaneous retry attempts still produce one tunnel profile process because the mutex remains authoritative.
- A forced web exit restarts only the web task while the tunnel remains connected. A forced tunnel exit restarts only the tunnel task while the viewer stays available. A forced backend exit lets the independently running clients converge on one replacement backend. All cases exercise bounded logging without visible windows or prompts.
- Task definitions are inspected to prove they contain no plaintext API key, database connection string, or unexpected executable path.
- The stable web URL survives process restart and logon simulation. An occupied configured port produces a clear failed task/status result and never selects another port. Hidden/background startup does not open a browser or the tunnel administration UI.
- Maintenance stop disables current retries for the session, drains both adapters and the backend, and permits an exclusive administrative database check. Restart restores both tasks and health.
- Removal deletes only the two Writing Vault tasks and leaves the DPAPI credential, tunnel profile, database, backups, assets, and logs intact unless the user invokes their separate cleanup operations.

## 11. Viewer freshness fixture

- Mutations from Claude, ChatGPT, and a test adapter change visible core fields, notes, relations, events, images, tags, sources, deletion state, clock values, and timeline ranges.
- A commit in the interval after a page read and before the first bounded wait must be returned on that wait.
- Bursts of related changes may coalesce network refreshes but must end at the newest committed revision with every affected visible section current.
- Unknown-impact changes, expired cursors, continuity switches, backend restarts, browser reconnects, and restored hidden tabs force the documented catch-up or full-refresh path.
- Forced watcher failure removes the Live indicator immediately and displays the persistent stale warning plus last-successful-refresh time. Live cannot return until data and cursor catch up.
- Rendering tests ensure an updating section cannot combine fields from two revisions and that a failed replacement keeps the last snapshot visibly marked stale.
