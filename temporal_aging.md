# Lostville Temporal Aging

Historical user-side requirements, preserved for the acceptance case. The v4 Debug implementation is tracked in [V4_API_PLAN.md](V4_API_PLAN.md); this original proposal does not certify Release readiness.

## User-Side Functional Specification

**Status:** Proposed
**Scope:** Character age calculations in continuities where calendar time, biological time, and experienced time can diverge.

## 1. Purpose

The Writer's Vault currently calculates a character's age from the character's birth date and the continuity's artificial clock. That correctly answers how much outside-world calendar time has passed, but it cannot represent characters who experience a temporal pause, time dilation, accelerated aging, suspended animation, or another discontinuity.

Lostville requires this distinction. Kirsty is born on 2012-08-11. Lostville vanishes on 2023-08-14 and returns on 2024-09-23, while no time passes for Kirsty. On Arrival Day she is twelve by the outside calendar but remains approximately eleven biologically and experientially.

When temporal aging is enabled for a character, the vault must preserve and calculate these differences without changing the meaning of the continuity clock or the character's birth date.

## 2. User Goals

The system must let the user:

1. Ask how old a character is at the continuity's current artificial instant.
2. Distinguish calendar, legal, biological, and experienced age.
3. Record intervals during which biological or experienced time passes at a different rate from outside-world time.
4. See which temporal effects contributed to a calculated age.
5. Preserve uncertain dates and return bounded answers instead of inventing precision.
6. Detect contradictory or overlapping temporal effects before they corrupt calculations.
7. View the results automatically in the Access front end after MCP writes are committed.
8. Continue using the existing age calculation unchanged for characters who do not have temporal aging enabled.

## 3. Terminology

### 3.1 Calendar age

Elapsed time between the character's birth and the calculation instant on the continuity's outside-world calendar.

Calendar age is independent of temporal pauses experienced by the character.

### 3.2 Legal age

The age recognized by the applicable civil authority. In the first release, legal age is equal to calendar age. The result must identify that policy explicitly rather than implying that legal age is a physical fact.

The model should permit a future jurisdiction-specific legal-age policy without changing stored birth dates or temporal effects.

### 3.3 Biological age

Time accumulated by the character's body since birth. A complete biological pause contributes zero biological elapsed time.

Biological age is a continuity measure, not a medical estimate of apparent health or physical condition.

### 3.4 Experienced age

Time consciously or personally lived by the character since birth. A complete experienced-time pause contributes zero experienced elapsed time.

Experienced age may diverge from biological age. A mind may remain active while the body is frozen, or a body may age while consciousness is suspended.

### 3.5 Temporal effect

A structured interval during which biological or experienced time accumulates at a rate other than the normal rate of `1.0`.

Examples:

- Complete pause: biological `0.0`, experienced `0.0`
- Suspended body with an active mind: biological `0.0`, experienced `1.0`
- Unconscious accelerated aging: biological `5.0`, experienced `0.0`
- Time dilation: biological `0.25`, experienced `0.25`

### 3.6 Calculation instant

The instant against which age is calculated. By default this is the continuity's artificial current instant. A read-only age query may optionally supply an alternate instant without changing the continuity clock.

## 4. Feature Enablement

Temporal aging is opt-in per character.

Each character has a temporal-age profile with the following user-visible state:

| Field | Meaning |
|---|---|
| `enabled` | Whether biological and experienced age are calculated from temporal effects |
| `legalAgePolicy` | Policy used for legal age; initially `CalendarAge` only |
| `notes` | Optional explanation of why special aging rules apply |
| `version` | Optimistic-concurrency version |

When `enabled` is false or no profile exists:

- Existing calendar age behavior remains unchanged.
- `biologicalAge` and `experiencedAge` equal calendar age.
- The result reports that temporal tracking is disabled.

When `enabled` is true:

- The calculator loads all applicable temporal effects.
- Biological and experienced elapsed time are calculated separately.
- The result identifies every effect that changed either value.

Creating a temporal effect for a character should automatically enable temporal aging unless the request explicitly opts out. Disabling the profile must retain existing temporal-effect records but exclude them from calculations.

## 5. User-Facing MCP Behavior

### 5.1 Read a character's age

The existing `character_age` operation should remain the primary user-facing query.

#### Request

```json
{
  "characterId": 42,
  "at": null
}
```

`at` is optional. When omitted or null, the operation uses the continuity's artificial clock. Supplying `at` performs a read-only hypothetical calculation and does not change stored clock state.

#### Response

```json
{
  "characterId": 42,
  "continuityId": 3,
  "asOf": "2024-09-23T12:00:00-07:00",
  "referenceTimeZoneId": "America/Vancouver",
  "status": "Alive",
  "precision": "Exact",
  "temporalTrackingEnabled": true,
  "calendarAge": {
    "exactYears": 12,
    "minimumYears": 12,
    "maximumYears": 12
  },
  "legalAge": {
    "exactYears": 12,
    "minimumYears": 12,
    "maximumYears": 12,
    "policy": "CalendarAge"
  },
  "biologicalAge": {
    "exactYears": 11,
    "minimumYears": 11,
    "maximumYears": 11
  },
  "experiencedAge": {
    "exactYears": 11,
    "minimumYears": 11,
    "maximumYears": 11
  },
  "effectsApplied": [
    {
      "temporalEffectId": 7,
      "name": "The Lost Year",
      "effectiveFrom": "2023-08-14T00:00:00-07:00",
      "effectiveTo": "2024-09-23T00:00:00-07:00",
      "biologicalRate": 0.0,
      "experiencedRate": 0.0
    }
  ],
  "warnings": []
}
```

Exact durations should be retained internally even when the user-facing summary reports completed years. Future response formats may expose years, months, days, and duration ticks without changing stored data.

### 5.2 Manage the temporal-age profile

The MCP should expose operations equivalent to:

- `character_temporal_profile_get`
- `character_temporal_profile_set`

Writes require `expectedVersion` and `operationId`, follow existing idempotency rules, and are journaled.

### 5.3 Manage temporal effects

The MCP should expose operations equivalent to:

- `character_temporal_effect_create`
- `character_temporal_effect_patch`
- `character_temporal_effect_list`
- `character_temporal_effect_delete_preview`
- `character_temporal_effect_soft_delete`
- `character_temporal_effect_restore`

Creating or patching an effect must return the affected resource key and new version. List and graph operations must hide soft-deleted effects unless explicitly requested.

### 5.4 Preview calculations before writing

A read-only preview operation should accept a proposed temporal effect and return the character's resulting ages and any conflicts without saving the effect.

Suggested operation:

- `character_temporal_effect_preview`

This lets the user ask, for example, "What would Kirsty's biological age be if no time passed for her during the disappearance?" before changing canon.

## 6. Logical Data Structures

### 6.1 CharacterTemporalProfile

One optional profile per character.

| Field | Type | Rules |
|---|---|---|
| `CharacterId` | Identifier | Primary key and foreign key to a Character entity |
| `Enabled` | Boolean | Required; defaults to true when created |
| `LegalAgePolicy` | Enum | Required; initially `CalendarAge` |
| `Notes` | Text, nullable | User explanation |
| `Version` | Integer | Required optimistic-concurrency version |
| `IsDeleted` | Boolean | Supports restoration and history |
| Audit fields | Existing conventions | Creation, update, operation, and journal metadata |

### 6.2 CharacterTemporalEffect

One row per character-specific temporal interval.

| Field | Type | Rules |
|---|---|---|
| `Id` | Identifier | Primary key |
| `ContinuityId` | Identifier | Must match the character and associated event |
| `CharacterId` | Identifier | Required target character |
| `Name` | Text | Required user-facing label |
| `Period` | Fictional-date interval | Required; uses the vault's structured date model |
| `BiologicalTimeRate` | Decimal | Required; normal is `1.0`; pause is `0.0` |
| `ExperiencedTimeRate` | Decimal | Required; normal is `1.0`; pause is `0.0` |
| `WorldEventId` | Identifier, nullable | Optional cause or context |
| `Notes` | Text, nullable | Explanation, exceptions, or narrative context |
| `Version` | Integer | Required optimistic-concurrency version |
| `IsDeleted` | Boolean | Supports restoration and history |
| Audit fields | Existing conventions | Creation, update, operation, and journal metadata |

Rates must be finite and non-negative in the first release. Negative personal time, age reversal, and discontinuous resets require a later explicit design rather than accidental support through negative rates.

### 6.3 Future shared effects

The first release may store one temporal effect per character. A later normalized design may separate the effect from its targets:

- `TemporalEffect`
- `TemporalEffectTarget`

That design would allow one event such as The Lost Year to affect every resident of Lostville while supporting per-character exceptions. The initial MCP contract should use stable resource concepts so storage can be normalized later without changing how users describe an effect.

## 7. Automatic Calculation Rules

### 7.1 Baseline

1. Resolve the calculation instant from `at` or the continuity clock.
2. Resolve the character's structured birth interval.
3. Calculate calendar-age bounds using the existing age algorithm.
4. Apply the legal-age policy. In the first release, legal age equals calendar age.
5. If temporal tracking is disabled, return calendar age for all age categories.
6. If temporal tracking is enabled, integrate biological and experienced rates over applicable portions of the character's life.

### 7.2 Interval boundaries

Temporal effects use half-open intervals: `[start, end)`.

- The start instant is included.
- The end instant is excluded.
- An open-ended effect continues through the calculation instant.
- Portions before birth are ignored.
- Portions after the calculation instant are ignored.
- Future effects remain stored but do not affect the current result.

### 7.3 Rate application

For each applicable interval:

```text
biological elapsed  = outside elapsed Ã— BiologicalTimeRate
experienced elapsed = outside elapsed Ã— ExperiencedTimeRate
```

Outside periods without an applicable effect use rate `1.0` for both measures.

For Kirsty's Lost Year:

```text
outside elapsed     = 2023-08-14 through 2024-09-23
biological rate     = 0.0
experienced rate    = 0.0
biological elapsed  = 0
experienced elapsed = 0
```

### 7.4 Overlapping effects

The first release must reject overlapping active effects for the same character. The write response must identify the conflicting record IDs and overlapping interval.

This avoids silently multiplying, adding, or prioritizing rates with unclear narrative meaning. The user may split intervals into adjacent non-overlapping effects when the character's temporal state changes.

### 7.5 Uncertain dates

Temporal effects may use the vault's uncertain fictional-date forms, including `Circa`, `Before`, `After`, and `Range`.

When uncertainty changes the answer:

- Return minimum and maximum values for each affected age category.
- Set `exactYears` to null.
- Set `precision` to `Bounded`.
- Include a warning naming the uncertain birth date or temporal effect.

The calculator must never collapse an uncertain interval into an arbitrary exact date.

### 7.6 Death and chronology statuses

Existing status behavior remains available, including:

- `TimelineUnset`
- `BirthDateUnknown`
- `NotYetBorn`
- `PossiblyNotYetBorn`
- `Alive`
- `Deceased`
- `PossiblyDeceased`
- `InvalidChronology`

A temporal pause does not change birth or death dates. If an effect crosses a death boundary, only the portion before death contributes to biological or experienced age unless the character's canon explicitly models post-death existence through a separate feature.

## 8. Validation and Integrity Rules

The server must reject a write when:

1. The character does not exist, is deleted, or is not a Character entity.
2. The effect and character belong to different continuities.
3. The associated world event belongs to another continuity.
4. The interval is empty or structurally invalid.
5. Either rate is missing, negative, non-finite, or outside the supported range.
6. The effect overlaps another active effect for the character.
7. `expectedVersion` is stale.
8. The operation ID is malformed or conflicts with an earlier non-identical operation.

All successful writes must be transactional, versioned, idempotent, soft-deletable, restorable, and present in record and operation history.

## 9. Entity Graph and Continuity Checks

When temporal aging is enabled, `entity_graph` for a character should include:

- The temporal-age profile
- Active temporal effects
- Associated world events
- Whether results were truncated

Continuity checking should flag:

- A scene describing the character as a different biological or experienced age from the calculated result
- Overlapping or contradictory temporal effects
- An effect that claims to pause a character while another canon event requires them to experience time
- A legal-age assertion that uses a policy different from the one recorded
- A calculation performed while the continuity clock is unset

Warnings should describe the conflict and supporting records. They should not automatically rewrite canon.

## 10. Access Viewer Requirements

The Access character page should show an **Age and Time** panel when a character is selected.

### Summary display

- As-of date and reference timezone
- Calendar age
- Legal age and policy
- Biological age
- Experienced age
- Exact or bounded precision
- Temporal tracking enabled/disabled state

### Temporal-effect list

Each row should show:

- Effect name
- Outside-world interval
- Biological rate
- Experienced rate
- Associated world event
- Active, future, completed, or deleted status

### Refresh behavior

After an MCP write, the viewer should automatically requery the character, age result, temporal profile, effects, and related event subforms. `Requery` is required because effects may be inserted, deleted, or restored; a simple record refresh is insufficient.

The viewer remains read-only. It must not bypass MCP validation, history, or concurrency rules.

## 11. Kirsty Acceptance Scenario

Given:

- Kirsty is born on 2012-08-11.
- The continuity clock is 2024-09-23 in `America/Vancouver`.
- Temporal aging is enabled for Kirsty.
- An effect named `The Lost Year` covers 2023-08-14 through 2024-09-23.
- Both effect rates are `0.0`.

When the user asks, "How old is Kirsty at the beginning of the book?"

Then the system reports:

- Calendar age: 12
- Legal age: 12 under the `CalendarAge` policy
- Biological age: approximately 11, calculated from exact elapsed duration
- Experienced age: approximately 11, calculated from exact elapsed duration
- Applied effect: `The Lost Year`
- No use of the machine clock

When the effect is disabled or deleted, biological and experienced age return to the calendar-age result.

When the continuity clock is unset and no `at` value is supplied, the operation returns `TimelineUnset` and does not calculate against the machine clock.

## 12. Out of Scope for the First Release

The following require later explicit designs:

- Negative time rates and age reversal
- A character resetting to an earlier physical age
- Branching personal timelines or duplicate versions of one character
- Legal-age policies that vary by jurisdiction
- Shared population effects with per-character exceptions
- Apparent age, medical aging, immortality, and non-human maturation curves
- Automatic inference of temporal effects from prose

The stored data and MCP resource model should avoid preventing these future additions.
