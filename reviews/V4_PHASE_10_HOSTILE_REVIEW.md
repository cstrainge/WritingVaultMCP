# Phase 10 hostile review: graphical timeline

Status: **zero open Phase 10 technical findings** on 2026-09-29. This review covers only Debug builds and disposable databases. The user deferred visual acceptance to Phase 11.

## Findings fixed during review

1. Timeline snapshots resolved labels for every canon entity, even if no timeline link needed them. The association index now resolves only referenced owners and links. On the disposable 50,000-event fixture, the graph request measured about 610 ms after backend startup and the first detail page 56 ms.
2. The continuity overview searched all 50,000 entities before displaying its first small section. Cross-kind search now stops after enough eligible rows and starts subsequent pages at the decoded cursor. A fresh browser load reached the timeline in about 2.1 seconds after capture began, including 1.17 seconds for bootstrap, 476 ms for continuity selection, 987 ms for the first graph query, and 100 ms for the first table page.
3. All entity-specific events shared one filter. `entityEventKinds` now distinguishes Character, Location, Organization, and Object owners, while `kinds` still controls timeline record kinds. The browser exposes four separate controls; the focused backend and Chrome probes passed.
4. Dense graph clusters turned before/after dates into falsely closed ranges. Buckets now retain open-start and open-end semantics; the 520-event regression asserts both kinds survive aggregation.
5. Undated entries could be buried behind thousands of dated detail pages. `undatedOnly` gives them an independent revision-bound cursor, and the viewer renders a separate paged list. A 520-dated-plus-one-undated regression and Chrome probe found the undated item immediately.
6. The chronology table appended every page into the DOM. It now navigates one bounded 100-entry page at a time. Chrome traversed six pages of a 520-entry fixture with 520 unique entries, no gaps or duplicates, at most 100 rows in the DOM, and successful reverse paging.
7. A global search with content enabled failed for Character because it queried a nonexistent `Characters.Description` column. Character content search now checks `PhysicalDescription` and `PersonalitySummary`. A Unicode cross-kind regression and the browser timeline-to-character-to-focused-timeline path passed.
8. The SVG's `role=img` hid interactive descendants from assistive technology semantics. It now uses a labelled group with keyboard-focusable marker buttons and has an independently usable date-control/table path. Chrome opened marker details with Enter.
9. Dense graph clusters exposed only a bounded sample of event references, so a selected entity's later event could not reliably highlight its cluster. The graph query now accepts a same-continuity `highlightRef` and marks each cluster with `containsHighlight` when any member matches. The viewer refreshes only the graph on entity click, preserving the current table page; a regression links a late event beyond the cluster sample.
10. A graph containing dates near the supported year-0100 or year-9999 boundary could pad its viewport outside the Access date range. The graph domain, pan/zoom, and query bounds now stay within 0100-01-01 through 9999-12-30. A two-endpoint fixture and browser zoom probe kept both records visible and showed five distinct, correctly spaced axis labels.

## Evidence completed

- Focused Phase 10, search paging, and contract snapshot tests passed after the changes. The preview fixture includes exact, range, before, after, unknown, multi-entity, project-linked, and narrative-order events.
- Chrome changed the table from calendar to narrative order, kept the graph on a date axis, showed historical material with an unset story clock, and restored a character-focused bookmarkable route.
- The 50,000-event database is disposable and passed schema and integrity verification. Neither the production database nor Release output was changed.
- Headless Chrome traversed all 500 detail pages of that fixture in 20.3 seconds: 50,000 unique records, zero duplicates, both endpoint titles present, at most 100 table rows in the DOM, and a hidden Next control on the final page.
- A disposable MCP writer advanced the timeline revision while Chrome was on page 2. A synchronized probe observed the browser return to page 1 and Live state with the new event discoverable less than a second after the writer started; the watch response included the changed event. An earlier unsynchronized timeout did not record commit time and is inconclusive, not a failed latency measurement.
- The web timeline route now forwards text and all supported timeline filters. A direct `text=Probe writer race 4` request returned only the matching event; previously it returned the generic first page.
- After the dense-highlight fix, the 520-entry browser probe selected the witness linked only to the final event. One graph cluster and the corresponding row highlighted while the table stayed on page 6 of 6. Focused Phase 10, contract, and preview tests passed 17/17.
- Final Chrome interaction probes selected a graphical date range (five rows to one), cleared it and recovered the original five rows, applied an exact two-day range with matching entries, opened marker detail by keyboard, and moved focus from a table row to its marker. Calendar/narrative ordering, four separate entity-event controls, entity-page timeline focus, unset-clock history, and undated discovery also passed against the disposable preview.
- The post-fix full Debug suite passed 177/177 with zero failures or skips. After the final date-boundary fixture and cleanup-only log test were added, 13/13 focused Debug tests passed with a zero-warning solution build. The final boundary browser probe showed both year-0100 and year-9999 entries after zoom and pan; no query error or misleading repeated axis label remained. The latest highlight probe also retained keyboard focus on the selected witness button after graph refresh.

## Carried into Phase 11

- The first 50,000-event graph query measured 987 ms, above the 750 ms initial local target. Repeat graph query measured about 610 ms. This is a measured Phase 11 performance exception pending profiling or optimization, not a passing latency result.
- Complete the Phase 11 automated accessibility and screen-reader walkthrough, plus the consolidated user visual review. Phase 10's keyboard and semantic-order probes passed; the broader accessibility and visual acceptance gates were explicitly deferred.
