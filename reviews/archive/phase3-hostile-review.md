# Phase 3 hostile review

Reviewed: 2026-09-27

Scope: continuity isolation, artificial story time, timezone resolution, projects, residences, events, character relationships, organizations, ownership/custody/location, provenance, and partial dates.

## Attacks performed

| Attack | Required result | Evidence |
|---|---|---|
| Link records across continuities | Write rejected and transaction rolled back | Cross-continuity project assignment test plus shared pre-commit and startup probes for every continuity-scoped relationship |
| Assign an entity to a variant group from another continuity or type | Write rejected | Variant-group acceptance test returns `continuity.mismatch`; invariant probes independently detect persisted mismatch |
| Resolve time for an entity with no location | Continuity clock timezone used explicitly | Project fallback regression returns `continuity-default` and no location |
| Resolve through a child location with no timezone | Nearest ancestor timezone used | Custodied-object regression resolves through organization headquarters to the parent location's `America/New_York` zone |
| Resolve an object's location through custody | Custodian residence/headquarters supplies context | Regression exercises object to organization principal to primary organization location to inherited timezone |
| Let machine local time leak into fictional dates | Rejected or normalized away | Story-date validation requires `DateTimeKind.Unspecified`; artificial clocks store UTC plus named timezone |
| Duplicate an undirected relationship by reversing endpoints | Exact duplicate rejected | Endpoints canonicalize before duplicate detection, including types that allow distinct overlapping periods |
| Hide a relationship from one endpoint | Both endpoint reads return the same canonical row | Bidirectional relationship acceptance test covers forward/inverse labels, delete, and restore |
| Add unsupported world-event participant types | Project/location/event rejected | Participant matrix allows characters, organizations, and objects; locations use `WorldEventLocations` |
| Add entity-local events to projects or world events | Write rejected | Owner matrix limits entity events to characters, locations, organizations, and objects |
| Add a second primary world-event location | Write and invariant guard reject it | Service cardinality check plus shared persisted-shape probe |
| Represent unknown or unowned ownership | No fake owner required | End-to-end workflow stores both explicit states with zero owner rows |
| Represent organization and co-ownership | Same period semantics as one owner | End-to-end workflow stores organization ownership and two-owner 50/50 composition |
| Supply partial shares or a non-100-percent total | Write rejected | Ownership validator and transaction invariant guard require all-or-none shares totaling 1,000,000 parts |
| Conflate ownership, custody, and physical location | Separate histories retained | Separate tables, commands, graph reads, active-state evaluation, and overlap policies |
| Use an arbitrary claim-evidence relation | Write rejected | Closed canonical set is `Supports`, `Contradicts`, and `Context`; startup probe detects invalid persisted values |
| Lose a source claim when the URL/cache disappears | Local claim remains queryable | Provenance tests verify Access claim/evidence rows and content-addressed local snapshot metadata |
| Store year-only or other fuzzy dates | Precision and original wording survive | Unit tests cover all nine date kinds; database round-trip verifies year bounds without invented month/day |
| Give every canon type shared associations | Matrix works without polymorphic foreign keys | Acceptance test covers notes, tags, sources, claims, and same-continuity project assignments for every supported type |

## Review findings fixed

1. Exact duplicate character relationships were possible when a relationship type allowed overlaps. Writes and startup diagnostics now reject equivalent active periods after undirected canonicalization.
2. The generic event APIs accepted project/world-event owners and unsupported participant types despite the documented matrix. Command validation and invariant probes now enforce the accepted scope.
3. Claim evidence accepted arbitrary relationship text. Inputs are now validated and stored with canonical closed-set values, and startup diagnostics detect invalid persisted values.
4. Project hierarchy, organization hierarchy, real-world place labels, and event ownership were not explicit enough in the decision record. `DESIGN_DECISIONS.md` now records the deliberately flat first-release scope.
5. The model document described variant groups as vault-global even though the implemented and accepted boundary is continuity-scoped. The documentation now matches the enforced model.

## Verification result

- Full command: `dotnet test .\tests\WritingVaultMcp.Tests\WritingVaultMcp.Tests.csproj -c Debug --no-restore`
- Result: 48 passed, 0 failed, 0 skipped.
- Configuration: Debug only. Release artifacts were not rebuilt or modified.
- Final test databases pass exact schema verification and all startup integrity diagnostics.

## Unresolved findings

None.
