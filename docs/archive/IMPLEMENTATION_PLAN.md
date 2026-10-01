# Writing Vault MCP â€” Completion Plan

Last reviewed: 2026-09-27  
Plan status: Historical v3 completion record; the active v4 ledger is `V4_API_PLAN.md`  
Target: A recoverable, integrity-preserving MCP server for a Microsoft Access writing vault

## How to use this plan

- `[x]` means the item has current evidence behind it.
- `[ ]` means the item is incomplete, even if scaffolding exists.
- A phase is complete only when every task and every exit criterion in that phase is checked.
- Work should proceed in phase order unless a later item is explicitly marked as independent.
- Every schema change must include its migration, verifier, rollback/recovery procedure, and tests in the same change.
- The live database is canonical data. Destructive migrations require a verified backup and restore test first.

## Completion definition

The system is complete only when all of the following are true:

- [x] The MCP host starts from configuration, validates the database, and exposes documented tools over the selected transport.
- [x] Every declared entity and relationship has a working Access implementation.
- [x] Database constraints and application services jointly prevent orphan references, invalid hierarchy cycles, overlapping exclusive time periods, and duplicate canonical values.
- [x] All updates and deletes use mandatory concurrency protection.
- [x] Retried MCP requests are idempotent and cannot silently duplicate writes.
- [x] Artificial story time is an explicit, persistent, continuity-aware zoned instant and is used by every operation whose meaning depends on â€œnow.â€
- [x] Partial and uncertain fictional dates can be stored without inventing precision.
- [x] Sources can be linked to the facts and entities they support.
- [x] Every mutation is attributable and reversible through history, restore, or a tested backup.
- [x] Concurrent MCP calls cannot produce lost updates or invalid temporal records.
- [x] Startup detects schema drift before serving tools.
- [x] Automated unit, integration, migration, concurrency, protocol, and recovery tests pass.
- [x] A clean machine can be configured and operated from the documentation.

## Current evidence

- [x] Provider-free domain entities and relationship records exist.
- [x] Typed search, validation, patch, result, and transaction contracts exist.
- [x] Artificial current-time persistence and character age calculation exist internally.
- [x] A complete character Access repository exists.
- [x] Parameterized OleDb command construction exists.
- [x] Transaction commit and rollback infrastructure exists.
- [x] The project builds with zero warnings and errors.
- [x] The live Access schema, indexes, nullability, and relationships were inspected.
- [x] Rollback-only hostile probes verified that the database currently accepts orphan tag links, duplicate tag names, null audit values, overlapping open ownership, and self-parenting locations.
- [x] The hostile probes were rolled back; all touched tables remained empty and the lock file was released.
- [x] The current program is an MCP server.
- [x] The current database schema matches the declared object model.
- [x] Repositories other than characters are implemented.
- [x] Automated tests exist.

## Phase dashboard

- [x] Baseline hostile review and live-database probes complete.
- [x] Phase 0 â€” Target design and recovery baseline.
- [x] Phase 1 â€” Governed schema creation and migration.
- [x] Phase 2 â€” Relational and field integrity.
- [x] Phase 3 â€” Complete story and continuity model.
- [x] Phase 4 â€” Safe application write model.
- [x] Phase 5 â€” History, deletion safety, and recovery.
- [x] Phase 6 â€” Complete Access infrastructure.
- [x] Phase 7 â€” MCP host and tool surface.
- [x] Phase 8 â€” Verification suite.
- [x] Phase 9 â€” Operational hardening and documentation.
- [x] Phase 10 â€” Acceptance, cutover, and release.

Critical path: `0 â†’ 1 â†’ 2 â†’ 3 â†’ 4 â†’ 5 â†’ 6 â†’ 7 â†’ 8 â†’ 9 â†’ 10`. Documentation and test scaffolding may begin earlier, but no phase is considered closed out of order unless all upstream exit criteria are already satisfied.

---

## Phase 0 â€” Freeze the target design and establish recovery

Goal: Make the intended model explicit before rebuilding the empty database.

### Safety baseline

- [x] Copy `WritingVault.accdb` to a timestamped backup outside the working file path and store it under the configured OneDrive backup root.
- [x] Record the source file size and SHA-256 hash.
- [x] Export a machine-readable snapshot of tables, columns, indexes, relationships, required flags, and validation rules.
- [x] Confirm all user-data tables are empty; separately record any `VaultSettings` values.
- [x] Prove that the backup can be opened and queried.
- [x] Create a disposable integration-test database; tests must never run against the live file.
- [x] Document the supported ACE provider architecture and process architecture, including x64/x86 requirements.

### Architecture decisions

Accepted decisions are recorded in `DESIGN_DECISIONS.md`.

- [x] Define the ownership boundary for the Access file â€” one elected backend per database owns writes; direct concurrent Access writes are unsupported.
- [x] Define continuity scope â€” canon entities, projects, claims, and artificial time are continuity-scoped; tags and sources remain vault-global.
- [x] Define cross-project reuse â€” entities may be shared by projects inside one continuity; alternate versions are separate records linked by an optional variant-group identity.
- [x] Define fictional date representation â€” structured fuzzy values expose precision, normalized bounds, and original text while remaining timezone-free.
- [x] Define the artificial clock â€” store a continuity-scoped instant with a named timezone and derive entity-local time from location context.
- [x] Define leap-day age policy â€” February 29 advances on February 28 in non-leap years.
- [x] Define object ownership principals â€” use extensible character, organization, and external principals; ownership periods support one or more co-owners plus explicit unknown and unowned states.
- [x] Define relationship direction â€” relationship types declare direction and inverse labels; undirected pairs use canonical ordering.
- [x] Define source granularity â€” retain revisit URLs, optional content-addressed snapshots, and locally stored continuity-scoped claims.
- [x] Define deletion policy â€” soft delete by default; deleted records remain discoverable and restorable; purge is guarded.
- [x] Define history scope â€” use a compact audit/change log, soft-delete restore, and backups without event sourcing or general-purpose undo.
- [x] Define world versus entity events â€” keep them distinct, allow impact links, and permit entity events to reference world events.
- [x] Define API compatibility policy â€” tool schemas and error codes are versioned.
- [x] Define database support boundary â€” single user, single local writer, and no supported multi-host shared-file writes.

### Phase 0 exit criteria

- [x] Every architecture decision above is recorded and approved.
- [x] The live file has a verified restorable backup.
- [x] A disposable database path is available for development and tests.
- [x] The intended v2 entity and relationship diagram is reviewed before DDL is written.
- [x] **Phase 0 complete.**

---

## Phase 1 â€” Introduce governed schema creation and migration

Goal: Replace manual/lazy schema drift with a repeatable, verifiable database lifecycle.

### Migration infrastructure

- [x] Add a `SchemaMigrations` table containing migration ID, checksum, applied UTC time, application version, and status.
- [x] Store ordered migrations in the repository.
- [x] Make each migration resumable or fail safely with a clear recovery instruction.
- [x] Verify migration checksums so an applied migration cannot be silently edited.
- [x] Add a database initializer for a new empty `.accdb` file.
- [x] Add a startup schema verifier that compares required tables, columns, types, nullability, indexes, and relationships.
- [x] Refuse to expose write tools when the schema is older, newer, partially migrated, or unexpectedly different.
- [x] Expose schema status through a read-only health tool without exposing the database path.
- [x] Remove schema creation from read paths; `AccessVaultTimeline.GetAsync` must not execute DDL.
- [x] Add a dedicated administrative migration command that is never invoked implicitly by a normal MCP request.

### Empty-database rebuild

- [x] Generate the complete v2 schema from one reviewed source of truth.
- [x] Rebuild the currently empty live database rather than preserving known structural mistakes.
- [x] Add the missing `ObjectNotes` table.
- [x] Remove the obsolete `WorldEvents.Location` free-text column.
- [x] Create `VaultSettings` or its continuity-scoped replacement through the migration system.
- [x] Retain the original file as a rollback artifact until acceptance is complete.

### Phase 1 exit criteria

- [x] A blank database migrates from version zero to the current version in one command.
- [x] Re-running migrations makes no changes and reports success.
- [x] An intentionally altered test database is rejected by startup with a structured schema-drift report.
- [x] An interrupted migration follows the documented recovery path without losing user data.
- [x] The live rebuilt database passes the schema verifier.
- [x] **Phase 1 complete.**

---

## Phase 2 â€” Enforce relational and field integrity

Goal: Make malformed persisted states difficult or impossible, including when code paths race.

### Foreign keys and delete rules

- [x] Add both foreign keys to unified `EntityTags` (the accepted replacement for per-entity tag tables).
- [x] Add both foreign keys to `SourceTags`.
- [x] Verify every other declared relationship has an enforced Access relationship.
- [x] Keep destructive cascade delete disabled unless an individually reviewed relationship requires it.
- [x] Index every foreign-key column whose leading position is not already covered by a primary or unique index.
- [x] Add integrity probes proving orphan inserts are rejected and exact metadata checks covering every declared foreign key.

### Required values and canonical uniqueness

- [x] Make all entity `CreatedAt` and `UpdatedAt` columns required.
- [x] Make all note/event audit columns required.
- [x] Add required/default rules only where the database can supply a semantically correct value.
- [x] Normalize canonical tag names in one application function.
- [x] Persist a normalized tag key and enforce case-insensitive, whitespace-stable uniqueness.
- [x] Add the unique tag index; retain friendly duplicate errors in the application.
- [x] Decide and enforce uniqueness rules for aliases within a character and organization.
- [x] Decide which entity names may duplicate and document that duplicates are intentional where allowed.
- [x] Reject zero-length required text at both the application and database boundary where Access supports it.

### Temporal and hierarchy integrity

- [x] Enforce ordered, kind-correct bounds for every temporal interval.
- [x] Centralize location inserts and updates so hierarchy validation cannot be bypassed.
- [x] Reject direct self-parenting.
- [x] Reject multi-level location cycles inside the same transaction as the update.
- [x] Centralize ownership writes so overlapping exclusive periods cannot be inserted directly.
- [x] Forbid overlapping memberships with the same character, organization, and normalized role; allow different simultaneous roles.
- [x] Add post-write invariant verification before transaction commit for constraints Access cannot express.
- [x] Add startup diagnostics that identify pre-existing hierarchy or interval corruption.

### Phase 2 exit criteria

- [x] The previous hostile inserts are all rejected.
- [x] Audit-column null insertion is rejected by Access.
- [x] Duplicate normalized tag names are rejected under concurrent requests.
- [x] Declared relationships survive creation, exact metadata reads, and non-cascading delete-rule verification.
- [x] Fresh and migrated verification databases contain no orphan, cyclic, or invalid-interval rows.
- [x] **Phase 2 complete.**

---

## Phase 3 â€” Complete the story and continuity model

Goal: Represent the facts writers need without fake records, free-text identity forks, or forced date precision.

### Continuities and projects

- [x] Add the approved continuity entity.
- [x] Associate projects with a continuity.
- [x] Scope persisted artificial current time to continuity and store its named reference timezone.
- [x] Add location timezones with ancestor inheritance and explicit continuity-default fallback.
- [x] Resolve entity-local current time from residence, location, custody, or headquarters context as applicable.
- [x] Return the timezone source with every derived local-current-time result; never silently use the machine timezone.
- [x] Prevent a single operation from linking records across incompatible continuities.
- [x] Add project-to-organization assignments.
- [x] Add project-to-object assignments.
- [x] Add project-specific role and notes to assignments.
- [x] Keep series, novels, stories, and editions as deliberately flat named project containers until a concrete hierarchy is required.

### Locations and residence

- [x] Replace character `PlaceOfBirth` text with an optional `BirthLocationId` relationship plus optional free-text clarification.
- [x] Replace `CurrentResidence` text with residence-history intervals linked to locations.
- [x] Resolve current residence using the continuity's artificial current time.
- [x] Support unknown or approximate residence periods.
- [x] Preserve lightweight real-world place wording in relationship detail or notes instead of manufacturing a location identity.

### Events

- [x] Support multiple locations per world event, including at most one active primary location.
- [x] Add world-event participants for organizations.
- [x] Add world-event participants for objects.
- [x] Link sources directly to world events.
- [x] Define entity-specific events as local facts that may optionally reference a world event.
- [x] Add an optional link from character, location, organization, and object events to a world event.
- [x] Add ordering fields where narrative order differs from chronological order.

### Relationships and organizations

- [x] Add character-to-character relationships with type, direction, time interval, notes, and continuity scope.
- [x] Establish canonical storage and duplicate rejection for undirected relationships so Aâ†”B cannot be duplicated as Bâ†”A.
- [x] Add organization notes.
- [x] Add organization events.
- [x] Add organization tags.
- [x] Defer organization hierarchy until a concrete workflow requires it; document the flat first-release scope.
- [x] Add organization aliases if names can change over time.
- [x] Prevent invalid or overlapping membership histories according to the Phase 0 decision.

### Objects and ownership

- [x] Implement the approved owner-principal representation.
- [x] Represent ownership as non-overlapping object ownership periods containing one or more principal links.
- [x] Optimize the common one-owner case without giving it different semantics from co-ownership.
- [x] Support optional shares; require either no shares or shares for every co-owner totaling 100 percent.
- [x] Define a versioned command that atomically replaces the complete ownership group for add/remove co-owner workflows.
- [x] Represent unknown ownership explicitly.
- [x] Support organization ownership.
- [x] Distinguish ownership, custody/possession, and physical location histories.
- [x] Link objects to world events and projects.
- [x] Ensure an object can be unowned during a time gap without manufacturing an owner.

### Sources and provenance

- [x] Link sources to characters.
- [x] Link sources to locations.
- [x] Link sources to organizations.
- [x] Link sources to objects.
- [x] Link sources to world events.
- [x] Link sources to notes or claims where sentence-level provenance matters.
- [x] Store citation locator, evidence excerpt/summary, evidence relation, and claim confidence.
- [x] Preserve foreign-key integrity through shared canon identity and explicit junctions; do not use an unenforceable polymorphic key.

### Partial and uncertain dates

- [x] Add a provider-free `StoryDate` interval value model.
- [x] Support exact timestamp, exact date, month, year, circa, before, after, range, and unknown.
- [x] Define interval comparison, containment, overlap, and deterministic sorting rules.
- [x] Report age as exact, bounded, future/deceased-aware, or unknown according to available precision.
- [x] Store birth, death, event, relationship, membership, ownership, custody, location, and residence times in the approved representation.
- [x] Preserve original display text and calendar identity when wording cannot be fully normalized.

### Phase 3 exit criteria

- [x] Every first-class entity can participate in projects, events, tags, notes, sources, and claims according to the approved matrix.
- [x] The â€œUnknown ownerâ€ example is representable without a fake character.
- [x] Character birthplace and residence use location identity without losing useful prose.
- [x] A year-only date can be stored and round-tripped without inventing a month or day.
- [x] Alternate continuities cannot accidentally share mutable canon.
- [x] **Phase 3 complete.**

---

## Phase 4 â€” Make the application write model safe by construction

Goal: Ensure MCP handlers cannot accidentally bypass validation, concurrency, invariants, or history.

### Command boundary

- [x] Replace public raw mutation access with application commands for every create, update, relationship change, and delete.
- [x] Keep raw Access mutation interfaces internal to infrastructure/application composition.
- [x] Require all commands to return `VaultMutationResult` rather than leaking provider exceptions.
- [x] Validate all identifiers, text limits, supported date ranges, enum values, finite numbers, and references before writing.
- [x] Normalize identity text once at the command boundary; preserve intentional formatting in long-form content.
- [x] Add aggregate commands for event creation, ownership transfer, residence moves, membership changes, and continuity-aware core duplication.
- [x] Run reference checks and writes in the same transaction.
- [x] Translate unique, reference, not-found, conflict, invariant, and delete-blocked failures into stable safe errors.

### Optimistic concurrency

- [x] Add a monotonically increasing integer `Version` to every mutable entity and editable dependent record.
- [x] Require `ExpectedVersion` on every update, temporal/group edit, soft delete, restore, and purge; immutable composite-link removal is exact-key and idempotent.
- [x] Increment versions atomically in the update statement.
- [x] Return resource type/key and the current version on conflicts.
- [x] Remove optional unguarded update paths.
- [x] Protect artificial-current-time updates with a version and active-continuity check.
- [x] Ensure aggregate commands protect every prior row that affects the decision and run under the serialized write transaction.

### Idempotency and retries

- [x] Add a unique nonempty client operation GUID to every mutating MCP call and canonicalize equivalent GUID text forms.
- [x] Persist request ID, command type, input hash, result identity, and completion status transactionally.
- [x] Return the original result when a completed request is retried.
- [x] Detect and reject reuse of one request ID with different input.
- [x] Classify transient Access errors, including lock/share failures, separately from permanent storage failures.
- [x] Add bounded retry with jitter only around transactional, idempotent operations.
- [x] Serialize writes through a process-wide queue; the approved multi-client backend will retain one queue per database.

### Patches and validation

- [x] Expose immutable specified-field state from patch documents.
- [x] Add patch services that validate and conditionally update only specified fields in one transaction.
- [x] Require expected version on patches.
- [x] Ensure omitted, explicit-null, empty, and whitespace inputs have documented distinct behavior.
- [x] Do not expose the legacy ambiguous `OwnedAt` search filter; current ownership-at-time is evaluated by explicit object temporal-state queries.
- [x] Validate artificial/story dates and transition boundaries against the actual Access-supported range.

### Phase 4 exit criteria

- [x] No public MCP-facing service can directly call an invariant-bypassing repository mutation.
- [x] Two concurrent updates to the same record produce one success and one structured conflict.
- [x] Replaying a successful create request returns the same identity without a duplicate row.
- [x] Concurrent ownership transfers cannot create overlapping ownership periods.
- [x] Lock contention yields a safe retryable result or succeeds through bounded retry.
- [x] **Phase 4 complete.**

---

## Phase 5 â€” Add history, deletion safety, and recovery

Goal: Make autonomous edits attributable and recoverable.

### Mutation journal

- [x] Add an append-only `ChangeJournal` with operation ID, UTC time, actor/client identity, tool name, entity reference, action, version before/after, and safe change payload.
- [x] Record journal entries in the same transaction as the mutation.
- [x] Redact configured sensitive long-form fields from normal logs while retaining recoverable history in the vault.
- [x] Add read tools for entity history and operation history.
- [x] Add correlation IDs across MCP request, application command, journal, and structured logs.

### Delete workflow

- [x] Add soft-delete state and deletion metadata to appropriate entities.
- [x] Exclude deleted records from normal searches while allowing explicit inclusion.
- [x] Add a delete-preview operation listing every blocker and consequence.
- [x] Require expected version and operation ID for delete.
- [x] Add restore operations.
- [x] Restrict permanent purge to an administrative tool with an explicit preview token and verified recent backup.
- [x] Return `DeleteBlockedError` with concrete blocker counts rather than a generic OleDb storage failure.

### Backup and restore

- [x] Use the current user's OneDrive `Documents\WritingValutBackup` folder as the canonical backup root. (Historical local path sanitized for repository publication.)
- [x] Implement safe backup only after all connections are closed or through a proven consistent-copy procedure.
- [x] Use timestamped, hashed backups with configurable retention.
- [x] Never overwrite the sole previous backup.
- [x] Verify copied databases can be opened and pass schema/integrity checks.
- [x] Add a documented restore procedure.
- [x] Perform and record a full restore drill using a disposable location.
- [x] Add pre-migration and pre-purge backup requirements.

### Phase 5 exit criteria

- [x] Every mutation creates exactly one attributable journal entry.
- [x] A soft-deleted entity can be restored with relationships intact.
- [x] A failed transaction creates neither partial data nor a false completed journal entry.
- [x] A backup can be restored and served by the application.
- [x] A permanent purge cannot run without its required safety evidence.
- [x] **Phase 5 complete.**

---

## Phase 6 â€” Implement the complete Access infrastructure

Goal: Fulfill every application contract against Access with consistent behavior.

### Repository implementations

- [x] Use `AccessVaultService` as the single active Access implementation; exclude the superseded repository experiment from production compilation.
- [x] Complete character create/patch/read coverage for every core field, aliases, notes, events, relationships, residence history, versioning, and history.
- [x] Implement locations, hierarchy queries, notes, events, tags, residence history, and continuity checks.
- [x] Implement world events, supported participant types, locations, sources, tags, and project assignments.
- [x] Implement projects, continuity membership, and all accepted project assignments.
- [x] Implement organizations, memberships, locations, aliases, notes, events, tags, sources, and projects; hierarchy remains explicitly deferred by `DESIGN_DECISIONS.md` section 13.
- [x] Implement objects, notes, events, ownership/custody/location history, tags, projects, and world-event links.
- [x] Implement sources, citations/links, snapshots, local claims, tags, and reverse lookups; sources remain vault-global and are not project members by accepted design.
- [x] Implement tags and all accepted tag assignments.
- [x] Implement bidirectionally discoverable character relationships from one canonical stored row.
- [x] Implement the change journal and versioned soft-delete/restore lifecycle.
- [x] Implement transactionally coupled idempotency storage.
- [x] Implement continuity-scoped fictional dates, clocks, timelines, and derived temporal reads.

### Query behavior

- [x] Use ID keyset pagination for entity search; no active offset API remains.
- [x] Keep every active sort deterministic with the record ID as the final key.
- [x] Use the same trimmed, case-insensitive canonical normalization for display-name search and writes.
- [x] Omit misleading totals from keyset pages rather than performing a second inconsistent count.
- [x] Serialize aggregate reads against in-process writes through the shared coordinator.
- [x] Cap graph collections at 50, character relationships at 100, history at 200, and returned long-text fields at 65,536 characters with truncation flags.
- [x] Verify foreign-key leading indexes structurally and exercise representative 1,000-entity reads against documented latency ceilings.

### Infrastructure consistency

- [x] Centralize Access write-error classification and sanitize all read-tool exceptions at the MCP boundary.
- [x] Apply and document a 30-second timeout to every `AccessCommand`.
- [x] Ensure cancellation rolls back and releases transactions, connections, and lock files.
- [x] Restrict DDL to the schema migrator.
- [x] Read every generated identity on the same connection and transaction as its insert.
- [x] Keep fictional dates timezone-free, continuity clock instants UTC with a named timezone, and audit timestamps real UTC.
- [x] Ensure no error returned to an MCP client exposes SQL, connection strings, provider details, or filesystem paths.

### Phase 6 exit criteria

- [x] The unified active application contract has exactly one production Access implementation.
- [x] Contract and integration tests cover the unified service across every accepted aggregate and relation family.
- [x] Large-list tests meet the documented local latency and bounded-result limits.
- [x] Cancellation and exceptions leave no partial transaction or abandoned lock file.
- [x] No implementation uses caller-supplied SQL identifiers or expressions.
- [x] **Phase 6 complete.** Hostile review: `reviews/phase6-hostile-review.md`.

---

## Phase 7 â€” Build the MCP host and tool surface

Goal: Provide a coherent, bounded interface that an AI can use safely.

### Host composition

- [x] Pin ModelContextProtocol 2.2.0 and exercise its stdio client and stream-server transport in protocol tests.
- [x] Run each client-facing process as a stdio adapter to a current-user named-pipe backend.
- [x] Elect exactly one backend per normalized database path with a named mutex and share one connection factory, coordinator, service, and verifier set.
- [x] Validate platform, path, size, schema, and integrity before the backend accepts a client.
- [x] Enforce read-only tool registration independently for each adapter connection.
- [x] Count live connection tasks, drain their in-flight MCP work, and stop the backend after the last client plus a short grace period.

### Read tools

- [x] Add server/database health, schema-version, and tool-surface-version reporting.
- [x] Add get/search/details tools for every entity using semantic references.
- [x] Add relationship and reverse-relationship queries with semantic references in nested results.
- [x] Select continuity by name once per connection and expose persisted plus connection-local artificial time.
- [x] Make age, local-time, and temporal-state tools report or follow the exact connection clock source.
- [x] Add history and deleted-record inspection without record keys or operation GUIDs.
- [x] Use opaque semantic cursors and retain all Phase 6 expansion bounds.

### Write tools

- [x] Add create and versioned patch tools for every supported entity.
- [x] Add explicit relationship add/unlink and versioned soft-delete/restore tools.
- [x] Add aggregate tools for transfers, moves, memberships, residence, and world events.
- [x] Add connection-local set/clear time tools and persisted shared-clock set/clear with named timezone and explicit offset handling.
- [x] Add entity-local time resolution with location/timezone source and connection/shared clock source.
- [x] Add delete preview, soft delete, and restore tools.
- [x] Require readable vault-wide request tokens on every mutation and derive operation GUIDs internally.
- [x] Require expected versions on every edit or deletion.
- [x] Keep permanent purge and migration CLI-only.
- [x] Expose no arbitrary SQL, database path, connection string, numeric storage key, or internal GUID.

### Tool schema and response design

- [x] Give every tool a precise description of side effects, continuity scope, and temporal semantics.
- [x] Use stable semantic-reference, continuity-name, and request-token input schemas.
- [x] Return stable error codes and sanitize provider/storage details at the MCP boundary.
- [x] Return created/updated semantic references and versions.
- [x] Return conflict versions sufficient to reread and retry deliberately without leaking record keys.
- [x] Retain bounded strings, lists, results, and expansion depth.
- [x] Distinguish unknown, omitted, cleared, and empty values.
- [x] Version the semantic tool surface as 3.0 and document the breaking v2 compatibility boundary.

### Phase 7 exit criteria

- [x] MCP clients discover the documented semantic read/write surface through both stdio adapters and the shared stream server.
- [x] Separate clients can set different artificial times and obtain ages calculated from their exact connection-local values.
- [x] Every write tool resolves references and then enters the existing command, idempotency, concurrency, journal, and transaction boundary.
- [x] Read-only connections expose no database-mutating or administrative tools.
- [x] Protocol inputs, results, cursors, history, and errors contain no numeric Access keys, internal GUIDs, SQL, provider details, connection strings, or filesystem paths.
- [x] **Phase 7 complete.** Hostile review: `reviews/phase7-hostile-review.md`.

---

## Phase 8 â€” Build the verification suite

Goal: Prove behavior at domain, database, concurrency, migration, and MCP boundaries.

### Unit tests

- [x] Test required text, normalization, limits, IDs, enums, Access date range, and date ordering.
- [x] Test every partial-date precision and comparison rule.
- [x] Test age calculations before birth, on birthdays, after death, leap day, imprecise birth, invalid chronology, and unset timeline.
- [x] Test patch omitted/null/value behavior and immutable specified fields.
- [x] Test location-cycle detection.
- [x] Test overlap detection for ownership, membership, and residence.
- [x] Test error/result invariants and serialization.

### Access integration and contract tests

- [x] Create a fresh disposable `.accdb` for each isolated test collection or run.
- [x] Run the same CRUD/search/version/delete contract suite against every repository.
- [x] Test long text, Unicode, apostrophes, wildcard characters, nulls, and maximum-length values.
- [x] Test all foreign keys, unique indexes, required fields, and delete rules directly against Access.
- [x] Test complete aggregate reads and reverse relationships.
- [x] Test transaction rollback at every write step through fault injection.
- [x] Test provider errors map to the intended structured result.
- [x] Verify each test closes connections and releases `.laccdb` files.

### Concurrency and idempotency tests

- [x] Race two versioned updates and prove one conflicts.
- [x] Race duplicate tag creates and prove only one canonical tag exists.
- [x] Race ownership transfers and prove no overlap.
- [x] Replay every mutating tool with the same request token and identical input.
- [x] Reuse a request token with changed input and prove it is rejected.
- [x] Simulate lock contention and verify bounded retry behavior.
- [x] Verify aggregate reads do not return mixed snapshots under concurrent writes.

### Migration and recovery tests

- [x] Migrate a blank file.
- [x] Migrate a copy of the current pre-v2 file.
- [x] Detect missing tables, columns, indexes, and relationships.
- [x] Simulate interrupted migration recovery.
- [x] Verify pre-migration backup creation.
- [x] Restore a backup and rerun schema and integrity checks.

### MCP protocol tests

- [x] Test initialization, capability discovery, tool listing, and graceful shutdown.
- [x] Validate every toolâ€™s success and error response against its schema.
- [x] Test cancellation and client disconnect during reads and writes.
- [x] Test oversized and malformed input rejection.
- [x] Test read-only mode.
- [x] Test that errors and logs do not disclose the database path or SQL.

### Phase 8 exit criteria

- [x] All required suites pass from a clean checkout.
- [x] Tests never modify the live database.
- [x] The original hostile probes are permanent regression tests.
- [x] There are no skipped safety, migration, concurrency, or recovery tests.
- [x] **Phase 8 complete.** Hostile review: `reviews/phase8-hostile-review.md`.

---

## Phase 9 â€” Operational hardening and documentation

Goal: Make the local service diagnosable, maintainable, and safe to operate.

### Configuration and security

- [x] Validate and canonicalize the configured database path.
- [x] Refuse unexpected file extensions and directories according to policy.
- [x] Keep connection strings and filesystem paths out of client-facing errors.
- [x] Document filesystem permissions and backup-directory permissions.
- [x] Add explicit read-only, write-enabled, and administrative modes.
- [x] Bind only to the intended local transport unless remote access is intentionally configured and authenticated.
- [x] Document the trust boundary: direct edits in Access can bypass application-only invariants.

### Logging and health

- [x] Add structured logs for startup, schema verification, MCP calls, retries, conflicts, migration, backup, and shutdown.
- [x] Use operation IDs without logging full sensitive story content by default.
- [x] Add health signals for schema state, connectivity, write queue, last successful backup, and unresolved migration.
- [x] Add actionable diagnostics for ACE provider absence and architecture mismatch.
- [x] Define log retention and redaction policy.

### Performance and maintenance

- [x] Generate a representative-volume synthetic vault.
- [x] Establish latency targets for get, search, details, aggregate write, and backup.
- [x] Verify indexes against representative searches.
- [x] Document safe compact/repair procedures and require exclusive access.
- [x] Define maximum supported database size and warn before approaching it.
- [x] Define when archival or migration to a server database should be considered.

### Documentation

- [x] Write setup instructions for .NET, ACE, database path, and MCP client configuration.
- [x] Document all tools with examples and error behavior.
- [x] Document story-time, entity-local timezone resolution, continuity, date precision, and age semantics.
- [x] Document backup, restore, schema migration, lock recovery, and compact/repair procedures.
- [x] Document data-model relationships and deletion behavior.
- [x] Add a troubleshooting guide for locks, provider errors, schema drift, and corrupted files.
- [x] Add a release checklist and incident-recovery checklist.

### Phase 9 exit criteria

- [x] A clean machine can be configured from the documentation.
- [x] A lock/provider/schema failure produces an actionable diagnosis.
- [x] Backup and restore can be performed from documented commands.
- [x] Representative data meets the performance targets.
- [x] **Phase 9 complete.** Hostile review: `reviews/phase9-hostile-review.md`.

---

## Phase 10 â€” Acceptance, cutover, and release

Goal: Prove the finished system against realistic use and move the live vault safely.

### Acceptance scenarios

- [x] Create a continuity and two projects sharing appropriate canon.
- [x] Create characters with aliases, relationships, partial birth dates, residences, memberships, tags, notes, and sources.
- [x] Create nested locations and prove cycles are rejected.
- [x] Create a multi-location world event involving characters, organizations, and objects.
- [x] Create an object with unknown ownership, organization ownership, a gap, character co-ownership, and later single-character ownership.
- [x] Link sources to specific supported facts and recover them through reverse queries.
- [x] Set artificial time and verify ages, residence, ownership, and membership as-of results.
- [x] Concurrently edit one record and verify conflict handling.
- [x] Retry a timed-out create and verify idempotency.
- [x] Preview-delete, soft-delete, inspect history, and restore an entity.
- [x] Back up, intentionally damage a disposable copy, restore, and verify it.

### Cutover

- [x] Freeze live writes.
- [x] Create and verify the final pre-cutover backup.
- [x] Apply the approved migrations or replace the empty schema with the verified v2 database.
- [x] Run schema, integrity, and row-count verification.
- [x] Start the MCP server in read-only mode and run smoke tests.
- [x] Enable writes and run one reversible mutation scenario.
- [x] Confirm journal, version, idempotency, artificial-time, and backup state.
- [x] Retain the pre-cutover database for the approved rollback period.

### Release gate

- [x] Release build succeeds with zero warnings and errors.
- [x] All test suites pass.
- [x] No critical or high-severity review findings remain open.
- [x] The restore drill is current.
- [x] Tool documentation matches the actual exposed schemas.
- [x] The completion definition at the top of this document is fully checked.
- [x] **Phase 10 complete â€” Writing Vault MCP production-ready.** Hostile review: `reviews/phase10-hostile-review.md`.

---

## Finding-to-phase traceability

| Review finding | Primary phase |
|---|---:|
| Missing `ObjectNotes`, stale `WorldEvents.Location`, schema drift | 1 |
| Missing tag foreign keys and tag uniqueness | 2 |
| Nullable audit fields | 2 |
| Location cycles and overlapping ownership accepted by Access | 2, 4 |
| Projects/events/sources/organizations/objects lack important links | 3 |
| Free-text birthplace and current residence | 3 |
| Unknown or organization ownership cannot be represented | 3 |
| Exact-or-null dates force false precision | 3 |
| Global artificial time | 3, 4 |
| Optional or absent concurrency protection | 4 |
| Retry can duplicate writes | 4 |
| Hard deletes and no revision history | 5 |
| Only the character Access repository exists | 6 |
| Offset scanning and inconsistent multi-query reads | 6 |
| No MCP host or tools | 7 |
| No automated tests | 8 |
| Lock handling, backups, diagnostics, and operating procedures | 5, 9 |

## Progress update template

Use this block after each implementation session:

```text
Date:
Phase:
Completed checkboxes:
Evidence:
  - Build/test command:
  - Result:
  - Database verification:
Open risks:
Next unchecked item:
```

