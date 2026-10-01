# Phase 10 hostile review

Date: 2026-09-28  
Result: PASS - zero unresolved Phase 10 issues  
Validation: 84/84 Release tests passed, 0 skipped

## Attack surface reviewed

- Build reproducibility, stale binaries, explicit test-project selection, skipped-test detection, and Release/Debug parity.
- Live-database ownership, lock release, pre-migration evidence, migration authorization, schema fingerprint, physical relationships, row counts, and post-migration integrity.
- Read-only discovery, write enablement, semantic references, hidden storage identities, connection-local continuity/time, idempotent retry, journaling, versioning, soft deletion, restoration, and queue drainage.
- Backup hash and manifest verification, restoration to a new path, deliberate isolated mutations after restore, and rollback retention.
- Tool-reference completeness, client installation instructions, accepted design decisions, release checklist, runtime guidance, and cutover evidence.

## Hostile findings resolved

1. The first Release test invocation used a stale test assembly and reported only 38 passing tests. The Release test project is now built explicitly before `dotnet test --no-build`; the current 84-test assembly passed with zero failures and zero skips.
2. Claude still held the old Release DLL open. The exact child command line was inspected, only that WritingVault MCP child was stopped, and the replacement Release artifact then built cleanly. No project process or live Access lock remained after validation.
3. The client guide and design decisions still described the Release as pre-Phase-7 and the live database as uncut-over. Those statements now describe the verified shared backend and completed production migration.
4. A valid backup alone would not prove operational recovery. The final regular backup was verified, restored to a new disposable path, checked for schema and integrity, and exercised through both read-only and write-enabled Release MCP sessions.
5. A release smoke test could leave unexplained live rows. The cutover record identifies every nonempty table and explicitly records the two auditable verification continuities and their journal/idempotency rows.
6. Documentation could drift from tool discovery. A source-to-reference reconciliation found 68 registered semantic tools and 68 documented tools, with no missing names; protocol tests separately assert exact registration and read-only filtering.
7. The tunnel launcher still targeted a populated usability database at migration 001, while the new Release requires migration 003. A verified OneDrive pre-migration backup was created, the ordered forward migration preserved every existing data row, and the database then passed schema, integrity, Release MCP smoke, and post-migration backup verification.
8. Two local mount paths (`D:\<workspace>` and `%USERPROFILE%\workdir\<workspace>`) mapped to the same ReFS volume. Text-only path normalization let Claude and the tunnel elect competing backends for the same physical database. Process and backup identities now use the Windows volume GUID plus relative path; an isolated Release smoke proved adapters on both mounts shared one pipe and exited successfully. (Historical paths sanitized for repository publication.)
9. The batch launcher discarded a prompted tunnel API key when it exited. It now stores a current-user DPAPI ciphertext outside the repository, reloads it into process scope, clears the environment value on exit, and supports explicit deletion with `-ForgetApiKey`. A disposable round-trip passed in Windows PowerShell 5.1.

## Gate evidence

- Release builds: 0 warnings, 0 errors.
- Release suite: 84 passed, 0 failed, 0 skipped in 2 minutes 49 seconds.
- Live Release MCP smoke: 1 passed, 0 failed in 24 seconds.
- Restored-copy Release MCP smoke: 1 passed, 0 failed in 24 seconds.
- Live schema: migration `20260927_003_longtext_checks`, checksum `73EEB64F808A189EFD88B979CD4883E529896F9EA451430D86CB03AD81AA3A2A`, zero issues.
- Live integrity: valid, zero issues.
- Final backup: 3,039,232 bytes, SHA-256 `120B89E49D7FD018D67B82C45CD6B7FEE9A65BEE3CE7F3E356C390415A78DB33`, manifest verified.
- Restored copy: schema and integrity valid; read-only and write-enabled MCP smoke passed.
- Tunnel usability database: existing rows preserved, migration 003 valid, integrity valid, Release read-only/write MCP smoke passed, and a verified post-smoke backup was created.
- Rollback artifacts have an explicit retain-through date of 2026-10-28.
- Full evidence: `artifacts/PHASE10_CUTOVER_REPORT.md`.
