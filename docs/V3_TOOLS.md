# MCP tool reference: v3 compatibility

This page documents the retained v3 surface. See [V4_TOOLS.md](V4_TOOLS.md) for the v4 task-oriented surface and [`contracts/v4/tool-schemas.json`](../contracts/v4/tool-schemas.json) for its exact generated declarations.

Tool surface version: **3.0**. Transport: local stdio adapter to a shared, current-user named-pipe backend. Every result uses structured content.

## API identity rules

- Numeric Access keys and internal operation GUIDs never appear in MCP inputs, results, errors, history, or cursors.
- Continuities are selected by their unique name with `session_continuity_set`. The selected continuity is implicit for later continuity-scoped calls on that connection.
- Other records use semantic references such as `character:ada-north~K7U2F7NKXF`. The readable slug may change after a rename; the opaque suffix remains stable, and an older reference continues to resolve.
- Every mutation requires a caller-chosen `requestToken` of at most 100 characters, such as `create-ada-v1`. The backend converts it to an internal operation key. Retrying identical input with the same token replays the result; reusing the token for different input returns `idempotency.input_mismatch`.
- `requestToken` values are vault-wide, so use a distinct descriptive token for each intended mutation.
- Versioned edits, restores, and deletions also require `expectedVersion`.

## Session tools

| Tool | Purpose |
|---|---|
| `session_continuity_set` | Select this connection's continuity by unique name and clear its time override. |
| `session_get` | Return the selected continuity name and current connection override. |
| `session_time_set` | Set a `DateTimeOffset` and named timezone for this connection only. |
| `session_time_clear` | Return this connection to the selected continuity's persisted shared clock. |

Each MCP connection has independent session state. A ChatGPT connection can work in Lostville at one artificial time while Claude uses another continuity or time against the same backend.

Start a new connection by calling `continuity_list`, choosing an exact returned name, and passing it as `continuityName` to `session_continuity_set`. Continuity-scoped tools return an actionable MCP error naming this sequence when no continuity is selected. `continuity_select` is not a separate tool.

## Read tools

| Tool | Purpose and bounds |
|---|---|
| `vault_health` | Schema/integrity status, queued writes, backup time, and tool-surface version. |
| `continuity_list` | Continuity names and persisted clock versions; active, both, or deleted-only. |
| `entity_search` | Search the selected continuity; semantic cursor, deterministic keyset paging, at most 100 rows. |
| `entity_get` | Read one semantic entity reference, with optional deleted inspection. |
| `variant_group_list` | Up to 200 groups in the selected continuity. |
| `source_search`, `tag_search` | Bounded vault-global searches returning semantic references. |
| `entity_graph`, `source_graph` | Reverse and aggregate graph reads; each collection is capped at 50. |
| `character_relationships` | Relationships found from either endpoint with perspective-correct labels; at most 100. |
| `entity_local_time` | Entity-local time plus timezone/location source and whether the instant came from the client override or shared clock. |
| `character_age` | Exact or bounded age from the connection override when set, otherwise the persisted continuity clock. |
| `entity_temporal_state` | Residence, membership, relationship, ownership, custody, and location rows active at the same effective artificial time. |
| `entity_delete_preview` | Retained consequence counts; makes no change. |
| `record_history` | Up to 200 entries for a semantic reference, without storage keys or operation GUIDs. |
| `operation_history` | Up to 200 entries for a readable `requestToken`. |

## Write tools

Creation and metadata: `continuity_create`, `continuity_patch`, `variant_group_create`, `variant_group_patch`, `entity_variant_group_set`, `entity_create`, `entity_patch`, `entity_duplicate_to_continuity`, `continuity_clock_set`, `tag_create`, `tag_patch`, `source_create`, `source_patch`, `source_snapshot_add`, and `claim_create`.

Content and links: `entity_note_add`, `entity_event_add`, `entity_alias_add`, `note_source_link`, `entity_tag_link`, `entity_tag_unlink`, `entity_source_link`, `entity_source_unlink`, `source_tag_link`, `source_tag_unlink`, and `project_entity_link`.

Temporal and graph operations: `location_move`, `character_residence_add`, `character_residence_transition`, `organization_membership_add`, `organization_membership_transition`, `organization_location_add`, `relationship_type_create`, `character_relationship_create`, `ownership_principal_create`, `object_ownership_add`, `object_ownership_replace_owners`, `object_ownership_transfer`, `object_location_add`, `object_custody_add`, `world_event_participant_add`, and `world_event_location_add`.

Lifecycle: `entity_soft_delete`, `entity_restore`, `relationship_soft_delete`, `relationship_restore`, `vault_record_soft_delete`, and `vault_record_restore`.

Migration and permanent purge remain CLI-only. Read-only connections expose none of the write or administrative operations. No tool accepts SQL, a database path, or a connection string.

## Examples

Create and select a continuity:

Call `continuity_create`:

```json
{
  "request": {
    "requestToken": "create-lostville-v1",
    "name": "Lostville",
    "defaultTimeZoneId": "America/Vancouver"
  }
}
```

Call `session_continuity_set`:

```json
{ "continuityName": "Lostville" }
```

Create a character. `continuityId` is absent because the connection already selected Lostville:

```json
{
  "request": {
    "requestToken": "create-ada-north-v1",
    "entityType": "Character",
    "name": "Ada",
    "familyName": "North",
    "pronouns": "they/them",
    "birth": {
      "kind": "Year",
      "lowerBound": "2000-01-01T00:00:00",
      "upperBound": "2001-01-01T00:00:00",
      "lowerInclusive": true,
      "upperInclusive": false,
      "calendarId": "Gregorian"
    }
  }
}
```

The result contains a reference such as `character:ada~K7U2F7NKXF` and version `1`. Use that reference for reads, patches, relationships, and history.

Set a connection-only artificial time and calculate age:

Call `session_time_set`:

```json
{
  "currentInstant": "2027-11-07T01:30:00-08:00",
  "referenceTimeZoneId": "America/Vancouver"
}
```

Call `character_age`:

```json
{ "characterReference": "character:ada~K7U2F7NKXF" }
```

Use `continuity_clock_set` instead when the time should become the selected continuity's shared persisted default.

## Stable error families

| Code/family | Meaning |
|---|---|
| MCP tool error naming `session_continuity_set` | This connection has no selected continuity. List continuities and select one by exact name, then retry. |
| `validation.*` | A supplied value or semantic reference is malformed or outside a documented bound. |
| `entity.not_found` | A semantic reference does not resolve to an available record. |
| `constraint.duplicate`, `constraint.reference` | A canonical value/link already exists, or a required target is unavailable/in use. |
| `continuity.mismatch`, `variant_group.mismatch` | A relation would cross the selected canon boundary or group type. |
| `location.cycle`, `interval.overlap` | A hierarchy or temporal invariant would be violated. |
| `concurrency.conflict` | `expectedVersion` is stale; reread before retrying with a new token. |
| `idempotency.input_mismatch` | The readable request token was already used with different semantic input. |
| `delete.blocked` | Retained dependent records prevent deletion. |
| `schema.not_ready`, `storage.failure` | The backend cannot safely complete the operation. Client output omits provider, SQL, paths, storage keys, and GUIDs. |

Patch fields use `{ "specified": true, "value": ... }`. Omitted or `specified: false` means unchanged; specified `null` clears a nullable field. Inputs are capped at 100 list members, stored long text at 1,000,000 characters, and returned long text at 65,536 characters with a `<Field>Truncated` flag.

Version 3.0 is a breaking replacement for the numeric-ID/GUID v2 tool surface. Clients must rescan tools and begin by selecting a continuity. Internal Access schema keys and existing data are unchanged.
