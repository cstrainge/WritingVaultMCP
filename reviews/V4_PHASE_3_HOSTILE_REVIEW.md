# v4 Phase 3 hostile application review

Review date: 2026-09-28  
Scope: semantic resolution, Unicode handling, selected-continuity isolation, dates, sparse changes, result/error envelopes, idempotency, transactional editorial commands, reciprocal relationships, multi-owner invariants, backup API boundary, and storage-identity disclosure  
Verdict: **Pass with zero unresolved findings.**

## Attacks and results

| Attack | Required result | Evidence |
| --- | --- | --- |
| Resolve case, whitespace, or compatibility-Unicode variants | Resolve the one exact canonical match | `NaturalResolutionIsExactScopedAliasAwareAndBounded` covers preferred/given names and aliases, including full-width text normalized with Form KC. |
| Present several exact names or aliases | Return bounded labelled choices and perform no guessing | `AmbiguityCandidatesAreCappedWithoutGuessing` creates twelve matches and exposes ten semantic candidates with the true total. |
| Supply a reference of the wrong kind | Reject it before mutation | Semantic-reference tests require `reference.invalid`; character/object wrappers also enforce their exact kind. |
| Supply a valid reference from another continuity | Report not found or scope mismatch and write nothing | Resolver scope checks and `EventProjectBatchRejectsCrossContinuityBeforeAnyWrite` prove isolation and atomic failure. |
| Resolve a deleted record, deleted alias owner, or child of a deleted owner | Hide it unless an operation explicitly includes deleted records | Deletion-policy tests cover direct references, aliases, and notes whose owner is deleted. |
| Smuggle a locale date, timezone, invalid day, irrelevant field, overflow year, empty interval, trailing decimal, or excess fractional precision | Return a field-level date validation error | Story-date tests exercise every listed shape and accept only timezone-free ISO forms in the supported Access range. |
| Vary calendar casing or whitespace | Store one canonical calendar identity | Accepted Gregorian input always normalizes to `Gregorian`; unsupported calendars fail. |
| Confuse omitted patch fields with explicit JSON null | Preserve omission and retain explicit null for nullable fields | Sparse contracts use a required allowlisted `changes` map; parser and contract tests preserve `JsonValueKind.Null` and reject empty or unknown fields. |
| Retry an uncertain mutation with the same token | Replay the prior result; changed semantic input fails | Shared note and backup tests cover successful replay and input mismatch. Backup receipts keep token identity after retention expiry. |
| Reconnect with a different client label | Keep semantic idempotency stable | The coordinator removes attribution from its semantic request hash while retaining it in audit metadata. |
| Associate several projects and make one invalid | Commit none of the batch | Project references resolve before queueing and are revalidated inside one transaction; the cross-continuity batch leaves both physical tables unchanged. |
| Add/remove/restore projects for either event kind | Use the correct physical relation while preserving event ownership | One service dispatches world events to `ProjectEntities` and entity-specific events to `EntityEventProjects`; tests cover add, remove, restore, and zero-association validity. |
| Create a directed character relationship | Reverse traversal must immediately show the inverse label | The semantic wrapper reuses the established reciprocal relationship transaction; the application test observes `parent of` and `child of` from opposite sides. |
| Create malformed or underallocated co-ownership | Reject without a partial period or owner set | Empty-owner input returns validation, and group-share validation leaves only the valid ownership period committed. |
| Map a stale expected version | Return recovery detail without the numeric storage key | Result mapping emits `version.conflict` with current-version detail and no affected storage identity. |
| Return ambiguity or validation failure | Preserve candidates and field detail without IDs/GUIDs/paths | Result-mapper tests require semantic references, labels, bounded candidates, and field names. |
| Request a backup through v4 | Accept no caller path and return no path | The coordinated service uses configured roots/retention, quiesces writes, verifies database plus assets, and maps only safe summary metadata. |
| Search public schemas/results for internal identifiers | Reject numeric database keys, operation GUIDs, SQL, connection strings, and paths | Contract snapshot tests and serialized-result assertions enforce the boundary; meaningful timezone/calendar identifiers remain allowed. |

## Findings resolved during review

1. Nullable update-record properties could not distinguish an omitted edit from an explicit JSON `null`. Update tools now require a non-empty `changes` object with per-tool allowlists and nullability.
2. Story dates accepted Gregorian case-insensitively but could retain the caller's casing. Accepted values now canonicalize to `Gregorian`.
3. Exact instants relied only on framework parsing. An explicit ASCII ISO shape now rejects trailing decimal separators and more than seven fractional digits before parsing.
4. Malformed empty ownership input could dereference a null/empty collection before reaching domain validation. The common service now rejects absent, empty, oversized, or blank-principal owner sets without writing.
5. The plan and contract still mentioned a separate `certainty` value even though storage cannot preserve it. Public documentation now represents uncertainty through the story-date kind and bounds.
6. The continuity mismatch mapper emitted `scope.continuity_mismatch` while the frozen contract names `scope.mismatch`. The mapper and regression test now use the frozen code.
7. Tag, source, image-metadata, and relation target categories were not all explicit in the common resolver facade. They are now centralized with entity, note, event, project, relationship-type, and ownership-principal targets.
8. The first backup API copied only the database. The final common service snapshots and verifies referenced assets and preserves mutation-token identity beyond retention; the public result remains path-free.

## Verification

- Phase 2/3 focused Debug gate: **39 passed, 0 failed, 0 skipped**.
- Full Debug regression suite: **121 passed, 0 failed, 0 skipped**.
- Debug build: zero warnings and zero errors.
- Generated v4 contract snapshots remain exact.
- Tests used disposable Access databases and temporary backup roots.
- The production database was not modified.
- Release artifacts were not built, launched, or changed.

## Unresolved findings

None.
