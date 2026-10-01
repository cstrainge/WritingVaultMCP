using System.Globalization;
using WritingVault.Client;

namespace WritingVault.Web;

internal sealed record ViewerSessionUpdateRequest(
    string? ContinuityName = null,
    string TimeAction = "Keep",
    DateTimeOffset? CurrentTime = null,
    string? CurrentLocalTime = null,
    DateOnly? CurrentLocalDate = null,
    string? ReferenceTimeZoneId = null,
    string AmbiguousTime = "Earlier")
{
    public VaultSessionUpdate ToVaultUpdate()
    {
        if (TimeAction.Equals("Keep", StringComparison.OrdinalIgnoreCase))
            return new(ContinuityName, "Keep");
        if (TimeAction.Equals("Clear", StringComparison.OrdinalIgnoreCase))
            return new(ContinuityName, "Clear");
        if (!TimeAction.Equals("Set", StringComparison.OrdinalIgnoreCase))
            throw Invalid("timeAction must be Keep, Set, or Clear.");
        if (string.IsNullOrWhiteSpace(ReferenceTimeZoneId))
            throw Invalid("A time zone is required when story time is set.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(ReferenceTimeZoneId); }
        catch (TimeZoneNotFoundException) { throw Invalid($"The time zone '{ReferenceTimeZoneId}' is not available on this computer."); }
        catch (InvalidTimeZoneException) { throw Invalid($"The time zone '{ReferenceTimeZoneId}' is invalid on this computer."); }
        if (CurrentTime is not null)
        {
            if (CurrentLocalDate is not null) throw Invalid("Set a date or a specific time, not both.");
            return new(ContinuityName, "Set", CurrentTime, ReferenceTimeZoneId);
        }
        if (CurrentLocalDate is { } date)
        {
            if (!string.IsNullOrWhiteSpace(CurrentLocalTime))
                throw Invalid("Set a date or a specific time, not both.");
            return new(ContinuityName, "Set", ReferenceTimeZoneId: ReferenceTimeZoneId,
                CurrentDate: date);
        }
        if (string.IsNullOrWhiteSpace(CurrentLocalTime) ||
            !DateTime.TryParse(CurrentLocalTime, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            throw Invalid("Enter a valid local story date and time.");

        var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw Invalid("That local time does not exist because the clock moves forward. Choose another time.");

        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
        {
            if (!AmbiguousTime.Equals("Earlier", StringComparison.OrdinalIgnoreCase) &&
                !AmbiguousTime.Equals("Later", StringComparison.OrdinalIgnoreCase))
                throw Invalid("ambiguousTime must be Earlier or Later.");
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            offset = AmbiguousTime.Equals("Earlier", StringComparison.OrdinalIgnoreCase) ? offsets.Max() : offsets.Min();
        }
        else offset = zone.GetUtcOffset(local);

        return new(ContinuityName, "Set", new DateTimeOffset(local, offset), ReferenceTimeZoneId);
    }

    private static ViewerRequestException Invalid(string message) => new(400, "session.time_invalid", message);
}
