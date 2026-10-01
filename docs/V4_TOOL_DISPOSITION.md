# v3 to v4 tool disposition

Status: Phase 0 decision record  
Accepted: 2026-09-28  
Inventory: 68 v3 tools

This inventory gives every v3 tool an explicit destination. “Retained” means the capability and usually the name remain in v4, with the common v4 request and error conventions. “Replaced” means v4 exposes the capability through the named cohesive operation. Every old name remains available only when a connection explicitly requests the v3 compatibility surface during the one-release transition. No public v3 MCP tool becomes an administrative CLI operation.

The v4 binary serves either a complete v3 surface or a complete v4 surface per connection. It never advertises both sets together.

## Read tools

| v3 tool | v4 disposition | v4 operation or reason |
| --- | --- | --- |
| `session_continuity_set` | Replaced | `session_set` selects continuity and may set/clear the connection time in one call. |
| `session_get` | Retained | Expanded to report effective time and clock provenance. |
| `session_time_set` | Replaced | `session_set`. |
| `session_time_clear` | Replaced | `session_set` with an explicit clear-time action. |
| `vault_health` | Retained | Adds tool-surface and storage/image capacity bands; still exposes no path. |
| `continuity_list` | Retained | Names remain the public continuity identity. |
| `entity_search` | Replaced | Cross-type `search`. |
| `entity_get` | Replaced | `get`. |
| `variant_group_list` | Replaced | `search` with `VariantGroup` kind and `get` for details. |
| `source_search` | Replaced | `search` with `Source` kind or all applicable kinds. |
| `tag_search` | Replaced | `search` with `Tag` kind; `tag_targets` browses reverse links. |
| `entity_graph` | Replaced | `get` overview plus independently paged `list_related`. |
| `source_graph` | Replaced | `get` overview plus `list_related`. |
| `character_relationships` | Replaced | `list_related({ relation: "relationships" })`. |
| `character_age` | Retained and expanded | Adds `at`, four age meanings, applied effects, bounds, and warnings. |
| `entity_local_time` | Retained | Uses the same reference and session conventions. |
| `entity_temporal_state` | Replaced | `get` temporal-state section and `list_related` for periods. |
| `entity_delete_preview` | Replaced | Cross-kind `record_delete_preview`. |
| `record_history` | Replaced | `history_get` by semantic record reference. |
| `operation_history` | Replaced | `history_get` by public mutation token; internal operation GUIDs stay hidden. |

New v4 reads with no v3 equivalent are `timeline_get`, `changes_since`, `tag_targets`, `temporal_effect_preview`, `image_list`, `image_search`, and `image_view`. `get` with no reference returns the selected-continuity overview.

## Core and metadata writes

| v3 tool | v4 disposition | v4 operation or reason |
| --- | --- | --- |
| `continuity_create` | Retained | Uses common mutation metadata and result shape. |
| `continuity_patch` | Replaced | `continuity_update` with sparse changes. |
| `continuity_clock_set` | Retained | Shared persisted clock remains distinct from connection time. |
| `variant_group_create` | Retained | Specialized metadata operation. |
| `variant_group_patch` | Replaced | `variant_group_update` with sparse changes. |
| `entity_variant_group_set` | Retained | Validates continuity and entity type. |
| `entity_create` | Retained | Supports all six canon types through validated type-specific fields. |
| `entity_patch` | Replaced | `entity_update` with sparse changes and expected version. |
| `entity_duplicate_to_continuity` | Retained | Keeps the existing deliberate shallow-copy rules. |
| `tag_create` | Retained | `tag_apply` may also create a missing tag when explicitly requested. |
| `tag_patch` | Replaced | `tag_update`. |
| `source_create` | Retained | Vault-global source creation. |
| `source_patch` | Replaced | `source_update` with sparse changes. |
| `source_snapshot_add` | Retained | Keeps explicit, non-network snapshot ingestion. |
| `source_snapshot_view` | Added in Debug Phase 9 | Returns verified cached source text in bounded pages without a local path. |
| `claim_create` | Retained | Claims/evidence remain specialized. |
| `entity_note_add` | Replaced | Cross-target `note_add`; v4 also adds `note_update`. |
| `entity_event_add` | Retained | Lower-level local event operation; `event_record` is the multi-participant convenience operation. Both accept optional project associations. |
| `entity_alias_add` | Retained | Applies only to supported entity kinds and retains alias rules. |
| `note_source_link` | Retained | Carries locator and notes, so it is not a generic source link. |
| `entity_tag_link` | Replaced | Atomic cross-target `tag_apply({ action: "add" })`. |
| `entity_tag_unlink` | Replaced | Atomic cross-target `tag_apply({ action: "remove" })`. |
| `source_tag_link` | Replaced | `tag_apply`; sources are valid targets. |
| `source_tag_unlink` | Replaced | `tag_apply`; sources are valid targets. |
| `entity_source_link` | Retained | Simple entity provenance link with common v4 request conventions. |
| `entity_source_unlink` | Retained | Explicit inverse of the provenance link. |
| `project_entity_link` | Retained | Project membership retains role and notes. |

New v4 write `event_project_apply` has no v3 equivalent. It adds or removes project associations for either a world event or an entity-specific event through one semantic contract. It never requires an association, never transfers ownership, rejects cross-continuity links, and retains optional role and notes.

New v4 write `vault_backup_create` has no v3 MCP equivalent. It creates only a regular verified backup under the configured backup root, includes companion assets, uses the configured retention policy, is idempotent by mutation token, and returns verification metadata without paths. Restore, pre-migration backup selection, retention changes, and purge remain administrative CLI operations. Read-only connections never advertise it.

## Invariant-bearing relationship writes

| v3 tool | v4 disposition | v4 operation or reason |
| --- | --- | --- |
| `location_move` | Retained | Maintains location hierarchy and cycle checks. |
| `character_residence_add` | Retained | Enforces period and primary-residence rules. |
| `character_residence_transition` | Retained | Atomic close/open transition. |
| `organization_membership_add` | Retained | Enforces role-period overlap rules. |
| `organization_membership_transition` | Retained | Atomic close/open transition. |
| `organization_location_add` | Retained | Enforces period and primary-location rules. |
| `relationship_type_create` | Retained | Defines direction, inverse label, and overlap policy. |
| `character_relationship_create` | Retained | One canonical row is automatically visible from both endpoints. |
| `ownership_principal_create` | Retained | Creates a typed or external ownership principal. |
| `object_ownership_add` | Retained | Enforces ownership state, periods, and shares. |
| `object_ownership_replace_owners` | Retained | Versioned correction of one ownership composition. |
| `object_ownership_transfer` | Retained | Atomic close/open transfer. |
| `object_location_add` | Retained | Enforces object-location periods. |
| `object_custody_add` | Retained | Keeps custody distinct from ownership and location. |
| `world_event_participant_add` | Retained | Preserves role, impact, outcome, and notes. |
| `world_event_location_add` | Retained | Preserves multi-location and primary-location rules. |

v4 adds temporal-profile and temporal-effect create/update/delete/restore operations. They remain character-specific because they carry overlap and age-calculation invariants.

## Deletion and restoration

| v3 tool | v4 disposition | v4 operation or reason |
| --- | --- | --- |
| `entity_soft_delete` | Replaced | `record_soft_delete` dispatches safely from the semantic reference. |
| `entity_restore` | Replaced | `record_restore`. |
| `relationship_soft_delete` | Replaced | `record_soft_delete`; restoration rechecks relationship invariants. |
| `relationship_restore` | Replaced | `record_restore`. |
| `vault_record_soft_delete` | Replaced | `record_soft_delete`. |
| `vault_record_restore` | Replaced | `record_restore`. |

`record_delete_preview`, `record_soft_delete`, and `record_restore` resolve the semantic reference to an allowlisted record descriptor. They are a common dispatch boundary, not arbitrary table access. Permanent purge remains CLI-only.

## Discovery decision

v4 does not use dynamic tool registration because supported hosts may cache declarations differently. Common operations use predictable short names; specialized tools use domain prefixes and consistent argument shapes. Phase 7 measured the original 69-tool declaration against v3; the current Phase 11 Debug candidate declares 76 tools, including group relationship, membership-period correction, and relationship-event operations. Re-measure the final declaration before Phase 11 closes. If the full surface remains a practical discovery problem, that evidence will drive a later profile mechanism rather than hiding tools speculatively.
