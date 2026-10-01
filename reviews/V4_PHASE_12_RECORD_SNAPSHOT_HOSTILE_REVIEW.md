# Phase 12 historical record-page hostile review

Status: Debug candidate, zero unresolved technical findings. Release cutover and
real-client usability are separate Phase 12 gates.

## Findings corrected

- A continuity clock or timezone change altered character age without changing
  the character row. The affected-page capture now saves every character page
  in that continuity in the same transaction. The clock regression passes.
- Removed tag/source and event/project associations could lose their former
  owner's historical view. Composite mutation keys and project-change payloads
  now identify those owners; a removed-tag regression confirms the old page
  retains the association and the next version omits it.
- A deleted character blocked backend startup while creating the migration
  baseline because age computation required an active character. Deleted pages
  now retain their stored fields without an invalid derived-age query. The
  previously failing read-only client startup test passes.
- A pinned note link to a record deleted later was disabled by current-record
  lookup. The viewer requests `includeDeleted` only for pinned link lookup;
  ordinary current links remain unavailable. A focused test proves both cases.
- The page versions had no discovery operation. `record_snapshot_list` now
  pages retained versions newest first; `record_snapshot_get` and Markdown
  `?v=` use returned positive versions. The generated v4 declarations and
  contract tests pass.
- Migration 012 initially lacked leading indexes for its two continuity
  foreign keys. The indexes were added and the foreign-key index invariant
  passes. Its checksum is frozen only in the new release commit; it has not
  been applied to production.

## Evidence

- The final focused snapshot suite passed 13/13, including merged-source
  historical pages, verified backup/restore, and tamper detection. Rename,
  alias, note, deletion, continuity, clock, rollback, and stable-ref cases are
  covered. Missing versions return `snapshot.not_found`.
- A populated migration-011 disposable database upgraded to 012 with exact
  schema verification and zero integrity issues. Startup created a labeled
  baseline without inventing older page states; a second pass was idempotent.
- Loaded Chrome opened a pinned Chloë character page with its saved note body,
  a historical banner, and a View latest link. A separate keyboard probe
  followed a pinned note link to that page. The saved page kept the version in
  the URL across navigation.
- Every snapshot is JSON-bounded to one MiB, stored with byte count and SHA-256,
  and captured before write commit. The integrity audit checks content hashes
  and per-record version sequences without exposing internal row keys.

The scoped Debug hostile review has zero unresolved findings. Phase 12 still
requires the immutable Release build, verified production backup, controlled
migration, task installation, and live acceptance.
