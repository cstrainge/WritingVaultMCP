# Phase 7 hostile review

Date: 2026-09-27  
Result: PASS - zero unresolved Phase 7 issues  
Validation: 73/73 Debug tests passed; Release was not built or modified

## Attack surface reviewed

- MCP initialization, exact tool discovery, read-only registration, structured schemas, and stdio-to-stream forwarding.
- Backend election per normalized database path, current-user named-pipe isolation, simultaneous adapters, connection teardown, in-flight draining, and idle shutdown.
- Per-connection continuity selection and artificial-time state, persisted continuity-clock fallback, and exact age/local-time/temporal-state behavior.
- Semantic-reference creation, resolution, rename stability, continuity scoping, cursor use, nested graph/history mapping, and every Access descriptor query.
- Mutation request-token derivation, cross-client replay, changed-input rejection boundary, optimistic versions, transaction/journal entry, and safe response mapping.
- Client-facing inputs, outputs, schemas, errors, history, cursors, and discovery metadata for numeric Access keys, internal operation GUIDs, SQL, provider details, connection strings, and filesystem paths.
- Documentation, design decisions, Debug-only development status, and the unchanged older Release artifact.

## Hostile findings resolved

1. The v2 MCP surface accepted and returned numeric keys and operation GUIDs. The registered v3 surface now selects continuity by name, uses stable semantic references for other records, and accepts readable vault-wide request tokens whose internal GUIDs never cross the MCP boundary.
2. Legacy numeric tool classes remained compiled with MCP attributes even though the host did not register them. `Mcp/VaultTools.cs` is now excluded from the production compile graph, its neutral health result moved to the semantic result module, and a protocol assertion proves both legacy tool types are absent from the assembly.
3. Tool discovery had only spot checks. Protocol tests now require the exact 20 read/session tools on read-only connections and the exact 68-tool read/write surface on normal connections; unexpected legacy tools and missing documented tools fail the test.
4. Separate client processes would compete for Access ownership. Each stdio process is now an adapter to one mutex-elected backend per database path, using a current-user-only named pipe and one shared write coordinator.
5. Continuity and artificial time were process-global concepts. They are now connection-local session state; simultaneous clients can choose different continuity names and exact zoned instants while sharing the same database backend.
6. Client attribution was included in the idempotency input hash, so an identical timed-out request could not replay through a different adapter. Attribution remains journaled but no longer changes semantic request identity.
7. Several semantic-reference queries used Access's `Nz()` expression. ACE OleDb rejects that function, allowing a mutation to commit and then return `storage.failure` while formatting the response. All reference descriptors now select nullable values directly and apply fallback labels in managed code; every descriptor query has a provider regression test.
8. Organization temporal-state locations were mapped as object-location references. Mapping now selects `OrganizationLocation` for organizations and `ObjectLocationPeriod` for objects, with a regression test covering the public result.
9. A backend launched by an adapter that disappeared before connecting could remain alive with a zero reference count. An initial five-second connection grace now terminates that orphan, while the ordinary two-second grace still applies after the last established client disconnects.
10. Operational and installation documents still described one client process, operation IDs, and numeric record IDs. They now describe the shared backend, request tokens, semantic references, and the temporary distinction between the older Release build and Phase 7 Debug build.

## Gate evidence

- The production assembly exposes 68 documented semantic tools: 20 read/session tools and 48 mutation tools.
- Read-only adapters expose exactly the 20 read/session tools and no database mutation or administrative tool.
- Schema inspection rejects `operationId`, `continuityId`, entity/key ID fields, and operation-GUID language; result and history probes reject GUID values and storage-key properties.
- Two real stdio adapters concurrently attach to one stream backend, maintain independent time state, calculate different ages from the same character, and replay an identical mutation across client labels.
- Semantic references remain stable across rename, old slugs continue resolving, and all 30 provider-backed reference descriptors execute successfully.
- Backend tests prove shutdown after the last adapter and self-termination when no initial adapter arrives.
- Targeted command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj --configuration Debug --no-restore --filter "FullyQualifiedName~Phase7McpTests|FullyQualifiedName~McpProtocolTests"`
- Targeted result: 9 passed, 0 failed, 0 skipped in 58 seconds.
- Full command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj --configuration Debug --no-restore`
- Full result: 73 passed, 0 failed, 0 skipped in 1 minute 56 seconds.
- Build command: `dotnet build --configuration Debug --no-restore`
- Build result: 0 warnings, 0 errors.
- The Release DLL timestamp remains 2026-09-27 21:45:40 UTC; no Release command was run.

Phase 8 remains responsible for exhaustive success/error-schema coverage, malformed and oversized protocol input, disconnect/cancellation fault injection, and replay coverage for every individual mutation tool.
