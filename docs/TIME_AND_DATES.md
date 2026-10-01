# Story time and dates

## Two separate clocks

Audit fields such as `CreatedAtUtc` record real UTC operational time. Fictional facts use timezone-free `StoryDate` values. A continuity clock stores an artificial real instant as UTC plus a named reference timezone. No story-time operation reads the machine clock.

`continuity_clock_set` sets or clears the artificial instant using optimistic concurrency. When setting it, the supplied numeric offset must match the named timezone at that instant, which resolves daylight-saving ambiguity. Clearing the clock makes current-state and age queries return no as-of result.

## StoryDate

The structured kinds are `Unknown`, `ExactInstant`, `ExactDate`, `Month`, `Year`, `Circa`, `KnownRange`, `UncertainRange`, `Before`, and `After`. `KnownRange` is a known interval with a start and end; `UncertainRange` is a bounded window within which the date may fall. Older stored `Range` values have no recorded meaning and remain readable but cannot be written through v4. Bounds are stored as timezone-free Access date values with explicit inclusivity, `OriginalText`, and a calendar identifier. Supported normalized years are 100 through 9999.

Precision is preserved. A year value remains a year and round-trips with bounds covering that year; it does not acquire January 1 as an asserted exact date. Open-ended and unknown values remain open or unknown. Interval overlap and as-of tests honor boundary inclusivity.

The Debug viewer renders story dates as prose in record Details, the timeline table, graph labels, and timeline details. A timed `KnownRange` says when it starts and ends and gives its elapsed days, hours, and minutes; a date-only known interval omits clock times and gives elapsed calendar days. An `UncertainRange` describes possible bounds without inventing an event duration. A legacy `Range` explicitly asks for review of its meaning. Exact dates, months, years, circa dates, and open-ended dates do not acquire a fabricated duration. Story-date formatting never converts through the browser's timezone. The artificial clock label converts its real UTC instant through its selected named timezone and displays that zone explicitly. Operational `...AtUtc` fields are rendered with an explicit UTC label.

The browser's session override can also hold a local date without a clock time. It is saved per continuity in the browser and sent to v4 `session_set` as `currentDate`; the effective clock reports `DateOnly` and no `currentTime`. The timeline marks the whole story day, character ages use a day-wide interval, and an exact current-state read asks for a clock time. Temporal aging measures elapsed **timezone-free story-clock time**, so a story day spans 24 clock hours even when the corresponding real UTC interval crosses a daylight-saving change. This preserves the established fictional-date model; it does not claim a precise physical duration for a date-only selection.

The Debug timeline uses a calendar axis for exact dates and uncertain periods. Its chronology table shows all selected dated records, loading bounded API batches internally without page controls. Graph pan and zoom change the graphical viewport only; an explicit dragged date selection filters the table to overlapping dated records, and clearing the selection restores the full chronology. Unknown dates remain in a separate, complete undated section when no date range is selected. When story time is set, a highlighted Now box marks its exact local date and time; if that time is outside the graph window, a highlighted control jumps to it. The complete chronology table also includes a highlighted Now row at its calendar position even when graph pan or zoom places that time outside the viewport. An explicit date selection shows that row only when Now falls inside the selected dates. Historical entries remain visible with the artificial clock unset; only the Now box and time-dependent projections require a clock.

## Entity-local current time

`entity_local_time` begins with the continuity clock, then resolves location context at that story time:

1. character primary residence;
2. organization primary location;
3. object physical location, then custody context where available;
4. the location's timezone or nearest ancestor timezone;
5. the continuity reference/default timezone.

The result always names the timezone and the source used. It never silently substitutes the Windows timezone.

`entity_temporal_state` evaluates residence, membership, relationship, ownership, custody, and location intervals against the stored clock. `character_age` uses the same clock and returns exact, bounded, possibly-not-yet-born, possibly-deceased, or indeterminate output as the available precision permits. February 29 birthdays advance on February 28 in non-leap years.
