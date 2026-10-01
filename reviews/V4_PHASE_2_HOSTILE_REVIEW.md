# v4 Phase 2 hostile storage review

Review date: 2026-09-28  
Scope: additive Access migration, exact schema verification, referential and continuity boundaries, temporal rows, event/project associations, image components, backup/recovery, interruption behavior, and v3 transitional compatibility  
Verdict: **Pass with zero unresolved findings.**

## Attacks and results

| Attack | Required result | Evidence |
| --- | --- | --- |
| Migrate a populated v3-shaped database | Preserve existing rows and apply only the additive v4 objects | `PopulatedV3ShapeMigratesForwardWithoutLosingExistingRows` preserves a sentinel continuity and finishes with exact schema verification. |
| Interrupt Access DDL after only one new table | Resume object by object without duplicating or rebuilding correct objects | `InterruptedV4ForwardMigrationResumesIdempotently` completes the partial migration and proves the next migration is a no-op. |
| Insert orphan temporal, image, or event/project rows | Access foreign keys reject every orphan | `NewDatabaseConstraintsRejectInvalidTemporalAndImageRows` performs direct hostile inserts. |
| Leave a v4 foreign key without a leading index | Static schema gate fails | `EveryV4ForeignKeyHasALeadingIndex` covers all twelve new foreign keys. |
| Cross continuity with an entity event/project or temporal cause | Transaction guard and startup integrity checks reject or diagnose it | New continuity probes cover both event kinds and both temporal-effect references; the application batch test proves zero partial writes. |
| Create duplicate active primary images | Integrity verification fails | `IntegrityVerifierFindsMalformedV4ShapesThatForeignKeysCannotExpress` creates two primaries and requires an `EntityImages.IsPrimary` issue. |
| Commit an image missing one or both renditions | Entire mutation rolls back | `TransactionGuardRollsBackAnImageWithoutBothRenditions` leaves no image row. |
| Lie about rendition byte length or use an unsafe master path/hash | Integrity verification reports the exact malformed shape | The hostile malformed-database test checks byte length and path; the verifier also validates SHA-256 shape. |
| Exceed input or rendition capacity | Database checks reject the row | Direct inserts exercise the 20 MiB original ceiling and the 512-pixel thumbnail ceiling; schema also fixes display and byte ceilings. |
| Overlap active temporal effects | Post-write and startup interval checks reject the overlap | `CharacterTemporalEffects` participates in the common active-row overlap verifier and transaction invariant guard. |
| Lose the database during migration | Restore the exact pre-migration bytes, then migrate the restored copy | `PreMigrationBackupRestoresByteForByteThenMigratesForward` records the actual v3 migration, matches SHA-256, migrates, and preserves the sentinel row. |
| Lose live companion image files after a regular backup | Recover immutable originals from the backup set itself | `BackupManifestVerifiesEveryReferencedImageMaster` proves a separate hashed asset snapshot survives live-file tampering and restores to a new asset root. |
| Tamper with a database, manifest, or asset snapshot | Backup verification fails closed | Hash, schema, integrity, manifest metadata, safe-path, size, and asset hash checks are all required before a backup is accepted. |
| Keep an old backup mutation token after retention expires | Do not create a second backup | Durable receipts preserve token/input identity; `ExpiredBackupTokenCannotCreateASecondBackupAfterRetention` proves the retained backup count does not change. |
| Run v3 operations on the transitional v4 schema | Existing behavior remains valid | `V3ApplicationOperationsContinueOnTheTransitionalV4Schema` creates and searches v3 entities successfully. |

## Findings resolved during review

1. The first backup implementation verified live image masters and listed their hashes, but did not copy them into the retained backup. It now creates a per-backup asset snapshot, verifies each copied file, includes the snapshot in retention, verifies it independently of live assets, and supports database-plus-assets restoration to new paths.
2. Backup idempotency originally depended on a retained manifest. After retention removed it, the same mutation token could create a second backup. Durable operation receipts now reject reuse after expiry and still reject changed input.
3. Image checks initially covered positive dimensions but not all storage ceilings. Exact original, thumbnail, and display byte/dimension constraints are now in the schema and verifier path.
4. Pre-migration manifests initially risked claiming the current migration rather than the copied database's actual migration. Manifest creation and verification now read and compare the latest applied migration from the backup itself.
5. Combined database/asset restore cleanup could race a path created by another process after validation. Restore now verifies into unique staging paths, installs without overwrite, and removes only paths created by that restore. Backup reads also reject reparse points below the configured asset root.

## Verification

- Phase 2/3 focused Debug gate: **39 passed, 0 failed, 0 skipped**.
- Full Debug regression suite: **121 passed, 0 failed, 0 skipped**.
- Debug build: zero warnings and zero errors.
- Tests used disposable Access databases and temporary asset roots.
- The production database was not migrated or modified.
- Release artifacts were not built, launched, or changed.

## Unresolved findings

None.
