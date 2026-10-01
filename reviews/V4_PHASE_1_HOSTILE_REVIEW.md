# v4 Phase 1 hostile contract review

Review date: 2026-09-28  
Scope: generated v4 schemas and examples, common shapes, v3 compatibility snapshot, identity exposure, bounds, date/reference behavior, Debug image-ingress probe, and the new event/project requirement  
Verdict: **Implemented contract work passes with zero unresolved implementation findings.** The later 2026-09-30 user decision moved the two real-client PNG proofs to Phase 12 Release usability validation, so they no longer block the amended Phase 1 exit gate.

At the time of this 2026-09-28 review, the external proofs were Phase 1 validation gates. The user subsequently deferred those tests until the v4 Release deployment and directed that they not block switch-over. They remain required Phase 12 usability checks; this historical review retains the original test instructions below.

## Findings and resolutions

| Finding | Risk | Resolution and evidence | State |
| --- | --- | --- | --- |
| The exporter reported 68 tools but omitted the tool array. | A tiny metadata file could appear valid while exposing no reviewable schemas. | Added the emitted `tools` array; tests require 68 unique definitions and exact checked-in snapshots. | Resolved |
| Timeline `limit` inherited the ordinary 100-item maximum. | The documented 200 default was invalid and detailed timeline requests could not reach the accepted 500 cap. | Root-schema specialization now emits 1–500; contract test asserts it. | Resolved |
| Enums were emitted as integers. | Assistants would need hidden ordinal knowledge and could send brittle values. | Added string-enum serialization; tests assert semantic values such as `Add` and `Remove`. | Resolved |
| Unknown JSON properties were silently tolerated by the draft schema. | Misspelled changes could appear successful while being ignored. | v4 schemas now reject unmapped members; sparse/null semantics remain explicit. | Resolved |
| Collection limits were documented but not consistently visible in schemas. | Large include, association, result, or candidate arrays could amplify payloads. | Added explicit request-list, page, section, timeline, include, section-map, and ambiguity bounds; byte-only limits remain enforced at runtime and documented as such. | Resolved |
| A blanket `...Id` check would reject `valid` and legitimate domain identifiers. | A case-insensitive suffix test produces false positives and could remove useful timezone/calendar fields. | Tests reject exact `id` and camel-case `Id` storage fields while allowlisting `referenceTimeZoneId`, `timeZoneId`, `defaultTimeZoneId`, and `calendarId`. | Resolved |
| Rich dates allowed structurally incomplete kind combinations in the generic schema. | A malformed range or before/after value could have an unclear outcome. | The contract now states kind-specific required fields, impossible-date and bound-order rejection, unknown-field rejection, and no `originalText` override. Phase 3 implements this application validation. | Resolved |
| Name resolution could leak or select a record from another continuity. | A same-named record could be mutated incorrectly. | Resolution order, Unicode normalization, exact unique match, deleted-record policy, ten-candidate cap, kind checks, and cross-continuity not-found behavior are frozen in `docs/V4_CONTRACT.md`. | Resolved |
| Stale/constructed cursors might be parsed or accepted as identity. | Paging could skip records or expose change sequence internals. | Cursors are opaque, bounded, expirable, and require restart/full refresh on expiry; clients may not construct them. | Resolved |
| Entity-specific events had no project relation. | Project pages would omit story events or force duplicate world events. | Both event kinds accept optional projects; `event_project_apply` handles later atomic changes; storage and same-continuity invariants are assigned to Phase 2. Contract tests require these shapes. | Resolved |
| The image probe's first method shape would have nested all fields under `request`. | Host calls and the written examples would disagree. | The MCP tool now advertises flat `clientName`, `mediaType`, `dataBase64`, and `dataUrl` fields. A real stdio MCP integration test verifies discovery and invocation. | Resolved |
| The image probe could accidentally persist content or depend on a path. | Test images could leak or give a false feasibility result. | The Debug-only process accepts one inline representation, rejects paths/remote fetches, bounds input, validates PNG/IHDR safety, hashes then zeroes bytes, and logs metadata only. Unit and stdio tests verify content is absent from evidence. | Resolved |
| v4 work could silently alter the supported v3 declaration surface. | Current Claude/ChatGPT clients could regress before cutover. | A disposable migrated Access database now captures read-only and read-write v3 declarations; the snapshot test compares them exactly. | Resolved |

## External evidence deferred to Phase 12

| Gate | Current evidence | Required to close |
| --- | --- | --- |
| ChatGPT PNG ingress | Automated base64 and stdio MCP calls pass; no real ChatGPT attachment call is recorded. | Stop the production tunnel, run `Run-WritingVault-Image-Probe-Tunnel.bat`, refresh the tool, send one real PNG, record the actual shape and usable/over-limit behavior, then restore the production tunnel. |
| Claude PNG ingress | Automated base64 and stdio MCP calls pass; no real Claude attachment call is recorded. | Temporarily configure the Debug stdio probe, send one real PNG, and record the actual shape and usable/over-limit behavior. |

## Verification

- Debug build: zero warnings and zero errors.
- Focused v4 contract/probe suite: 7 passed; focused disposable-database v3 snapshot suite: 1 passed.
- Full Debug suite after all implementation changes: **92 passed, 0 failed, 0 skipped**.
- Release artifacts were not built or changed.

No known implementation defect remains in the Phase 1 work. The two named host ingress proofs remain unverified and must be reported as such until Phase 12 tests them on Release.
