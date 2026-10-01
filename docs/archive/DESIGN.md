# Internal data model

Historical design sketch. Several repository-contract files described below are excluded from the current build; see `DESIGN_DECISIONS.md` and `V4_API_PLAN.md` for the active architecture and open work.

The server is divided into three layers:

```text
MCP tools -> application services/repository contracts -> OleDb repositories -> Access
```

`Domain` contains provider-free records matching stored facts. Base entities do not carry
navigation collections, so listing characters does not implicitly load notes, tags, projects,
and membership history. `*Details` records are explicit read models for tools that need a full
view.

`Application` owns repository contracts and relationship operations. Junction-table changes
are explicit operations (`ITagAssignments`, `IProjectAssignments`) rather than mutations of
in-memory collections. This maps cleanly to composite keys and makes each MCP write narrow.
Dedicated `*Input` records omit generated IDs and audit columns, so callers never manufacture
database state. An update receives an ID and a complete replacement input; partial MCP inputs
should be merged and validated in an application service first.

`Infrastructure.Access` owns all OleDb behavior. In particular, `AccessCommand` preserves the
order of `?` parameters and maps null to `DBNull.Value`. Concrete repositories should use that
class and keep table/column SQL private.

## Story time

`IVaultTimeline` stores an artificial current time in the database's `VaultSettings` table.
`AccessVaultTimeline` creates that support table on first use. Story time is stored as a
timezone-free Access `Date/Time` value because it describes the fictional timeline rather than
the server's location.

Character ages must go through `CharacterAgeCalculator` or `CharacterTimelineService`. If story
time has not been set, the result is `TimelineUnset`; there is deliberately no fallback to the
machine clock. A character with a future birth date is `NotYetBorn`. A recorded death date is
used only when story time has reached it; before then, age is calculated at the artificial
current time. `CharacterTimelineService.GetDetailsAsync` returns `CharacterAtCurrentTime`, which
packages character details, the timeline used, and the calculated age for MCP tools.

Audit columns are separate operational data. `CreatedAt`, `UpdatedAt`, and the settings row's
`UpdatedAt` continue to use real UTC time and never change the fictional timeline.

## Validation

`InputValidation` checks required text, Access short-text limits, positive identifiers, and date
ordering before values reach OleDb. It returns a `ValidationResult` containing stable error codes,
field names, and messages suitable for MCP responses. Callers that prefer exception flow can use
`ThrowIfInvalid`, which raises `VaultValidationException` with the same structured errors.

`LocationHierarchyValidator` adds database-aware validation for parent references. It rejects a
missing parent, direct self-parenting, a proposed ancestor cycle, and a cycle already present in
the stored hierarchy. Concrete location write services must call it before inserts and updates.

`TagUniquenessValidator` checks tag names while allowing an existing tag to retain its own name.
`RelationshipUniquenessValidator` uses a fixed `RelationshipKind` enum, so infrastructure code
can map each supported junction to static SQL rather than accepting table names from callers.
These checks improve error messages; unique indexes and composite primary keys remain the final
authority if two requests race.

## Relationship editing

Dependent records are edited through owner-scoped contracts. For example, changing a character
note requires both `characterId` and `noteId`; an implementation must include both values in its
`WHERE` clause. Update and delete return `false` when the child is absent under that owner. This
prevents an ID obtained in one context from changing a record owned by another entity.

`ICharacterRelations`, `ILocationRelations`, `IObjectRelations`, and
`IOrganizationRelations` expose add, update, and delete operations for every dependent table.
Composite junctions use add/remove operations through `IProjectAssignments`, `ITagAssignments`,
and `IWorldEventRelations`, since a junction row has no independent identity to update. Junction
removals return `false` when the association did not exist.

## Searching and sorting

Each entity repository accepts a typed query and a `SortOrder<TField>` whose field is constrained
to that entity's sort enum. MCP inputs cannot supply SQL expressions, table names, column names,
or arbitrary `ORDER BY` text. Concrete repositories map these enums to static SQL fragments and
append `Id` as a deterministic final sort key before applying pagination.

Text filters mean case-insensitive contains matching. Access repositories use parameterized
`InStr` expressions, avoiding the provider's ANSI-89 versus ANSI-92 wildcard differences.

`LocationParentScope` distinguishes no parent filter, root locations, and direct children of one
parent. `UndatedEventHandling` explicitly controls whether events with no date accompany a date
range. An object owner filter searches ownership history; when `OwnedAt` is supplied, the owner
window must contain that time using inclusive starts and exclusive ends.

`SearchQueryValidation` rejects empty supplied text, text over 255 characters, invalid IDs,
invalid enum values, empty or reversed date ranges, and inconsistent location-parent filters.

## Partial updates

Patch models derive from `PatchDocument`. Their ordinary nullable properties remain friendly to
JSON schema generation, while each initializer records whether the caller actually supplied the
property. An omitted field remains unchanged; an explicit `null` clears a nullable field; and a
supplied value replaces it. `HasChanges` and `SpecifiedFields` are ignored by JSON serialization.

`PatchMerging` combines a patch with the current domain record and produces the same complete
`*Input` model used by full updates. `PatchValidation` rejects null and empty patches, then runs
the merged input through the existing validation rules. Clearing a required property therefore
produces the same structured validation error as an invalid full update.

## Application results and errors

Application services return `OperationResult` or `OperationResult<T>`. A result is either a
success value or one structured `ApplicationError`; it cannot contain both. Errors have stable
codes and JSON type discriminators for validation, not-found, duplicate, constraint violation,
blocked deletion, optimistic-concurrency conflict, and storage failure.

Validation failures retain every field-level `ValidationError`. Delete failures identify the
blocking resource types and counts. Storage failures expose a safe operation name and retryable
flag rather than an OleDb exception or database path. `UpdatePrecondition` carries the
`UpdatedAt` value observed by the caller; audited update implementations should include it in
their `WHERE` clause and return `ConcurrencyConflictError` when a newer version exists.

## Transactional operations

`IWritingVaultTransactionRunner` supplies repositories bound to one Access connection and
transaction. It commits only when the callback returns success. A structured failure, exception,
or cancellation rolls the transaction back; provider exceptions are translated to safe
application errors at this boundary.

`TransactionalVaultOperations` defines the first aggregate writes: creating a character with
aliases, tags, and projects; creating a world event with participants, tags, and projects;
transferring object ownership; and moving a location. All references are checked before the first
write. Commands reject repeated IDs before opening a transaction.

Ownership intervals use inclusive starts and exclusive ends. A transfer closes the active period
at the transfer time and opens the new period at that same instant. It rejects multiple active
owners, the same owner, and future periods that the new open-ended period would overlap.

Location moves run hierarchy validation inside the transaction and update against the location's
observed `UpdatedAt` value. A failed conditional update is distinguished as not-found or a
concurrency conflict by rereading the row before the transaction ends.

## Access character repository

`AccessCharacterRepository` is the first complete provider implementation. It supports character
CRUD, optimistic updates, typed search, aggregate reads, and owner-scoped alias, note, and event
editing. Inserts retrieve `@@IDENTITY` on the same connection and transaction. Aggregate reads
issue several ordered queries on one connection rather than multiplying child collections in one
join.

Access has no portable offset syntax, so the initial paging implementation reads an ordered
result and skips rows through the data reader before collecting the requested page. This is
correct and bounded in memory, though large offsets will require a keyset or nested-`TOP` strategy
if vault size makes sequential skipping expensive.

`AccessWritingVaultTransactionRunner` owns connection, transaction, commit, and rollback. A
structured failure rolls back; cancellation rolls back and propagates; OleDb failures at open,
begin, command, or commit become `StorageFailureError`; other exceptions roll back and propagate
as programming failures.

## Conventions

- Access `AutoNumber` and `Long` values are represented by `int`.
- Access `Date/Time` values are represented by `DateTime`, not `DateTimeOffset`; timezone meaning
  must be defined by the application rather than inferred from the database value.
- `VaultObject` avoids colliding with C# `object` and the database table named `Objects`.
- Insert methods return the generated identity. Concrete repositories should retrieve it in the
  same open connection with `SELECT @@IDENTITY`.
- Update and delete methods return `false` when the target did not exist.
- Page sizes are capped before SQL generation. Access pagination will require a deterministic
  `ORDER BY` and a provider-specific query strategy.
- Aggregate reads may use several small queries on one connection. They should not use a large
  join that multiplies notes, events, tags, and memberships into a Cartesian result.

## Next implementation slice

Implement one vertical slice first: `AccessCharacterRepository`, including list/get/insert/update,
then aliases, notes, events, tags, memberships, and ownership for `GetDetailsAsync`. That proves
identity retrieval, long-text parameters, nullable dates, paging, and multi-query aggregation
before the same pattern is repeated for the other entities.
