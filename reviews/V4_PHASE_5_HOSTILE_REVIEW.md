# v4 Phase 5 hostile temporal-aging review

Review date: 2026-09-28  
Scope: temporal profiles, legal policy, separate biological/experienced rates, fuzzy bounds, overlap prevention, death clamping, preview parity, timezones, leap days, missing clocks, concurrency, and causal events  
Verdict: **Pass with zero unresolved findings.**

## Attacks and results

| Attack | Required result | Evidence |
| --- | --- | --- |
| Apply Lostville's complete pause | Calendar/legal age remains outside-world age while biological/experienced age loses the paused duration | `KirstysLostYearSeparatesCalendarLegalBiologicalAndExperiencedAge` verifies the four meanings and applied effect. |
| Use uncertain birth, death, or effect boundaries | Return defensible minimum/maximum values and warnings without inventing a point | `FuzzyBirthOpenEffectAndDeathProduceBoundsAndWarnings` checks bounded output, death clamp, and open-boundary warnings. |
| Submit overlaps or NaN/negative rates | Reject before writing | Preview and mutation validation reject overlap and non-finite or negative rates. |
| Replace an effect in preview | Exclude the original from both conflict detection and projected calculation | `ReplacementPreviewExcludesOriginalEffectAndProfileMutationMapsToCharacter` proves preview/write parity. |
| Disable tracking | Retain effects while returning baseline biological/experienced age | Disabled-profile coverage verifies effects remain stored and are excluded from calculation. |
| Use a leap-day birth and a timezone-local as-of time | Apply stable calendar rules using the requested story timezone | Leap-day coverage asserts the expected completed-year result; no machine clock is consulted. |
| Leave story time unset | Return `TimelineUnset` for all dependent measures | Character overview and timeline tests distinguish clockless chronology from clock-dependent age. |
| Move a causal world event between resolution and commit | Revalidate it as active and same-continuity inside the transaction | Create/update services recheck the causal event under the serialized write transaction. |
| Update an existing profile | Expose the profile version so a client never guesses `expectedVersion` | Character pages now include `temporalProfile` with enabled state, policy, notes, version, and deletion state. |
| Inspect an effect | Expose its affected character and optional causal world event | Effect overview has bounded `character` and `worldEvent` sections. |

## Findings resolved during review

1. Fuzzy effects and death dates were being collapsed too aggressively. Calculations now propagate defensible bounds, clamp elapsed time at death, and name uncertainty in warnings.
2. Replacement preview removed the original effect from overlap checks but still included it in projected age. It now excludes the original from both paths.
3. `excludeEffectRef` could identify an unrelated relation. It must now be a temporal effect belonging to the same character.
4. Profile and effect text accepted unbounded notes/names. Public limits are now enforced before writes.
5. Causal events were resolved before queueing but not revalidated inside create/update transactions. Both mutations now recheck active same-continuity world events.
6. Re-enabling a soft-deleted profile left deletion audit fields populated. Reactivation clears them atomically.
7. Profile mutation returned an internal profile kind that the public mapper could not represent. It now reports the affected character while exposing the profile version through the character read.
8. Temporal-effect pages hid their causal world event, and character pages hid profile concurrency state. Both are now visible through bounded public projections.

## Verification

- Phase 5 focused Debug gate: **5 passed, 0 failed, 0 skipped**.
- Full Debug regression suite: **148 passed, 0 failed, 0 skipped**.
- Kirsty's Lost Year, fuzzy/open dates, overlap failure, profile disable, leap day, and replacement preview are covered.
- Debug build: zero warnings and zero errors.
- Tests use artificial/session time only.

## Unresolved findings

None.
