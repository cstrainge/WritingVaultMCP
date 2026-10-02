# Writing Vault MCP v4 tools

Tool surface `4.0` uses continuity names and opaque semantic references. Call `continuity_list`, then `session_set` once per client connection. Later continuity-scoped calls use that connection state. Never guess a database ID or GUID.

The exact input and output schemas, limits, examples, access flags, and destructive annotations are generated in [`contracts/v4`](../contracts/v4). This page groups the tools by task; the generated catalog is authoritative for individual fields.

## Read workflow

- `vault_health` reports schema, integrity, queue, backup, and capacity status without paths.
- `continuity_list`, `session_set`, and `session_get` establish and inspect connection-local continuity and artificial time.
- `search` finds records across selected kinds. `get` returns one cohesive bounded overview. `record_locate` resolves an explicit semantic reference across continuities for viewer links without changing the MCP session. `record_snapshot_list` finds retained historical page versions; `record_snapshot_get` reads one saved page with its notes and associations. `list_related` pages any clipped current section. `source_snapshot_view` pages verified cached source text explicitly; `includeDeleted` permits an intentionally opened deleted snapshot to show its cached text.
- `timeline_get` returns calendar or narrative chronology, including fuzzy ranges and undated records. Historical chronology works with an unset clock.
- `tag_targets`, `history_get`, and `changes_since` provide tag navigation, separate real-UTC history, and committed-change watching.
- `character_age`, `temporal_effect_preview`, and `entity_local_time` use the effective session clock or a supplied read-only `at` value.
- `record_delete_preview` reports blockers for supported canon, metadata, and relationship records. Its `target.ref` is a semantic reference accepted by `record_soft_delete`; a selected continuity's plain name may be used as the preview input, but the returned ref is always semantic.
- `relationship_merge_preview` shows both legacy relationship identities, periods, events and their context, Markdown notes, linked claims, and audit counts. It returns a review token only when the proposed merge has no conflicts.
- `image_list` remains scoped to the selected continuity and accepts canon-entity and story-record owners. `image_search` includes both image storage kinds and can search across continuities when explicitly requested. `image_view` accepts an explicit semantic image reference from any active continuity and returns a bounded thumbnail, display, or verified original image block; optional `revision` pins retained image content. `image_revision_history` lists the available positive content revisions without bytes. Cross-continuity reads do not change the selected continuity.

Every page-shaped read includes an opaque observed revision. Use `changes_since` with that revision. If it reports expiry or an unknown change, refresh the visible page.

## Core writes

- Continuities: `continuity_create`, `continuity_update`, `continuity_clock_set`
- Backups: `vault_backup_create`
- Variant groups: `variant_group_create`, `variant_group_update`, `entity_variant_group_set`
- Canon entities: `entity_create`, `entity_update`, `entity_duplicate_to_continuity`
- Tags: `tag_create`, `tag_update`, `tag_apply`
- Sources and claims: `source_create`, `source_update`, `source_snapshot_add`, `claim_create`, `claim_update`
- Notes: `note_add`, `note_update`, `note_source_link`
- Events: `event_record`, `entity_event_add`, `relationship_event_add`, `event_project_apply`
- General links: `entity_alias_add`, `entity_source_link`, `entity_source_unlink`, `project_entity_link`

All three event creation tools accept zero or more projects. An event with no project remains visible in its continuity. `event_project_apply` changes project associations later without copying or re-owning the event. A relationship event belongs to one shared relationship, appears from each active participant's page, and may link to a world event even when it falls outside a known membership period.

## Graph, place, and time writes

- Location and residence: `location_move`, `character_residence_add`, `character_residence_transition`
- Organizations: `organization_membership_add`, `organization_membership_transition`, `organization_membership_transitions_set`, `organization_location_add`
- Character relationships: `relationship_type_create`, `character_relationship_create`, `relationship_create`, `relationship_participant_add`, `relationship_membership_period_add`, `relationship_membership_period_update`, `relationship_membership_transitions_set`
- Relationship notes: `relationship_notes_set`
- Legacy relationship consolidation: `relationship_merge_apply`
- Ownership, custody, and location: `ownership_principal_create`, `object_ownership_add`, `object_ownership_replace_owners`, `object_ownership_transfer`, `object_location_add`, `object_custody_add`
- World-event links: `world_event_participant_add`, `world_event_location_add`
- Temporal aging: `character_temporal_profile_set`, `character_temporal_effect_create`, `character_temporal_effect_update`
- Lifecycle: `record_soft_delete`, `record_restore`
- Images: `image_attach` (canon entity), `story_image_attach` (continuity, relationship, entity event, relationship event), `image_replace` (new retained content revision), `image_update` (metadata only)

The three image-content writes accept a host-provided `file` (`download_url`, `file_id`, optional `mime_type`/`file_name`) or legacy inline `image`, exactly one per request. Keep the mutation token and file ID stable when refreshing a temporary URL. See [image import limits and examples](V4_CONTRACT.md#importing-image-files). Refresh the client's tool definitions to discover the new `file` parameter.

Relationships are stored once and are discoverable from every active participant. `relationship_create` accepts 2–100 distinct characters with one shared undirected type; directed forward/inverse relationships remain pairs. Its optional `initialPeriod` gives every initial member the same period; omit it when characters join at different times, then add each period separately. Each character can have several non-overlapping membership periods for leaving and re-entering. `relationship_membership_period_add` accepts either one `period` or the pair `joined` and `left` atomically; the latter is the normal path for independently fuzzy boundaries and re-entry. Participant and period add calls return the new record's semantic ref; use `get` for the relationship's updated version before another membership change. A membership-period `get` includes its participant and shared relationship as linked sections. `relationship_membership_transitions_set` edits a period's independently fuzzy or unknown `joined` and `left` dates, using its own expected version. The stored period becomes the conservative possible occupancy interval for overlap checks; a fuzzy transition never becomes an exact relationship-level timeline boundary. `relationship_membership_period_update` replaces that pair with a whole-period correction, archives the old transition rows, and rechecks overlap. Omitting `notes` preserves existing notes; supply new text to replace them, or set `clearNotes: true` to remove them. `record_soft_delete` and `record_restore` manage participant and period records; deleting a participant cannot leave fewer than two active members. Ownership can have several owners. Dates accept the compact exact form such as `"2026-09-28"` or the rich fuzzy/range object declared in the generated schema.

Relationship create calls accept optional Markdown `notes`. Use `get` on the returned relationship ref to read them. `relationship_notes_set` later replaces or clears that field using the current `version`, an opaque `relationshipRef`, and a fresh `mutationToken`; `notes: null` clears it. It supports character relationships, residences, organization memberships and locations, object ownership/custody/location periods, and world-event participants and locations. These are single Markdown documents on the relationship record, visible through that same ref from either endpoint.

A character `get` includes its current four-way age result and `temporalProfile`. Read `temporalProfile.version` before changing an existing profile and send that value as `expectedVersion`; a character without a profile reports `exists: false` and a null version. Temporal-effect pages link back to their character and to the optional causal world event.

To consolidate two separately authored legacy pair records, call `relationship_merge_preview` with their refs. Review both sets of periods and events, each Markdown note, claim-link and history counts, and any overlap conflicts. Then call `relationship_merge_apply` with the returned `reviewToken` and a fresh `mutationToken`. The apply operation checks the complete preview again in one transaction; any intervening change requires a new preview. The source becomes an archived redirect. Ordinary `get` on its old ref opens the survivor; `get` with `includeDeleted: true` opens the archived source, including its original notes, claims, and history. Period and event refs survive under the target. The target's `mergedSources` section links to every archived source. A merged source cannot be restored as a second active relationship.

## Mutations and recovery

Every write requires a caller-chosen `mutationToken`. Reusing it with identical input replays the result safely. Different input requires a new token. Versioned changes require the visible `version`; after `version.conflict`, read the record again, reconcile, and retry with the current version and a new token.

Ambiguous semantic names return bounded candidates. Retry with one returned opaque `ref`. Public responses and errors do not expose Access row keys, internal operation GUIDs, database paths, hashes, or asset paths.

Use `--read-only` to advertise only read tools. Use `--tool-surface v4` explicitly until v4 becomes the default at cutover.
