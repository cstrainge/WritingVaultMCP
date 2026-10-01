# v3 session onboarding hostile review

Date: 2026-09-28  
Result: PASS - zero unresolved implementation issues  
Validation: 84/84 Debug and 84/84 Release tests passed, 0 skipped

## Report reviewed

An external ChatGPT usability run saw v2 numeric-ID declarations while the live v3 server returned ID-free continuity records. The same connection called `entity_search` before selecting a continuity and received only `INVALID_ARGUMENT`.

## Findings and disposition

1. ID-free `continuity_list` results are correct v3 behavior. The locally discovered schemas contain no numeric storage-ID inputs or outputs; the externally advertised `id`, `continuityId`, and `entityId` declarations were a stale client catalog.
2. `artificialInstantUtc` and `referenceTimeZoneId` are optional clock-state fields. Their omission means no persisted artificial clock is set and is valid structured-output behavior.
3. The continuity workflow is `continuity_list` followed by `session_continuity_set(continuityName)`. A second `continuity_select` synonym would create an ambiguous duplicate operation and would not repair a client cached on the full v2 catalog.
4. The missing-session exception was a real server usability defect. It used a normal exception type, so the MCP SDK replaced its safe message with a generic tool error. The guard now throws `McpException`, whose content tells the client exactly which two calls to make.
5. Server initialization now carries concise v3 instructions: use names and semantic references, never guess IDs or GUIDs, select continuity before scoped calls, and set connection time only after selection.
6. Protocol regression tests assert both the initialization instructions and the exact user-visible error content. Storage identities remain excluded from that content.
7. The tunnel control-plane record was fetched read-only and is healthy. Tunnel registration stores identity and scope, not tool schemas; stale v2 declarations therefore remain a ChatGPT connection/catalog refresh issue.
8. Live acceptance exposed one transient empty `session_get`. Process inspection found two tunnel-client instances using the same remote profile and therefore two adapter-local session contexts. The extra instance was stopped while Claude remained attached to the shared backend. The launcher now detects existing profile processes and serializes launch with a current-user named mutex; a live duplicate-launch check exited successfully before credential or profile work.

## Verification

- Focused Debug protocol test: 1 passed, 0 failed.
- Complete Debug suite: 84 passed, 0 failed, 0 skipped in 2 minutes 34 seconds.
- Complete Release suite: 84 passed, 0 failed, 0 skipped in 2 minutes 34 seconds.
- Production schema and integrity checks passed read-only; no production writes were made.
