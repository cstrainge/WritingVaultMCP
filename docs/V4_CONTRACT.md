# Writing Vault v4 public contract

Status: v4 Debug implementation; Phase 11 accepted, Phase 12 in progress  
Surface: `4.0`  
Generated artifacts: [`contracts/v4`](../contracts/v4)

## Surface selection and compatibility

One MCP connection advertises exactly one complete tool surface. The Debug v4 executable accepts explicit `--tool-surface v3` or `--tool-surface v4` selection through the implemented registration and pipe negotiation. It defaults to v3 until the production cutover. A v4-schema database may be served through the v3 compatibility surface by the v4 executable for one release; an old v3 executable is not allowed to open the migrated schema. Phase 9 and 10 additions are reflected in the generated v4 snapshots. A final protocol and compatibility review is required before release.

`vault_health.toolSurfaceVersion` is authoritative. After changing the requested surface, Claude Desktop and ChatGPT must establish a new MCP connection and refresh cached tool declarations. Calls using a tool or shape from another surface receive `surface.unsupported`; the server never advertises a mixed union of v3 and v4 tools.

## Identity and scope

Private AI memories have their own `Global` and selected `Continuity` scopes and do not attach to canon records. `vault_health.memories` reports `globalCount`, nullable `selectedContinuityCount`, the selected name, `readTool=memory_read`, and `globalRead` / `selectedContinuityRead` arguments. Counts do not load instructions: call the read tool for each available scope, following `nextCursor` until `hasMore=false`. The complete bodies are returned, with no truncation. Read-only clients can load memories but cannot save them. Saved context remains subject to current user instructions.

`memory_save` creates with `expectedVersion=0` or replaces with the last read positive version; a stale version fails without overwriting. Its common mutation envelope adds a `memory` receipt (`scope`, `key`, `version`). The scope is part of the idempotency fingerprint. Database migration 015 adds a private table; public record reads, viewer pages, search, history, and snapshots never include its keys or bodies. Backups include the table. These notes are shared Vault context rather than per-client secrets.

Numeric Access keys and operation GUIDs are private storage details. Public requests and responses use continuity names, natural names where safe, and opaque semantic references such as `character:aurora~ABCDEFGHJK`. Meaningful domain identifiers remain valid, including `referenceTimeZoneId`, `timeZoneId`, and `calendarId`.

The selected continuity is implicit for continuity-scoped calls. A semantic reference is authoritative and must have the expected record kind and belong to the permitted scope. Supplying a reference in a natural-name field does not change the field's expected kind.

Natural-name resolution applies this sequence:

1. Trim leading and trailing whitespace, collapse internal Unicode whitespace, normalize to Unicode Form KC, and compare case-insensitively with invariant rules.
2. Consider preferred names and aliases only for record kinds allowed by that argument, inside the selected continuity unless the record kind is documented as vault-global.
3. Resolve only when exactly one active record matches. Deleted records are excluded unless the operation explicitly includes or restores them.
4. On no match, return `record.not_found`. On multiple matches, return `record.ambiguous` with at most 10 labelled semantic candidates and perform no write.

A match that exists only in another continuity is reported as not found in the selected continuity. Cross-continuity event/project links and other forbidden relations fail atomically with `scope.mismatch`.

## Dates and clocks

An exact date may be written as `"2026-09-28"`; a year may be written as `{ "kind": "Year", "value": "2026" }`. The rich form represents exact instants, dates, month/year precision, circa values, known intervals (`KnownRange`), bounded uncertainty windows (`UncertainRange`), before/after bounds, unknown dates, original text, inclusivity, and calendar identity. The older stored `Range` kind did not record which meaning was intended: v4 reads expose it as legacy ambiguity, and v4 writes reject it with `date.range_meaning_required`. `originalText` preserves wording and is never used to infer certainty. Fictional date values are timezone-free. Zoned artificial current time uses an offset-bearing timestamp plus `referenceTimeZoneId`.

Rich dates follow deterministic kind rules: `Unknown` has no normalized value; `ExactInstant`, `ExactDate`, `Month`, and `Year` require a matching `value`; `Circa`, `KnownRange`, and `UncertainRange` require lower and upper bounds; `Before` requires `upper`; and `After` requires `lower`. Gregorian calendar values must be real dates, a lower bound cannot follow its upper bound, and fields that do not apply to the selected kind are rejected. `originalText` preserves wording but never overrides invalid normalized fields. Unknown object properties are rejected throughout the v4 contract.

`session_set` accepts exactly one of `currentTime` (offset-bearing instant) or `currentDate` (`YYYY-MM-DD`) when `timeAction` is `Set`, plus `referenceTimeZoneId`. `currentDate` preserves day precision: the clock reports `DateOnly` with no exact instant, the viewer highlights the whole local day, and character ages report bounds across that day. Those age bounds use timezone-free story-clock time: a story day spans 24 local clock hours even when its real UTC interval crosses daylight-saving time. `entity_local_time` returns the date with `DateOnly` status. An exact current state cannot be asserted for an entire day, so `get.currentTemporalState` reports `DateOnly` and asks for a clock time rather than silently choosing one. An explicit `at` instant on a read still takes precedence.

No call falls back to the machine clock. Historical and fuzzy timeline entries remain readable when the artificial clock is unset. Only the current-story-time marker and calculations requiring an as-of time report `timeline.unset` unless the call supplies `at`.

## Paging, sections, and limits

- Ordinary pages default to 50 and allow at most 100 items.
- An overview section defaults to 20 and allows at most 100 items. Every clipped section has its own opaque cursor.
- Timeline pages default to 200 and allow at most 500 items.
- `get.include` accepts at most 20 sections so character overviews can include both shared relationships and each character's membership periods.
- Ambiguity responses contain at most 10 candidates.
- Cursors are opaque, limited to 512 characters, and may expire. Clients must not parse or construct them.
- Search text is limited to 500 characters; names and titles to 255; ordinary long text to 65,536 characters.
- Source snapshots are limited to 1,000,000 UTF-8 bytes. Runtime byte validation remains authoritative when a JSON schema can express only a character bound.
- Image input is limited to 20 MiB decoded; the encoded transport is bounded separately. Search and list calls never return image bytes.
- `changes_since.waitSeconds` ranges from 0 through 30.

Every page-shaped read includes an opaque `observedRevision`. `changes_since` includes selected-continuity changes plus vault-global source and tag changes, because either can invalidate a visible page. An unknown or expired cursor requires a full visible-page refresh.

## Mutations and errors

Every mutation has a caller-readable `mutationToken` of at most 100 characters. Retrying an uncertain outcome with the same token and equivalent request returns the original result; reusing it for a different request fails. Versioned edits require `expectedVersion`. Multi-row editorial operations are transactional.

`relationship_notes_set` replaces or clears the Markdown `notes` field of a versioned relationship in the selected continuity. It requires the relationship's opaque semantic ref and current version, rejects deleted records and cross-continuity refs, and preserves the one shared record seen from both endpoints. The field is limited to 65,536 characters. Separate titled note collections remain the entity and continuity note model.

Relationship and organization memberships can record independent `joined` and `left` story dates, including fuzzy or unknown dates. `relationship_membership_period_add` and `organization_membership_add` accept optional `joinDescription` and `leaveDescription` when those transitions are supplied. `relationship_membership_transitions_set` and `organization_membership_transitions_set` can change the dates and descriptions; `clearJoinDescription` and `clearLeaveDescription` restore generic timeline wording. `organization_membership_transition` accepts `leaveDescription` and, when it creates a replacement, `replacementJoinDescription`. A description belongs to one transition, not the whole membership period. Without one, the timeline says that the named character entered or left a relationship, or joined or left the named organization. Simultaneous exact transitions with generic wording may be combined into a natural list in the viewer. The additive schema migration preserves earlier period-only records and backfills named transitions only when an exact legacy date proves them; it does not treat a fuzzy duration bound as a definite join or leave.

## Importing image files

`image_attach`, `story_image_attach`, and `image_replace` accept exactly one non-null `file` or legacy `image` input. The public MCP descriptors advertise `_meta["openai/fileParams"] = ["file"]`. The `file` object declares four snake-case string properties: required `download_url` and `file_id`, optional `mime_type` and `file_name`, matching the [OpenAI file-input contract](https://developers.openai.com/plugins/reference#file-apis). The MCP host supplies this authorized object; assistants must not invent a URL or file ID or transcribe image bytes into a tool call.

Example shape (the file values below are placeholders, not a usable upload):

```json
{
  "mutationToken": "attach-portrait-2026-10-01",
  "entity": "<entity reference returned by the Vault>",
  "title": "Portrait",
  "file": {
    "download_url": "<temporary HTTPS URL supplied by the MCP host>",
    "file_id": "<file ID supplied by the MCP host>",
    "mime_type": "image/png",
    "file_name": "portrait.png"
  }
}
```

Use `owner` instead of `entity` for story attachments. Replacement uses `imageRef` and `expectedVersion` and retains prior originals and renditions. Inline clients may still supply `image: { "mediaType": "image/png", "dataBase64": "..." }` or `dataUrl` instead of `dataBase64`, omitting `file`.

Authenticated clients may supply public HTTPS URLs without hostname setup. Each URL and redirect is checked; DNS resolutions must contain only public addresses and connections are pinned to checked addresses. Private, loopback, link-local, metadata, reserved, and mapped private destinations are blocked. No credentials, cookies, proxy, or authorization headers are forwarded. Deployments may optionally restrict exact origins with the semicolon-separated `WRITINGVAULT_IMAGE_IMPORT_ORIGINS` environment variable (for example, `https://files.example;https://cdn.example`); it is read when the MCP session starts. Omit it for public HTTPS host files.

Downloads have a 30-second total deadline, at most three redirects, and a 20 MiB streaming byte cap even without Content-Length. Decoding verifies PNG/JPEG/WebP bytes, rejects unsupported or multi-frame formats and dimensions over 50 million pixels, and checks any declared image MIME type. The filename is never a storage path. Original bytes use the existing content-addressed asset store; viewing renditions are persisted in the database. Bounded downloads remain in memory until the normal atomic asset write, so network failures leave no staging files to recover.

Owner authorization, metadata, the mutation token, completed replay, and replacement version are checked before download. No database transaction or global write queue is held over network I/O. Commit repeats the owner/version checks and remains atomic. For retry, retain the same `mutationToken`, `file_id`, target, expected version, and business metadata. The temporary URL may rotate; completed retries return the stored result without refetching. The completed mutation journal retains the verified SHA-256 and host file ID. Temporary URLs and host filenames are excluded from fingerprints and persisted journals. Failed downloads do not consume the token.

Import failures use `image.input`, `image.url_not_allowed`, `image.download_denied` (401/403, without assuming expiry), `image.download_failed`, `image.download_timeout`, `image.too_large`, and `image.invalid`. Refresh an unauthorized or expired host URL through the client and retry the same logical action. `vault_health.imageImportReady` reports importer availability, not a guarantee that a specific client's link is reachable. Outbound download links are not part of this change; `image_view` continues returning the existing MCP image content.

## Markdown links in notes

`note_add.body`, the `body` change in `note_update.changes`, and `relationship_notes_set.notes` store Markdown source. The Debug viewer renders current and pinned record and image links. A pinned record link loads an immutable saved page, including its fields, notes, and associations.

Use the **opaque semantic reference returned by the MCP API**, including its type and stable suffix. Do not use a database ID, display name, filename, local path, or viewer URL in place of a reference. Record references come from `search`, `get`, and related reads; image references come from `image_search`, `image_list`, or an image mutation result. Author-written link text and image alt text are part of the note itself and do not have to match the target's current title.

| Purpose | Markdown in a note | Resolution |
| --- | --- | --- |
| Current record | `[Chloë Bell](vault-record:character:chloe-bell~ABCDEFGHJK)` | Opens the latest record page, even after a name change. |
| Historical record | `[Chloë in the first draft](vault-record:character:chloe-bell~ABCDEFGHJK?v=3)` | Opens immutable page snapshot 3, visibly marked as historical with a **View latest** link. |
| Current image | `![Shimmer](vault-image:<image-ref>)` | Displays the current image-content revision inline. |
| Historical image | `![Earlier Shimmer](vault-image:<image-ref>?v=2)` | Displays retained image-content revision 2 inline. |
| Sized image | `![Shimmer](vault-image:<image-ref>){width=300px}` or `![Shimmer](vault-image:<image-ref>){width=50%}` | Changes only inline display width; height follows aspect ratio and width cannot exceed the note container. |

Replace `<image-ref>` with the complete value returned by an image read or mutation. Image `?v=` identifies an immutable **content revision**, available from `image_revision_history`; `image_replace` adds a revision while preserving older originals and renditions. Image metadata edits do not create a content revision. Record `?v=` identifies an immutable **page snapshot**, read with `record_snapshot_get(ref, snapshotVersion)`, including that version's visible fields, notes, and associations. Use `record_snapshot_list(ref)` to find available versions, newest first; pass `beforeVersion` to page backwards. A v4 migration captures a clearly labeled baseline of each existing page; it does not fabricate earlier versions. Future changes capture affected pages in the same transaction as the write. Neither kind of revision is the `expectedVersion` used for optimistic-concurrency writes. Do not guess a version number.

An image in a note may belong to any supported story record, including one in another continuity; the note's text decides its context. Clicking an inline image opens a bookmarkable original-image page at the same content revision, with metadata, owner, linked source when present, real-UTC creation and update times, revision history, and a latest-view link for pinned revisions. A record link may likewise cross continuities: an explicit click navigates the viewer to the target's continuity without changing any MCP connection's selected continuity. A pinned record page is labeled as historical, shows its save time, and links to the latest page. Missing or deleted current targets show an unavailable state; a retained pinned snapshot remains readable if its record was later deleted. An unavailable pinned version returns `snapshot.not_found`.

The viewer resolves only explicit `vault-record:` and `vault-image:` forms for internal links and stored images. Remote image URLs, relative paths, raw HTML, arbitrary CSS/attributes, and malformed or unbounded width values do not become image fetches. Useful alt text is required for inline images. Each rendered note document is capped at 24 inline images and 50 record links. Link and embed syntax creates no new ownership, relationship, or source association in the database.

`relationship_membership_period_add` accepts either one legacy `period` or both `joined` and `left` story dates. The two forms are mutually exclusive and the entire addition is atomic. Each transition can independently be exact, fuzzy, or unknown. `relationship_membership_transitions_set` edits both dates on an existing period with `expectedVersion`; a whole-period update supersedes and archives the transition pair. The stable period ref remains the same. Its stored date is a conservative possible-occupancy envelope for overlap checks, not evidence that a fuzzy boundary occurred at an exact time. A timeline item for each named transition retains its own precision; an old whole-period range is not split into invented join and leave events. Merge preview includes these dates, and an explicit merge preserves them with the period ref.

`vault_backup_create` is an idempotent mutation that accepts only a mutation token and optional short human purpose. The caller cannot choose a path, file name, retention count, or omit companion assets. The backend quiesces application writes, copies the database and every referenced companion asset into one retained backup set, verifies their hashes plus database schema and integrity, and only then returns success. Durable token receipts prevent a token whose backup has aged out of retention from creating another backup; the caller must use a new token. The response exposes real-UTC creation time, byte/hash and verification summaries, retention count, and asset totals; it never exposes a filesystem path. Restore, migration backups, retention changes, and permanent purge remain administrative operations.

Errors use a stable code, plain-language message, retry flag, optional field details, bounded semantic candidates, and recovery guidance. They never expose SQL, connection strings, paths, numeric database keys, or operation GUIDs. Common families are `validation.*`, `record.*`, `scope.*`, `version.conflict`, `cursor.*`, `timeline.unset`, `capacity.*`, and `surface.unsupported`.

Sparse metadata update tools carry a required, non-empty `changes` object with a tool-specific field allowlist and `additionalProperties: false`. An omitted member is preserved. An explicit JSON `null` clears only fields whose generated schema admits `null`; required values such as names reject it. Set/clear operations such as `entity_variant_group_set.variantGroupRef` and `location_move.parentLocation` require their nullable field so clearing cannot be confused with omission.

## Event and project semantics

World events and entity-specific events may each have zero or more project associations. Zero is a normal state for an idea that is not tied to a story. `event_record` and `entity_event_add` accept initial project names or references. `event_project_apply` later adds or removes several same-continuity projects atomically and may store role and notes. Removing the last association does not delete or hide the event at continuity or owning-entity scope.

Projects can own their own `EntityEvent` records. `entity_event_add.entity` accepts a project ref and optional `projectBoundary: "StoryBegins"` or `"StoryEnds"`. Each role can have at most one active event per project; other story events omit the role. Titles are editable and do not determine the role. Boundaries may link to a world event without copying it. A linked world event is contextual: changing its date does not silently rewrite the project event.

The project page exposes `storyBegins`, `storyEnds`, and derived `storyRange`. The timeline exposes a `Project` item in the `Projects` lane, with independent fuzzy boundary dates and a conservative span. Deleting, restoring, or editing a boundary immediately recalculates the span. An absent endpoint stays open; no date is invented. Project-owned events match their owning project filter automatically, without a redundant project association. Project pages still include independently associated world, entity, and relationship events.

`event_update` edits any existing world, entity, or relationship event using `event` and `expectedVersion`. Omitted properties are preserved. Explicit `clearDescription`, `clearWorldEvent`, `clearNarrativeOrder`, `clearProjectBoundary`, and `clearRecurrence` flags remove optional values. Conflicting set/clear pairs are rejected. Boundary uniqueness, chronology, continuity, and repeat constraints apply transactionally, including on restoration.

Exact Gregorian single-day events accept `recurrence: { "frequency": "Daily" | "Weekly" | "Monthly" | "Yearly", "interval": 1, "until": "YYYY-MM-DD" }` on creation or update. The interval is a positive integer (maximum 10000); `until` is optional and inclusive. Repeats start at the original event, skip nonexistent calendar dates (January 31 does not drift to February 28; February 29 repeats only in leap years), and retain the original record ref. Returned `isOccurrence` identifies generated repeats. Project start/end boundaries cannot recur.

A character's `birthdayRecurring: true` field enables annual birthdays from an exact Gregorian birth date. Set it through `entity_create.fields` or `entity_update.changes`. Birthday occurrences stop at death and recalculate after birth/death edits. Fuzzy deaths use the latest possible day as the stop boundary, with a warning on birthdays that might fall after the actual death. An unknown death does not create a stop date. The original birth remains labeled as birth; subsequent occurrences are labeled as birthdays.

Recurrences expand only inside the requested date window. Without explicit bounds, the filtered stored dates establish the domain; repeat stop dates and generated occurrences never enlarge it. `expandRecurrences: false` returns only stored anchors, allowing the viewer to fit its usual range before requesting occurrences inside that fixed viewport. The table and graph use the same occurrence projection and preserve paging. Queries containing more than 100000 repeated occurrences return `timeline.recurrence_too_broad` and require a narrower window or filters.

A nominal approximate day accepts `{ "kind": "Circa", "value": "2023-08-07", "originalText": "Circa August 7, 2023" }`. Its normalized day is a plotting anchor, not an invented confidence interval; the `Circa` kind and wording are retained. For an authored uncertainty window, continue to supply explicit `lower` and `upper` bounds.

`timeline_get.kinds` selects timeline record kinds. For entity-specific events, `entityEventKinds` further selects Character, Location, Organization, Object, or Project owners; omitting it includes all five. The two filters compose, so a `kinds` list without `EntityEvent` excludes entity-specific events regardless of `entityEventKinds`. A timeline cursor is bound to both filters and the observed revision.

`highlightRef` marks matching graph items without filtering out other records. Aggregate clusters set `containsHighlight` when any member matches the requested same-continuity reference, even if that member is outside the cluster's bounded reference sample. It is part of the cursor scope. The viewer uses this when an entity is clicked in the chronology table.

The viewer requests dated detail pages with `includeUndated: false`. It requests the separate undated collection with `undatedOnly: true`, which has its own cursor. This keeps undated records discoverable even when tens of thousands of dated entries precede them.

## Generated artifacts

`tool-schemas.json` is the complete machine-readable catalog, including access and destructive annotations. `common-schemas.json` freezes reusable public shapes. `examples.json` contains one generated request and response example for every declared tool. The test suite compares fresh generation with the checked-in files byte-for-byte.
