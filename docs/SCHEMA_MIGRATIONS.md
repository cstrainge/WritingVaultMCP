# Schema lifecycle

The application never creates or changes schema during an ordinary read or write. Schema changes run only through the administrative command in `Program.cs`.

## Commands

```powershell
dotnet WritingVaultMcp.dll schema status
dotnet WritingVaultMcp.dll schema init --database C:\path\to\new.accdb
dotnet WritingVaultMcp.dll schema migrate
dotnet WritingVaultMcp.dll schema migrate --allow-empty-rebuild --backup-manifest C:\path\to\backup-manifest.json
dotnet WritingVaultMcp.dll schema range-review --database C:\path\to\vault.accdb
```

`schema status` is read-only and returns a structured issue list. It does not include the database path. `schema init` refuses to overwrite a file. `schema migrate` is idempotent on the current schema.

`schema range-review` is a local, read-only inventory of legacy `Range` values after migration 009. It reports each affected table, date field, internal storage key, bounds, and preserved original wording so an administrator can review ambiguous rows. It does not reclassify any value or expose storage keys through the MCP or viewer API. Run it against a verified backup or a stopped database before cutover, and do not interpret `originalText` as certainty.

The v2 baseline is intentionally an empty-database rebuild. The destructive flag requires a manifest whose backup hash matches the recorded source hash. The migrator then counts every user table and refuses the rebuild if any table other than `SchemaMigrations` contains a row. It also refuses to start while a matching `.ldb` or `.laccdb` lock exists. This exception exists only for incomplete legacy baseline initialization; it is not the v4 upgrade path.

Applied migration checksums are immutable. The ordered ledger is `20260927_001_v2_baseline`, `20260927_002_fk_indexes`, `20260927_003_longtext_checks`, `20260928_004_v4_foundations`, `20260929_005_relationship_membership`, `20260930_006_relationship_merge`, `20260930_007_relationship_transitions`, `20260930_008_membership_transition_descriptions`, `20261001_009_explicit_range_meaning`, then `20261001_010_story_image_owners`. The index migration adds leading continuity foreign-key indexes. The Long Text migration removes two Access checks that fail on large values; application validation still rejects blank text. Migration 004 adds continuity notes, entity-event/project associations, temporal profiles/effects, and entity-owned image metadata/renditions. Migration 005 adds group relationship participants, per-character repeatable membership periods, relationship-owned events, and event/project links; it backfills existing pairs without merging independently authored relationships. Migration 006 adds a one-way redirect ledger for explicit, reviewed consolidation of legacy pair identities. Migration 007 stores independently fuzzy join and leave dates for each relationship membership period, preserving the period reference and its possible-occupancy envelope. Migration 008 adds transition-specific relationship descriptions and named organization joins/leaves with independent dates and descriptions. Exact legacy dates are backfilled; fuzzy and open legacy periods remain unasserted. Migration 009 expands stored date constraints to permit `KnownRange` and `UncertainRange`; existing `Range` rows remain unchanged and must be reviewed rather than guessed. Migration 010 adds continuity, relationship, entity-event, and relationship-event image storage and renditions without copying old rendition blobs or changing old entity-image references. Existing databases upgrade in place without dropping story data. Forward DDL can resume when exact expected objects already exist; unexpected definitions fail verification. Migrations 005–010 have not been applied to production.

The ledger continues with `20261001_011_image_content_revisions` and `20261001_012_record_page_snapshots`. Migration 011 records the current content revision for each existing entity or story image, starting at revision 1, and creates storage for retained older originals and checksummed display/thumbnail renditions. It does not duplicate the current rendition blobs during upgrade. A populated migration-010 copy upgraded to 011 with exact schema verification and zero integrity issues. Migration 012 adds immutable, checksummed, bounded page snapshots and a mutable reverse-dependency index. The backend captures a clearly labeled baseline of the current page for every navigable record on first v4 startup; earlier page states are not reconstructed from incomplete audit deltas. Later affected-page snapshots are saved in the mutation transaction. A populated 011 copy upgraded to 012 with exact schema verification and zero integrity issues.

## Verification

The verifier compares the database with the current definition in `AccessSchemaDefinition.cs`: exact user tables, columns, provider types, text lengths, required flags, defaults, primary and secondary indexes, foreign-key columns, non-cascading rules, and every ordered migration ledger ID/checksum/application version/status. Unexpected user objects and unexpected migration rows are drift. Access-owned `MSys` objects are ignored.

Writes must pass `SchemaWriteGate` first. A failed verification must keep normal write tools unavailable. Health reporting may expose issue codes and object names, but it must not expose SQL, connection strings, or filesystem paths.

## Interrupted rebuild recovery

Access DDL is not transactionally atomic. A process interruption can therefore leave a partial empty schema. Recovery is safe only because this baseline migration is restricted to an empty database:

1. Stop the MCP process and Microsoft Access.
2. Confirm no matching `.ldb` or `.laccdb` remains. Investigate a persistent lock rather than deleting an active lock file.
3. Run `schema status` and retain its report.
4. Confirm the Phase 0 backup still matches its manifest.
5. Rerun the same `schema migrate --allow-empty-rebuild --backup-manifest ...` command.
6. The migrator refuses if any non-ledger table contains data. Otherwise, it removes the partial user schema and recreates the reviewed definition.
7. Run `schema status` again and require `valid: true`.

If any user table contains data, do not force the baseline rebuild. Preserve the failed file and follow the verified-backup procedure in [INCIDENT_RECOVERY.md](INCIDENT_RECOVERY.md). The Phase 0 baseline record is local recovery evidence and is excluded from the prospective repository until reviewed.

For an interrupted forward migration on a populated database, preserve the file and verified pre-migration backup, then run `schema status`. Resume `schema migrate` only when the verifier reports missing objects or migration rows accounted for by the pending migration commands. A mismatched or unexpected object requires investigation or restore; the empty-baseline rebuild flag must never be used to bypass it.

## Project story events and repeats

Migration `20261002_013_project_story_events` adds nullable boundary-role metadata to entity events, nullable recurrence frequency/interval/stop-date fields to world/entity/relationship events, and a false-by-default recurring-birthday flag to characters. Existing records keep their dates and remain nonrecurring. The previously released migration-012 fingerprint is fixed. The forward migrator recognizes additive columns and safely resumes a partially applied migration; it does not rebuild populated tables. Project ownership, unique active boundaries, chronology, recurrence shape, and birthday date compatibility are checked in write transactions and integrity verification.

Migration 013 was verified on a restored, checksummed migration-012 production backup before release: exact schema verification passed and integrity reported no issues.
