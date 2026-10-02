namespace WritingVaultMcp.Domain;

public enum CanonEntityType { Project, Location, Character, Organization, Object, WorldEvent, Species }
public enum OwnershipState { Owned, Unknown, Unowned }
public enum CustodyState { Known, Unknown, Unowned }
public enum PrincipalKind { Character, Organization, External }

public sealed record Continuity(
    int Id, string Name, string Description, string DefaultTimeZoneId,
    int Version, bool IsDeleted, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record ContinuityClock(
    int ContinuityId, DateTime? CurrentInstantUtc, string? ReferenceTimeZoneId, int Version, DateTime UpdatedAtUtc);

public sealed record CanonEntityHeader(
    int Id, int ContinuityId, CanonEntityType EntityType, int? VariantGroupId,
    int Version, bool IsDeleted, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record OwnershipPrincipal(
    int Id, int ContinuityId, PrincipalKind Kind, int? CharacterId, int? OrganizationId, string? Label, int Version);

public sealed record OwnershipShare(int PrincipalId, int? PartsPerMillion, string? Notes);

public sealed record OwnershipPeriod(
    int Id, int ObjectId, OwnershipState State, StoryDate Period,
    IReadOnlyList<OwnershipShare> Owners, string? Notes, int Version);

public sealed record LocalCurrentTime(
    int ContinuityId,
    DateTimeOffset Instant,
    string TimeZoneId,
    string TimeZoneSource,
    int? LocationId,
    int ClockVersion);

public static class ZonedClock
{
    public static LocalCurrentTime Resolve(
        Continuity continuity,
        ContinuityClock clock,
        string? entityTimeZoneId,
        int? sourceLocationId,
        string? sourceDescription = null)
    {
        if (clock.CurrentInstantUtc is null)
            throw new InvalidOperationException("Artificial current time is not set for this continuity.");
        var zoneId = entityTimeZoneId ?? clock.ReferenceTimeZoneId ?? continuity.DefaultTimeZoneId;
        if (string.IsNullOrWhiteSpace(zoneId)) throw new InvalidOperationException("No timezone is configured.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var utc = DateTime.SpecifyKind(clock.CurrentInstantUtc.Value, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), zone);
        return new LocalCurrentTime(
            continuity.Id,
            local,
            zone.Id,
            sourceDescription ?? (entityTimeZoneId is null ? "continuity-default" : "entity-location"),
            sourceLocationId,
            clock.Version);
    }
}
