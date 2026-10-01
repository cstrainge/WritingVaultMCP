# Writing Vault MCP — Accepted Design Decisions

Status: Sections 1-14 describe the deployed v3 foundation; sections 15-29 record accepted v4 decisions and their per-section Debug implementation status  
Accepted: 2026-09-27  
Implementation state: Sections 1 through 14 were production-verified under the earlier v3 plan. The existing Release artifact remains in use, while v4 and the read-only viewer are being validated in Debug. The user accepted the viewer as presented on 2026-09-30, and the final Phase 11 hostile review closed with zero unresolved findings. Phase 12 images and historical links, external-client image proof, and production cutover remain open under the separate [v4 plan](V4_API_PLAN.md).

## 1. Continuities are the canon boundary

Canon is grouped by continuity. Facts in one fictional world must not affect facts in another.

- Add a first-class `Continuity` entity.
- Every project belongs to exactly one continuity.
- Characters, locations, organizations, objects, world events, claims, and fictional temporal facts belong to exactly one continuity.
- Projects in the same continuity may share canon entities.
- Cross-continuity mutable relationships are rejected.
- Alternate versions of a concept are separate records. An optional continuity-scoped variant group says that records are counterparts without sharing mutable facts; a group can never bridge continuities or entity types.
- Tags and research sources are vault-global and may be used in multiple continuities.
- Claims drawn from a source are continuity-scoped because the interpretation or fictional fact may differ by continuity.
- Artificial current time belongs to a continuity.

Example: records from a science-fiction setting and records from Lostville live in separate continuities even when both are stored in the same vault.

## 2. Fictional dates are structured, fuzzy, and timezone-free

Ordinary fictional dates do not carry a timezone. They must support increasing precision without losing the writer's original wording.

The application exposes a provider-free `StoryDate`/`StoryInterval` value with:

- `Kind`: `Unknown`, `ExactInstant`, `ExactDate`, `Month`, `Year`, `Circa`, `KnownRange`, `UncertainRange`, `Before`, or `After`. Legacy stored `Range` values remain readable as ambiguous data; v4 rejects new `Range` writes.
- Optional normalized lower and upper bounds.
- Explicit boundary inclusivity where a range needs it.
- `OriginalText` for wording that should be preserved.
- A calendar identifier, initially Gregorian.

The MCP representation is a structured object rather than an encoded string. Reads return both the entered representation and computed bounds, making fuzzy values easy to inspect, compare, and patch.

Phase 12 stores known intervals as `KnownRange` and bounded uncertainty as `UncertainRange`. Migration 009 expands the allowed stored kinds without changing legacy `Range` rows; `originalText` remains wording only. The local, read-only `schema range-review` administrative command inventories ambiguous rows with their internal storage keys for review. MCP inputs reject legacy `Range`, and MCP/viewer results never expose those storage keys.

Examples:

| Input | Meaning |
|---|---|
| `1987` | A year-precision value spanning 1987 |
| `March 1987` | A month-precision value |
| `circa 1987` | An approximate value with preserved wording |
| `before 1990` | An open-ended interval with an upper bound |
| `1987–1991` | A bounded range |
| `Third winter after the Fall` | Preserved text without invented Gregorian precision |

Age calculation returns an exact age when the inputs permit it, an age range when bounds permit one, and an explicit unknown/indeterminate status otherwise.

The existing leap-day rule is retained: in a non-leap year, a February 29 birthday advances on February 28.

## 3. Artificial current time is a zoned continuity clock

The artificial clock represents a real instant within a continuity and also records the timezone in which the user set or views the reference clock.

Persist:

- `CurrentInstantUtc` as an Access `Date/Time` containing the UTC wall-clock value by convention.
- `ReferenceTimeZoneId` as a validated IANA timezone identifier such as `America/Vancouver`.
- `Version` for optimistic concurrency.
- Operational `UpdatedAtUtc` separately from fictional time.

The MCP API accepts and returns a local date/time, timezone identifier, resolved offset, and UTC instant. Ambiguous or nonexistent local times at daylight-saving transitions are rejected unless the caller supplies an explicit offset that resolves the ambiguity.

Entity-local current time is resolved in this order:

1. An explicit timezone attached to the requested context.
2. The entity's relevant location as of the continuity clock.
3. The nearest ancestor location with a timezone.
4. The continuity's reference timezone, returned with `TimezoneSource = ContinuityDefault`.
5. If no timezone is available, return `TimezoneUnknown`; never use the machine timezone silently.

Characters derive location from residence history. Objects derive it from location/custody history when available. Organizations may use a headquarters or primary-location history. Locations carry or inherit their own timezone. Tools expose which rule supplied the timezone.

## 4. Object owners use an extensible principal model

Object ownership is not restricted to characters.

- Add a continuity-scoped ownership-principal identity.
- Initially supported principal kinds are `Character`, `Organization`, and `External`.
- Character and organization principals retain enforceable foreign keys through explicit subtype/link tables.
- External principals carry a stable descriptive label.
- Ownership distinguishes `Owned`, `Unknown`, and `Unowned`; unknown and unowned are not represented by fake entities.
- Ownership history is grouped into non-overlapping ownership periods for each object.
- A known-owner period contains one or more ownership-principal links. One link is the common case; multiple links represent simultaneous co-owners.
- An unknown-owner or unowned period has no principal links and records the corresponding state explicitly.
- Owner links may carry an optional ownership share and notes. A period either omits all shares or supplies shares for every owner; supplied shares must total 100 percent.
- Adding or removing a co-owner changes the ownership composition and therefore starts a new period unless it corrects the current period in place through a versioned edit.
- The ownership-transfer command performs that close/open pair atomically at a timezone-free effective boundary and requires the prior period's version.
- Ownership, possession/custody, and physical location are separate concepts.
- At most one ownership period may be active for an object at an instant, but that period may contain any positive number of co-owners.

## 5. Character relationships are typed and temporal

- **Current implementation:** A `CharacterRelationships` row owns one period and exactly two characters. Separate rows can represent an on/off relationship, but they have separate semantic references, notes, and histories. The timeline currently projects the one period, and the expanded chronology splits only bounded multi-day ranges. This does not meet the group or per-person history requirements below.
- **Accepted target:** A relationship is one versioned, continuity-scoped identity with a shared type, Markdown notes, and any number of relationship events. It has between two and 100 distinct character participants. The 100-character limit is a practical request/storage bound, not a two-character data-model restriction. Every participant can discover the same relationship from their own page and semantic reference.
- Each participant owns a repeatable collection of timezone-free, fuzzy-datable **membership periods** under that relationship. Members may enter, leave, and re-enter independently; another member's departure does not end the relationship or erase the remaining members' histories. A bounded period supplies a distinct enter and exit chronology entry for that character, including when both boundaries fall on one day. An open or uncertain period displays only the boundary actually known and does not invent an exact date.
- The relationship's own active intervals and enter/exit entries are derived from the membership chronology when at least two characters are participating. Intervals may stop and restart as membership changes. If fuzzy dates prevent a defensible exact transition, the relationship-level chronology must expose the uncertainty instead of fabricating a day. Each participant entry links to its relationship and character; each relationship transition links to the involved participants. The combined timeline renders a transition once, while focused character views include that character's own entries and the shared relationship context.
- One undirected relationship type and label applies to every participant in a group. Directed forward/inverse types retain their existing explicit two-person semantics; a directed type cannot be used for a group of more than two without a separate directional-role design. The shared-type choice does not imply a source/target hierarchy among group participants.
- A relationship event (meeting, betrayal, reconciliation, and so on) belongs to the relationship identity, not to a membership period or one endpoint. Its own date may precede or follow any membership period. The relationship overview and every participating character expose the same event; the continuity timeline emits it once and can find it from any participant, project, or linked world event.
- The schema change must be additive and preserve all applied migration fingerprints. Existing two-character rows migrate losslessly to one relationship identity with two membership periods matching the legacy interval; distinct legacy rows must not be silently merged because their references, notes, and histories may differ. An explicit merge operation may consolidate legacy identities only after showing their periods, notes, events, and reference redirects for review; historical references must still resolve. New writes use one relationship reference plus repeatable membership-period operations. Legacy pair reads remain valid during cutover. Delete/restore, audit, duplicate/overlap checks, and stale-version handling apply to the group and its child periods. The additive group, member, event, reviewed legacy-merge, and independently fuzzy join/leave paths are implemented in Debug and passed the final Phase 11 hostile review.
- A 2026-09-30 Phase 11 clarification requires each membership period to hold independent join and leave story dates, each with its own exact/fuzzy/unknown precision. Additive Debug migration 007 preserves existing refs and data and backfills both transitions only for exact-date and exact-instant legacy periods. The period row stores a conservative possible-occupancy envelope for overlap validation; neither fuzzy boundary is promoted into an exact group transition. Timeline entry and exit items use their respective boundary dates without inventing precision. The add operation can create both dates atomically; the set operation edits them with a period version check. This passed Phase 11 review in Debug and has not reached Release.
- Membership transition titles say that a character "entered a relationship" or "left a relationship." The timeline viewer combines distinct characters' transitions into one natural-language list only when they refer to the same relationship, direction, and exact date or instant. It retains each period and transition as a separate underlying record, keeps fuzzy dates separate, and links a combined display entry to the relationship and its participants. Join/leave entries are independent events, not the endpoints of a drawn duration track.
- The existing `relationship_notes_set` mutation continues to edit the identity-level Markdown note using an expected version. The same notes convention applies to residences, memberships, organization locations, object periods, and world-event links.

## 6. Sources retain URLs, snapshots, and local claims

A source may point to material that changes or disappears, so the vault preserves both how to revisit it and what was learned from it.

### Source record

- Canonical URL and optional alternate/archive URL.
- Title, author/publisher, source type, citation, and notes.
- Last successful retrieval time and retrieval status.

### Source snapshot

- Retrieval UTC time, media type, byte size, content hash, and extraction status.
- Optional content-addressed cached file stored in a companion vault cache directory rather than as a large Access attachment.
- A relative cache path and SHA-256 hash stored in Access.
- Reads never fetch the network implicitly; refresh is an explicit operation.

### Local claim

- Continuity-scoped claim text retained in Access.
- Confidence/status, commentary, and optional target field.
- Source locator such as page, chapter, section, timestamp, fragment, or selector.
- Link to the source and, when available, the exact snapshot from which it was derived.
- Optional short evidence excerpt or writer-authored summary.
- Explicit links to supported entities, events, notes, or relationships through foreign-key-preserving junctions.

The claim remains readable if the URL disappears or the optional cached file is unavailable.

## 7. Deletion is soft and discoverable

- Normal deletion records `IsDeleted`, deletion UTC time, deleting operation ID, and a new version.
- Normal searches exclude deleted records.
- Every search can explicitly include only deleted records or both active and deleted records where appropriate.
- Direct lookup can return a deleted-state result instead of pretending the identifier never existed.
- Restore is a normal versioned operation.
- Unique identities remain reserved while deleted; callers are directed to restore the old record.
- Normal graph reads hide deleted owners and deleted counterpart records. Explicit deleted-state graph reads are opt-in.
- Relationship types and ownership principals use the same versioned delete/restore lifecycle and cannot be deleted while retained relationships, ownership links, or custody periods reference them.
- Permanent purge is an administrative operation requiring a preview, expected versions, and a recent verified backup.

## 8. History stays deliberately simple

The vault uses an audit/change log, not event sourcing.

For each successful mutation, store:

- Internal operation ID derived from the MCP request token.
- Real UTC timestamp.
- Local client/tool label; there are no user-account or role tables.
- Action and affected record references.
- Version before and after.
- A compact JSON change summary or before/after values needed to understand the edit.

Long-form story content is retained in the vault records and verified backups. Journal summaries replace configured long-form values with their length and SHA-256 fingerprint, omit duplicated operation/client fields, and operational logs record only correlation metadata. Missing client labels are recorded as `unspecified` rather than leaving attribution blank.

The log supports inspection and diagnosis. Recovery relies on soft-delete restore and verified database backups. The first release does not provide arbitrary transaction replay, automatic inverse-command generation, or a general-purpose undo engine.

## 9. World events and entity events remain distinct

- A world event is an independent occurrence in the continuity.
- Characters, locations, organizations, and objects may be participants in or affected by it.
- Participant links can record role, impact, outcome, and notes.
- Entity-specific events remain available for facts that do not warrant a world event.
- An entity-specific event may reference a world event and add entity-specific context.
- Shared date, title, and core description remain on the world event so linked records do not become divergent copies.
- World events and entity-specific events may be associated with zero or more projects in the same continuity; the pending relationship-event amendment extends the same rule to relationship events. An unassigned event is valid continuity canon, which covers ideas that are not yet tied to a particular story.
- Project association is classification, not ownership: removing a project link never deletes the event, and a project page aggregates both event kinds without copying their date, title, or description.

## 10. Single-user and single-writer operating model

- The system has one human user; it does not add accounts, roles, or ACL tables.
- One elected backend process per normalized database path owns writes to the local Access file; local stdio adapters share it.
- Mutations pass through the backend's process-wide write coordinator.
- Direct Access edits while the MCP server is write-enabled are unsupported because they can bypass application-only invariants.
- Read-only inspection may be supported when it does not interfere with locks.
- Multi-host writes and network-share hosting are outside the supported boundary.
- Every MCP mutation carries a readable vault-wide request token; the backend derives the internal operation ID used for idempotency and audit correlation.
- Database backups and their verification metadata are stored under the current user's OneDrive-backed `Documents\WritingValutBackup` folder, configured as `BackupRoot` in the ignored local settings.
- Backup creation holds the Writing Vault owner lease and a stable read handle for the entire hash-and-copy operation. Its manifest records source and copy hashes, schema identity, provider, purpose, and retention setting.
- Permanent purge requires the verified backup hash to match the current closed database byte-for-byte, so intervening writes invalidate the purge evidence.

## 11. Accepted engineering defaults

- Every mutable record uses a mandatory monotonic integer version.
- Operational timestamps use real UTC.
- Fictional dates remain timezone-free except for the zoned artificial continuity clock and derived entity-local current time.
- MCP tool and error schemas use semantic tool-surface version 3.0.
- No arbitrary SQL tool is exposed.
- Reads never execute schema changes.
- Schema migration is an explicit administrative operation.
- Client-facing errors do not expose SQL, connection strings, or filesystem paths.
- The active persistence contract is the unified `AccessVaultService`; superseded repository experiments remain excluded from production compilation.
- All Access commands have a 30-second timeout. Entity search uses ID keyset pagination, graph collections are capped at 50, and returned long-text values are capped at 65,536 characters with explicit truncation flags.
- Required Long Text columns use `NOT NULL` plus application whitespace validation. ACE cannot reliably evaluate `Len` or `Trim` check expressions on large Long Text values, so migration `20260927_003_longtext_checks` removes those two unsafe checks without rebuilding populated databases.
- Forward migrations are ordered, fingerprinted, idempotent after partial DDL application, and are the only runtime component allowed to issue DDL.

## 12. Names, aliases, and membership history

- Preferred display names for characters, locations, organizations, objects, projects, and world events may repeat within a continuity. Shared names are normal story data and are not treated as identity.
- Numeric Access keys are storage details. The implemented MCP surface uses the semantic-reference model specified in section 14.
- An alias is unique within its owning character or organization after trimming and case-insensitive canonical normalization. The same alias may belong to different owners.
- Organization memberships for the same character, organization, and normalized role may recur but may not overlap. Different simultaneous roles are allowed.
- Soft-deleted temporal records do not participate in active overlap checks. Restoring one must re-run the same invariants as a new active write.

## 13. Project, place, event, and organization scope stays flat

- Projects are flat work containers. A project name and description identify a series, novel, story, edition, or other writing unit; project-to-project nesting is not supported until a concrete workflow requires it.
- Project assignments may attach a role and notes. Characters, locations, organizations, objects, world events, and entity-specific events can belong to any number of projects in the same continuity; relationship events join that set when the Phase 11 amendment is implemented. The common case may be zero or one project, but the model permits reuse across stories without duplicating the event.
- Continuity-aware duplication copies only an entity's core subtype fields into a new independent record. It clears parent, birthplace identity, variant group, aliases, notes, events, tags, sources, projects, and temporal relationships so mutable canon is never shared accidentally.
- Organization hierarchy and organization-to-organization relationships are deferred. The current model covers aliases, members, locations, notes, events, tags, sources, projects, and world-event participation without introducing a speculative hierarchy.
- A real-world place that does not warrant its own location record is preserved in the nearest relationship detail or notes field. Character birthplaces have the dedicated `BirthLocationDetail` field. The system does not create fake location identities solely to hold a display label.
- Entity-specific events belong to characters, locations, organizations, or objects and may reference one world event. Projects do not own either event kind, and world events do not contain duplicate entity-event copies of themselves.
- World-event project associations continue to use `ProjectEntities`. Phase 2 adds `EntityEventProjects` for local events, with one active row per event/project pair, optional role and notes, same-continuity enforcement, version/audit/soft-delete behavior, and restore-time invariant checks. v4 presents both physical relations through `event_project_apply` and the same read projection.
- World-event participants are characters, organizations, or objects. Locations use the separate multi-location relation, which can mark at most one active primary location.
- In the current pair-only model, relationship types may permit distinct overlapping relationship rows, but an exact equivalent period is a duplicate; undirected endpoints are canonicalized. In the group model, participant identity is order-independent. Two active groups with the same participant set and type require an explicit distinction rather than an accidental duplicate, and membership periods for the same character in the same group may never overlap. Distinct non-overlapping periods remain separate to preserve exits and re-entries. The legacy type-level overlap policy still governs separate directed pair relationships.
- Claim evidence relations form a closed set: `Supports`, `Contradicts`, and `Context`.
- Residence moves and membership end/change operations close the prior versioned period and open an optional ongoing replacement in one transaction at a timezone-free effective boundary.

## 14. Multi-client process and session model

Each client-facing process is a thin stdio adapter to one shared database backend. The v3 backend and tool surface were verified in Debug and Release; v4 remains under Debug validation.

- A backend is unique per physical local-volume path and elected with a named mutex. Windows drive-letter and local volume mount-point aliases resolve through the volume GUID so two spellings of the same database cannot elect competing backends.
- Client adapters connect through a current-user-only named pipe. If adapters race to launch a backend, the named mutex elects one and the others connect to it.
- The backend counts live pipe connections and in-flight operations. After the last client disconnects, it shuts down only after a short grace period and after active work finishes.
- Microsoft Access is a read-only viewer while the backend owns writes. All mutations continue to pass through the guarded application command boundary.
- Each client connection has independent session context: a selected continuity and optional artificial-time override. The persisted continuity clock remains the shared default when a client has no override.
- A client selects continuity by its unique normalized name. Once selected, continuity is implicit for later continuity-scoped calls on that connection.
- Server initialization instructions tell model clients to call `continuity_list` and then `session_continuity_set` before continuity-scoped work. Calling a scoped tool without a selection returns a safe, actionable MCP tool error naming that sequence; it must not be collapsed into an opaque storage or argument failure.
- Numeric Access IDs are internal storage keys. MCP tool inputs, results, errors, and ordinary history navigation must use human-readable semantic references and must not expose raw database IDs.
- Semantic references combine a readable type/name slug with an opaque stable suffix. Renames update the displayed slug while older references remain resolvable by their suffix.
- Mutations accept a readable vault-wide `requestToken`, derive the operation GUID internally, and never expose that GUID. Client attribution does not change the semantic idempotency hash, so an identical retry can replay after reconnecting through another client.
- Continuity selection by name and artificial-time overrides are connection-local. Temporal reads report whether they used the connection override or the persisted continuity clock.
- Read-only mode is enforced per adapter connection; a shared backend can concurrently serve read-only and read/write clients without advertising write tools to the read-only connection.
- One local tunnel-client instance owns each remote ChatGPT tunnel profile. Duplicate instances of the same profile would create separate adapter sessions behind one logical remote client and make connection-local continuity or time appear nondeterministic, so the launcher rejects both existing instances and simultaneous launch races. Distinct clients such as Claude and ChatGPT still use independent adapters against the shared backend.
- The production Release passed the complete automated suite, live read-only/write smoke tests, and a verified backup/restore drill before cutover was closed.

## 15. v4 evolves the surface without creating another database owner

Status: Implemented through the Phase 3 application and Phase 4 read model on 2026-09-28; web rendering passed its Phase 9 technical review on 2026-09-29. The user accepted the viewer presentation on 2026-09-30.

- The elected backend remains the only application process that opens the Access database. MCP adapters and the web companion attach through the current-user named pipe.
- A connection handshake selects one complete tool surface. The same v4 backend may serve v3 and v4 connections concurrently, but one connection never receives a mixed surface.
- During development, v3 remains the default production surface and v4 is selected explicitly. At v4 production cutover, v4 becomes the default and the new binary retains a v3 compatibility surface for one release cycle.
- “v3 compatibility” means the v4 binary serving v3 contracts over the migrated schema. An old binary is not expected to open a newer exact schema.
- Requested-surface negotiation fails explicitly when the backend cannot supply that version. Clients never infer a surface from missing tools.
- Work remains in Debug builds through Phases 1–11. A Release candidate is produced only for the approved Phase 12 cutover.

## 16. v4 uses page-shaped reads and guarded domain writes

Status: Implemented in Phase 6 on 2026-09-28; the two real chat-client ingress probes remain the open Phase 1 deployment check.

- Common reads are `search`, `get`, `list_related`, `timeline_get`, `history_get`, and `changes_since`. They return bounded summaries or independently paged sections.
- Common editorial writes may batch one intent, including sparse entity updates, notes, tags, images, and recording a world event with its participants and locations.
- Invariant-bearing operations remain specialized. Ownership transfer, residence transition, membership transition, character relationships, claims/evidence, and similar commands are not flattened into an unchecked generic link operation.
- A semantic reference is authoritative. Natural-name input resolves only on an exact unique normalized match in the selected context. Ambiguity returns labelled candidates and performs no write.
- Continuity selection continues to use its unique name. All other public record identity uses a stable semantic reference.
- Database row keys, Access AutoNumber values, database GUIDs, internal operation GUIDs, pipe names, and database paths are forbidden in public MCP and web contracts.
- The public contract does **not** ban every field ending in `Id`. Domain identifiers with real external meaning, such as `referenceTimeZoneId` and `calendarId`, are allowed and documented. Contract tests distinguish an internal storage identifier from a domain identifier instead of relying on the field suffix alone.
- Compact exact-date and year inputs normalize into the full fuzzy story-date model. Omission means unchanged in sparse patches; explicit `null` clears a nullable field.
- Mutation idempotency remains an explicit common top-level `mutationToken` until supported MCP hosts provide a retry identity that survives uncertain retries and reconnects. Existing-record changes retain expected-version checks.
- Public errors use stable codes plus field and recovery details. A bare `INVALID_ARGUMENT` is not an acceptable v4 response.

## 17. The companion web application is local and read-only by construction

Status: Phase 8 completed and visually accepted on 2026-09-29. Record pages and graphical rendering passed Phase 9 and 10 technical review; the user accepted the consolidated Phase 11 viewer presentation on 2026-09-30.

- `WritingVault.Web` is an ASP.NET Core local web application with a browser-native, progressively layered shell. It uses local HTML, CSS, and JavaScript assets and will host the custom accessible SVG timeline without adding a second server query API.
- The default Combined timeline follows the user's 2026-09-29 card-and-ruler visual reference: dated records share one time axis, staggered cards connect to their dates, periods remain visible as spans, and colors complement the Vault's copper palette. Packed dates can collapse to compact marks until zoomed. The optional Grouped layout is implemented in Debug and persists in the timeline URL. The user accepted the viewer presentation for Phase 11 on 2026-09-30.
- The viewer consumes the typed public v4 read contract over a read-only backend connection. It neither opens Access nor references OleDb repositories, command services, migration, purge, or backup writers.
- Each browser tab receives independent continuity and artificial-time session state backed by separate interactive and watcher connections. A cloned tab detects a duplicated session token and claims a new one. The continuity also appears in the route so refresh and reconnect restore it deterministically without affecting Claude, ChatGPT, or another tab.
- The viewer remembers a successfully set artificial time, timezone, and local input in a browser cookie keyed by continuity name for one year. Selecting that continuity later, or reconnecting its backend session, applies the saved instant to the tab's independent session. Clearing the tab override also removes that continuity's cookie and returns to the shared continuity clock. The cookie is only a local viewer preference; it never writes the database clock or changes an already open tab's session. Session-time labels use the backend-accepted local offset when browser and backend timezone rules differ.
- The backend records the read-only connection capability, omits mutation tools, and rejects mutation dispatch even if a request fabricates a write tool name.
- The Release endpoint is the literal `http://127.0.0.1:5284`; Debug uses its own fixed `http://127.0.0.1:5285`. A collision is an actionable startup error rather than a silent address change.
- Host and Origin validation, disabled CORS, same-origin browser state, strict content security policy, and no remote scripts, fonts, analytics, or automatic remote images protect the local reading surface.
- There are no editing controls or application write endpoints. The user asks an MCP client to make changes and the viewer refreshes committed results.
- The viewer provides “Copy page link” and “Copy Vault reference.” The latter copies label, kind, continuity name, and semantic reference so a chat client can resolve the exact visible record without seeing the browser or receiving a database key.
- LAN/public hosting, authentication, accounts, and web editing require a separate threat model and are outside v4.

## 18. Continuity notes and Markdown share behavior without forcing one physical table

Status: Storage and API implemented in Phase 4; browser subscription and stale-state UI implemented in Phase 8; Markdown record rendering passed Phase 9 technical review. The user accepted the viewer presentation on 2026-09-30.

- Continuities receive notes with the same public add, update, list, version, deletion, history, and source-provenance behavior as entity notes.
- Storage uses a separate `ContinuityNotes` table and continuity-note/source junction rather than rebuilding existing entity-note and claim-note foreign keys. A shared application model hides that physical split.
- Notes store Markdown source. The web viewer renders a documented CommonMark subset with raw HTML disabled and sanitizes the result again.
- Scripts, event attributes, unsafe URL schemes, frames, and automatic remote image fetches are rejected. Exact source remains available in a collapsible read-only view and is easy to copy.

## 19. Images keep safe Access renditions and recoverable external masters

Status: Storage and MCP image handling implemented in Phase 6 on 2026-09-28; browser galleries passed Phase 9 technical review. At the user's 2026-09-30 direction, real PNG ingress from both chat clients is a Phase 12 Release usability check, not a switch-over prerequisite. The user accepted the viewer presentation on 2026-09-30.

Phase 12 owner expansion (requested 2026-09-30): Images attached to CanonEntities (Project, Location, Character, Organization, Object, and WorldEvent) retain their existing rows and public references. Additive Debug migration 010 creates separate story-image storage and renditions for continuity, shared relationship, entity-specific event, and relationship event owners. It does not copy existing rendition blobs or create fake canon entities. A `story_image_attach` write uses a story owner reference; the original `image_attach` remains valid for canon entities. Each supported owner's page must expose its images, and image search, change notifications, backups, and lifecycle behavior follow the same owner scope. A reviewed relationship merge moves relationship-owned images to the surviving relationship without changing their image references, resolves primary-image collisions in favor of the target, and leaves relationship-event images on their stable event records. The merge preview and review token include image references and versions. Sources, tags, and notes are supporting records rather than image owners. A note may embed an existing Vault image from any supported story-record owner in any continuity, not just its own owner, using a safe semantic-reference Markdown form with alt text and a read-only fetch; remote image URLs and raw HTML do not become image fetches. This is an explicit exception to ordinary continuity-scoped reads, so the image read path accepts a globally unique explicit image reference; ordinary entity/timeline reads stay scoped. The inline image adds no owner, continuity, or caption prose: the note author controls its surrounding content. A cross-continuity image lookup may show ownership context to help authors obtain references without exposing database identifiers. Validate all owner kinds and note rendering in Debug before building Release. Real PNG transmission from the two chat clients is a separate Phase 12 Release usability test.

Debug progress on 2026-10-01: Migration 010 and focused tests cover continuity, relationship, entity-event, and relationship-event image owners alongside unchanged legacy entity-image references. Global `image_search` discovers both kinds. The viewer shows owner galleries, a vault-wide lookup, safe current `vault-image:` embeds, and a bookmarkable original-image page with bounded fit/zoom/pan controls, owner, linked source, and UTC metadata timestamps. `record_locate` resolves current semantic note links across continuities without changing the MCP session; an explicit viewer click changes its own continuity if needed. A real Debug browser probe loaded the original and inline bytes from a disposable migrated fixture and followed the current record link. Pinned content revisions, page snapshots, full audit presentation, and Phase 12 hostile review remain open.

Phase 12 image versioning (requested 2026-09-30): Image content has immutable revisions distinct from the image record's metadata/concurrency version. Replacing image content creates a new revision and makes it current without changing historical bytes or the public image identity. In note Markdown, `vault-image:<image-ref>` follows the current revision, while `vault-image:<image-ref>?v=<positive-revision>` stays on that retained revision. Galleries expose both copyable forms and revision history. The same continuity, deletion, backup, integrity, storage-capacity, and read-only viewer rules apply to pinned and current reads; metadata-only edits do not change the pinned content. The Phase 12 Debug hostile review must prove latest and pinned links remain distinct after replacement and live refresh.

Phase 12 image detail (requested 2026-09-30): Clicking or keyboard-activating an image embedded in note Markdown opens a stable image page at the displayed revision. The page supports full original resolution as well as fit/zoom viewing and shows ownership, source/provenance, image metadata, content revisions, and relevant audit information without exposing database keys or local file paths. An unpinned image opens the latest revision; a pinned image opens that exact older revision. Inline Markdown rendering still adds no automatic caption or provenance prose around the image.

Phase 12 image-content revision implementation (Debug, 2026-10-01): Additive migration 011 maps each existing image to current content revision 1 without changing its public semantic reference. `image_replace` keeps old originals and checked historical display/thumbnail renditions; `image_update` edits metadata without advancing the content revision. An unpinned image link follows the latest bytes, while `?v=N` selects an immutable retained content revision. `image_revision_history` pages these revisions without returning bytes. Backup and restore include every retained original, and image mutations invalidate viewer notes across continuities because a note may embed any image in the Vault. A populated migration fixture and focused Debug tests passed; Release and hostile review are still pending.

Phase 12 Markdown image sizing (requested 2026-09-30): Inline Vault image links accept a restricted `{width=300px}` or `{width=50%}` suffix, with bounded numeric pixel/percentage values, automatic height, and a container-width cap. The sizing changes only the inline rendering, not the stored image or the full-size detail page. Arbitrary attributes, CSS, raw HTML, relative filesystem paths, and remote image URLs do not become image-loading mechanisms.

Phase 12 note links (requested 2026-09-30): A note's Markdown may link to any navigable Vault record with author-chosen text and an opaque semantic reference, for example `[Chloë Bell](vault-record:<semantic-ref>)`. The existing reference token resolves independently of the display-name slug, so a rename does not break an old link; test this explicitly. This covers characters, other core entities, world/entity/relationship events, relationships, continuities, and supporting record pages. A cross-continuity click navigates the viewer to the target continuity; it does not silently change an MCP client's selected continuity. Missing or deleted targets show an explicit unavailable state. Unsafe URL schemes, raw HTML, and arbitrary remote image loads remain inert; links do not inject owner or other explanatory prose into the note. Unpinned links show the latest page. A `?v=<positive-snapshot-version>` link shows one immutable, coherent older page with page-visible fields, notes, and associations, a prominent historical-version notice, and a **View latest** link. Capture future versions transactionally and migrate only a truthful current-state baseline; incomplete historical audit deltas must not be presented as reconstructed pages.

- One image API works for all six canon entity types. Attachment metadata belongs to the entity-image relationship and includes title, caption, alt text, role, canon status, optional source, primary state, version, and deletion state.
- Access stores bounded JPEG display and thumbnail renditions in ordinary binary columns. It does not use the Access complex `Attachment` type.
- Uploaded originals are kept as immutable content-addressed files in a configured companion asset root beneath the current user's OneDrive-backed `Documents\WritingValutBackup` folder by default. Access stores only their relative path, hash, media type, dimensions, and size.
- File ingest writes a temporary file, verifies its SHA-256 hash, and atomically renames it before committing the Access reference. Orphan immutable files are safe and may be collected after a retention window; a missing or hash-mismatched referenced file is an integrity issue.
- Verified backups include an asset manifest and validate referenced hashes without duplicating unchanged content-addressed masters into every backup.
- The Debug implementation enforces a 20 MB input ceiling, 2048 pixels and 5 MB for the display rendition, and 512 pixels and 512 KB for the thumbnail. Release acceptance still requires the real-client ingress probes.
- Conversion applies orientation, converts to sRGB, strips unsafe metadata, preserves a suitable JPEG without needless recompression, and reports transparency flattening. Search/list calls return metadata only; bytes are fetched explicitly.
- Aggregate image bytes and database capacity bands appear in health. Existing database warning and hard-stop ceilings remain authoritative.
- A non-persisting Debug PNG ingress probe is available for ChatGPT and Claude. It hashes and discards bytes and logs no content. A server-local attachment path is never assumed. The user chose to run the real-client image path against the v4 Release deployment and fix defects then; actual request shapes, payload ceilings, and any adapter normalization are Phase 12 usability evidence, not a switch-over gate.

## 20. Story chronology remains useful without an artificial clock

Status: Accepted for v4 on 2026-09-28; graphical timeline implementation passed Phase 10 technical review on 2026-09-29, and the user accepted the Phase 11 viewer presentation on 2026-09-30.

- `timeline_get` renders stored historical events, fuzzy dates, and temporal ranges whether or not the selected continuity has an artificial current time.
- An unset clock suppresses the positioned Now box and shows a small unset status. When story time is set, a highlighted Now box is placed at its precise local date and time on the graph. If it lies outside the graph's date window, a highlighted control shows its direction and lets the user jump to it without stretching the historical view. Only calculations and projections that require an as-of time, including age and current temporal state, return `TimelineUnset` unless the call supplies `at`.
- Story chronology and real-UTC audit history remain separate views.
- The timeline projects authoritative records rather than duplicating them into a timeline table. It includes world and entity events, character lifespan bounds, relationships, residences, memberships, organization locations, object ownership/custody/location, and temporal-age effects.
- The graphical timeline has a synchronized chronology table immediately beneath it. The table integrates world events with Character, Location, Organization, and Object events in one ordered stream and can also include the other temporal lanes. It is a read projection, not a duplicated storage table.
- The chronology table presents precise Gregorian dates in separate Year, Month, and Day or period columns. A component appears only when it changes from the previous visible row; the first row establishes its own context. Same-day timed events still show their distinct clock times. Year- and month-precision records leave finer columns empty. Fuzzy ranges, circa dates, open-ended bounds, and non-Gregorian dates span all three date columns with their complete human-readable period wording, so a bound cannot masquerade as an exact event date. Assistive technology receives the full date on every precise row.
- A multi-day bounded Gregorian `KnownRange` appears twice in the integrated chronology: one start entry with the full event details and one short end entry, sorted at their respective dates by the backend's optional `expandRanges` detail projection. `UncertainRange` and ambiguous legacy `Range` values remain single entries so a possible bound is never presented as a certain start or end. The graphical timeline and ordinary MCP detail reads retain one underlying event. A one-day known range, a circa date, and an open-ended bound remain one entry. The table gives overlapping known ranges separate arrow tracks in a left gutter, with both arrows pointing toward their item; tracks clipped by an explicit date selection continue to the table edge and indicate that the paired entry is outside the selected dates. A selected date window lying entirely inside a known range receives one `Ongoing` context entry rather than an invented start or end. Exclusive midnight upper bounds use the final included day for the displayed end entry.
- The default graphical layout is Combined: enabled dated items share one time axis instead of starting in separate record-type lanes. Grouped is an optional alternate layout. Both are implemented in Debug with the user's accepted card-and-ruler style. Changing layout must not change the filtered items, chronology table, or treatment of uncertain and undated dates.
- Timeline filters include independently selectable entity types and a searchable selector for individual entities. The graph, table, item counts, Undated group, and bookmark URL share exactly the same filter state. An event related to multiple selected entities appears once and lists each matching entity.
- The viewer retrieves every filtered chronology item through internal revision-bound API batches and renders one continuous table without page controls. Ordinary graph pan and zoom only change the viewport and do not hide table rows. With story time set, a highlighted Now row appears at its calendar position independently of graph viewport; in Narrative mode it appears first as a separate clock reference. Brushing or selecting a date range filters the table to items whose possible bounds overlap that range and includes Now only if it lies inside the selected dates; clearing the range restores the complete chronology allowed by the other filters. The separate undated section also loads completely when no dated range is active.
- The graph uses a horizontally scrollable date canvas with sticky lane labels. Canvas width grows with the current date span but is capped at 16,000 pixels; keyboard arrows and touch gestures can scroll it. Earlier/Later traverse the canvas and request an adjacent bounded date window at its edge, while zoom preserves the visible date. Graph scrolling never changes the integrated table filter.
- Clicking an entity link or chip highlights every currently visible graph item and table row involving that entity, while unrelated items remain present but visually subdued. This transient highlight is separate from the persistent individual-entity inclusion filter and can be cleared by clicking again, pressing Escape, or using a visible clear action.
- The primary table columns are Year, Month, Day or range, Event, Event type, Related entities, and Projects. Visual grouping may suppress repeated date text, but accessible row data always retains a complete date label. Fuzzy ranges and missing date components remain explicit rather than being filled with invented values.
- Exact points, closed ranges, month/year precision, open ranges, approximate bounds, undated records, and narrative-only order remain visually distinct. `KnownRange` bars are solid, `UncertainRange` bars use diagonal barber-pole stripes, and open-ended `Before`/`After` ranges and ambiguous legacy `Range` values use a checkerboard pattern; all bars have the same thickness. Start and end arrows point toward their items, use only the half-head inside their range, and have a visible gap from the vertical bar. Fully undated records have no drawable bounds and stay in Undated Material. The UI never invents an exact point for uncertain or ambiguous data. `OriginalText` affects wording only, not certainty.
- v4 supports the existing Gregorian/.NET/Access date range. BCE and additional calendars remain deferred; original text can preserve chronology the current calendar cannot normalize.
- Temporal aging follows `temporal_aging.md`: opt-in profiles, half-open effects, separate biological and experienced rates, calendar/legal/biological/experienced ages, overlap rejection, bounds for uncertain data, per-call hypothetical `at`, and no machine-clock fallback.

## 21. The read-only viewer follows writes through an opaque change cursor

Status: Implemented and visually accepted in Phase 8 on 2026-09-29.

- Every page-shaped read returns an opaque observed revision. `changes_since({ cursor, waitSeconds? })` is a bounded read that returns a new opaque cursor, changed semantic references where available, and invalidation flags without returning story content, internal sequence values, database keys, operation GUIDs, or paths.
- Results include canon changes from the selected continuity **and every changed vault-global source or tag**. Global records are visible across continuities, so filtering them out could leave an entity page stale.
- The viewer maintains a dedicated read-only watcher connection and issues bounded waits. The backend completes a wait immediately after a relevant database transaction and its change-log rows commit, or returns a heartbeat at timeout.
- Watching begins at the revision returned with the page read. A commit between reading and establishing the wait is therefore returned immediately; there is no unobserved subscription gap.
- The viewer marks affected visible sections as updating, requeries them, and advances its applied revision only after replacement data is rendered. Unknown impact, cursor expiry, backend restart, or browser reconnection causes one full visible-page refresh.
- The viewer displays **Live** only when its watcher is connected and the applied revision is caught up. Watcher failure or excessive delay displays a persistent **Updates paused - displayed data may be stale** state, last successful refresh time, and retry status. Focus/reconnect must catch up before Live returns.
- The freshness guarantee applies to committed changes through the elected backend. Direct Access writes remain unsupported and are outside the change log; Access is a viewer only while this system operates.

## 22. v4 scope and human review gates are explicit

Status: Accepted for v4 on 2026-09-28; the Phase 8 visual gate was accepted on 2026-09-29.

- Deferred work includes web editing, multiple human accounts, LAN/public hosting, automatic network fetching, audio/video, GIS, manuscript editing, general undo/event sourcing, jurisdiction-specific legal-age rules, and replacing Access solely for the viewer.
- The earlier v4 scope deferred browsing and versioning image masters. The user's 2026-09-30 Phase 12 image revision and full-size image-page requests supersede that deferral. General-purpose raster editing remains deferred.
- Image content reads and revision-history reads hold the backend's consistent-read gate across their database and asset reads, so a concurrent replacement cannot pair an old revision label with new bytes. A replacement using the same transparent original can still create a new content revision when its background color changes the rendered display or thumbnail; a byte-for-byte identical original and renditions are rejected as redundant.
- Phase 8 cannot close until the user visually reviews and accepts the running navigation shell and visual system.
- Phase 9 and Phase 10 each require a zero-finding technical hostile review, but their separate visual acceptance checkpoints are deferred at the user's request on 2026-09-29.
- Phase 11 presents the complete viewer for one end-of-development visual review: representative record pages, Markdown, images, relationships, sources, deleted records, and global/focused timelines in dense, sparse, fuzzy, undated, and unset-clock states. Incorporate refinements and repeat until accepted before a Release candidate is prepared.
- This consolidated product-design gate complements automated accessibility and visual-regression checks. The implementation must be concrete and reviewable before acceptance is requested.
- The v4 tool disposition is recorded in `docs/V4_TOOL_DISPOSITION.md`, representative fixtures in `docs/V4_TEST_FIXTURES.md`, and the Phase 0 hostile review in `reviews/V4_PHASE_0_HOSTILE_REVIEW.md`.

## 23. Windows startup uses supervised current-user tasks

Status: Implemented in Phase 8 on 2026-09-29; installation against Release remains deferred until approved cutover.

- Automatic startup means current-user logon startup, implemented as separate hidden Windows Scheduled Tasks for the read-only web viewer and ChatGPT tunnel. A machine-account Windows service is outside v4 because it would conflict with the current-user named pipe and DPAPI credential boundary.
- The viewer uses the stable bookmarkable default `http://127.0.0.1:5284`. Startup does not open a browser automatically. The configured address is literal: if the port is occupied, the web task fails with a clear status/log message and never chooses another port. Changing the bookmark address requires an explicit configuration change.
- The tunnel background mode requires a previously initialized profile and a decryptable API key stored with Windows DPAPI for the current user. It never prompts, opens the tunnel administration UI, or stores the plaintext key in task arguments, task XML, project files, or persistent machine/user environment variables.
- The existing per-profile mutex and process check remain mandatory. Task retries cannot create two `writing-vault` tunnel instances.
- Both background clients tolerate either startup order, retry transient failures, and converge on the one elected backend. They are supervised independently: a web crash restarts only the web task and a tunnel crash restarts only the tunnel task. In addition to logon and restart-on-failure settings, each definition has a one-minute repeating watchdog trigger because Windows does not reliably apply restart-on-failure to a manually started long-running task. `MultipleInstances IgnoreNew` makes the watchdog inert while the task is healthy. Neither recovery path bounces the healthy companion. The persistent viewer connection normally keeps the backend alive while the user is logged on.
- Bounded redacted logs live under `%LOCALAPPDATA%\WritingVaultMCP\Logs`. They contain lifecycle, health, and correlation data but no story text, image bytes, API key, connection string, or full database path.
- `Configure-WritingVault-Startup.bat` exposes `install`, `status`, `start`, `stop`, `restart`, and `remove`. It manages only the two Writing Vault tasks and verifies their resolved absolute paths stay within the configured installation before changing them.
- Installation is transactional from the user's perspective: validate Release artifacts, production database, fixed web port, tunnel profile, and decryptable credential first; if registration fails, remove any task created during that attempt and leave the previous configuration intact.
- `stop` and `restart` are explicit maintenance commands that coordinate both tasks. Ordinary task failure and recovery never coordinate them. Maintenance stop waits for adapters to disconnect and the backend to drain so schema migration, verified backup/restore, or other closed-database administration is not raced by automatic restart.
- Startup never migrates or repairs a schema. The viewer may remain available with a safe unavailable/status page, while the tunnel exits with an actionable code and scheduled retry, until an explicit administrative operation resolves the condition.

Phase 12 amendment (accepted 2026-09-30): Add a separate current-user notification-area controller, `WritingVault.Tray`, using the Writer's Vault tan **W** icon. It controls the existing independently supervised viewer and tunnel tasks; it is not a third database client or the owner of either background process. Its menu opens the fixed viewer URL, reports task and endpoint status, and starts, stops, or restarts both tasks together or each separately. The icon itself has legible, non-color-only badges for both healthy, partly running or degraded, both stopped, and starting or recovering; its tooltip names the viewer and tunnel states. Unknown health never appears healthy. It checks the viewer's fixed loopback endpoint and the tunnel client's actual readiness, using the installed client's `health --json --url-file ... --pid-file ... --require-control-plane-poll` command and bounded waits rather than assuming a running process is connected. Stop All waits for the backend to drain before reporting success. An intentional Exit Tray leaves viewer and tunnel running and suppresses tray-only watchdog relaunch until the next explicit start or logon. The tray is developed and tested in Debug, then built and registered as a third independently recoverable logon task only during Phase 12 Release cutover.

## 24. v4 storage is additive, verified, and component-aware

Status: Implemented in Phase 2 on 2026-09-28; production remains on the separately running Release build until cutover approval.

- Migration `20260928_004_v4_foundations` adds continuity notes and provenance, entity-event/project links, character temporal profiles and effects, entity-image metadata, and two managed rendition components. Existing v3 tables are not rebuilt.
- The Debug-only additive migration `20260929_005_relationship_membership` adds group participants, per-character membership periods, relationship-owned events, and their project links. Migration `20260930_006_relationship_merge` adds reviewed, one-way legacy relationship redirects while keeping the 005 checksum immutable. Legacy pairs backfill into two participant records without merging their identities. The explicit preview/apply API archives a reviewed source pair, moves its period and event references to a surviving relationship, and retains the source's Markdown, claim links, and audit. Old relationship refs open the survivor in active reads and the archived source with `includeDeleted`; no production cutover has run.
- In the current Debug schema, `ImageRenditions` are owned binary components of an `EntityImage`, so they use the composite image/kind key and do not have independent versions, deletion state, references, or history. The parent image supplies those lifecycle properties. Every active image must have exactly one `Thumbnail` and one `Display` rendition. Phase 12 will add immutable content revisions and migrate these current renditions without changing public image references.
- Database constraints enforce positive dimensions and bytes, the 20 MiB original ceiling, and the separate thumbnail/display dimension and byte ceilings. Integrity verification also checks rendition byte counts, safe relative master paths, SHA-256 shape, primary-image uniqueness, and rendition completeness.
- World-event project membership continues to use `ProjectEntities`. Entity-specific events use `EntityEventProjects`. Both have one physical row per event/project pair; removing an association soft-deletes that row and adding it again restores and versions the same row.
- Forward migration tolerates interruption between Access DDL statements by recognizing already-created exact objects. Unexpected definitions, populated destructive rebuilds, and unaddressed verifier differences stop rather than guess.
- A pre-migration backup records the latest migration actually present in the copied database. Regular v4 backups quiesce backend writes, copy every referenced immutable image master into a per-backup asset snapshot, verify database and asset hashes plus schema and integrity, deduplicate asset-manifest entries, and expose only path-free verification metadata. Retention removes the database, manifest, and asset snapshot together. A durable receipt keeps mutation-token identity after a backup ages out, so reusing an expired token cannot create a second backup.

## 25. v4 application commands resolve names safely and preserve explicit edits

Status: Implemented as the common Phase 3 application foundation on 2026-09-28; Debug MCP publication passed Phase 7 technical review. Production publication remains a Phase 12 gate.

- Natural-name resolution is exact after trimming, Unicode Form KC normalization, whitespace collapsing, and invariant case folding. It is restricted to the selected continuity except for explicitly vault-global kinds. Preferred and given character names plus active aliases participate.
- More than one exact match returns at most ten labelled semantic-reference candidates and performs no write. Deleted records and aliases owned by deleted records do not resolve through normal active lookup.
- Resolution occurs before queueing for useful ambiguity feedback, and every resolved storage key, record kind, deletion state, and continuity boundary is checked again inside the write transaction before commit.
- All sparse update contracts use a required non-empty `changes` object with a field allowlist. Omission preserves a value; explicit JSON `null` clears only fields whose schema permits null. Dedicated set/clear operations require their nullable target field.
- Compact and rich story dates normalize into the existing timezone-free `StoryDate` model. The parser accepts only supported ISO-shaped values, rejects irrelevant fields and timezones, and does not accept a precision or certainty field that storage cannot preserve.
- One transactional service dispatches project association to the correct physical table for world and entity-specific events. One shared note service targets either a continuity or canon entity. Semantic wrappers reuse the established reciprocal relationship and multi-owner invariants rather than duplicating them.
- Public mutation mapping converts storage outcomes into semantic references, normalizes stable v4 error codes, retains field/version/ambiguity recovery detail, and drops numeric keys, operation GUIDs, SQL, and paths.

## 26. v4 reads, temporal aging, images, and publication use one frozen public contract

Status: Implemented in Phases 4 through 7 on 2026-09-28; production cutover is not approved.

- Page-shaped reads expose semantic references and opaque revisions. Record sections are independently bounded and pageable; source reverse links are filtered to the selected continuity even though sources and tags are vault-global.
- Timeline aggregation occurs before page limiting so one visual bucket cannot be split or undercounted. Historical and undated records remain available with an unset clock; only the current-time marker and time-dependent calculations report the unset state.
- Character pages expose the temporal profile, including its optimistic-concurrency version, and all four age meanings. Temporal-effect pages expose both the affected character and an optional causal world event.
- Managed image masters use paths relative to the configured `assets` root. Database rows never store an extra `assets` prefix, so ingestion, verified backup, and restore resolve the same safe location.
- `vault_health` reports backup recency only for a backup that currently passes manifest, database, schema, integrity, and companion-asset verification.
- The Debug v4 surface is selected explicitly with `--tool-surface v4`; v3 remains the process default until cutover. Its generated schemas include group, membership-period, independent join/leave transition, relationship-event, and explicit legacy-merge operations. The full surface requires renewed protocol and hostile review before production release.
- An unselected session can be inspected safely. Natural-name ambiguity uses `record.ambiguous` with bounded candidates and direct retry guidance.
- The tunnel's background mode is non-interactive, keeps its profile mutex for the process lifetime, requires an existing profile and current-user DPAPI credential, and exits with documented task-friendly codes.
- Release output and the production database remain untouched during Debug validation.

## 27. Record pages use stable revisions and verified companion reads

Status: Debug implementation passed the Phase 9 technical hostile review on 2026-09-29; the user accepted the consolidated Phase 11 viewer presentation on 2026-09-30.

- The `observedRevision` for an unchanged database position is a deterministic, authenticated cursor within a UTC issuance window. This lets independent page, history, and image reads compare revisions without treating every randomized cursor as a write. Ordinary paging cursors remain opaque and randomized. A window rollover may prompt a harmless refresh; a backend restart invalidates old cursors and requires a full visible refresh.
- Record pages expose every stored non-identifier field through the shared typed read model, plus independently paged reverse sections. Project/event association role and notes belong to the association row and appear from both project and event reads. A directed character relationship shows its inverse label from the other character's page but retains one canonical semantic reference.
- Source snapshots stay vault-global while a selected continuity is required to read them. The text reader accepts only the content-addressed cache path expected from the stored SHA-256, verifies size, hash, and strict UTF-8, and pages text without returning a local path. Deleted snapshot text requires an explicit `includeDeleted` read. The web viewer fetches image renditions only through its authenticated read-only API and never loads remote image assets.
- The viewer renders Markdown with DOM construction, treats stored HTML as text, and accepts only HTTP(S) external links plus explicit safe Vault record links and image embeds. It searches full note, claim, and event content when requested while keeping result previews bounded. No browser route gains a write path.

## 28. Timeline graph and table share filters but load independently

Status: Debug Phase 10 implementation passed its zero-finding technical hostile review on 2026-09-29; the user accepted the consolidated Phase 11 viewer presentation on 2026-09-30.

- A revision-bound snapshot supplies one canonical timeline row per event. Graph queries aggregate by lane and date slice with bounded reference samples; detail queries retain a sorted, cached result and page by an opaque cursor. Graph pan and zoom change only the graph viewport. An explicit date selection filters the table until cleared.
- `kinds` selects timeline record kinds; `entityEventKinds` selects Character, Location, Organization, or Object owners within the entity-event kind. Focus uses semantic references and includes directly linked entities and every event/project association. The UI exposes these separately so a user can choose a type or one individual entity without database IDs.
- The graph remains a story-date axis. Narrative mode changes the table order by stored narrative order; it does not reinterpret dates as sequence positions. Unknown-date records have their own `undatedOnly` query and cursor, so they remain discoverable even when many dated events exist. An unset artificial clock never suppresses historical items.
- The browser loads the entire selected dated chronology and undated list through bounded 500-item API batches, then renders them without page controls. It restarts on revision mismatch rather than combining rows from different database revisions. The viewer remains a read-only client of the shared backend.
- Clicking an entity highlights its matching table entries and requests graph cluster membership independently of the cluster's bounded sample. `highlightRef` does not filter the chronology; `containsHighlight` marks a cluster if any member belongs to the selected entity.

## 29. Build-specific tray controllers and background services

Status: Debug implementation and fixed-port smoke test passed on 2026-09-30; Release build, live scheduled-task exercise, and Phase 12 cutover remain open.

- The Debug tray controls only Debug tasks and processes; the Release tray controls only Release. They use compile-time build identities and different single-instance mutexes. A task action or status request always carries that identity, checks an owned root-level scheduled task, and never selects a task by a name shared across builds.
- Release keeps the fixed bookmarked viewer at `http://127.0.0.1:5284`; Debug uses `http://127.0.0.1:5285`. The web executable itself enforces its build-specific port and sends a build-identity response header. The tray requires that header and a matching managed process before showing the viewer as healthy.
- Debug uses the usability database and the configured OneDrive backup root's `Debug` subfolder. Release uses the configured production database and backup root. Each build has separate tunnel profiles and IDs, DPAPI-encrypted credential files, PID/health files, and logs. A Debug tunnel profile or database that targets Release is rejected. A diagnostic Debug image probe also uses only the Debug tunnel profile and credential.
- The tray uses the Writer's Vault icon with live healthy, partial, stopped, and recovering badges. Its menu controls the viewer and tunnel independently or together; **Exit Tray** leaves those services running and disables only its own restart watchdog. Actual Release task installation and cross-build live restart drills remain Phase 12 gates.

## 30. Membership transitions carry their own text; browser story time preserves date precision

Status: Implemented in the Debug v4 candidate on 2026-09-30; Release remains untouched.

- Relationship and organization membership join/leave dates are separate story dates. Each named transition may have a description; if absent, the timeline uses a human sentence naming the character and, for organizations, the organization. Clearing a description restores that fallback. Period notes remain independent editorial notes.
- The new schema migration adds relationship transition descriptions and organization transition rows without rewriting the existing 007 migration. Exact legacy organization dates are backfilled; a fuzzy legacy period remains a period until a writer supplies independent join/leave dates.
- A browser story-time override can be a whole local date or a specific local clock time. The viewer uses separate date and keyboard-friendly `HH:MM` inputs. The v4 session reports `DateOnly` without inventing an instant. Character ages cover the local day; a point-in-time current state is not asserted for an unspecified hour. The timeline marks the day as a span.

## 31. Pinned record pages are captured automatically

Status: Debug implementation under Phase 12 review on 2026-10-01; production migration and Release cutover remain open.

- The user chose automatic immutable page snapshots after every record-related change. A preexisting record receives one clearly labeled current-state baseline during the v4 upgrade; incomplete audit deltas are never presented as earlier page versions.
- A saved page contains the full bounded overview, its visible associations, and its Markdown note bodies. The snapshot is written in the mutation's Access transaction, so a failed capture rolls back the record change. Each page has a SHA-256 and one-megabyte cap; integrity verification checks the saved content and version sequence.
- A note may link to a current `vault-record:` reference or pin one of the versions returned by `record_snapshot_list` using `?v=`. `record_snapshot_get` reads that version. Pinned links can resolve a record deleted after the snapshot; an ordinary current link still treats a deleted target as unavailable. Explicit cross-continuity navigation affects only the viewer session.
- Page-visible dependencies, including a continuity clock's effect on character age, are captured with the changed record. Image bytes remain in the separate immutable image-content revision store. Notes can embed any image, across owners and continuities, with a current or pinned content revision and a bounded display width.
