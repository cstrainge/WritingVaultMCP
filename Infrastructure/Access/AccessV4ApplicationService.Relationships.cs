using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ApplicationService
{
    public async Task<VaultMutationResult> RecordRelationshipEventAsync(
        V4RelationshipEventAddRequest request, int continuity, string? clientLabel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 255)
            return new(false, "validation.title", Message: "An event title is required and cannot exceed 255 characters.");
        if (request.Description?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.NarrativeOrder is { } order && !double.IsFinite(order))
            return new(false, "validation.event", Message: "The event description or narrative order is invalid.");
        StoryDate occurred;
        try
        {
            occurred = V4StoryDateParser.Parse(new(
                Kind: Enum.Parse<StoryDateKind>(request.Occurred.Kind?.ToString() ?? "Unknown"),
                Value: request.Occurred.Value, Lower: request.Occurred.Lower, Upper: request.Occurred.Upper,
                LowerInclusive: request.Occurred.LowerInclusive, UpperInclusive: request.Occurred.UpperInclusive,
                OriginalText: request.Occurred.OriginalText,
                CalendarId: request.Occurred.CalendarId ?? "Gregorian"));
        }
        catch (Exception exception) when (exception is ArgumentException or VaultValidationException)
        { return new(false, "validation.event_date", Message: exception.Message); }
        V4ResolvedTarget relationship;
        V4ResolvedTarget? worldEvent = null;
        var projects = new List<V4ResolvedTarget>();
        try
        {
            relationship = await ResolveRelationshipAsync(request.RelationshipRef, continuity, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.WorldEvent))
            {
                worldEvent = await targets.EntityAsync(request.WorldEvent, continuity, cancellationToken).ConfigureAwait(false);
                if (!worldEvent.ResourceType.Equals("WorldEvent", StringComparison.OrdinalIgnoreCase))
                    throw new V4ResolutionException("reference.type_invalid", "worldEvent must identify a WorldEvent.");
            }
            foreach (var project in request.Projects ?? [])
                projects.Add(await targets.ProjectAsync(project, continuity, cancellationToken).ConfigureAwait(false));
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        if (projects.Select(project => project.StorageKey).Distinct().Count() != projects.Count)
            return new(false, "validation.projects_duplicate", Message: "The same project cannot appear twice.");
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await writes.ExecuteAsync(operation, "v4.relationship.event.add", request,
            "relationship_event_add", clientLabel, async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, continuity, token).ConfigureAwait(false);
                using var parent = context.Command(
                    "SELECT COUNT(*) FROM [CharacterRelationships] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, relationship.StorageKey).Add(OleDbType.Integer, continuity);
                if (Convert.ToInt32(await parent.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                    throw new VaultCommandException("record.not_found", "The relationship is unavailable in this continuity.");
                if (worldEvent is not null)
                    await RequireCanonEntityAsync(context, worldEvent.StorageKey, continuity, "WorldEvent", token)
                        .ConfigureAwait(false);
                foreach (var project in projects)
                    await RequireCanonEntityAsync(context, project.StorageKey, continuity, "Project", token)
                        .ConfigureAwait(false);
                ValidateRecurrence(occurred, request.Recurrence);
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                    "INSERT INTO [RelationshipEvents] ([RelationshipId],[WorldEventId],[Title],[Description],[NarrativeOrder]," +
                    "[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive]," +
                    "[EventOriginalText],[EventCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, relationship.StorageKey)
                    .Add(OleDbType.Integer, worldEvent?.StorageKey)
                    .Add(OleDbType.VarWChar, request.Title.Trim(), 255)
                    .Add(OleDbType.LongVarWChar, request.Description)
                    .Add(OleDbType.Double, request.NarrativeOrder);
                AddDate(insert, occurred);
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                await WriteRecurrenceAsync(context, "RelationshipEvents", "Id", id, request.Recurrence, token);
                var eventTarget = new V4ResolvedTarget("RelationshipEvent", id, string.Empty, request.Title.Trim(), continuity, false);
                foreach (var project in projects)
                    await ApplyOneProjectAsync(context, eventTarget, project.StorageKey, true, null, null, token)
                        .ConfigureAwait(false);
                return new VaultMutationOutcome("RelationshipEvent", id.ToString(), 1, "add",
                    new { relationship = relationship.Reference, worldEvent = worldEvent?.Reference,
                        projects = projects.Select(project => project.Reference).ToArray() });
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> AddRelationshipParticipantAsync(
        V4RelationshipParticipantAddCommand request, CancellationToken cancellationToken = default)
    {
        if (request.InitialPeriod?.Validate().Count > 0)
            return new(false, "validation.period", Message: "The initial membership period is invalid.");
        V4ResolvedTarget relationship;
        V4ResolvedTarget character;
        try
        {
            relationship = await ResolveRelationshipAsync(request.Relationship, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
            character = await ResolveCharacterAsync(request.Character, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await writes.ExecuteAsync(operation, "v4.relationship.participant.add", request,
            "relationship_participant_add", request.ClientLabel, async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, request.ContinuityKey, token).ConfigureAwait(false);
                await RequireCanonEntityAsync(context, character.StorageKey, request.ContinuityKey, "Character", token).ConfigureAwait(false);
                var version = await RequireRelationshipVersionAsync(context, relationship.StorageKey,
                    request.ContinuityKey, request.ExpectedVersion, token).ConfigureAwait(false);
                using var directed = context.Command(
                    "SELECT t.[IsDirected] FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] WHERE r.[Id]=?")
                    .Add(OleDbType.Integer, relationship.StorageKey);
                if (Convert.ToBoolean(await directed.ExecuteScalarAsync(token).ConfigureAwait(false)))
                    throw new VaultCommandException("relationship.directed_pair", "A directed relationship cannot add a third character.");
                using var count = context.Command(
                    "SELECT COUNT(*) FROM [RelationshipParticipants] WHERE [RelationshipId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, relationship.StorageKey);
                if (Convert.ToInt32(await count.ExecuteScalarAsync(token).ConfigureAwait(false)) >= 100)
                    throw new VaultCommandException("validation.participants", "A relationship can have at most 100 characters.");
                using var duplicate = context.Command(
                    "SELECT COUNT(*) FROM [RelationshipParticipants] WHERE [RelationshipId]=? AND [CharacterId]=?")
                    .Add(OleDbType.Integer, relationship.StorageKey).Add(OleDbType.Integer, character.StorageKey);
                if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
                    throw new VaultCommandException("constraint.duplicate", "That character already has a participation record; restore it if deleted.");
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                    "INSERT INTO [RelationshipParticipants] ([RelationshipId],[CharacterId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?)")
                    .Add(OleDbType.Integer, relationship.StorageKey).Add(OleDbType.Integer, character.StorageKey)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var participantId = await IdentityAsync(context, token).ConfigureAwait(false);
                if (request.InitialPeriod is not null)
                    await InsertRelationshipPeriodAsync(context, participantId, request.InitialPeriod, null, now, token)
                        .ConfigureAwait(false);
                await BumpRelationshipAsync(context, relationship.StorageKey, version, now, token).ConfigureAwait(false);
                return new VaultMutationOutcome("RelationshipParticipant", participantId.ToString(),
                    1, "participant-add", new { character = character.Reference,
                        relationship = relationship.Reference, relationshipVersion = version + 1 });
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> AddRelationshipMembershipPeriodAsync(
        V4RelationshipPeriodAddCommand request, CancellationToken cancellationToken = default)
    {
        var hasTransitions = request.Joined is not null || request.Left is not null;
        if (hasTransitions && (request.Joined is null || request.Left is null || request.Period is not null) ||
            !hasTransitions && request.Period is null)
            return new(false, "validation.period",
                Message: "Provide either one period or both joined and left dates.");
        if (request.Joined?.Validate().Count > 0 || request.Left?.Validate().Count > 0 ||
            request.Period?.Validate().Count > 0)
            return new(false, "validation.period", Message: "A membership date is invalid.");
        if (hasTransitions && !string.Equals(request.Joined!.CalendarId, request.Left!.CalendarId,
            StringComparison.OrdinalIgnoreCase))
            return new(false, "validation.calendar", Message: "Join and leave dates must use the same calendar.");
        var period = hasTransitions
            ? AccessV4RecordService.PossibleOccupancy(request.Joined!, request.Left!)
            : request.Period;
        if (period is null)
            return new(false, "validation.transition_order", Message: "The character cannot leave before every possible join date.");
        if (request.Notes?.Length > V4ContractLimits.MaximumLongTextLength)
            return new(false, "validation.notes", Message: "Membership notes are too long.");
        if (!hasTransitions && (request.JoinDescription is not null || request.LeaveDescription is not null) ||
            request.JoinDescription?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.LeaveDescription?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.JoinDescription is { Length: > 0 } && string.IsNullOrWhiteSpace(request.JoinDescription) ||
            request.LeaveDescription is { Length: > 0 } && string.IsNullOrWhiteSpace(request.LeaveDescription))
            return new(false, "validation.description", Message: "Descriptions require explicit join and leave dates and must contain text.");
        V4ResolvedTarget relationship;
        V4ResolvedTarget character;
        try
        {
            relationship = await ResolveRelationshipAsync(request.Relationship, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
            character = await ResolveCharacterAsync(request.Character, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await writes.ExecuteAsync(operation, "v4.relationship.period.add", request,
            "relationship_membership_period_add", request.ClientLabel, async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, request.ContinuityKey, token).ConfigureAwait(false);
                await RequireCanonEntityAsync(context, character.StorageKey, request.ContinuityKey, "Character", token).ConfigureAwait(false);
                var version = await RequireRelationshipVersionAsync(context, relationship.StorageKey,
                    request.ContinuityKey, request.ExpectedVersion, token).ConfigureAwait(false);
                using var participant = context.Command(
                    "SELECT [Id] FROM [RelationshipParticipants] WHERE [RelationshipId]=? AND [CharacterId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, relationship.StorageKey).Add(OleDbType.Integer, character.StorageKey);
                var value = await participant.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (value is null or DBNull)
                    throw new VaultCommandException("record.not_found", "The character does not participate in that relationship.");
                var participantId = Convert.ToInt32(value);
                using var existing = context.Command(
                    "SELECT [PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] " +
                    "FROM [RelationshipMembershipPeriods] WHERE [ParticipantId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, participantId);
                var periods = await existing.QueryAsync(reader => new StoryDate(
                    Enum.Parse<StoryDateKind>(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.GetBoolean(3), reader.GetBoolean(4),
                    CalendarId: reader.GetString(5)), token).ConfigureAwait(false);
                if (periods.Any(existingPeriod => existingPeriod.Overlaps(period)))
                    throw new VaultCommandException("interval.overlap",
                        "That character already has a membership period that may overlap. Correct the existing period first.");
                var now = DateTime.UtcNow;
                var periodId = await InsertRelationshipPeriodAsync(context, participantId, period, request.Notes, now, token)
                    .ConfigureAwait(false);
                if (hasTransitions)
                {
                    var joinId = await AccessV4RecordService.SaveTransitionAsync(context, periodId, "Join", request.Joined!, now, token)
                        .ConfigureAwait(false);
                    var leaveId = await AccessV4RecordService.SaveTransitionAsync(context, periodId, "Leave", request.Left!, now, token)
                        .ConfigureAwait(false);
                    await AccessV4RecordService.SaveTransitionDescriptionAsync(context, joinId,
                        request.JoinDescription, false, token).ConfigureAwait(false);
                    await AccessV4RecordService.SaveTransitionDescriptionAsync(context, leaveId,
                        request.LeaveDescription, false, token).ConfigureAwait(false);
                }
                await BumpRelationshipAsync(context, relationship.StorageKey, version, now, token).ConfigureAwait(false);
                return new VaultMutationOutcome("RelationshipMembershipPeriod", periodId.ToString(),
                    1, "period-add", new { character = character.Reference, relationship = relationship.Reference,
                        relationshipVersion = version + 1, period, request.Joined, request.Left });
            }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<V4ResolvedTarget> ResolveRelationshipAsync(string value, int continuity, CancellationToken token)
    {
        var target = await targets.RelationAsync(value, continuity, token: token).ConfigureAwait(false);
        if (!target.ResourceType.Equals("CharacterRelationship", StringComparison.OrdinalIgnoreCase))
            throw new V4ResolutionException("reference.type_invalid", "A relationship reference is required.");
        return target;
    }

    private async Task<V4ResolvedTarget> ResolveCharacterAsync(string value, int continuity, CancellationToken token)
    {
        var target = await targets.EntityAsync(value, continuity, token).ConfigureAwait(false);
        if (!target.ResourceType.Equals("Character", StringComparison.OrdinalIgnoreCase))
            throw new V4ResolutionException("reference.type_invalid", "A character is required.");
        return target;
    }

    private static async Task<int> RequireRelationshipVersionAsync(VaultWriteContext context,
        int relationshipId, int continuity, int expectedVersion, CancellationToken token)
    {
        using var query = context.Command(
            "SELECT [Version],[IsDeleted] FROM [CharacterRelationships] WHERE [Id]=? AND [ContinuityId]=?")
            .Add(OleDbType.Integer, relationshipId).Add(OleDbType.Integer, continuity);
        var rows = await query.QueryAsync(reader => (Version: reader.GetInt32(0), Deleted: reader.GetBoolean(1)), token)
            .ConfigureAwait(false);
        if (rows.Count != 1 || rows[0].Deleted)
            throw new VaultCommandException("record.not_found", "The relationship is unavailable in this continuity.");
        if (rows[0].Version != expectedVersion)
            throw new VaultCommandException("concurrency.conflict", "The relationship changed; read it again before retrying.",
                "CharacterRelationship", relationshipId.ToString(), rows[0].Version);
        return rows[0].Version;
    }

    private static async Task<int> InsertRelationshipPeriodAsync(VaultWriteContext context, int participantId,
        StoryDate period, string? notes, DateTime now, CancellationToken token)
    {
        using var insert = context.Command(
            "INSERT INTO [RelationshipMembershipPeriods] ([ParticipantId],[Notes],[PeriodKind],[PeriodLowerBound]," +
            "[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId]," +
            "[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?)")
            .Add(OleDbType.Integer, participantId).Add(OleDbType.LongVarWChar, notes);
        AddDate(insert, period);
        insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await IdentityAsync(context, token).ConfigureAwait(false);
    }

    private static async Task BumpRelationshipAsync(VaultWriteContext context,
        int relationshipId, int version, DateTime now, CancellationToken token)
    {
        using var bump = context.Command(
            "UPDATE [CharacterRelationships] SET [Version]=[Version]+1,[UpdatedAtUtc]=? WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False")
            .Add(OleDbType.Date, now).Add(OleDbType.Integer, relationshipId).Add(OleDbType.Integer, version);
        if (await bump.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new VaultCommandException("concurrency.conflict", "The relationship changed; read it again before retrying.");
    }
}
