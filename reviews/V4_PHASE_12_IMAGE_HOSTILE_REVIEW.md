# Phase 12 image hostile review (Debug candidate, zero open findings)

The scope is migration 010 story-image ownership, migration 011 immutable image
content revisions, MCP reads and writes, backup/restore, read-only viewer image
links, and change invalidation. This is a working review, not Release approval.
The production database and Release clients were not changed.

## Findings corrected during review

1. **A concurrent replacement could mislabel content.** `image_view` previously
   read the content revision and image bytes through separate connections while
   a writer could commit between them. `image_view` and `image_revision_history`
   now hold the backend's consistent-read gate for the whole read. Nested reads
   use the coordinator's reentrant gate.
2. **A transparent original could not be rerendered against a new background.**
   The redundant-replacement check compared only the original SHA-256. It now
   compares the original and both rendered byte sequences. A focused regression
   proves a new background creates revision 2, retains revision 1, and rejects
   an unchanged repeat.
3. **The Release hash inventory omitted dependencies.** The candidate manifest
   now hashes every file in all four Release output directories as well as its
   named executable, script, contract, and static-asset entry points. The
   generator refuses a missing Git commit or dirty checkout before building;
   its no-commit failure path was exercised without touching Release.
4. **A valid replay failed after deletion.** The replacement preflight denied a
   deleted image before consulting the operation journal. Semantic resolution
   now permits the journal lookup; a new replacement still checks active image
   and owner state inside its transaction. A focused test proves replay after
   deletion and rejection of a new write to the deleted image.

## Evidence so far

- A populated migration-010 database copy upgraded to 011 with exact schema
  verification and zero integrity issues.
- Focused Debug image, tray, and contract tests passed **28/28**; after the
  read-consistency change, three targeted image revision tests passed **3/3**;
  the transparent-background regression passed **1/1**.
- Debug solution build passed with zero warnings or errors after the
  read-consistency change. A disposable Debug browser route displayed pinned
  image revision 1, its original bytes, both history entries, and View latest.
- Replacement tests cover idempotent replay, stale expected version, current
  and pinned originals/renditions, metadata-only edits, deletion/restore,
  missing revisions, cross-continuity invalidation, backup/restore, and a
  same-length tampered historical rendition. The story-image replacement path
  has its own focused test.

## Final Debug review evidence

- A regenerated disposable Lostville fixture contains one note with a current
  image embed, an immutable revision-1 embed at 50% width, and a pinned record
  link. Loaded Chrome displayed both images as verified local blobs and the
  pinned record link. Keyboard Enter on the pinned image opened its bookmarkable
  original page, which identified content revision 1 and linked to the latest.
- The owner-kind tests cover continuity, relationship, entity-event,
  relationship-event, and canon-entity attachments, search, gallery reads,
  effective deletion, restore, and cross-continuity explicit reads. The viewer
  uses one gallery and image-detail route for all owner kinds. The focused image,
  contract, tray, and snapshot matrix passed 41/41 after the deleted-owner
  baseline startup fix; the new image rollback case passed separately.
- A forced post-write failure during `image_replace` rolled back the database
  revision and removed the newly staged original asset. The retained original
  remained byte-for-byte readable and integrity had no issues. Existing tests
  cover replacement near the content/size limits, verified backup/restore,
  tampered renditions, and deleted/missing revisions.
- `changes_since` tests and a cross-continuity image replacement test prove
  viewer invalidation is delivered even when the changed image belongs to a
  different continuity. The loaded browser probe proves rendering and keyboard
  navigation; the refresh path is the same record watcher used by other pages.
- The separate record-snapshot review covers pinned record links. Release
  rollout, production migration, and the user-directed real PNG probes through
  ChatGPT and Claude remain Phase 12 cutover/usability work.

**Result:** zero unresolved Debug image findings. This is not Release or
real-client attachment acceptance.
