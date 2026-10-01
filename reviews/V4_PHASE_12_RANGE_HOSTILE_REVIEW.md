# Phase 12 explicit range meaning: Debug hostile review

Status: **zero open technical findings** for the 009 date-meaning change on a
disposable schema-011 fixture. Production migration remains a cutover task.

## Adversarial checks

- Additive migration 009 leaves legacy `Range` values unchanged and admits
  distinct `KnownRange` and `UncertainRange` values. The administrative
  `schema range-review` command found the fixture's ambiguous fog-week row;
  no wording was used to reclassify it silently.
- Twenty-two focused Debug range, timeline, and temporal-age tests passed.
  These cover known intervals with wording, uncertainty without wording,
  effect calculation, and legacy review behavior.
- A loaded Chrome date-language probe passed against the disposable database
  migrated through 011. It verified readable one-day and multi-day durations,
  date-only and exclusive bounds, and a single ambiguous legacy chronology
  entry with a review cue and checkerboard graph pattern. Synthetic known
  intervals retained separate, non-overlapping start/end arrow tracks;
  uncertain intervals did not acquire definite boundaries.
- The administrative inventory was corrected to inspect a pre-migration v3
  database without requiring the latest v4 schema; it still rejects incomplete
  date columns in a table that exists. A read-only run against the active
  production v3 database on 2026-10-01 found **zero** legacy `Range` rows.

The prior Phase 11 browser assertions expected the old generic `Range` to
expand into start/end rows and carry a four-day duration. That would falsely
assert certainty after migration 009. The probe was corrected to use
`KnownRange` for definite durations and to require the legacy fog-week row to
remain unsplit. A focused rerun passed.

No Release binary, production row, or live client was changed. A full viewer
regression and production migration remain in the broader Phase 12 gate.
