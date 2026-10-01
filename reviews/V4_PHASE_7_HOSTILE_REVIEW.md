# v4 Phase 7 hostile MCP publication review

Review date: 2026-09-28  
Scope: exact tool registration, generated schemas, read-only capability, errors/recovery, identity disclosure, complete workflows, discovery overhead, tunnel background operation, documentation, and v3 coexistence  
Verdict: **Pass with zero unresolved findings.**

## Attacks and results

| Attack | Required result | Evidence |
| --- | --- | --- |
| Connect with v4 read-only | Advertise exactly the 18 read tools and no mutation | `SurfaceSelectorAdvertisesExactV4CatalogAndReadOnlyOmitsMutations` compares live discovery with the catalog for both connection modes. |
| Compare live declarations with frozen files | Match every name, description, and full input schema | Protocol tests deep-compare all 69 advertised schemas; snapshot tests regenerate all checked-in contract files byte-for-byte. |
| Send compact and rich story dates | Bind both promised forms through the real MCP adapter | Onboarding creates a character with a compact exact date; contract tests freeze the union schema. |
| Call `session_get` before selecting a continuity | Return a safe unscoped state | The real adapter reports an unset clock with source `none`; scoped calls still require selection. |
| Complete normal editorial work | Use only names and semantic references through notes, tags, events/projects, time, images, backup, deletion, and ambiguity recovery | `CompleteEditorialWorkflowUsesOnlyNamesAndOpaqueReferences` exercises the entire public adapter path. |
| Return a requested image | Emit one MCP image content block and path-free metadata, with no bytes embedded in structured JSON | `ImageViewReturnsAnMcpImageBlockAndMetadataWithoutEmbeddedBytes` verifies the transport boundary. |
| Trigger ambiguity or version failure | Return stable code, bounded candidates/detail, and direct recovery without IDs/GUIDs/paths | Result mapping uses `record.ambiguous`, `version.conflict`, `scope.mismatch`, and token recovery consistently. |
| Fail backup storage or verification | Return a stable redacted `backup.failed` envelope | Backup catches expected provider/storage/verification errors, logs only the category, and exposes no path. |
| Run tunnel under Task Scheduler | Never prompt or open admin UI; require profile/DPAPI secret; hold one mutex; exit actionably | Static deployment tests cover background branches, mutex lifetime, BAT pause suppression, v4 selection, and exit-code documentation. |
| Cache v3 declarations while requesting v4 | Keep one explicit complete surface per connection | Named-pipe handshake carries `v3` or `v4`; the process default remains v3 until approved cutover. |

## Findings resolved during review

1. SDK-generated declarations wrapped every request under `request` and lost several frozen constraints. The v4 contract filter now publishes the exact catalog input and output schemas plus description while adapting flat arguments internally.
2. The frozen schema promised compact date strings, but normal JSON binding accepted only objects. A strict converter now binds compact exact dates and rich objects and rejects unknown object fields.
3. Several successful relation/alias mutations used record kinds the public result mapper did not recognize. All public aliases, junctions, participants, locations, event projects, and temporal kinds now map without exposing storage keys.
4. Natural-name ambiguity used `record.ambiguous` while recovery logic looked for `reference.ambiguous`. Recovery and tests now use the actual frozen code.
5. `session_get` required a selected continuity even though its output permits an unselected session. It now reports the unscoped state safely.
6. Health always returned null backup recency. It now reports only the newest currently verified regular backup.
7. Backup exceptions escaped as generic MCP invocation errors. Expected validation, token, provider, storage, and verification failures now use stable redacted v4 results.
8. The tunnel background path could inherit an API key and still behave interactively. It now requires the saved current-user DPAPI credential, skips init/doctor/admin UI, retains its named mutex, and returns documented exit codes.
9. Installation and tool documentation still blurred deployed v3 Release and v4 Debug validation. The documents now state the boundary, require explicit `--tool-surface v4`, and describe background operation and every published tool.

## Verification

- Frozen-contract plus Phase 7 protocol/deployment Debug gate: **14 passed, 0 failed, 0 skipped**.
- Aggregate Phase 4-7/backup/contract focused coverage after fixes: **38 passed, 0 failed, 0 skipped**.
- Discovery measurement: v3 has 68 tools and 41,271 input-discovery bytes; v4 has 69 tools and 61,076 bytes (1.48x). The accepted tradeoff preserves explicit constraints while reducing workflow calls; details are in `V4_PHASE_7_DISCOVERY_MEASUREMENT.md`.
- Full Debug regression suite: **148 passed, 0 failed, 0 skipped** (5 minutes 56 seconds on the final exact build).
- Debug build: zero warnings and zero errors.
- Production database and Release output were not touched.

## Unresolved findings

None.
