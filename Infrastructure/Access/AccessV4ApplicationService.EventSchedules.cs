using System.Data.OleDb;
using System.Globalization;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ApplicationService
{
    public async Task<VaultMutationResult> UpdateEventAsync(V4EventUpdateRequest request,
        int continuity, string? clientLabel, CancellationToken cancellationToken = default)
    {
        if (request.Title is null && request.Description is null && request.Occurred is null &&
            request.WorldEvent is null && request.NarrativeOrder is null && request.ProjectBoundary is null && request.Recurrence is null &&
            !request.ClearDescription && !request.ClearWorldEvent && !request.ClearNarrativeOrder && !request.ClearProjectBoundary && !request.ClearRecurrence)
            return new(false, "patch.empty", Message: "Specify at least one event field or clear flag.");
        V4ResolvedTarget target;
        V4ResolvedTarget? world = null;
        StoryDate? changedDate = null;
        string operation;
        try
        {
            target = await targets.EventAsync(request.Event, continuity, cancellationToken);
            if (request.WorldEvent is not null)
            {
                world = await targets.EntityAsync(request.WorldEvent, continuity, cancellationToken);
                if (world.ResourceType != "WorldEvent")
                    throw new V4ResolutionException("reference.type_invalid", "worldEvent must identify a WorldEvent.");
            }
            if (request.Occurred is { } date)
                changedDate = V4StoryDateParser.Parse(new(Kind: Enum.Parse<StoryDateKind>(date.Kind?.ToString() ?? "Unknown"),
                    Value: date.Value, Lower: date.Lower, Upper: date.Upper,
                    LowerInclusive: date.LowerInclusive, UpperInclusive: date.UpperInclusive,
                    OriginalText: date.OriginalText, CalendarId: date.CalendarId ?? "Gregorian"));
            operation = references.OperationId(request.MutationToken);
            if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title) ||
                request.NarrativeOrder is { } order && !double.IsFinite(order) || request.ExpectedVersion < 1)
                throw new ArgumentException("Supply a nonempty title, finite narrative order, and positive expectedVersion.");
            if (request.ClearDescription && request.Description is not null || request.ClearWorldEvent && request.WorldEvent is not null ||
                request.ClearNarrativeOrder && request.NarrativeOrder is not null || request.ClearProjectBoundary && request.ProjectBoundary is not null ||
                request.ClearRecurrence && request.Recurrence is not null)
                throw new ArgumentException("Do not set and clear the same field in one request.");
            if (target.ResourceType == "WorldEvent" && (request.WorldEvent is not null || request.ClearWorldEvent))
                throw new ArgumentException("Only entity and relationship events can link to a world event.");
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        catch (Exception exception) when (exception is ArgumentException or VaultValidationException)
        { return new(false, "validation.event", Message: exception.Message); }

        return await writes.ExecuteAsync(operation, "v4.event.update", request, "event_update", clientLabel,
            async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, continuity, token);
                var (table, key, join, owner) = target.ResourceType switch
                {
                    "WorldEvent" => ("WorldEvents", "EntityId", "[CanonEntities] AS c ON e.[EntityId]=c.[Id]", "c.[EntityType]"),
                    "EntityEvent" => ("EntityEvents", "Id", "[CanonEntities] AS c ON e.[EntityId]=c.[Id]", "c.[EntityType]"),
                    _ => ("RelationshipEvents", "Id", "[CharacterRelationships] AS c ON e.[RelationshipId]=c.[Id]", "'Relationship'")
                };
                var versionField = target.ResourceType == "WorldEvent" ? "c.[Version]" : "e.[Version]";
                var deleted = target.ResourceType == "WorldEvent" ? "c.[IsDeleted]" : "(e.[IsDeleted] OR c.[IsDeleted])";
                using var read = context.Command($"SELECT e.*,{versionField} AS CurrentVersion,{deleted} AS Unavailable,{owner} AS OwnerType FROM [{table}] AS e INNER JOIN {join} WHERE e.[{key}]=? AND c.[ContinuityId]=?")
                    .Add(OleDbType.Integer, target.StorageKey).Add(OleDbType.Integer, continuity);
                var records = await read.QueryAsync(r =>
                {
                    string? S(string name) => r[name] is DBNull ? null : Convert.ToString(r[name], CultureInfo.InvariantCulture);
                    var stored = new StoryDate(Enum.Parse<StoryDateKind>(S("EventKind")!),
                        r["EventLowerBound"] is DBNull ? null : (DateTime)r["EventLowerBound"],
                        r["EventUpperBound"] is DBNull ? null : (DateTime)r["EventUpperBound"],
                        (bool)r["EventLowerInclusive"], (bool)r["EventUpperInclusive"], S("EventOriginalText"), S("EventCalendarId")!);
                    var repeat = S("RecurrenceFrequency") is { } frequency
                        ? new V4EventRecurrence(Enum.Parse<V4RecurrenceFrequency>(frequency), (int)r["RecurrenceInterval"],
                            r["RecurrenceUntil"] is DBNull ? null : ((DateTime)r["RecurrenceUntil"]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null;
                    return new { Version = (int)r["CurrentVersion"], Deleted = Convert.ToBoolean(r["Unavailable"], CultureInfo.InvariantCulture), Owner = S("OwnerType"),
                        Title = S("Title"), Description = S("Description"), Date = stored, Recurrence = repeat,
                        Narrative = r["NarrativeOrder"] is DBNull ? (double?)null : (double)r["NarrativeOrder"],
                        World = target.ResourceType == "WorldEvent" || r["WorldEventId"] is DBNull ? (int?)null : (int)r["WorldEventId"],
                        Boundary = target.ResourceType == "EntityEvent" && S("ProjectBoundary") is { } role ? Enum.Parse<V4ProjectBoundary>(role) : (V4ProjectBoundary?)null };
                }, token);
                if (records.Count != 1 || records[0].Deleted) throw new VaultCommandException("record.not_found", "The event or its owner is unavailable.");
                var current = records[0];
                if (current.Version != request.ExpectedVersion)
                    throw new VaultCommandException("concurrency.conflict", "The event changed; read it again before retrying.", target.ResourceType, target.StorageKey.ToString(), current.Version);
                var boundary = request.ClearProjectBoundary ? null : request.ProjectBoundary ?? current.Boundary;
                if ((boundary is not null || request.ClearProjectBoundary) && current.Owner != "Project")
                    throw new VaultCommandException("event.project_boundary", "Only project-owned events can define story boundaries.");
                var occurred = changedDate ?? current.Date;
                var recurrence = request.ClearRecurrence ? null : request.Recurrence ?? current.Recurrence;
                ValidateRecurrence(occurred, recurrence, boundary);
                if (world is not null) await RequireCanonEntityAsync(context, world.StorageKey, continuity, "WorldEvent", token);
                using var update = context.Command($"UPDATE [{table}] SET [Title]=?,[Description]=?,[NarrativeOrder]=?,[EventKind]=?,[EventLowerBound]=?,[EventUpperBound]=?,[EventLowerInclusive]=?,[EventUpperInclusive]=?,[EventOriginalText]=?,[EventCalendarId]=?" +
                    (target.ResourceType != "WorldEvent" ? ",[WorldEventId]=?" : "") +
                    (target.ResourceType == "EntityEvent" ? ",[ProjectBoundary]=?" : "") + $" WHERE [{key}]=?")
                    .Add(OleDbType.VarWChar, request.Title?.Trim() ?? current.Title, 255)
                    .Add(OleDbType.LongVarWChar, request.ClearDescription ? null : request.Description ?? current.Description)
                    .Add(OleDbType.Double, request.ClearNarrativeOrder ? null : request.NarrativeOrder ?? current.Narrative);
                AddDate(update, occurred);
                if (target.ResourceType != "WorldEvent") update.Add(OleDbType.Integer, request.ClearWorldEvent ? null : world?.StorageKey ?? current.World);
                if (target.ResourceType == "EntityEvent") update.Add(OleDbType.VarWChar, boundary?.ToString(), 20);
                update.Add(OleDbType.Integer, target.StorageKey);
                await update.ExecuteNonQueryAsync(token);
                await WriteRecurrenceAsync(context, table, key, target.StorageKey, recurrence, token);
                var versionTable = target.ResourceType == "WorldEvent" ? "CanonEntities" : table;
                using var bump = context.Command($"UPDATE [{versionTable}] SET [Version]=[Version]+1,[UpdatedAtUtc]=? WHERE [Id]=?")
                    .Add(OleDbType.Date, DateTime.UtcNow).Add(OleDbType.Integer, target.StorageKey);
                await bump.ExecuteNonQueryAsync(token);
                return new VaultMutationOutcome(target.ResourceType, target.StorageKey.ToString(), current.Version + 1, "update", request, current.Version);
            }, cancellationToken);
    }

    private static void ValidateRecurrence(StoryDate date, V4EventRecurrence? recurrence,
        V4ProjectBoundary? boundary = null)
    {
        if (recurrence is null) return;
        if (boundary is not null || date.Kind != StoryDateKind.ExactDate || date.CalendarId != "Gregorian")
            throw new VaultCommandException("event.recurrence_date", "Only exact Gregorian single-day events can recur; project boundaries cannot repeat.");
        if (!Enum.IsDefined(recurrence.Frequency) || recurrence.Interval is < 1 or > 10000)
            throw new VaultCommandException("event.recurrence_interval", "Repeat interval must be between 1 and 10000.");
        if (recurrence.Until is { } until && (!DateOnly.TryParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stop) || stop < DateOnly.FromDateTime(date.LowerBound!.Value)))
            throw new VaultCommandException("event.recurrence_until", "The inclusive stop date must be YYYY-MM-DD on or after the first occurrence.");
    }

    private static async Task WriteRecurrenceAsync(VaultWriteContext context, string table, string key,
        int id, V4EventRecurrence? recurrence, CancellationToken token)
    {
        using var command = context.Command($"UPDATE [{table}] SET [RecurrenceFrequency]=?,[RecurrenceInterval]=?,[RecurrenceUntil]=? WHERE [{key}]=?")
            .Add(OleDbType.VarWChar, recurrence?.Frequency.ToString(), 10)
            .Add(OleDbType.Integer, recurrence?.Interval)
            .Add(OleDbType.Date, recurrence?.Until is { } until ? DateOnly.ParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue) : null)
            .Add(OleDbType.Integer, id);
        await command.ExecuteNonQueryAsync(token);
    }
}
