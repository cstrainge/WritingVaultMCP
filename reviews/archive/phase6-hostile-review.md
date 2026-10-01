# Phase 6 hostile review

Date: 2026-09-27  
Result: PASS — zero unresolved Phase 6 issues  
Validation: 66/66 Debug tests passed; Release was not built

## Attack surface reviewed

- Production compile graph and the risk of two competing Access implementations.
- Create, patch, read, graph, reverse-lookup, history, timeline, and metadata coverage for every accepted entity and relation family.
- Pagination, deterministic ordering, aggregate consistency, result-size limits, Long Text handling, and representative local performance.
- Positional parameter use, generated identities, transaction scope, cancellation, rollback, command disposal, connection disposal, and ACE lock-file cleanup.
- Error classification and MCP-boundary redaction of provider messages, SQL, database paths, and connection details.
- Schema ownership, forward migration ordering, immutable prior checksums, partial-DDL recovery, exact verification, and populated-database preservation.
- UTC audit conventions, timezone-free fictional dates, zoned continuity clocks, and source-snapshot retrieval metadata.

## Hostile findings resolved

1. The active character contract did not expose every stored core field. Create and patch now cover middle/family/preferred names, gender, pronouns, species, occupation, nationality, physical description, and personality summary, with subtype and duplicate-field conflict validation.
2. Graph expansion accepted arbitrarily large requested limits and returned complete Long Text values. Graph collections now cap at 50, relationship reads at 100, history at 200, and Long Text reads at 65,536 characters with explicit truncation flags.
3. Required Long Text check constraints used `Len(Trim(...))`. ACE throws while evaluating those expressions for large values, including valid 70,000-character notes. Required columns retain `NOT NULL`, application validation rejects blank writes, and forward migration `20260927_003_longtext_checks` removes the unsafe database expressions.
4. The forward migrator assumed only one missing migration. It now applies every missing known migration in order, accepts only drift addressed by those migrations, and safely resumes when non-transactional Access DDL was partially applied.
5. Adding a cached source snapshot left the source's retrieval metadata stale. Snapshot creation now updates `LastRetrievedAtUtc`, `RetrievalStatus`, and `UpdatedAtUtc` in the same transaction.
6. Read tools could propagate raw ACE or filesystem exception text through MCP. Every read tool now uses one sanitizing boundary that emits a fixed operation message without an inner exception; cancellation still propagates unchanged. Write failures continue through the centralized stable-code classifier.
7. Command timeout policy existed only as an unexplained literal. `AccessCommand.DefaultTimeoutSeconds` now defines the 30-second policy used by every application command and the runtime documentation records it.
8. Superseded repository experiments could be mistaken for active production contracts. The project explicitly excludes them, and the implementation plan and design decisions name `AccessVaultService` as the sole active Access implementation.
9. Tool documentation and graph defaults still advertised the older 100/200 expansion behavior. The documented and declared defaults now match the enforced 50-item graph cap and truncation contract.

## Gate evidence

- Phase 6 integration tests round-trip and patch every character core field, verify graph and Long Text caps, prove source snapshot metadata coupling, and prove client-error redaction.
- Schema tests create an exact fresh vault, rerun migration idempotently, and upgrade a populated migration-001 vault through migrations 002 and 003 without rebuilding or losing its row.
- Structural tests prove every foreign-key column has a leading index and exact schema verification rejects index or constraint drift.
- The 1,000-entity performance test enforces two-second search/detail ceilings, a ten-second integrity ceiling, and a fifteen-second verified-backup ceiling on the supported local development environment.
- Cancellation and injected-failure tests prove data, idempotency, and journal writes roll back together and temporary ACE lock files are released.
- Inspection of all active interpolated SQL found only constants, validated integer limits, and closed internal table/column mappings. No client supplies an identifier, expression, database path, connection string, or SQL fragment.
- Full command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj --configuration Debug --no-restore`
- Result: 66 passed, 0 failed, 0 skipped in 1 minute 20 seconds.

The approved semantic-reference and shared-backend/session model remains Phase 7 work under `DESIGN_DECISIONS.md` section 14. Phase 6 does not implement or claim those user-facing process changes.
