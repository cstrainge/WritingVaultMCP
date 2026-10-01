# Phase 1 hostile review

Reviewed: 2026-09-27

Scope: database initialization, v2 baseline migration, drift detection, backup evidence, interruption recovery, lock release, and live empty-database cutover.

## Attacks performed

| Attack | Required result | Evidence |
|---|---|---|
| Initialize a nonexistent database | File created and fully migrated | `fresh-init-hostile.accdb` initialized; verifier returned no issues |
| Re-run current migration | No DDL change | `changed: false`; checksum unchanged |
| Remove a required unique index | Structured drift rejection | `schema.index_missing` for `Tags.UX_Tags_NormalizedName` |
| Leave a partial empty schema | Documented rebuild recovers it | `interrupted.accdb` rebuilt and verified |
| Put a row in a legacy user table | Destructive rebuild refused | Refused with `Tags` named as nonempty |
| Present an Access lock file | Migration refused before opening | Simulated `.ldb` rejected with an actionable lock message |
| Initialize through ADOX | No abandoned lock | Both `.ldb` and `.laccdb` absent immediately after completion |
| Use a live file containing `MSysNavPane` relationships | System metadata ignored, user metadata exact | Live verifier returned no issues after system-object filtering |
| Modify the migration definition after application | Checksum mismatch reported | Verifier compares the stored and compiled checksums |
| Add a newer/unknown migration ledger row | Newer schema rejected | Verifier reports `schema.migration_unexpected` |
| Supply a damaged or unrelated backup | Rebuild refused | Streaming SHA-256 verification requires backup and recorded source hashes to match |
| Attempt rebuild with an active schema but no destructive flag | Rebuild refused | Explicit `--allow-empty-rebuild` is required |

## Live result

- Phase 0 source and OneDrive backup hashes matched immediately before cutover.
- Microsoft Access process count was zero and no live lock file existed.
- The live database now contains 42 v2 user tables, 62 enforced user relationships, one migration-ledger row, and zero non-ledger rows.
- Schema migration ID: `20260927_001_v2_baseline`.
- Schema definition checksum: `7293C15E58E12AF4C6D1E504EE055D0224959DABCFD5AEF1E3677D62CDCB6C2F`.
- A second live migration run returned `changed: false`.
- The final live connection released its lock file.
- The original verified Phase 0 database remains in the OneDrive backup root.

## Unresolved findings

None.

