using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

public sealed class StoryDateTests
{
    [Fact]
    public void YearRoundTripsWithoutInventingMonthOrDay()
    {
        var value = StoryDate.Year(2001, "in 2001");
        Assert.Equal(StoryDateKind.Year, value.Kind);
        Assert.Equal(new DateTime(2001, 1, 1), value.LowerBound);
        Assert.Equal(new DateTime(2002, 1, 1), value.UpperBound);
        Assert.Empty(value.Validate());
    }

    [Fact]
    public void ImpreciseBirthReturnsBoundedAge()
    {
        var age = StoryAge.Calculate(StoryDate.Year(2000), new DateOnly(2025, 6, 1));
        Assert.Null(age.ExactYears);
        Assert.Equal(24, age.MinimumYears);
        Assert.Equal(25, age.MaximumYears);
    }

    [Fact]
    public void LeapDayAdvancesOnFebruary28()
    {
        var age = StoryAge.Calculate(StoryDate.ExactDate(new DateOnly(2000, 2, 29)), new DateOnly(2025, 2, 28));
        Assert.Equal(25, age.ExactYears);
    }

    [Fact]
    public void ReversedRangeIsInvalid()
    {
        var value = new StoryDate(StoryDateKind.Range, new DateTime(2030, 1, 1), new DateTime(2020, 1, 1));
        Assert.NotEmpty(value.Validate());
    }

    [Fact]
    public void IntervalBoundariesHonorInclusivity()
    {
        var boundary = new DateTime(2010, 1, 1);
        var leftOpen = new StoryDate(StoryDateKind.Range, new DateTime(2000, 1, 1), boundary, true, false);
        var right = new StoryDate(StoryDateKind.Range, boundary, new DateTime(2020, 1, 1), true, false);
        Assert.False(leftOpen.Overlaps(right));
        Assert.False(leftOpen.Contains(boundary));
        Assert.True(right.Contains(boundary));

        var leftClosed = leftOpen with { UpperInclusive = true };
        Assert.True(leftClosed.Overlaps(right));
        Assert.True(leftClosed.Contains(boundary));
    }

    [Fact]
    public void PrecisionAndAccessRangeAreValidated()
    {
        var malformedDay = new StoryDate(
            StoryDateKind.ExactDate, new DateTime(2000, 1, 1), new DateTime(2000, 1, 3), true, false);
        Assert.Contains(malformedDay.Validate(), issue => issue.StartsWith("ExactDate", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryDate.Year(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoryDate.ExactDate(DateOnly.MaxValue));
    }

    [Fact]
    public void EverySupportedPrecisionValidatesAndHasDeterministicSortOrder()
    {
        var values = new[]
        {
            StoryDate.ExactInstant(new DateTime(2000, 1, 1, 12, 30, 0), "at noon"),
            StoryDate.ExactDate(new DateOnly(2000, 1, 2), "the next day"),
            new StoryDate(StoryDateKind.Month, new DateTime(2000, 2, 1), new DateTime(2000, 3, 1), OriginalText: "February 2000"),
            StoryDate.Year(2001, "in 2001"),
            new StoryDate(StoryDateKind.Circa, new DateTime(2001, 1, 1), new DateTime(2003, 1, 1), OriginalText: "circa 2002"),
            new StoryDate(StoryDateKind.Before, null, new DateTime(2004, 1, 1), OriginalText: "before 2004"),
            new StoryDate(StoryDateKind.After, new DateTime(2005, 1, 1), null, OriginalText: "after 2005"),
            new StoryDate(StoryDateKind.Range, new DateTime(2006, 1, 1), new DateTime(2007, 1, 1), OriginalText: "2006 to 2007"),
            StoryDate.Unknown("sometime later")
        };

        Assert.All(values, value => Assert.Empty(value.Validate()));
        Assert.True(StoryDate.CompareForSort(values[0], values[1]) < 0);
        Assert.True(StoryDate.CompareForSort(values[^2], values[^1]) < 0);
        Assert.Equal(0, StoryDate.CompareForSort(StoryDate.Unknown(), null));
    }

    [Fact]
    public void ImpreciseDeathAndFutureBirthReturnHonestStatuses()
    {
        var death = StoryAge.Calculate(
            StoryDate.ExactDate(new DateOnly(2000, 12, 31)),
            new DateOnly(2030, 1, 1),
            StoryDate.Year(2020));
        Assert.Equal(CharacterAgeStatus.Deceased, death.Status);
        Assert.Equal(19, death.MinimumYears);
        Assert.Equal(20, death.MaximumYears);

        var possibleFuture = StoryAge.Calculate(
            new StoryDate(StoryDateKind.Range, new DateTime(2029, 1, 1), new DateTime(2031, 1, 1), true, false),
            new DateOnly(2030, 1, 1));
        Assert.Equal(CharacterAgeStatus.PossiblyNotYetBorn, possibleFuture.Status);
        Assert.Equal(0, possibleFuture.MinimumYears);
    }
}
