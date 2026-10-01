# Phase 8 hostile review

Date: 2026-09-28  
Result: PASS - zero unresolved Phase 8 issues  
Validation: 83/83 Debug tests passed, 0 skipped

## Attack surface reviewed

- Required text, normalization, maximum lengths, invalid IDs, enums, finite numbers, UTC ranges, fictional-date kinds, inclusivity, ordering, and age status boundaries.
- Patch omitted/null/value semantics, immutable subtype identity, optimistic concurrency, hierarchy cycles, and exclusive temporal overlap.
- Fresh disposable Access isolation, schema constraints, foreign keys, indexes, Unicode/Long Text, graph/reverse reads, versioned lifecycle, rollback, cancellation, and lock-file release.
- Concurrent updates, canonical uniqueness races, ownership races, idempotent replay, changed-input rejection, Access lock contention, and consistent aggregate reads.
- Blank/current/legacy migration, forward populated migration, drift detection, interrupted recovery, pre-migration backup, verified restore, and destructive-rebuild refusal when user data exists.
- MCP initialization, exact tool discovery, read-only registration, shared transport, semantic contracts, malformed/oversized input, cancellation, disconnect shutdown, response redaction, and legacy-surface absence.

## Hostile findings resolved

1. Phase 7 protocol tests covered representative tools but did not make the complete public contract an executable invariant. Reflection tests now require all 48 mutation tools to advertise idempotency, accept a request type with `RequestToken`, and return the single `McpMutationResult` schema.
2. Storage-key names could reappear through a future request/result record even if discovery snapshots were not updated. The contract test recursively inspects every registered method's MCP-owned parameter and result properties and rejects known storage-identity names.
3. Malformed and oversized requests, cancelled calls, and read errors lacked one hostile protocol regression. The new protocol test verifies stable structured validation, cancellation, and absence of paths, SQL, OleDb details, numeric storage keys, and GUIDs.
4. A passing suite could silently contain skipped safety coverage. The suite now reflects over all facts and fails if any has a skip reason.
5. Recovery tests restored valid copies but did not first prove that an intentionally damaged copy was rejected. The Phase 10 recovery acceptance test truncates a disposable copy, confirms rejection, restores a verified backup, reruns schema/integrity, and serves an application read.

## Coverage rationale

- `AccessVaultService` is the sole active repository contract. CRUD/search/version/delete coverage is therefore organized by every accepted aggregate and relation family rather than duplicated across nonexistent per-entity repository implementations.
- All mutation tools use one request-token derivation path and one `VaultWriteCoordinator` transaction/idempotency boundary. The 48-tool architectural contract plus coordinator race/replay tests proves the common property without copying the coordinator implementation into 48 shallow tests.
- Success behavior is exercised across every entity and relation family by application acceptance and end-to-end tests. Protocol tests separately prove JSON binding, discovery, read-only filtering, structured success/error envelopes, and transport lifecycle.

## Gate evidence

- Each `TestVault` creates a new random directory and `.accdb`, migrates it, asserts lock cleanup, and recursively removes it after the test.
- Direct hostile-row tests prove Access constraints and foreign keys reject invalid rows independently of application validation.
- Fault injection proves data, processed-operation state, and journal entries roll back together.
- The representative-volume test uses 1,000 entities and enforces search, details, integrity, and verified-backup latency ceilings.
- Command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj --configuration Debug --no-restore --no-build`
- Result: 83 passed, 0 failed, 0 skipped in 2 minutes 41 seconds.
- Build: 0 warnings, 0 errors.

