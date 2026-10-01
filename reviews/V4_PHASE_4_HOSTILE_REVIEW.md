# v4 Phase 4 hostile read-model review

Review date: 2026-09-28  
Scope: scoped search, semantic references, bounded page reads, reverse links, project events, change watching, timeline paging/aggregation, unset clocks, query diagnostics, deletion state, and payload amplification  
Verdict: **Pass with zero unresolved findings.**

## Attacks and results

| Attack | Required result | Evidence |
| --- | --- | --- |
| Page past 100 mixed records | Stable keyset pages with no duplicate or missing public reference | `SearchPagesPastOneHundredRecordsWithoutExposingStorageIdentity` pages 112 characters and separately checks continuity relation paging. |
| Read a record with many relations | Bounded sections with an independent cursor for every clipped section | `get`, `list_related`, project-event, location reverse-link, and nonentity overview tests exercise section limits and cursors. |
| Put both event kinds on a project | Project page and project-filtered chronology show both without changing event ownership | Project tests cover world events, entity events, initial entity-event projects, later add/remove, and unassigned continuity events. |
| Aggregate more same-date rows than the requested page size | Form the complete bucket before paging and do not repeat it | `AggregateTimelineGroupsBeforePagingAndDoesNotSplitABucket` proves three rows become one bucket at limit one and the following page starts on the next date. |
| Leave the artificial clock unset | Historical, ranged, and undated chronology still renders | Timeline tests return stored events and `Clock.Status=Unset`; only current calculations are unset. |
| Commit between initial read and watcher wait | Return that commit immediately from the read revision | `ChangesCursorCannotMissCommitAndIncludesVaultGlobalTagChanges` starts from the read cursor, commits, and observes the change without a subscription gap. |
| Change a global source/tag junction | Invalidate visible pages even when no single semantic reference can represent the junction | Junction changes return vault-global scope with `FullVisibleRefreshRequired=true`. |
| Link one global source to records in two continuities | Show only selected-continuity reverse links | Source overview tests cover entities and notes; claims and images use the same selected-continuity SQL boundary. |
| Read a large cached source snapshot | Return metadata and provenance, never the cached body | Snapshot overview excludes `Content`, retains byte metadata, and exposes its source relation. |
| Request deleted, cyclic, or unsupported relations | Apply explicit deletion policy and fail with a stable public relation error | All relation pages are bounded one level, deletion-aware, and use `relation.unsupported` rather than recursing. |
| Diagnose a slow category | Log category plus slow/ok state without SQL, parameters, story content, or paths | `MeasureAsync` emits only redacted query diagnostics. |

## Findings resolved during review

1. Entity-event creation accepted projects publicly but discarded them in the older handler. The v4 application service now inserts the event and every validated project association in one transaction.
2. Generic `get` returned cached source-snapshot content and could amplify a one-megabyte claim into an ordinary page read. Snapshot overview now returns bounded metadata only.
3. Several nonentity records lacked public owner/counterpart relations, and locations lacked important reverse views. The read model now covers owners, projects, sources, participants, locations, residents, organizations, objects, world events, and temporal causes.
4. Continuity `entities` paging decoded the search cursor as a relation cursor. It now forwards the opaque search cursor to the search service that created it.
5. Global source reverse entities were initially continuity-filtered while notes, claims, and images were not. Every canon-owned reverse section now enforces the selected continuity; continuity notes are included explicitly.
6. Composite global junction changes could be silently skipped because their keys are not simple row numbers. They now trigger an explicit full visible refresh.
7. Timeline aggregation happened after row limiting and could split a visual bucket across pages. Aggregation now precedes paging and has its own stable group cursor order.
8. Generic delete preview handled only canon entities. It now previews all public semantic records and applies continuity, variant-group, relationship-type, and ownership-principal blockers consistently with mutation behavior.

## Verification

- Phase 4 focused Debug gate: **11 passed, 0 failed, 0 skipped**.
- Full Debug regression suite: **148 passed, 0 failed, 0 skipped**.
- Debug build: zero warnings and zero errors.
- Tests used disposable Access databases and temporary storage roots.
- Production and Release artifacts were not touched.

## Unresolved findings

None.
