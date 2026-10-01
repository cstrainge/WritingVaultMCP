# Phase 9 hostile review

Date: 2026-09-28  
Result: PASS - zero unresolved Phase 9 issues  
Validation: 83/83 Debug tests passed, 0 skipped

## Attack surface reviewed

- Database-path canonicalization, extension checks, UNC/mapped-drive rejection, x64/provider diagnostics, local transport scope, per-connection read-only registration, and administrative command separation.
- Structured logging destinations and fields, story-content/path redaction, operation correlation, retries, conflicts, lifecycle, schema/integrity, backup/migration, health, and size warnings.
- Representative volume, required indexes, result bounds, 1.2 GiB warning/1.5 GiB refusal policy, compact/repair procedure, archival threshold, backup retention, and restore workflow.
- Setup, client installation, tool reference, time/date behavior, logical data model, deletion/recovery, schema migration, operations, troubleshooting, incident response, and release checklist.

## Hostile findings resolved

1. The backend cleared SDK log providers but supplied no replacement operational diagnostics. `VaultDiagnostics` now emits one JSON object per stderr line for adapter/backend/client lifecycle, schema/integrity checks, reads, mutation queue/commit/replay/rollback/retry, administrative commands, provider failures, and size warnings.
2. Diagnostic logging could have copied arbitrary request objects and story content. The logger has a closed scalar field set and no request/body/path parameter; a regression test writes private story text and proves neither it nor the database path appears.
3. The server canonicalized paths but accepted network locations despite the documented single-host boundary. Startup now rejects UNC paths and mapped network drives, as well as missing files and non-`.accdb` extensions.
4. Missing ACE/x64 failures were reduced to generic startup failure. The backend now records `provider.unavailable` and emits an actionable local message naming the configured provider and required x64 architecture while MCP clients retain sanitized errors.
5. The documented 1.2 GiB warning existed without an implementation. Startup now emits `database.size_warning` above 1.2 GiB and still refuses databases above 1.5 GiB.
6. The logical-model document still claimed the physical schema was unimplemented, included a rejected direct project-source relation, and used the obsolete `ExactDateTime` kind. Documentation now matches the implemented schema and `ExactInstant` contract.
7. The restore smoke command omitted the `serve` verb. It is corrected and covered by the actual MCP restore drill.
8. Pre-migration backups could be created but the CLI could not explicitly verify an intentionally older schema. `backup verify --allow-incompatible-schema` now verifies those manifests without weakening normal current-schema verification.

## Gate evidence

- Operational tests prove local-path policy, structured redaction, and all health signals.
- The generated synthetic vault contains 1,000 entities; measured search/details remain below 2 seconds, integrity below 10 seconds, and verified backup below 15 seconds on the supported machine.
- All 68 registered tools are documented, and client installation covers Claude Desktop plus OpenAI Secure MCP Tunnel.
- Backup, restore, migration, locks, compact/repair, permissions, modes, retention, incident response, and release procedures are documented under `docs/`.
- Command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj --configuration Debug --no-restore --no-build`
- Result: 83 passed, 0 failed, 0 skipped in 2 minutes 41 seconds.
- Build: 0 warnings, 0 errors.

