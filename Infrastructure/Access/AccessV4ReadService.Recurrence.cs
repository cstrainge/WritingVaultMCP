using System.Globalization;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ReadService
{
    private static IEnumerable<TimelineRow> ExpandRecurringRows(IReadOnlyList<TimelineRow> rows,
        DateTime? from, DateTime? to)
    {
        // Only stored dates establish the automatic domain. A recurrence's stop
        // date and generated instances never enlarge it.
        var dated = rows.Where(row => !row.Deleted && row.Date.Kind != StoryDateKind.Unknown).ToArray();
        var lower = from ?? dated.Select(row => row.Date.LowerBound ?? row.Date.UpperBound).Min();
        var upper = to ?? dated.Select(row => row.Date.UpperBound is { } end
            ? row.Date.UpperInclusive ? end : end.AddTicks(-1) : row.Date.LowerBound).Max();
        var generated = 0;
        foreach (var row in rows)
        {
            if (row.Recurrence is not { } repeat || row.Deleted || lower is null || upper is null)
            {
                yield return row;
                continue;
            }
            var first = DateOnly.FromDateTime(row.Date.LowerBound!.Value);
            var start = DateOnly.FromDateTime(lower.Value);
            var end = DateOnly.FromDateTime(upper.Value);
            if (repeat.Until is { } until)
            {
                var stop = DateOnly.ParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (stop < end) end = stop;
            }
            foreach (var date in RecurrenceDates(first, repeat, start, end))
            {
                if (++generated > 100_000)
                    throw new VaultValidationException([new("timeline.recurrence_too_broad", "from",
                        "This date window contains more than 100000 repeated occurrences. Narrow the date window or filters.")]);
                var occurrence = date != first;
                var warnings = row.Warnings;
                if (occurrence && row.BirthdayDeath is { Kind: not StoryDateKind.Unknown and not StoryDateKind.ExactDate and not StoryDateKind.ExactInstant } death &&
                    (death.LowerBound is null || date >= DateOnly.FromDateTime(death.LowerBound.Value)))
                    warnings = [.. warnings ?? [], "This birthday may be after death; the character's death date is uncertain."];
                yield return row with { Date = StoryDate.ExactDate(date, occurrence ? null : row.Date.OriginalText),
                    Title = occurrence && row.BirthdayDeath is not null ? row.Title[..^"birth".Length] + "birthday" : row.Title,
                    Warnings = warnings, IsOccurrence = occurrence, Discriminator = $"occurrence-{date:yyyy-MM-dd}" };
            }
        }
    }

    internal static IEnumerable<DateOnly> RecurrenceDates(DateOnly first, V4EventRecurrence recurrence,
        DateOnly from, DateOnly to)
    {
        if (recurrence.Interval < 1) yield break;
        var interval = recurrence.Interval;
        var step = recurrence.Frequency switch
        {
            V4RecurrenceFrequency.Daily => Math.Max(0, (from.DayNumber - first.DayNumber) / interval),
            V4RecurrenceFrequency.Weekly => Math.Max(0, (from.DayNumber - first.DayNumber) / (7 * interval)),
            V4RecurrenceFrequency.Monthly => Math.Max(0, ((from.Year - first.Year) * 12 + from.Month - first.Month) / interval),
            _ => Math.Max(0, (from.Year - first.Year) / interval)
        };
        for (; ; step++)
        {
            DateOnly candidate;
            try
            {
                candidate = recurrence.Frequency switch
                {
                    V4RecurrenceFrequency.Daily => first.AddDays(checked(step * interval)),
                    V4RecurrenceFrequency.Weekly => first.AddDays(checked(step * interval * 7)),
                    V4RecurrenceFrequency.Monthly => first.AddMonths(checked(step * interval)),
                    _ => first.AddYears(checked(step * interval))
                };
            }
            catch (ArgumentOutOfRangeException) { yield break; }
            catch (OverflowException) { yield break; }
            if (candidate > to || candidate == DateOnly.MaxValue) yield break;
            // January 31 does not become February 28; leap-day anniversaries
            // occur only when February 29 exists.
            if (recurrence.Frequency is V4RecurrenceFrequency.Monthly or V4RecurrenceFrequency.Yearly && candidate.Day != first.Day) continue;
            if (candidate >= from) yield return candidate;
        }
    }
}
