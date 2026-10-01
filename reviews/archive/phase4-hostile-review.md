# Phase 4 hostile review

Reviewed: 2026-09-27

Scope: MCP mutation boundary, validation, optimistic concurrency, idempotency, retries, transaction atomicity, patch semantics, aggregate temporal transitions, and provider-error containment.

## Attacks performed

| Attack | Required result | Evidence |
|---|---|---|
| Reach raw OleDb mutation from an MCP tool | No callable path exists | MCP write tools depend only on `AccessVaultService`; raw commands, write context, and coordinator execution are internal |
| Send non-positive IDs/versions, undefined enums, non-finite numbers, excessive strings/lists, or out-of-range dates | Structured validation failure before persistence | Shared reflection validator plus command-specific semantic validation and boundary tests |
| Use a local date whose UTC clock value falls below Access's minimum | Clock rejected | Clock validation checks the actual UTC value written to Access |
| Race two versioned updates | One success and one conflict with current version | Entity update acceptance test returns one `ok` and one `concurrency.conflict` |
| Race two ownership transfers from one prior period | One transfer commits; stale transfer conflicts | Aggregate-transition test verifies one success, one conflict, closed prior row version 2, and one active replacement |
| Retry one create concurrently with the same operation ID | One commit, one replay, one row, one operation row, one journal row | Concurrent idempotency test verifies exactly-once persistence |
| Reformat the same operation GUID with braces/case changes | Original result replays | Operation IDs are canonicalized to lowercase `D` form before hashing, lookup, persistence, and journaling |
| Reuse one operation ID with different semantic input | Request rejected | Returns stable `idempotency.input_mismatch` without a second mutation |
| Use the all-zero default GUID | Request rejected | Returns `validation.operation_id` |
| Throw or cancel after transactional work starts | Data, operation row, and journal all roll back | Fault and cancellation tests verify empty affected tables and released lock files |
| Cancel snapshot caching after a file is created | Unreferenced cache and temporary file cleaned | Cancellation/error cleanup uses an uncancelled cleanup token and temporary-file `finally` block |
| Hold the database with an exclusive Access lock | Bounded safe failure | Three jittered attempts finish within the test bound and return retryable `storage.failure` without paths or SQL |
| Link a note to a missing/deleted source | Stable not-found failure before insert | Same-transaction reference checks return `entity.not_found` |
| Mutate a clock belonging to a deleted continuity | Write rejected | Active-continuity reference check runs in the clock transaction |
| Close an open ownership/residence/membership history manually | One atomic transition closes prior history and opens or ends replacement history | Versioned aggregate commands update and insert under one transaction and invariant pass |
| Duplicate canon into another continuity | Independent core copy; no shared mutable relationships | Duplication test clears identity relationships/variant group and proves later target edits do not affect source |
| Send omitted, explicit null, empty, and whitespace patch values | Documented distinct behavior | Specified-field wrappers preserve omission; null clears nullable fields; empty optional long text is retained; required names reject blank input |
| Trigger an OleDb exception | No provider details escape | Central classifier returns stable error families and safe messages; MCP tests reject SQL/path leakage |

## Review findings fixed

1. Undefined enum values and non-finite floating-point inputs were not uniformly rejected at the shared boundary.
2. Artificial-clock range validation inspected the local year instead of the UTC value actually stored.
3. Snapshot cancellation could leave an unreferenced content file or partial temporary file.
4. Equivalent GUID text forms produced false idempotency mismatches; the empty GUID was also accepted.
5. Note-source linking relied on a foreign-key exception instead of active-record reference checks, and deleted continuities could still have their clocks changed.
6. Open-ended temporal histories lacked usable edit commands. Versioned ownership transfer, residence transition, and membership end/replacement commands now close and open periods atomically.
7. Continuity-aware duplication was missing. The new aggregate copies only core subtype data and deliberately clears identity-bearing relationships.

## Verification result

- Full command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj -c Debug --no-restore`
- Result: 53 passed, 0 failed, 0 skipped.
- Configuration: Debug only. Release artifacts were not rebuilt or modified.
- Final test databases pass schema and startup integrity verification after concurrency and transition attacks.

## Unresolved findings

None.
