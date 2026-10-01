namespace WritingVaultMcp.Domain;

public enum CharacterAgeStatus
{
    TimelineUnset,
    BirthDateUnknown,
    NotYetBorn,
    PossiblyNotYetBorn,
    Alive,
    Deceased,
    PossiblyDeceased,
    InvalidChronology
}

public enum StoryDateKind
{
    Unknown,
    ExactInstant,
    ExactDate,
    Month,
    Year,
    Circa,
    Before,
    After,
    Range, // Legacy stored value: meaning was not recorded.
    KnownRange,
    UncertainRange
}

/// <summary>A timezone-free fictional date or interval. Bounds always use DateTimeKind.Unspecified.</summary>
public sealed record StoryDate(
    StoryDateKind Kind,
    DateTime? LowerBound,
    DateTime? UpperBound,
    bool LowerInclusive = true,
    bool UpperInclusive = false,
    string? OriginalText = null,
    string CalendarId = "Gregorian")
{
    public static readonly DateTime AccessMinimum = new(100, 1, 1);
    public static readonly DateTime AccessMaximum = DateTime.MaxValue;

    public static StoryDate Unknown(string? text = null) =>
        new(StoryDateKind.Unknown, null, null, OriginalText: text);

    public static StoryDate ExactDate(DateOnly date, string? text = null) =>
        date.Year < AccessMinimum.Year || date == DateOnly.MaxValue
            ? throw new ArgumentOutOfRangeException(nameof(date), "Exact dates must be between 0100-01-01 and 9999-12-30.")
            : new(StoryDateKind.ExactDate, date.ToDateTime(TimeOnly.MinValue), date.AddDays(1).ToDateTime(TimeOnly.MinValue), true, false, text);

    public static StoryDate ExactInstant(DateTime value, string? text = null) =>
        value.Year < AccessMinimum.Year
            ? throw new ArgumentOutOfRangeException(nameof(value), "Exact instants must be in Access's supported year range 0100-9999.")
            : new(StoryDateKind.ExactInstant, DateTime.SpecifyKind(value, DateTimeKind.Unspecified), DateTime.SpecifyKind(value, DateTimeKind.Unspecified), true, true, text);

    public static StoryDate Year(int year, string? text = null) =>
        year is < 100 or >= 9999
            ? throw new ArgumentOutOfRangeException(nameof(year), "Year precision requires a year from 0100 through 9998.")
            : new(StoryDateKind.Year, new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1), true, false, text);

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(CalendarId)) issues.Add("CalendarId is required.");
        else if (CalendarId.Length > 50) issues.Add("CalendarId cannot exceed 50 characters.");
        if (LowerBound?.Kind is not null and not DateTimeKind.Unspecified) issues.Add("LowerBound must be timezone-free.");
        if (UpperBound?.Kind is not null and not DateTimeKind.Unspecified) issues.Add("UpperBound must be timezone-free.");
        if (LowerBound is { } supportedLower && supportedLower < AccessMinimum) issues.Add("LowerBound is outside Access's supported date range.");
        if (UpperBound is { } supportedUpper && supportedUpper < AccessMinimum) issues.Add("UpperBound is outside Access's supported date range.");
        if (LowerBound is { } lower && UpperBound is { } upper && lower > upper) issues.Add("LowerBound must not exceed UpperBound.");
        if (Kind == StoryDateKind.Unknown && (LowerBound is not null || UpperBound is not null)) issues.Add("Unknown dates cannot have normalized bounds.");
        if (Kind == StoryDateKind.ExactInstant && (LowerBound is null || UpperBound is null || LowerBound != UpperBound || !LowerInclusive || !UpperInclusive))
            issues.Add("ExactInstant requires one identical inclusive lower and upper bound.");
        if (Kind is StoryDateKind.ExactDate or StoryDateKind.Month or StoryDateKind.Year or StoryDateKind.Circa or StoryDateKind.Range or StoryDateKind.KnownRange or StoryDateKind.UncertainRange &&
            (LowerBound is null || UpperBound is null)) issues.Add($"{Kind} requires both bounds.");
        if (Kind is StoryDateKind.ExactDate or StoryDateKind.Month or StoryDateKind.Year or StoryDateKind.Circa or StoryDateKind.Range or StoryDateKind.KnownRange or StoryDateKind.UncertainRange &&
            LowerBound is { } rangedLower && UpperBound is { } rangedUpper && rangedLower >= rangedUpper)
            issues.Add($"{Kind} requires a non-empty interval.");
        if (Kind == StoryDateKind.ExactDate && LowerBound is { } dateLower && UpperBound is { } dateUpper &&
            (dateLower.TimeOfDay != TimeSpan.Zero || dateUpper != dateLower.AddDays(1) || !LowerInclusive || UpperInclusive))
            issues.Add("ExactDate requires one half-open calendar day at midnight.");
        if (Kind == StoryDateKind.Month && LowerBound is { } monthLower && UpperBound is { } monthUpper &&
            (monthLower.TimeOfDay != TimeSpan.Zero || monthLower.Day != 1 || monthUpper != monthLower.AddMonths(1) || !LowerInclusive || UpperInclusive))
            issues.Add("Month requires one half-open calendar month.");
        if (Kind == StoryDateKind.Year && LowerBound is { } yearLower && UpperBound is { } yearUpper &&
            (yearLower.TimeOfDay != TimeSpan.Zero || yearLower.Month != 1 || yearLower.Day != 1 || yearUpper != yearLower.AddYears(1) || !LowerInclusive || UpperInclusive))
            issues.Add("Year requires one half-open calendar year.");
        if (Kind == StoryDateKind.Before && (LowerBound is not null || UpperBound is null)) issues.Add("Before requires only an upper bound.");
        if (Kind == StoryDateKind.After && (LowerBound is null || UpperBound is not null)) issues.Add("After requires only a lower bound.");
        return issues;
    }

    public static int CompareForSort(StoryDate? left, StoryDate? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null || left.Kind == StoryDateKind.Unknown) return right is null || right.Kind == StoryDateKind.Unknown ? 0 : 1;
        if (right is null || right.Kind == StoryDateKind.Unknown) return -1;
        var lower = Nullable.Compare(left.LowerBound, right.LowerBound);
        return lower != 0 ? lower : Nullable.Compare(left.UpperBound, right.UpperBound);
    }

    public bool Overlaps(StoryDate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        static bool BeginsBeforeEnd(DateTime? begin, bool beginInclusive, DateTime? end, bool endInclusive)
        {
            if (begin is null || end is null) return true;
            return begin < end || (begin == end && beginInclusive && endInclusive);
        }
        return BeginsBeforeEnd(LowerBound, LowerInclusive, other.UpperBound, other.UpperInclusive) &&
               BeginsBeforeEnd(other.LowerBound, other.LowerInclusive, UpperBound, UpperInclusive);
    }

    public bool Contains(DateTime value)
    {
        var afterLower = LowerBound is null || value > LowerBound || (value == LowerBound && LowerInclusive);
        var beforeUpper = UpperBound is null || value < UpperBound || (value == UpperBound && UpperInclusive);
        return afterLower && beforeUpper;
    }
}

public sealed record StoryAge(int? ExactYears, int? MinimumYears, int? MaximumYears, CharacterAgeStatus Status)
{
    public static StoryAge Calculate(StoryDate birth, DateOnly asOf, StoryDate? death = null)
    {
        ArgumentNullException.ThrowIfNull(birth);
        var validation = birth.Validate();
        if (validation.Count > 0 || birth.Kind == StoryDateKind.Unknown)
            return new(null, null, null, CharacterAgeStatus.BirthDateUnknown);

        var earliest = birth.LowerBound is { } lower ? DateOnly.FromDateTime(lower) : (DateOnly?)null;
        var latest = birth.UpperBound is { } upper
            ? DateOnly.FromDateTime(birth.UpperInclusive ? upper : upper.AddTicks(-1))
            : (DateOnly?)null;
        if (earliest is null || latest is null) return new(null, null, null, CharacterAgeStatus.BirthDateUnknown);

        var endpointMinimum = asOf;
        var endpointMaximum = asOf;
        var status = CharacterAgeStatus.Alive;
        if (death is not null && death.Kind != StoryDateKind.Unknown)
        {
            if (death.Validate().Count > 0 || death.LowerBound is null || death.UpperBound is null)
                return new(null, null, null, CharacterAgeStatus.InvalidChronology);
            var deathEarliest = DateOnly.FromDateTime(death.LowerBound.Value);
            var deathLatest = DateOnly.FromDateTime(death.UpperInclusive ? death.UpperBound.Value : death.UpperBound.Value.AddTicks(-1));
            if (deathLatest < earliest) return new(null, null, null, CharacterAgeStatus.InvalidChronology);
            if (deathLatest <= asOf)
            {
                endpointMinimum = deathEarliest;
                endpointMaximum = deathLatest;
                status = CharacterAgeStatus.Deceased;
            }
            else if (deathEarliest <= asOf)
            {
                endpointMinimum = deathEarliest;
                endpointMaximum = asOf;
                status = CharacterAgeStatus.PossiblyDeceased;
            }
        }

        if (earliest > endpointMaximum) return new(null, null, null, CharacterAgeStatus.NotYetBorn);
        if (latest > endpointMaximum) status = CharacterAgeStatus.PossiblyNotYetBorn;

        static int Years(DateOnly from, DateOnly to)
        {
            var years = to.Year - from.Year;
            // The accepted policy treats Feb 28 as the birthday in non-leap years.
            var anniversary = from.Month == 2 && from.Day == 29 && !DateTime.IsLeapYear(to.Year)
                ? new DateOnly(to.Year, 2, 28)
                : from.AddYears(years);
            return anniversary > to ? years - 1 : years;
        }

        var minimum = latest > endpointMinimum ? 0 : Years(latest.Value, endpointMinimum);
        var maximum = Math.Max(0, Years(earliest.Value, endpointMaximum));
        return minimum == maximum
            ? new StoryAge(minimum, minimum, maximum, status)
            : new StoryAge(null, minimum, maximum, status);
    }
}
