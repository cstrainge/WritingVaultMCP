# Phase 2 hostile review

Reviewed: 2026-09-27

Scope: foreign keys, delete rules, required values, canonical uniqueness, temporal exclusivity, location hierarchy safety, post-write invariant checks, and startup integrity diagnostics.

## Attacks performed

| Attack | Required result | Evidence |
|---|---|---|
| Remove or redefine any declared relationship | Exact schema verification fails | The verifier compares all 63 declared foreign keys, their columns, and `NO ACTION` update/delete rules against ACE metadata |
| Insert an orphan junction row | Access rejects the write | Direct hostile `EntityTags` insert fails; the exact foreign-key verifier proves every declared dependent/principal pair is enforced |
| Leave a foreign key without a useful leading index | Static schema test fails | `EveryForeignKeyColumnHasALeadingIndex` covers primary, unique, and secondary indexes; two omissions found by review were added in migration `20260927_002_fk_indexes` |
| Upgrade a populated baseline database | Add indexes without rebuilding or losing rows | Forward-migration test preserves a sentinel continuity and verifies the exact current schema |
| Insert null or blank required audit/text values | Access rejects the write | Direct hostile inserts exercise null audit and blank required text; generated check constraints cover all required text columns |
| Race equivalent normalized tag creation | Exactly one row survives | Concurrent create test returns one success and one stable `constraint.duplicate` result |
| Reuse a normalized alias for one owner | Duplicate rejected | Acceptance test rejects case/whitespace variants for one character while permitting the alias for another owner |
| Create two entities with the same display name | Both records remain valid | Acceptance test confirms display names are descriptive and intentionally non-unique |
| Create overlapping membership periods | Same normalized role rejected; different role allowed | Acceptance test exercises case/whitespace normalization and simultaneous distinct roles |
| Store a reversed or malformed story interval | Database or application rejects the write | Generated date checks cover every story-date family; application validation runs before persistence |
| Make a location its own ancestor | Direct and multi-level cycles rejected | Database self-parent check plus in-transaction graph validation and startup cycle diagnostics |
| Race exclusive ownership periods | One commit succeeds and one overlap failure returns | Concurrency test verifies serialization and a valid final database |
| Replace a soft-deleted exclusive period with an overlapping active period | Active replacement remains valid | Regression proves startup diagnostics ignore deleted periods consistently with write-time checks |
| Restore a row that violates an application-only invariant | Transaction rolls back | Every mutation runs the invariant guard after its operation and before commit |
| Start with pre-existing corruption | Server refuses to expose tools | Startup runs exact schema verification followed by hierarchy, interval, ownership, continuity, shape, date, and timezone diagnostics |

## Review findings fixed

1. `CharacterRelationships.ContinuityId` and `OwnershipPrincipals.ContinuityId` lacked leading indexes. A forward migration adds both without rebuilding populated databases.
2. Generic startup overlap diagnostics included soft-deleted periods although write-time checks excluded them. Diagnostics now evaluate active periods only, with a regression covering replacement data.
3. Entity-name, alias, and organization-membership policies were implemented but not explicit enough in the design record. The accepted rules are now documented and directly tested.

## Verification result

- Full command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj -c Debug --no-restore`
- Result: 42 passed, 0 failed, 0 skipped.
- Configuration: Debug only. Release artifacts were not rebuilt or modified.
- Fresh and forward-migrated test databases both pass exact schema and integrity verification.

## Unresolved findings

None.
