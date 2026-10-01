using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Application.V4;

public sealed record V4ResolvedTarget(
    string ResourceType, int StorageKey, string Reference, string Label,
    int? ContinuityKey, bool IsDeleted);

public sealed record V4ResolutionCandidate(string ResourceType, string Reference, string Label, string? Context = null);

public sealed class V4ResolutionException(
    string code,
    string message,
    IReadOnlyList<V4ResolutionCandidate>? candidates = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<V4ResolutionCandidate> Candidates { get; } = candidates ?? [];
}

public interface IV4SemanticResolver
{
    Task<V4ResolvedTarget> ResolveAsync(
        string value, int? continuityKey, IReadOnlyCollection<string> allowedTypes,
        bool includeDeleted = false, CancellationToken cancellationToken = default);
}

/// <summary>Centralizes the public target categories used by v4 commands.</summary>
public sealed class V4TargetResolver(IV4SemanticResolver resolver)
{
    private static readonly string[] Entity = ["Entity"];
    private static readonly string[] NoteOwner = ["Continuity", "Entity"];
    private static readonly string[] Notes = ["ContinuityNote", "EntityNote"];
    private static readonly string[] Events = ["WorldEvent", "EntityEvent", "RelationshipEvent"];
    private static readonly string[] Projects = ["Project"];
    private static readonly string[] RelationshipTypes = ["RelationshipType"];
    private static readonly string[] OwnershipPrincipals = ["OwnershipPrincipal"];
    private static readonly string[] Tags = ["Tag"];
    private static readonly string[] Sources = ["Source"];
    private static readonly string[] Images = ["EntityImage", "StoryImage"];
    private static readonly string[] StoryImageOwners =
        ["Continuity", "CharacterRelationship", "EntityEvent", "RelationshipEvent"];
    private static readonly string[] ImageOwners =
        ["Entity", "Continuity", "CharacterRelationship", "EntityEvent", "RelationshipEvent"];
    private static readonly string[] Relations =
    [
        "CharacterRelationship", "CharacterResidence", "OrganizationMembership",
        "OrganizationLocation", "ObjectOwnershipPeriod", "ObjectCustodyPeriod",
        "ObjectLocationPeriod", "EntityEventProject", "CharacterTemporalEffect",
        "RelationshipParticipant", "RelationshipMembershipPeriod", "RelationshipEvent", "RelationshipEventProject"
    ];

    public Task<V4ResolvedTarget> EntityAsync(string value, int continuityKey, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Entity, cancellationToken: token);

    public Task<V4ResolvedTarget> EntityAsync(string value, int continuityKey, bool includeDeleted, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Entity, includeDeleted, token);

    public Task<V4ResolvedTarget> GlobalEntityAsync(string value, CancellationToken token = default) =>
        resolver.ResolveAsync(value, null, Entity, cancellationToken: token);

    public Task<V4ResolvedTarget> NoteOwnerAsync(string value, int continuityKey, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, NoteOwner, cancellationToken: token);

    public Task<V4ResolvedTarget> NoteAsync(string value, int continuityKey, bool includeDeleted = false, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Notes, includeDeleted, token);

    public Task<V4ResolvedTarget> EventAsync(string value, int continuityKey, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Events, cancellationToken: token);

    public Task<V4ResolvedTarget> ProjectAsync(string value, int continuityKey, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Projects, cancellationToken: token);

    public Task<V4ResolvedTarget> RelationshipTypeAsync(string value, CancellationToken token = default) =>
        resolver.ResolveAsync(value, null, RelationshipTypes, cancellationToken: token);

    public Task<V4ResolvedTarget> OwnershipPrincipalAsync(string value, int continuityKey, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, OwnershipPrincipals, cancellationToken: token);

    public Task<V4ResolvedTarget> TagAsync(string value, CancellationToken token = default) =>
        resolver.ResolveAsync(value, null, Tags, cancellationToken: token);

    public Task<V4ResolvedTarget> SourceAsync(string value, CancellationToken token = default) =>
        resolver.ResolveAsync(value, null, Sources, cancellationToken: token);

    public Task<V4ResolvedTarget> ImageAsync(string value, int continuityKey, bool includeDeleted = false, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Images, includeDeleted, token);

    // Inline note images are explicit semantic references. Their owner may be
    // in another continuity; natural-name lookup is intentionally unsupported.
    public Task<V4ResolvedTarget> GlobalImageAsync(string value, CancellationToken token = default) =>
        resolver.ResolveAsync(value, null, Images, cancellationToken: token);

    public Task<V4ResolvedTarget> StoryImageOwnerAsync(string value, int continuityKey, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, StoryImageOwners, cancellationToken: token);

    public Task<V4ResolvedTarget> StoryImageOwnerAsync(string value, int continuityKey, bool includeDeleted, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, StoryImageOwners, includeDeleted, token);

    public Task<V4ResolvedTarget> ImageOwnerAsync(string value, int continuityKey, bool includeDeleted = false, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, ImageOwners, includeDeleted, token);

    public Task<V4ResolvedTarget> RelationAsync(string value, int continuityKey, bool includeDeleted = false, CancellationToken token = default) =>
        resolver.ResolveAsync(value, continuityKey, Relations, includeDeleted, token);
}

public sealed record V4AddNoteCommand(
    string MutationToken, int ContinuityKey, string Target, string Body,
    string? Title = null, string Format = "markdown", string? ClientLabel = null);

public sealed record V4ApplyEventProjectsCommand(
    string MutationToken, int ContinuityKey, string Event, IReadOnlyList<string> Projects,
    bool Add, string? Role = null, string? Notes = null, string? ClientLabel = null);

public sealed record V4ApplyTagCommand(
    string MutationToken, int ContinuityKey, string Tag, IReadOnlyList<string> Targets,
    bool Add, bool CreateIfMissing = false, string? ClientLabel = null);

public sealed record V4CreateCharacterRelationshipCommand(
    string MutationToken, int ContinuityKey, string SourceCharacter, string TargetCharacter,
    string RelationshipType, StoryDate Period, string? Notes = null, string? ClientLabel = null);

public sealed record V4CreateRelationshipGroupCommand(
    string MutationToken, int ContinuityKey, IReadOnlyList<string> Characters,
    string RelationshipType, StoryDate? InitialPeriod, string? Notes = null, string? ClientLabel = null);

public sealed record V4RelationshipParticipantAddCommand(
    string MutationToken, int ContinuityKey, string Relationship, string Character,
    int ExpectedVersion, StoryDate? InitialPeriod = null, string? ClientLabel = null);

public sealed record V4RelationshipPeriodAddCommand(
    string MutationToken, int ContinuityKey, string Relationship, string Character,
    int ExpectedVersion, StoryDate? Period = null, string? Notes = null, string? ClientLabel = null,
    StoryDate? Joined = null, StoryDate? Left = null,
    string? JoinDescription = null, string? LeaveDescription = null);
public sealed record V4OrganizationMembershipAddCommand(
    string MutationToken, int ContinuityKey, string Organization, string Character,
    StoryDate? Period = null, string? Role = null, string? Notes = null,
    StoryDate? Joined = null, StoryDate? Left = null,
    string? JoinDescription = null, string? LeaveDescription = null,
    string? ClientLabel = null);

public sealed record V4OwnershipOwnerTarget(
    string Principal, int? SharePartsPerMillion = null, string? Notes = null);

public sealed record V4AddOwnershipCommand(
    string MutationToken, int ContinuityKey, string Object, OwnershipState State,
    StoryDate Period, IReadOnlyList<V4OwnershipOwnerTarget> Owners,
    string? Notes = null, string? ClientLabel = null);

public sealed record V4StoryDateValue(
    string? Compact = null,
    StoryDateKind? Kind = null,
    string? Value = null,
    string? Lower = null,
    string? Upper = null,
    bool? LowerInclusive = null,
    bool? UpperInclusive = null,
    string? OriginalText = null,
    string CalendarId = "Gregorian");

public static class V4StoryDateParser
{
    private const DateTimeStyles DateStyles = DateTimeStyles.AllowWhiteSpaces;
    private static readonly Regex InstantShape = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\\.[0-9]{1,7})?)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static StoryDate Parse(V4StoryDateValue input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var calendar = TextNormalization.Required(input.CalendarId, 50, nameof(input.CalendarId));
        if (!calendar.Equals("Gregorian", StringComparison.OrdinalIgnoreCase))
            throw Error("date.calendar_unsupported", "calendarId", "Only Gregorian is supported in v4.");
        calendar = "Gregorian";
        if (input.OriginalText?.Length > 4_000)
            throw Error("date.original_text_too_long", "originalText", "originalText cannot exceed 4,000 characters.");

        StoryDate result;
        if (input.Compact is not null)
        {
            if (input.Kind is not null || input.Value is not null || input.Lower is not null || input.Upper is not null ||
                input.LowerInclusive is not null || input.UpperInclusive is not null)
                throw Error("date.shape", "date", "A compact date cannot be combined with rich date fields.");
            result = StoryDate.ExactDate(ParseDate(input.Compact, "date"), input.OriginalText) with { CalendarId = calendar };
        }
        else
        {
            var kind = input.Kind ?? throw Error("date.kind_required", "kind", "kind is required for a rich date.");
            result = kind switch
            {
                StoryDateKind.Unknown => NoValue(input, StoryDate.Unknown(input.OriginalText), calendar),
                StoryDateKind.ExactDate => ExactDate(input) with { CalendarId = calendar },
                StoryDateKind.ExactInstant => ExactInstant(input) with { CalendarId = calendar },
                StoryDateKind.Month => Month(input, calendar),
                StoryDateKind.Year => Year(input, calendar),
                StoryDateKind.Circa => Bounded(input, StoryDateKind.Circa, calendar),
                StoryDateKind.Range => throw Error("date.range_meaning_required", "kind", "Use KnownRange for a known interval or UncertainRange for a bounded uncertainty window; legacy Range is read-only."),
                StoryDateKind.KnownRange => Bounded(input, StoryDateKind.KnownRange, calendar),
                StoryDateKind.UncertainRange => Bounded(input, StoryDateKind.UncertainRange, calendar),
                StoryDateKind.Before => Before(input, calendar),
                StoryDateKind.After => After(input, calendar),
                _ => throw Error("date.kind_invalid", "kind", "The story-date kind is not supported.")
            };
        }

        var issues = result.Validate();
        if (issues.Count > 0) throw Error("date.invalid", "date", string.Join(" ", issues));
        return result;
    }

    private static StoryDate ExactDate(V4StoryDateValue input)
    {
        NoBounds(input);
        return StoryDate.ExactDate(ParseDate(Required(input.Value, "value"), "value"), input.OriginalText);
    }

    private static StoryDate ExactInstant(V4StoryDateValue input)
    {
        NoBounds(input);
        return StoryDate.ExactInstant(ParseInstant(Required(input.Value, "value"), "value"), input.OriginalText);
    }

    private static StoryDate Month(V4StoryDateValue input, string calendar)
    {
        NoBounds(input);
        if (!DateTime.TryParseExact(Required(input.Value, "value") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateStyles, out var start) || start.Year < 100 || start.Year >= 9999)
            throw Error("date.month_invalid", "value", "Month must use YYYY-MM in the supported 0100-01 through 9998-12 range.");
        return new(StoryDateKind.Month, Unspecified(start), Unspecified(start.AddMonths(1)), true, false, input.OriginalText, calendar);
    }

    private static StoryDate Year(V4StoryDateValue input, string calendar)
    {
        NoBounds(input);
        var value = Required(input.Value, "value");
        if (value.Length != 4 || !value.All(char.IsAsciiDigit) ||
            (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 100 or >= 9999))
            throw Error("date.year_invalid", "value", "Year must use four decimal digits.");
        return StoryDate.Year(year, input.OriginalText) with { CalendarId = calendar };
    }

    private static StoryDate Bounded(V4StoryDateValue input, StoryDateKind kind, string calendar)
    {
        if (input.Value is not null) throw Error("date.shape", "value", $"{kind} uses lower and upper, not value.");
        var lower = ParseBoundary(Required(input.Lower, "lower"), "lower");
        var upper = ParseBoundary(Required(input.Upper, "upper"), "upper");
        return new(kind, lower, upper, input.LowerInclusive ?? true, input.UpperInclusive ?? false, input.OriginalText, calendar);
    }

    private static StoryDate Before(V4StoryDateValue input, string calendar)
    {
        if (input.Value is not null || input.Lower is not null || input.LowerInclusive is not null)
            throw Error("date.shape", "date", "Before accepts only upper.");
        return new(StoryDateKind.Before, null, ParseBoundary(Required(input.Upper, "upper"), "upper"),
            input.LowerInclusive ?? true, input.UpperInclusive ?? false, input.OriginalText, calendar);
    }

    private static StoryDate After(V4StoryDateValue input, string calendar)
    {
        if (input.Value is not null || input.Upper is not null || input.UpperInclusive is not null)
            throw Error("date.shape", "date", "After accepts only lower.");
        return new(StoryDateKind.After, ParseBoundary(Required(input.Lower, "lower"), "lower"), null,
            input.LowerInclusive ?? true, input.UpperInclusive ?? false, input.OriginalText, calendar);
    }

    private static StoryDate NoValue(V4StoryDateValue input, StoryDate result, string calendar)
    {
        if (input.Value is not null || input.Lower is not null || input.Upper is not null ||
            input.LowerInclusive is not null || input.UpperInclusive is not null)
            throw Error("date.shape", "date", "Unknown cannot contain normalized values or bounds.");
        return result with { CalendarId = calendar };
    }

    private static void NoBounds(V4StoryDateValue input)
    {
        if (input.Lower is not null || input.Upper is not null ||
            input.LowerInclusive is not null || input.UpperInclusive is not null)
            throw Error("date.shape", "date", "This precision uses value rather than lower or upper.");
    }

    private static DateOnly ParseDate(string value, string field)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Error("date.exact_invalid", field, "Exact dates must use YYYY-MM-DD and name a real date.");
        if (date.Year < 100 || date == DateOnly.MaxValue)
            throw Error("date.exact_invalid", field, "Exact dates must be between 0100-01-01 and 9999-12-30.");
        return date;
    }

    private static DateTime ParseInstant(string value, string field)
    {
        if (value.EndsWith('Z') || value.LastIndexOf('+') > 9 || value.LastIndexOf('-') > 9)
            throw Error("date.timezone_forbidden", field, "Fictional instants are timezone-free.");
        if (!InstantShape.IsMatch(value))
            throw Error("date.instant_invalid", field, "Fictional instants must use YYYY-MM-DDThh:mm, optional seconds, and at most seven fractional digits.");
        var formats = new[]
        {
            "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
        };
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ||
            instant.Year < 100)
            throw Error("date.instant_invalid", field, "The fictional instant is invalid.");
        return Unspecified(instant);
    }

    private static DateTime ParseBoundary(string value, string field) =>
        value.Length == 10 ? ParseDate(value, field).ToDateTime(TimeOnly.MinValue) : ParseInstant(value, field);

    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw Error("date.value_required", field, $"{field} is required.") : value.Trim();
    private static VaultValidationException Error(string code, string field, string message) =>
        new([new ValidationError(code, field, message)]);
}

public static class V4SparseChangeValidator
{
    public static IReadOnlyDictionary<string, JsonElement> Validate(
        IReadOnlyDictionary<string, JsonElement> changes,
        IReadOnlySet<string> allowedFields)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(allowedFields);
        if (changes.Count == 0) throw new VaultValidationException([new("changes.empty", "changes", "At least one change is required.")]);
        var unknown = changes.Keys.Where(field => !allowedFields.Contains(field)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new VaultValidationException(unknown.Select(field => new ValidationError("changes.field_unknown", $"changes.{field}", "This field cannot be changed by this operation.")));
        return new Dictionary<string, JsonElement>(changes, StringComparer.Ordinal);
    }
}
