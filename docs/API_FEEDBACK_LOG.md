# Writer's Vault API feedback log

Updated: 2026-09-28

This is the preserved v3 client-feedback snapshot that motivated v4. Its "current" and "unimplemented" statements describe the 2026-09-28 v3 client, not the Debug v4 code or the later Release candidate. Use the [v4 progress ledger](../V4_API_PLAN.md) for implementation status and add new observations in a dated section rather than silently rewriting the original report.

Purpose: Keep concrete friction from using the Vault as a writing assistant. Add examples during normal work, then review them together when a coherent API revision is warranted. Separate confirmed problems from untested ideas and resolved regressions.

The proposed implementation response is tracked in [`V4_API_PLAN.md`](../V4_API_PLAN.md).

## Model-facing design principle: minimize overhead

Optimize for the complete editorial task, not for mirroring tables or minimizing backend code. A common action should take one discoverable call, a small request, and a compact result. The Vault should resolve entity type, junction table, continuity scope, ordinary defaults, and retry bookkeeping. It should return the created/changed record reference and enough context for the next action, without making me fetch it again.

Use the selected continuity implicitly. Accept an opaque reference when I have one; for a natural name, resolve it **only when unique in context**. If “Aurora” or “the box” is ambiguous, return a small list of labelled candidates and make no mutation. Never guess a database ID or silently choose one match. This saves the routine search-before-write round trip without sacrificing correctness.

Batch one intent when appropriate: tagging several targets, attaching one image to an entity, or recording a scene with its participants should not require a separate tool call per database table. Keep validation, version checks, and atomic writes inside the server. Return summaries by default; fetch full graphs or image bytes only when requested. Favor consistent names and argument shapes so I need fewer schema inspections and spend fewer tokens on plumbing.

## Original v3 watchlist

1. **Tool discovery overhead.** The v3 surface has many narrow operations. For unfamiliar tasks I need to inspect individual argument schemas before acting. Track whether this causes repeated extra calls or mistakes once the Vault contains real story data. Potential improvement: clearer operation grouping or a small number of task-oriented entry points, if repeated usage justifies it.
2. **Temporal aging specification remains unimplemented.** The separate [`temporal_aging.md`](../temporal_aging.md) specification defines opt-in profiles, rate-based effects, and **calendar, legal, biological, and experienced** ages. The current `character_age` exposes only one exact/bounded age and has no per-call `at`; no profile, effect, or preview tools are visible. The shared/session clocks and other temporal relations exist, but they do not implement the Lost Year calculation. The detailed gap assessment is below.
3. **Continuity and story time ergonomics.** The desired workflow is to select a continuity once and forget about IDs and repeated scope arguments. v3 supports name-based session selection. Watch whether that selection persists reliably across longer conversations and whether time overrides or continuity clocks are easy to reason about in actual story use.
4. **Graph and temporal read quality.** Once characters, events, locations, relationships, and objects are entered, test whether the returned graphs answer natural questions directly, how truncation is communicated, and whether useful reverse links require too many separate calls. No real story graph has been tested yet.
5. **Continuity notes.** A continuity currently has name, description, timezone, and clock fields, but no note collection like canon entities have. Chloë wants notes attached directly to a continuity for world rules, timeline assumptions, unresolved questions, and revision history. Proposed API shape: add/list/edit continuity-scoped notes through the same note model and provenance conventions used for entities, and include them in continuity reads without requiring a fake entity.
6. **Image attachment and retrieval — current API gap.** No v3 tool can attach, list, search, or view an image. Chloë wants one `image_attach` call for every canon entity type, with the entity specified by reference. The server converts incoming images to reasonably compressed JPEG viewing copies. One `image_list` and `image_search` surface finds them, and `image_view` returns MCP image content so I can inspect and show a stored image. The full request, conversion policy, and example calls are under “Images for every entity” below. This is a requested API feature, not merely a possible later UI enhancement.
7. **Tags can be written but not browsed across record types.** `entity_tag_link` already handles characters and objects through one tool; `source_tag_link` remains separate. A cross-type tag query and batch apply are proposed in the concrete review.
8. **Search and page reads take category knowledge or several calls.** `entity_search` requires one type, and `entity_get` plus `entity_graph` split a page overview. Propose cross-type search and a bounded overview with per-section cursors.
9. **Small edits and dates carry too much request structure.** `entity_patch` uses specified/value wrappers; ordinary exact dates use the full uncertain-date shape. Propose sparse changes and short exact-date inputs without losing validation or version checks.
10. **Event choice and mutation bookkeeping need simpler guidance.** Clarify local versus world events, offer an atomic scene convenience call, and keep idempotency while moving `requestToken` out of the editorial payload.
11. **Read-only companion UI and storage capacity.** Chloë will keep the web view open beside the chat and ask the assistant to make edits through the Vault API. The UI should remember its current continuity independently, render Markdown notes, expose linked graphs, tags, timelines, and images without IDs, and refresh after MCP writes. Keep JPEG viewing copies modest in Access; if the database approaches its 2 GB limit, plan a migration behind the API. The web UI can be built before any storage migration.

12. **Both event kinds need optional project associations.** A story project must show its major world events and its character, location, organization, and object events, while an idea may remain continuity-only until it belongs to a story. v4 accepts zero or more same-continuity projects on `event_record` and `entity_event_add`, adds `event_project_apply` for later changes, and aggregates both event kinds on project pages and project-filtered timelines without making the project own or copy the event.

## Concrete v3 surface review

This review is based on the exposed tool declarations and the first production onboarding. Only continuity and empty searches have been exercised against production. The recommendations below describe workflow friction visible in the signatures, not claims that untested entity calls failed.

### 1. One tagging operation, then actually browse by tag

**Current:** `entity_tag_link` and `entity_tag_unlink` already cover Character, Object, Location, Project, Organization, and WorldEvent. This is exactly the abstraction Chloë wants for a character and an object. Sources have separate `source_tag_link` and `source_tag_unlink`; `tag_search` finds tags, while `entity_search` cannot filter by tag. No reverse tag query is exposed.

**Pain:** I can apply `liminal` to Aurora, a door, and a source, but cannot ask the Vault for everything tagged `liminal` in one query. Applying one tag to several targets takes repeated calls and prior tag lookup or creation.

**Proposed:** `tag_apply({ tag, targets: [reference...], action: "add" | "remove", createIfMissing?: boolean })`. The DB resolves the target type and its junction table, validates continuity scope, and makes a batch atomic. Add `search({ tags: ["liminal"], types?: [...] })` or `tag_targets({ tag, cursor? })` for the reverse direction. Include sources and, when supported, continuity notes in the same target model. Keep tag normalization and uniqueness rules.

### 2. Notes should share a target model and support editing

**Current:** `entity_note_add` attaches to a canon entity. `entity_graph` returns notes, and `relationship_soft_delete` can delete an `EntityNote`. No continuity-note operation or clear note-edit operation is exposed.

**Pain:** Lostville itself cannot hold world rules or timeline assumptions as notes. Correcting a note appears to require a replacement rather than an edit.

**Proposed:** `note_add({ target, title?, body, format?: "markdown" })`, `note_update({ note, changes, expectedVersion })`, and paginated `notes_list({ target })`. Targets include canon entities and a named continuity. Keep note versions and source links. Markdown is stored as editable text and rendered by the UI. There is no need for a fake Lostville entity.

### 3. Search across types

**Current:** `entity_search` requires one `entityType`; sources and tags have separate searches.

**Pain:** “Find Shimmer” should not require me to guess whether the record is a character, alias, object, event, or location before searching.

**Proposed:** `search({ text?, types?, tags?, scope?: "selected" | "all", limit?, cursor? })`, returning typed semantic references, names, and short summaries. Retain type-specific calls for advanced use. Define how vault-global sources appear when searching a selected continuity so results do not imply false continuity membership.

### 4. A useful page read with per-section pagination

**Current:** `entity_get` supplies fields; `entity_graph` supplies bounded notes, events, tags, sources, projects, claims, and other relations. The graph has one `truncated` Boolean and a shared relation limit.

**Pain:** Opening a character page requires at least two reads. If the graph clips, one Boolean does not identify the clipped section or provide its next page.

**Proposed:** `get({ ref, include: ["fields", "notes", "events", "tags", "relations", "sources"], limits? })` for an overview. Give each collection its own `hasMore` and `nextCursor`; use `list_related({ ref, relation, cursor, limit })` for subsequent pages. Keep payloads bounded.

### 5. Smaller edits and ordinary dates

**Current:** `entity_patch` uses `{ specified: true, value: ... }` for each field plus `expectedVersion`. Exact dates use the same `kind/lowerBound/upperBound` structure as uncertain intervals, with nullable bounds in every call.

**Pain:** A preferred-name correction or an exact birth date takes substantially more structure than the editorial intent.

**Proposed:** `entity_update({ ref, changes: { preferredName: "Aurora" }, expectedVersion })`. Omission means unchanged; explicit `null` clears a nullable field. Reject unsupported fields. Accept an exact date as `"2026-09-28"` and a year as `{ kind: "Year", value: "2026" }`, while retaining a rich interval form for circa/before/after/ranges. Normalize and validate on the server. Keep optimistic concurrency.

### 6. Explain the two event models

**Current:** `entity_event_add` creates a local event; `world_event_participant_add` and `world_event_location_add` connect a world event.

**Pain:** The declarations do not tell me when a scene is a world event versus an entity event, or when using both duplicates a fact.

**Proposed:** Document the decision rule with story examples. Consider `event_record({ title, occurred, participants?, locations?, project? })` as an atomic convenience call when a scene should exist once and connect to several entities. Preserve the lower-level operations.

### 7. Keep safety metadata without making it editorial data

**Current:** Each mutation embeds a required `requestToken` in its request.

**Pain:** I generate transport bookkeeping for each small action.

**Proposed:** Put idempotency in common mutation metadata or let the MCP adapter generate and retain a token for one logical action. An uncertain retry must reuse the same token. Do **not** remove idempotency merely to save an argument.

### The calls I would like to make

These are API sketches, not current tool names. Angle-bracket values stand for opaque references returned by the Vault.

```js
tag_apply({
  tag: "liminal",
  targets: ["<Aurora ref>", "<door object ref>"],
  createIfMissing: true,
  action: "add"
})

search({ tags: ["liminal"], types: ["Character", "Object", "Location"] })

note_add({
  target: { kind: "Continuity", name: "Lostville" },
  title: "Time in Lostville",
  body: "## Open questions\n- Who ages during a timeline pause?",
  format: "markdown"
})
```

That first call is one editorial operation even if Access stores the two links in different tables.

## New capabilities to design

### Temporal aging: comparison with the agreed specification

The authoritative user-side requirements are in [`temporal_aging.md`](../temporal_aging.md). The current v3 API supplies useful foundations: structured uncertain dates; a shared `continuity_clock_set`; connection-only `session_time_set/clear`; `entity_local_time`; `entity_temporal_state` for residence, membership, ownership, custody, and location; and a `character_age({ characterReference })` result with status and one exact or bounded age. These do **not** yet represent a character's personal flow of time.

| Requirement from the prior spec | Current v3 surface | Needed addition |
| --- | --- | --- |
| Opt-in temporal-age profile per character; disabled means ordinary age for all categories | No profile get/set exposed | `character_temporal_profile_get/set` by semantic character reference, with enabled state, legal-age policy, notes, version, history |
| Named effects with separate biological and experienced rates (a full pause is `0.0/0.0`) | No effect create/list/patch/delete/restore exposed | One effect model with period, rates, optional world-event cause, notes, version; automatic enablement unless explicitly suppressed |
| Calendar, legal, biological, and experienced ages | `character_age` returns one `exactYears/minimumYears/maximumYears` set | Expand the existing primary age call to labelled results for all four categories, temporal-tracking state, policy, precision, as-of/timezone, applied effects, and warnings |
| Read-only hypothetical `at` instant | `character_age` has no `at`; session override is a separate stateful call | Optional `at` on `character_age`, without changing shared or session clocks |
| Preview an effect and conflicts before saving | No preview tool exposed | `character_temporal_effect_preview` returns projected ages and validation conflicts without mutation |
| Effects in graph and viewer | `entity_graph` has no declared temporal-profile/effect section | Include bounded effects and profile, then expose the same data to the UI's Age and Time panel |

The calculator must use half-open `[start,end)` intervals, ordinary rate `1.0` outside effects, reject overlapping active effects for a character, clamp effects to birth/as-of/death as defined in the spec, and reject negative or non-finite rates. Uncertain dates must produce bounds and warnings instead of an invented exact age. Legal age initially follows an explicit `CalendarAge` policy; future jurisdiction rules are separate. Keep exact elapsed durations internally even if the short response shows completed years.

Kirsty's acceptance case is concrete: born 2012-08-11, Lostville absent from 2023-08-14 through 2024-09-23, biological and experienced rates `0.0`. At the 2024-09-23 story instant, calendar/legal age is 12, biological/experienced age approximately 11, and `The Lost Year` is listed as an applied effect. With the clock unset and no `at`, return `TimelineUnset`; never use the machine clock.

The older specification illustrates numeric `characterId`, `continuityId`, `effectId`, and `operationId`. Implement its **behavior** with v3 semantic references, selected continuity, and readable idempotency tokens, keeping numeric IDs internal. Its Access Age and Time panel can become the planned web UI panel without changing the calculation contract.

### Images for every entity

**User requirement:** There is exactly **one `image_attach` tool**, not a separate tool for each entity type. Its `entityReference` argument identifies the target; the Vault resolves whether that reference names a Project, Location, Character, Organization, Object, or WorldEvent and writes to the appropriate storage relationship. Every canon entity may have any number of images. A future continuity-note target can use the same pattern; the six canon types should not wait for it.

**Upload:** `image_attach({ entityReference, image, title?, caption?, role?, canonStatus?, sourceReference?, requestToken })`. The entity is identified by an opaque semantic reference; the caller never selects a database table or a type-specific upload tool. The image input should accept bytes with a declared media type (for example, base64 in a bounded MCP request) or a host-supported uploaded-file reference; the API must not require the assistant to write a path on the Vault server. Return an image reference, entity reference and type, MIME type, pixel dimensions, byte size, and version. The server validates the actual image bytes and enforces size and format limits before decoding.

The server stores a **reasonably compressed JPEG viewing copy**. Decode non-JPEG inputs, apply EXIF orientation, convert to sRGB, downscale oversized images while keeping aspect ratio (suggested default: maximum 2048 pixels on the long edge), and encode at a tested quality target around 85. Accept an already suitable JPEG without needless recompression; normalize an oversized one. Strip unnecessary metadata. JPEG has no transparency: flatten transparent inputs against a documented default background, with an optional background colour in the request, and return a warning if meaningful transparency was lost. The API should report the resulting dimensions and size so compression is visible rather than magical. Full-resolution masters can live outside Access.

**Discovery:** One `image_list({ entityReference, cursor?, limit?, role?, canonStatus? })` works for any entity. `image_search({ text?, entities?, entityTypes?, roles?, canonStatuses?, cursor?, limit? })` searches captions/titles and filters across entity types. Neither call dumps image bytes into every search result. The owning entity's overview should include a bounded image summary and per-collection cursor.

**Viewing:** One `image_view({ imageReference, size?: "thumbnail" | "display" })` works regardless of which entity owns the image. It returns an MCP image content block with `image/jpeg` and the actual bytes, plus caption, entity reference and type, dimensions, and status as text/structured metadata. This lets me visually inspect a stored render and show it in our conversation when useful. `image_get` may return metadata alone if the client wants to decide before fetching pixels. A stable image reference enables selecting, replacing, and soft-deleting one image without touching other attachments.

**Editorial metadata:** Distinguish `concept`, `approved reference`, and `superseded`; allow roles such as portrait, location view, scene reference, and diagram. The status belongs to the attachment, not to the underlying character or event. Edits to caption/status should use an expected version. Multiple images per target and one designated primary image are useful for the future UI.

```js
image_attach({
  entityReference: "<Aurora ref>",
  image: { mediaType: "image/png", dataBase64: "<encoded image>" },
  title: "Purple coat concept",
  canonStatus: "concept",
  role: "portrait",
  backgroundColor: "#FFFFFF",
  requestToken: "<stable logical-operation token>"
})

image_list({ entityReference: "<Aurora ref>", limit: 20 })
image_view({ imageReference: "<image ref>", size: "display" })
```

These are proposed calls. The current v3 Vault exposes no image attach, list, search, or view tool. Design the bounded upload and response sizes with the actual MCP host and Access limits in mind; a thumbnail should never require fetching the full image.

### UI and database capacity

The next product phase is a **read-only companion web UI**. Chloë keeps it open beside the chat, browses the Vault, and asks the assistant to make additions or corrections through the MCP API. The viewer remembers its selected continuity independently of MCP clients. Navigation, filters, date selection, and timeline exploration are allowed; data-editing controls are outside this phase.

An entity page should show its linked notes, events, relationships, projects, sources, tags, temporal state, and image gallery. Render Markdown notes safely and make the original text easy to copy for discussion, while edits still flow through chat/API. Tag clicks should lead to cross-type results. Use names and semantic links; storage IDs stay internal. The image gallery should show captions, role, approval status, primary image, and thumbnails, then load a display image on demand.

The global continuity timeline should graphically show world events, entity-specific events, and date ranges. Uncertain dates need visibly uncertain spans, not invented exact points. Filters should focus on characters, locations, projects, tags, and event types, with links from timeline items to entity pages. A character page should show that character's events and temporal effects in context, including the four age measures once implemented.

The graphical timeline also needs a full integrated chronology table directly beneath it, modeled on the writer's Year / Month / Day / Note chronology. World events and entity-specific events across Character, Location, Organization, and Object types appear in one ordered table. Entity types and individual entities are independently selectable, the graph and table share one filter state, and an event related to several selected entities appears only once with all matching entities listed. Clicking an entity highlights all timeline items involving it without removing unrelated items. Explicitly selecting a graphical date range restricts the table to overlapping dates; clearing the range restores the complete chronology under the remaining filters. Pan and zoom alone do not filter the table.

After an MCP write, the viewer should requery affected records and visible timeline sections, or receive an equivalent change notification, so it does not require a manual reload. Provide a human-readable deep link and “copy reference” action for the current entity or event. The copied context must let the user tell an assistant exactly what is on screen without exposing a database ID or assuming that the assistant can see the browser selection.

Access `.accdb` supports image attachments, but its database file limit is 2 GB and an individual attachment is limited to 256 MB ([Microsoft's attachment guidance](https://support.microsoft.com/en-us/access/attach-files-and-graphics-to-the-records-in-your-database)). The server should track aggregate image storage and expose a health metric before the database becomes full. Store compressed viewing copies in the Vault and keep full-resolution masters outside it. If the limit becomes a practical constraint, migrate image storage or the whole database behind the same logical API. The web UI does not depend on that migration and can be built first.

## Keep these specialized

Do not flatten all temporal relations into an unchecked `link(from, to, kind)`. `object_ownership_transfer`, `character_residence_transition`, and `organization_membership_transition` encode distinct atomic changes and invariants. Claims and evidence also carry more meaning than an ordinary source link. A convenience call may compose them, while the domain layer retains its overlap checks, version checks, and integrity rules.

## Evidence and revision trigger

Verified on 2026-09-28: v3 declarations, health, continuity creation and selection, actionable missing-selection error, retained selection in the latest retest, and empty character/project searches. The production continuity has no story entities yet, so graph and edit ergonomics remain untested in practice. Prior opaque `INVALID_ARGUMENT` and cached v2 ID declarations were resolved. The brief `session_get` omission was traced to two local tunnel-client instances using the same remote profile; the duplicate was removed and the launcher now prevents duplicate profile processes and launch races.

Propose a revision when a real Lostville task is blocked or several tasks repeat the same extra calls. Keep the task, exact calls, result, and preferred simpler interaction. Do not weaken the domain model just to shorten tool names.

## Resolved onboarding findings

- Initial `entity_search` calls returned opaque `INVALID_ARGUMENT` when no continuity was selected. The server now explains that `continuity_list` and `session_continuity_set` are required; verified on 2026-09-28.
- Cached v2 declarations falsely implied numeric IDs in v3 results. Refreshed v3 schemas use continuity names and opaque semantic references. Missing numeric IDs are intended behavior.
- A `session_get` response briefly omitted the selected continuity after setting it. Process inspection found two local tunnel-client instances using the same remote `writing-vault` profile, each with independent adapter-local session state. The duplicate was removed, subsequent reads were stable, and the launcher now prevents duplicate profile processes and launch races. Treat a recurrence with exactly one tunnel instance as a new defect.
