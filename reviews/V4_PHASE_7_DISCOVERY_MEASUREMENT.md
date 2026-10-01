# v4 discovery and workflow measurement

Measured: 2026-09-28 against the Phase 7 generated contracts and Debug process-boundary MCP tests. This historical comparison predates the Phase 9 `source_snapshot_view` addition; the current Debug v4 surface has 70 tools and requires a new measurement before release.

## Discovery surface

The comparison counts each read/write tool name, description, and compact input schema once. Output schemas are excluded because ordinary MCP tool discovery advertises input schemas.

| Surface | Tools | UTF-8 discovery bytes |
|---|---:|---:|
| v3 read/write | 68 | 41,271 |
| v4 read/write | 69 | 61,076 |

v4 adds one tool and 19,805 bytes, a 1.48x schema payload. The increase comes from explicit fuzzy-date unions, bounded arrays and strings, sparse-update allowlists, image transport alternatives, and the unified search/get filters. This is a measured cost, not a claimed discovery reduction. The schema remains a fixed 69-tool surface so hosts that cache declarations see stable metadata.

## Workflow plumbing

| Task | v3 calls | v4 calls |
|---|---:|---:|
| Select continuity and set a client clock | 2 | 1 `session_set` |
| Render a character page with graph, relationships, age, and temporal state | up to 5 focused reads | 1 `get`, plus `list_related` only for clipped sections |
| Record a world event with participants, locations, and projects | 1 entity create plus one call per relation | 1 `event_record` |
| Apply one tag to several mixed targets | one link call per target and target kind | 1 `tag_apply` |
| Reassign either event kind across several projects | one link operation per association | 1 `event_project_apply` |

The complete process-boundary workflow in `V4Phase7ProtocolTests` verifies onboarding, search, page reads, Markdown notes, multi-target tags, assigned and unassigned events, event reassignment, temporal age, MCP image content, verified backup, delete/restore, and ambiguous-name recovery without database identifiers.

## Decision

The larger declaration is accepted because it makes constraints visible to assistants and removes repeated calls from the expensive editorial paths. If real ChatGPT or Claude use shows repeated schema-inspection friction, that evidence should drive a later task-oriented profile; v4 does not dynamically hide tools because host metadata caching is inconsistent.
