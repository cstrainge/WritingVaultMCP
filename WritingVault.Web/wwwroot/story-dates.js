(() => {
  'use strict';

  // Story dates have no timezone. UTC is used only to make calendar arithmetic
  // stable across browsers; these values are never converted to local time.
  const dayMs = 86_400_000;
  const dateFormat = new Intl.DateTimeFormat('en-US', {
    weekday: 'long', month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC'
  });
  const monthFormat = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' });

  function parse(value) {
    const match = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d{1,7})?)?)?$/.exec(value || '');
    if (!match) return null;
    const [year, month, day, hour, minute, second] = match.slice(1).map(Number);
    const date = new Date(0);
    date.setUTCFullYear(year, month - 1, day);
    date.setUTCHours(hour || 0, minute || 0, second || 0, 0);
    if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 ||
        date.getUTCDate() !== day || date.getUTCHours() !== (hour || 0) ||
        date.getUTCMinutes() !== (minute || 0) || date.getUTCSeconds() !== (second || 0)) return null;
    return { date, timed: match[4] !== undefined };
  }

  const parts = date => Object.fromEntries(dateFormat.formatToParts(date).map(part => [part.type, part.value]));
  const ordinal = day => {
    const number = Number(day);
    return `${number}${number % 100 >= 11 && number % 100 <= 13 ? 'th' :
      number % 10 === 1 ? 'st' : number % 10 === 2 ? 'nd' : number % 10 === 3 ? 'rd' : 'th'}`;
  };
  const fullDate = date => {
    const value = parts(date);
    return `${value.weekday} ${value.month} ${ordinal(value.day)}, ${value.year}`;
  };
  const time = date => new Intl.DateTimeFormat('en-US', {
    hour: 'numeric', minute: '2-digit', ...(date.getUTCSeconds() ? { second: '2-digit' } : {}),
    hour12: true, timeZone: 'UTC'
  }).format(date).replace(/\s/g, '').toLowerCase();
  const plural = (count, unit) => `${count} ${unit}${count === 1 ? '' : 's'}`;

  function duration(milliseconds, timed) {
    if (milliseconds <= 0) return null;
    if (!timed) return `Over ${plural(Math.max(1, Math.round(milliseconds / dayMs)), 'day')}.`;
    let seconds = Math.floor(milliseconds / 1000);
    const days = Math.floor(seconds / 86400); seconds %= 86400;
    const hours = Math.floor(seconds / 3600); seconds %= 3600;
    const minutes = Math.floor(seconds / 60); seconds %= 60;
    const units = [];
    if (days) units.push(plural(days, 'day'));
    if (hours) units.push(plural(hours, 'hour'));
    if (minutes) units.push(plural(minutes, 'minute'));
    if (seconds) units.push(plural(seconds, 'second'));
    if (!units.length) return 'Over less than a second.';
    return `Over ${units.length > 1 ? `${units.slice(0, -1).join(', ')} and ${units.at(-1)}` : units[0]}.`;
  }

  function span(lower, upper, timed, lowerInclusive = true, upperInclusive = true) {
    const start = parts(lower.date), end = parts(upper.date);
    const sameDay = lower.date.toISOString().slice(0, 10) === upper.date.toISOString().slice(0, 10);
    const startTime = timed && lower.timed ? `${time(lower.date)} on ` : '';
    const endTime = timed && upper.timed ? `${time(upper.date)} on ` : '';
    if (sameDay) {
      if (startTime && endTime)
        return `From ${time(lower.date)} to ${time(upper.date)} on ${fullDate(lower.date)}.`;
      return `On ${fullDate(lower.date)}.`;
    }
    const sameMonth = start.month === end.month && start.year === end.year;
    const opening = lowerInclusive ? 'From' : 'After';
    const closing = upperInclusive ? 'to' : 'to before';
    if (sameMonth)
      return `${opening} ${startTime}${start.weekday} the ${ordinal(start.day)} ${closing} ${endTime}${end.weekday} the ${ordinal(end.day)} in ${end.month} ${end.year}.`;
    return `${opening} ${startTime}${fullDate(lower.date)} ${closing} ${endTime}${fullDate(upper.date)}.`;
  }

  function describe(input) {
    const kind = input?.kind;
    const original = input?.originalText?.trim();
    const calendar = input?.calendarId && input.calendarId !== 'Gregorian' ? input.calendarId : null;
    if (!kind || kind === 'Unknown') return { description: original || 'Date unknown' };
    if (calendar) return { description: original || input.display || 'Date unknown', context: `${calendar} calendar` };
    const lower = parse(input.lower), upper = parse(input.upper);
    if (kind === 'Year' && lower) return { description: String(lower.date.getUTCFullYear()), context: original };
    if (kind === 'Month' && lower) return { description: monthFormat.format(lower.date), context: original };
    if (kind === 'ExactDate' && lower) return { description: fullDate(lower.date), context: original };
    if (kind === 'ExactInstant' && lower)
      return { description: `${time(lower.date)} on ${fullDate(lower.date)}`, context: original };
    if (kind === 'Before' && upper)
      return { description: `${input.upperInclusive ? 'On or before' : 'Before'} ${upper.timed ? time(upper.date) + ' on ' : ''}${fullDate(upper.date)}`, context: original };
    if (kind === 'After' && lower)
      return { description: `${input.lowerInclusive ? 'On or after' : 'After'} ${lower.timed ? time(lower.date) + ' on ' : ''}${fullDate(lower.date)}`, context: original };
    if (kind === 'Circa' && lower && upper)
      return { description: original || `Around ${span(lower, upper, lower.timed || upper.timed).replace(/^From /, '').replace(/\.$/, '')}.` };
    if (['Range', 'KnownRange', 'UncertainRange'].includes(kind) && lower && upper) {
      const timed = lower.timed || upper.timed;
      if (kind === 'UncertainRange')
        return { description: original || `Sometime ${span(lower, upper, timed).replace(/^From /, 'from ')}`,
          context: 'Bounded uncertainty window' };
      if (kind === 'Range')
        return { description: span(lower, upper, timed),
          context: original ? `${original} · Range meaning needs review` : 'Range meaning needs review' };
      if (!timed) {
        return {
          description: span(lower, upper, false, input.lowerInclusive !== false, input.upperInclusive !== false),
          duration: duration(Math.max(dayMs, upper.date.getTime() - lower.date.getTime()), false),
          context: original || undefined
        };
      }
      return {
        description: span(lower, upper, timed),
        duration: duration(upper.date.getTime() - lower.date.getTime(), timed),
        context: original || undefined
      };
    }
    return { description: original || input?.display || 'Date unknown' };
  }

  // The chronology table suppresses repeated calendar components visually. A
  // fuzzy date uses the full period label so a bound is never mistaken for the
  // event's precise year, month, or day.
  function chronologyDate(input, previous = null) {
    const kind = input?.kind;
    if (!['Year', 'Month', 'ExactDate', 'ExactInstant'].includes(kind) ||
        (input.calendarId && input.calendarId !== 'Gregorian'))
      return { kind: 'period', full: describe(input), next: null };
    const parsed = parse(input.lower);
    if (!parsed) return { kind: 'period', full: describe(input), next: null };
    const date = parsed.date;
    const year = String(date.getUTCFullYear());
    const month = kind === 'Year' ? null : date.getUTCMonth() + 1;
    const day = kind === 'Year' || kind === 'Month' ? null : date.getUTCDate();
    const yearChanged = previous?.year !== year;
    const monthChanged = yearChanged || previous?.month !== month;
    const dayChanged = monthChanged || previous?.day !== day;
    return {
      kind: 'parts',
      year: yearChanged ? year : '',
      month: month !== null && monthChanged ?
        new Intl.DateTimeFormat('en-US', { month: 'long', timeZone: 'UTC' }).format(date) : '',
      day: day !== null && dayChanged ? ordinal(day) : '',
      time: kind === 'ExactInstant' ? time(date) : null,
      full: describe(input),
      next: { year, month, day }
    };
  }

  function fromFields(fields, prefix) {
    const field = part => fields[`${prefix}${part}`];
    let lower = field('LowerBound'), upper = field('UpperBound');
    // Access serializes date-only range bounds with a midnight time component.
    // Both-midnight ranges have no asserted clock precision.
    if (['Range', 'KnownRange', 'UncertainRange', 'Circa', 'Before', 'After'].includes(field('Kind')) &&
        [lower, upper].filter(Boolean).every(value =>
          typeof value === 'string' && /T00:00:00(?:\.0+)?$/.test(value))) {
      if (lower) lower = lower.slice(0, 10);
      if (upper) upper = upper.slice(0, 10);
    }
    return describe({ kind: field('Kind'), lower, upper,
      lowerInclusive: field('LowerInclusive'), upperInclusive: field('UpperInclusive'),
      originalText: field('OriginalText'), calendarId: field('CalendarId') });
  }

  function utcTimestamp(value) {
    const parsed = parse(typeof value === 'string' ? value.replace(/Z$/, '').replace(/\+00:00$/, '') : '');
    return parsed ? `${time(parsed.date)} UTC on ${fullDate(parsed.date)}` : value;
  }

  function clockTime(clock) {
    if (clock?.status === 'DateOnly' && clock.currentDate) {
      const date = new Date(`${clock.currentDate}T12:00:00Z`);
      const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', {
        weekday: 'long', month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC'
      }).formatToParts(date).map(part => [part.type, part.value]));
      return `${parts.weekday} ${parts.month} ${ordinal(parts.day)}, ${parts.year} (all day; ${clock.referenceTimeZoneId || 'UTC'})`;
    }
    if (!clock?.currentTime) return 'Story time unset';
    const zone = clock.referenceTimeZoneId || 'UTC';
    try {
      // A session override carries the offset that the backend accepted from the user's
      // local input. Keep that wall time even if the browser has different future zone rules.
      const wall = clock.source === 'session' &&
        /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}):\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.exec(clock.currentTime);
      const date = new Date(wall ? `${wall[1]}:00Z` : clock.currentTime);
      if (!Number.isFinite(date.getTime())) return clock.localDisplay || clock.currentTime;
      const formatter = new Intl.DateTimeFormat('en-US', {
        weekday: 'long', month: 'long', day: 'numeric', year: 'numeric',
        hour: 'numeric', minute: '2-digit', hour12: true, timeZone: wall ? 'UTC' : zone
      });
      const value = Object.fromEntries(formatter.formatToParts(date).map(part => [part.type, part.value]));
      return `${value.hour}:${value.minute}${value.dayPeriod.toLowerCase()} on ` +
        `${value.weekday} ${value.month} ${ordinal(value.day)}, ${value.year} (${zone})`;
    } catch { return clock.localDisplay || clock.currentTime; }
  }

  function clockLocalDateTime(clock) {
    if (clock?.status === 'DateOnly' && clock.currentDate)
      return `${clock.currentDate}T00:00:00`;
    if (!clock?.currentTime) return null;
    const wall = clock.source === 'session' &&
      /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.exec(clock.currentTime);
    if (wall) return wall[1];
    try {
      const parts = new Intl.DateTimeFormat('en-US', {
        timeZone: clock.referenceTimeZoneId || 'UTC', year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23'
      }).formatToParts(new Date(clock.currentTime));
      const values = Object.fromEntries(parts.map(part => [part.type, part.value]));
      return `${values.year.padStart(4, '0')}-${values.month}-${values.day}T${values.hour}:${values.minute}:${values.second}`;
    } catch { return null; }
  }

  function clockLocalDate(clock) { return clockLocalDateTime(clock)?.slice(0, 10) || null; }

  window.WritingVaultStoryDates = Object.freeze({ describe, chronologyDate, fromFields, utcTimestamp, clockTime, clockLocalDate, clockLocalDateTime });
})();
