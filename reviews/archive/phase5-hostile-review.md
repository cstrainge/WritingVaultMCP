# Phase 5 hostile review

Date: 2026-09-27  
Result: PASS — zero unresolved Phase 5 issues  
Validation: 62/62 Debug tests passed; Release was not built

## Attack surface reviewed

- Transactional coupling among domain writes, idempotency records, and `ChangeLog` entries.
- Correlation IDs across caller input, deletion metadata, processed operations, history queries, purge, and logs.
- Journal attribution and story-content exposure.
- Soft-delete visibility in direct reads, searches, entity graphs, source graphs, reverse relationships, and deleted-only recovery searches.
- Restore behavior for entities, relationships, vault metadata, relationship types, and ownership principals.
- Delete blocker reporting and purge blocker revalidation.
- Backup locking, stable-copy behavior, hashing, manifest configuration, retention, restore verification, and application serviceability.
- Purge replay, preview expiry, database-path binding, backup recency, backup purpose, and intervening database changes.

## Hostile findings resolved

1. Normal entity/source graphs could return deleted owners or deleted related records. Normal graph roots and counterpart joins now exclude them; explicit root inclusion is opt-in.
2. Search APIs could return active-only or active-plus-deleted but could not satisfy the accepted deleted-only recovery mode. All primary searches/lists now expose an explicit deleted-only mode.
3. Deletion metadata used the raw caller GUID. Brace-form GUIDs could exceed `TEXT(36)` and diverge from journal correlation. The coordinator now supplies one canonical `D` GUID to every transactional delete.
4. Purge preview tokens used a different GUID representation from normal operation history. Purge now uses the same canonical operation-ID form.
5. History reads omitted the client label and nullable labels weakened attribution. History now returns the label and writes `unspecified` when a caller supplies none.
6. Some journal outcomes serialized complete long-form request fields. Journal serialization now omits duplicate correlation fields and replaces configured long-form content with length and SHA-256 metadata. Operational logs still contain correlation metadata only.
7. Continuity and variant-group deletion returned generic blocker prose. Errors now report concrete entity, claim, member, relationship, ownership-link, and custody counts as applicable.
8. Relationship types and ownership principals had soft-delete columns but no supported delete/restore path. Both now use the versioned vault-record lifecycle and refuse deletion while referenced.
9. Backup checked locks only before copying and did not hold a stable source handle throughout hash/copy. It now holds the owner lease and a read handle that denies writers for the complete operation, cleans incomplete artifacts, and records provider and retention configuration.
10. Retention was available only as a method argument. It is now configurable through `BackupRetentionCount` and the `--retention` administrative option, with a hard minimum of two.
11. Purge accepted a recent valid backup even if the database changed afterward. Execute now requires the current closed database SHA-256 to match the backup's source SHA-256 exactly.
12. The prior restore test stopped after copying the file. The restore drill now constructs the application service against the restored vault and proves a known record can be served.

## Gate evidence

- Exactly-once mutation and replay tests prove one committed data change, one processed operation, and one journal row.
- Injected fault and cancellation tests prove rollback removes data, operation, and journal writes together.
- Delete/restore tests prove retained relationships disappear from normal reads while an endpoint is deleted and reappear after restore.
- Backup tests prove owner-lock refusal, hash/schema/integrity verification, minimum-two retention, no overwrite restore, and application reads from the restored path.
- Purge tests prove soft-delete/version/preview requirements, same-database backup binding, rejection after intervening writes, replay safety, and one purge journal entry.
- Full command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj --configuration Debug --no-restore`
- Result: 62 passed, 0 failed, 0 skipped.

The approved semantic-reference and shared-backend work remains assigned to the later MCP/process phases in `DESIGN_DECISIONS.md` section 14. It does not weaken the Phase 5 transaction, deletion, or recovery guarantees reviewed here.
