using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessVaultService
{
    public Task<VaultMutationResult> MoveLocationAsync(MoveLocationRequest request, CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(
            request.OperationId, "location.move", request, "location_move", request.ClientLabel,
            async (context, token) =>
            {
                var location = await RequireEntityAsync(context, request.LocationId, CanonEntityType.Location, null, token).ConfigureAwait(false);
                if (request.ParentLocationId == request.LocationId) throw new VaultCommandException("location.cycle", "A location cannot be its own parent.");
                if (request.ParentLocationId is { } parentId)
                {
                    await RequireEntityAsync(context, parentId, CanonEntityType.Location, location.ContinuityId, token).ConfigureAwait(false);
                    await RejectLocationCycleAsync(context, request.LocationId, parentId, token).ConfigureAwait(false);
                }
                using var updateLocation = context.Command("UPDATE [Locations] SET [ParentLocationId]=? WHERE [EntityId]=?")
                    .Add(OleDbType.Integer, request.ParentLocationId).Add(OleDbType.Integer, request.LocationId);
                await updateLocation.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                using var bump = context.Command("UPDATE [CanonEntities] SET [UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                    .Add(OleDbType.Date, DateTime.UtcNow).Add(OleDbType.Integer, request.LocationId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await bump.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "Location", request.LocationId, "CanonEntities", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome("Location", request.LocationId.ToString(), request.ExpectedVersion + 1, "move", new { request.ParentLocationId }, request.ExpectedVersion);
            }, cancellationToken);

    public Task<VaultMutationResult> CreateRelationshipTypeAsync(CreateRelationshipTypeRequest request, CancellationToken cancellationToken = default)
    {
        string name;
        try
        {
            name = TextNormalization.Required(request.Name, 100, nameof(request.Name));
            if (request.IsDirected)
                _ = TextNormalization.Required(request.InverseName!, 100, nameof(request.InverseName));
            else if (!string.IsNullOrWhiteSpace(request.InverseName))
                throw new ArgumentException("Undirected relationship types use the same label in both directions and cannot define InverseName.");
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "relationship_type.create", request, "relationship_type_create", request.ClientLabel,
            async (context, token) =>
            {
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [RelationshipTypes] ([Name],[NormalizedName],[IsDirected],[InverseName],[AllowsOverlappingPeriods],[Description],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.VarWChar, name, 100).Add(OleDbType.VarWChar, TextNormalization.CanonicalKey(name), 100)
                    .Add(OleDbType.Boolean, request.IsDirected).Add(OleDbType.VarWChar, request.IsDirected ? request.InverseName!.Trim() : null, 100)
                    .Add(OleDbType.Boolean, request.AllowsOverlappingPeriods).Add(OleDbType.LongVarWChar, request.Description)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("RelationshipType", id.ToString(), 1, "create", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> CreateRelationshipAsync(CreateRelationshipRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateStoryDate(request.Period); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.period", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "character.relationship.create", request, "character_relationship_create", request.ClientLabel,
            async (context, token) =>
            {
                await RequireEntityAsync(context, request.SourceCharacterId, CanonEntityType.Character, request.ContinuityId, token).ConfigureAwait(false);
                await RequireEntityAsync(context, request.TargetCharacterId, CanonEntityType.Character, request.ContinuityId, token).ConfigureAwait(false);
                using var typeCommand = context.Command("SELECT [IsDirected],[AllowsOverlappingPeriods] FROM [RelationshipTypes] WHERE [Id]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, request.RelationshipTypeId);
                var types = await typeCommand.QueryAsync(reader => (Directed: reader.GetBoolean(0), AllowsOverlap: reader.GetBoolean(1)), token).ConfigureAwait(false);
                if (types.Count != 1) throw new VaultCommandException("entity.not_found", "Relationship type was not found.");
                var source = request.SourceCharacterId;
                var target = request.TargetCharacterId;
                if (!types[0].Directed && source > target) (source, target) = (target, source);
                await RejectDuplicateOrDisallowedRelationshipOverlapAsync(
                    context, source, target, request.RelationshipTypeId, request.Period,
                    types[0].AllowsOverlap, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [CharacterRelationships] ([ContinuityId],[SourceCharacterId],[TargetCharacterId],[RelationshipTypeId],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ContinuityId).Add(OleDbType.Integer, source).Add(OleDbType.Integer, target)
                    .Add(OleDbType.Integer, request.RelationshipTypeId).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period); insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                await AddInitialRelationshipParticipantAsync(context, id, source, request.Period, now, token).ConfigureAwait(false);
                await AddInitialRelationshipParticipantAsync(context, id, target, request.Period, now, token).ConfigureAwait(false);
                return new VaultMutationOutcome("CharacterRelationship", id.ToString(), 1, "create", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> CreateRelationshipGroupAsync(
        CreateRelationshipGroupRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CharacterIds is null || request.CharacterIds.Count is < 2 or > 100 ||
            request.CharacterIds.Distinct().Count() != request.CharacterIds.Count)
            return Task.FromResult(new VaultMutationResult(false, "validation.participants",
                Message: "A relationship needs 2 to 100 distinct characters."));
        try { if (request.InitialPeriod is not null) ValidateStoryDate(request.InitialPeriod); }
        catch (ArgumentException exception)
        { return Task.FromResult(new VaultMutationResult(false, "validation.period", Message: exception.Message)); }
        return writes.ExecuteAsync(request.OperationId, "character.relationship.group.create", request,
            "relationship_create", request.ClientLabel, async (context, token) =>
            {
                foreach (var character in request.CharacterIds)
                    await RequireEntityAsync(context, character, CanonEntityType.Character, request.ContinuityId, token)
                        .ConfigureAwait(false);
                using var typeCommand = context.Command(
                    "SELECT [IsDirected],[AllowsOverlappingPeriods] FROM [RelationshipTypes] WHERE [Id]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, request.RelationshipTypeId);
                var types = await typeCommand.QueryAsync(reader =>
                    (Directed: reader.GetBoolean(0), AllowsOverlap: reader.GetBoolean(1)), token).ConfigureAwait(false);
                if (types.Count != 1) throw new VaultCommandException("entity.not_found", "Relationship type was not found.");
                if (types[0].Directed && request.CharacterIds.Count != 2)
                    throw new VaultCommandException("relationship.directed_pair", "A directed relationship must have exactly two characters.");
                var members = types[0].Directed ? request.CharacterIds.ToArray() : request.CharacterIds.Order().ToArray();
                var source = members[0];
                var target = members[1];
                await RejectDuplicateOrDisallowedRelationshipOverlapAsync(context, source, target,
                    request.RelationshipTypeId, request.InitialPeriod ?? StoryDate.Unknown(),
                    types[0].AllowsOverlap, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [CharacterRelationships] ([ContinuityId],[SourceCharacterId],[TargetCharacterId],[RelationshipTypeId],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ContinuityId).Add(OleDbType.Integer, source)
                    .Add(OleDbType.Integer, target).Add(OleDbType.Integer, request.RelationshipTypeId)
                    .Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.InitialPeriod ?? StoryDate.Unknown());
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                foreach (var member in members)
                    await AddInitialRelationshipParticipantAsync(context, id, member, request.InitialPeriod, now, token)
                        .ConfigureAwait(false);
                return new VaultMutationOutcome("CharacterRelationship", id.ToString(), 1, "create", new
                { request.RelationshipTypeId, Members = members, request.InitialPeriod });
            }, cancellationToken);
    }

    private static async Task AddInitialRelationshipParticipantAsync(
        VaultWriteContext context, int relationshipId, int characterId,
        StoryDate? period, DateTime now, CancellationToken token)
    {
        using var participant = context.Command(
            "INSERT INTO [RelationshipParticipants] ([RelationshipId],[CharacterId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?)")
            .Add(OleDbType.Integer, relationshipId).Add(OleDbType.Integer, characterId)
            .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
        await participant.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (period is null) return;
        var participantId = await IdentityAsync(context, token).ConfigureAwait(false);
        using var membership = context.Command(
            "INSERT INTO [RelationshipMembershipPeriods] ([ParticipantId],[PeriodKind],[PeriodLowerBound]," +
            "[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText]," +
            "[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?)")
            .Add(OleDbType.Integer, participantId);
        AddStoryDate(membership, period);
        membership.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
        await membership.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public Task<VaultMutationResult> AddWorldEventParticipantAsync(AddWorldEventParticipantRequest request, CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(
            request.OperationId, "world_event.participant.add", request, "world_event_participant_add", request.ClientLabel,
            async (context, token) =>
            {
                var worldEvent = await RequireEntityAsync(context, request.WorldEventId, CanonEntityType.WorldEvent, null, token).ConfigureAwait(false);
                var participant = await RequireEntityAsync(context, request.ParticipantEntityId, null, worldEvent.ContinuityId, token).ConfigureAwait(false);
                if (participant.Type is not (CanonEntityType.Character or CanonEntityType.Organization or CanonEntityType.Object))
                    throw new VaultCommandException("world_event.unsupported_participant", "World-event participants must be characters, organizations, or objects; use a world-event location for places.");
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [WorldEventParticipants] ([WorldEventId],[ParticipantEntityId],[Role],[Impact],[Outcome],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.WorldEventId).Add(OleDbType.Integer, request.ParticipantEntityId)
                    .Add(OleDbType.VarWChar, request.Role, 100).Add(OleDbType.LongVarWChar, request.Impact)
                    .Add(OleDbType.LongVarWChar, request.Outcome).Add(OleDbType.LongVarWChar, request.Notes)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("WorldEventParticipant", id.ToString(), 1, "add", request);
            }, cancellationToken);

    public Task<VaultMutationResult> AddWorldEventLocationAsync(AddWorldEventLocationRequest request, CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(
            request.OperationId, "world_event.location.add", request, "world_event_location_add", request.ClientLabel,
            async (context, token) =>
            {
                var worldEvent = await RequireEntityAsync(context, request.WorldEventId, CanonEntityType.WorldEvent, null, token).ConfigureAwait(false);
                await RequireEntityAsync(context, request.LocationId, CanonEntityType.Location, worldEvent.ContinuityId, token).ConfigureAwait(false);
                if (request.IsPrimary)
                {
                    using var existing = context.Command("SELECT COUNT(*) FROM [WorldEventLocations] WHERE [WorldEventId]=? AND [IsPrimary]=True AND [IsDeleted]=False")
                        .Add(OleDbType.Integer, request.WorldEventId);
                    if (Convert.ToInt32(await existing.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
                        throw new VaultCommandException("world_event.primary_location", "A world event can have only one primary location.");
                }
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [WorldEventLocations] ([WorldEventId],[LocationId],[IsPrimary],[Role],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.WorldEventId).Add(OleDbType.Integer, request.LocationId)
                    .Add(OleDbType.Boolean, request.IsPrimary).Add(OleDbType.VarWChar, request.Role, 100)
                    .Add(OleDbType.LongVarWChar, request.Notes).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("WorldEventLocation", id.ToString(), 1, "add", request);
            }, cancellationToken);

    private static async Task RejectLocationCycleAsync(VaultWriteContext context, int locationId, int parentId, CancellationToken token)
    {
        int? cursor = parentId;
        var seen = new HashSet<int>();
        while (cursor is { } current)
        {
            if (current == locationId || !seen.Add(current)) throw new VaultCommandException("location.cycle", "The requested parent would create a location cycle.");
            using var command = context.Command("SELECT [ParentLocationId] FROM [Locations] WHERE [EntityId]=?").Add(OleDbType.Integer, current);
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            cursor = value is null or DBNull ? null : Convert.ToInt32(value);
        }
    }

    private static async Task RejectDuplicateOrDisallowedRelationshipOverlapAsync(
        VaultWriteContext context,
        int source,
        int target,
        int typeId,
        StoryDate period,
        bool allowsOverlap,
        CancellationToken token)
    {
        using var command = context.Command("SELECT [PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] FROM [CharacterRelationships] WHERE [SourceCharacterId]=? AND [TargetCharacterId]=? AND [RelationshipTypeId]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, source).Add(OleDbType.Integer, target).Add(OleDbType.Integer, typeId);
        var rows = await command.QueryAsync(reader => (
            Kind: Enum.Parse<StoryDateKind>(reader.GetString(0)),
            Lower: reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
            Upper: reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
            LowerInclusive: reader.GetBoolean(3), UpperInclusive: reader.GetBoolean(4),
            CalendarId: reader.GetString(5)), token).ConfigureAwait(false);
        if (rows.Any(row => row.Kind == period.Kind && row.Lower == period.LowerBound && row.Upper == period.UpperBound &&
                            row.LowerInclusive == period.LowerInclusive && row.UpperInclusive == period.UpperInclusive &&
                            string.Equals(row.CalendarId, period.CalendarId, StringComparison.OrdinalIgnoreCase)))
            throw new VaultCommandException("constraint.duplicate", "An equivalent relationship period already exists.");
        if (!allowsOverlap && rows.Any(row => Overlaps(row.Lower, row.Upper, row.LowerInclusive, row.UpperInclusive, period)))
            throw new VaultCommandException("interval.overlap", "Relationship overlaps an existing period.");
    }
    public Task<VaultMutationResult> AddResidenceAsync(AddResidenceRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateStoryDate(request.Period); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.period", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "character.residence.add", request, "character_residence_add", request.ClientLabel,
            async (context, token) =>
            {
                var character = await RequireEntityAsync(context, request.CharacterId, CanonEntityType.Character, null, token).ConfigureAwait(false);
                await RequireEntityAsync(context, request.LocationId, CanonEntityType.Location, character.ContinuityId, token).ConfigureAwait(false);
                if (request.IsPrimary)
                    await RejectOverlapAsync(context, "CharacterResidences", "CharacterId", request.CharacterId, request.Period, token, "[IsPrimary]=True").ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [CharacterResidences] ([CharacterId],[LocationId],[IsPrimary],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.CharacterId).Add(OleDbType.Integer, request.LocationId)
                    .Add(OleDbType.Boolean, request.IsPrimary).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period);
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("CharacterResidence", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> TransitionResidenceAsync(TransitionResidenceRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateEffectiveAt(request.EffectiveAt); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.effective_at", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "character.residence.transition", request, "character_residence_transition", request.ClientLabel,
            async (context, token) =>
            {
                using var current = context.Command("SELECT r.[CharacterId],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[Version],c.[ContinuityId] FROM [CharacterResidences] AS r INNER JOIN [CanonEntities] AS c ON r.[CharacterId]=c.[Id] WHERE r.[Id]=? AND r.[IsDeleted]=False")
                    .Add(OleDbType.Integer, request.ResidenceId);
                var rows = await current.QueryAsync(reader => (
                    CharacterId: reader.GetInt32(0),
                    Lower: reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                    Upper: reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                    Version: reader.GetInt32(3), ContinuityId: reader.GetInt32(4)), token).ConfigureAwait(false);
                if (rows.Count != 1) throw new VaultCommandException("entity.not_found", "Residence was not found.", "CharacterResidence", request.ResidenceId.ToString());
                var row = rows[0];
                if (row.Version != request.ExpectedVersion)
                    throw new VaultCommandException("concurrency.conflict", "Residence changed after it was read.", "CharacterResidence", request.ResidenceId.ToString(), row.Version);
                ValidateTransitionBoundary(row.Lower, row.Upper, request.EffectiveAt);
                await RequireEntityAsync(context, request.NewLocationId, CanonEntityType.Location, row.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                await ClosePeriodAsync(context, "CharacterResidences", request.ResidenceId, request.ExpectedVersion, row.Lower, request.EffectiveAt, now, token).ConfigureAwait(false);
                using var insert = context.Command("INSERT INTO [CharacterResidences] ([CharacterId],[LocationId],[IsPrimary],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, row.CharacterId).Add(OleDbType.Integer, request.NewLocationId)
                    .Add(OleDbType.Boolean, request.IsPrimary).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, new StoryDate(StoryDateKind.After, request.EffectiveAt, null));
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("CharacterResidence", id.ToString(), 1, "transition", new
                {
                    closedResidenceId = request.ResidenceId,
                    closedResidenceVersion = request.ExpectedVersion + 1,
                    newResidenceId = id,
                    request.EffectiveAt,
                    request.NewLocationId
                }, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddMembershipAsync(AddMembershipRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateStoryDate(request.Period); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.period", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "organization.membership.add", request, "organization_membership_add", request.ClientLabel,
            async (context, token) =>
            {
                var organization = await RequireEntityAsync(context, request.OrganizationId, CanonEntityType.Organization, null, token).ConfigureAwait(false);
                await RequireEntityAsync(context, request.CharacterId, CanonEntityType.Character, organization.ContinuityId, token).ConfigureAwait(false);
                // Simultaneous different roles are allowed; the same normalized role may not overlap.
                await RejectMembershipOverlapAsync(context, request, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [OrganizationMemberships] ([OrganizationId],[CharacterId],[Role],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.OrganizationId).Add(OleDbType.Integer, request.CharacterId)
                    .Add(OleDbType.VarWChar, request.Role?.Trim(), 255).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period);
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("OrganizationMembership", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> TransitionMembershipAsync(TransitionMembershipRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.CreateReplacement && (request.NewRole is not null || request.Notes is not null))
            return Task.FromResult(new VaultMutationResult(false, "validation.membership", Message: "NewRole and Notes require CreateReplacement=true."));
        try { ValidateEffectiveAt(request.EffectiveAt); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.effective_at", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "organization.membership.transition", request, "organization_membership_transition", request.ClientLabel,
            async (context, token) =>
            {
                using var current = context.Command("SELECT [OrganizationId],[CharacterId],[PeriodLowerBound],[PeriodUpperBound],[Version] FROM [OrganizationMemberships] WHERE [Id]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, request.MembershipId);
                var rows = await current.QueryAsync(reader => (
                    OrganizationId: reader.GetInt32(0), CharacterId: reader.GetInt32(1),
                    Lower: reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                    Upper: reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                    Version: reader.GetInt32(4)), token).ConfigureAwait(false);
                if (rows.Count != 1) throw new VaultCommandException("entity.not_found", "Membership was not found.", "OrganizationMembership", request.MembershipId.ToString());
                var row = rows[0];
                if (row.Version != request.ExpectedVersion)
                    throw new VaultCommandException("concurrency.conflict", "Membership changed after it was read.", "OrganizationMembership", request.MembershipId.ToString(), row.Version);
                ValidateTransitionBoundary(row.Lower, row.Upper, request.EffectiveAt);
                var now = DateTime.UtcNow;
                await ClosePeriodAsync(context, "OrganizationMemberships", request.MembershipId, request.ExpectedVersion, row.Lower, request.EffectiveAt, now, token).ConfigureAwait(false);
                int? replacementId = null;
                if (request.CreateReplacement)
                {
                    using var insert = context.Command("INSERT INTO [OrganizationMemberships] ([OrganizationId],[CharacterId],[Role],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                        .Add(OleDbType.Integer, row.OrganizationId).Add(OleDbType.Integer, row.CharacterId)
                        .Add(OleDbType.VarWChar, request.NewRole?.Trim(), 255).Add(OleDbType.LongVarWChar, request.Notes);
                    AddStoryDate(insert, new StoryDate(StoryDateKind.After, request.EffectiveAt, null));
                    insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    replacementId = await IdentityAsync(context, token).ConfigureAwait(false);
                }
                return new VaultMutationOutcome(
                    "OrganizationMembership",
                    (replacementId ?? request.MembershipId).ToString(),
                    replacementId is null ? request.ExpectedVersion + 1 : 1,
                    request.CreateReplacement ? "transition" : "end",
                    new
                    {
                        closedMembershipId = request.MembershipId,
                        closedMembershipVersion = request.ExpectedVersion + 1,
                        replacementMembershipId = replacementId,
                        request.EffectiveAt,
                        request.NewRole
                    }, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> CreateOwnershipPrincipalAsync(CreateOwnershipPrincipalRequest request, CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(
            request.OperationId, "ownership.principal.create", request, "ownership_principal_create", request.ClientLabel,
            async (context, token) =>
            {
                await RequireContinuityAsync(context, request.ContinuityId, token).ConfigureAwait(false);
                switch (request.Kind)
                {
                    case PrincipalKind.Character when request.CharacterId is { } characterId && request.OrganizationId is null && request.Label is null:
                        await RequireEntityAsync(context, characterId, CanonEntityType.Character, request.ContinuityId, token).ConfigureAwait(false);
                        break;
                    case PrincipalKind.Organization when request.OrganizationId is { } organizationId && request.CharacterId is null && request.Label is null:
                        await RequireEntityAsync(context, organizationId, CanonEntityType.Organization, request.ContinuityId, token).ConfigureAwait(false);
                        break;
                    case PrincipalKind.External when request.CharacterId is null && request.OrganizationId is null && !string.IsNullOrWhiteSpace(request.Label):
                        break;
                    default:
                        throw new VaultCommandException("ownership.invalid_principal", "Principal kind and target fields are inconsistent.");
                }
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [OwnershipPrincipals] ([ContinuityId],[PrincipalKind],[CharacterId],[OrganizationId],[Label],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ContinuityId).Add(OleDbType.VarWChar, request.Kind.ToString(), 30)
                    .Add(OleDbType.Integer, request.CharacterId).Add(OleDbType.Integer, request.OrganizationId)
                    .Add(OleDbType.VarWChar, request.Label, 255).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("OwnershipPrincipal", id.ToString(), 1, "create", request);
            }, cancellationToken);

    public Task<VaultMutationResult> AddOwnershipPeriodAsync(AddOwnershipPeriodRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateStoryDate(request.Period);
            ValidateOwners(request.State, request.Owners);
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.ownership", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "object.ownership.add", request, "object_ownership_add", request.ClientLabel,
            async (context, token) =>
            {
                var obj = await RequireEntityAsync(context, request.ObjectId, CanonEntityType.Object, null, token).ConfigureAwait(false);
                await RejectOverlapAsync(context, "ObjectOwnershipPeriods", "ObjectId", request.ObjectId, request.Period, token).ConfigureAwait(false);
                foreach (var owner in request.Owners)
                    await RequirePrincipalAsync(context, owner.PrincipalId, obj.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [ObjectOwnershipPeriods] ([ObjectId],[OwnerState],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ObjectId).Add(OleDbType.VarWChar, request.State.ToString(), 30).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period);
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                foreach (var owner in request.Owners)
                {
                    using var addOwner = context.Command("INSERT INTO [ObjectOwnershipOwners] ([OwnershipPeriodId],[PrincipalId],[SharePartsPerMillion],[Notes]) VALUES (?,?,?,?)")
                        .Add(OleDbType.Integer, id).Add(OleDbType.Integer, owner.PrincipalId)
                        .Add(OleDbType.Integer, owner.SharePartsPerMillion).Add(OleDbType.LongVarWChar, owner.Notes);
                    await addOwner.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                return new VaultMutationOutcome("ObjectOwnershipPeriod", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> TransferOwnershipAsync(TransferOwnershipRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateEffectiveAt(request.EffectiveAt);
            ValidateOwners(request.NewState, request.NewOwners);
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.ownership", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "object.ownership.transfer", request, "object_ownership_transfer", request.ClientLabel,
            async (context, token) =>
            {
                using var current = context.Command("SELECT p.[ObjectId],p.[PeriodLowerBound],p.[PeriodUpperBound],p.[Version],c.[ContinuityId] FROM ([ObjectOwnershipPeriods] AS p INNER JOIN [Objects] AS o ON p.[ObjectId]=o.[EntityId]) INNER JOIN [CanonEntities] AS c ON o.[EntityId]=c.[Id] WHERE p.[Id]=? AND p.[IsDeleted]=False")
                    .Add(OleDbType.Integer, request.OwnershipPeriodId);
                var rows = await current.QueryAsync(reader => (
                    ObjectId: reader.GetInt32(0),
                    Lower: reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                    Upper: reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                    Version: reader.GetInt32(3), ContinuityId: reader.GetInt32(4)), token).ConfigureAwait(false);
                if (rows.Count != 1) throw new VaultCommandException("entity.not_found", "Ownership period was not found.", "ObjectOwnershipPeriod", request.OwnershipPeriodId.ToString());
                var row = rows[0];
                if (row.Version != request.ExpectedVersion)
                    throw new VaultCommandException("concurrency.conflict", "Ownership period changed after it was read.", "ObjectOwnershipPeriod", request.OwnershipPeriodId.ToString(), row.Version);
                ValidateTransitionBoundary(row.Lower, row.Upper, request.EffectiveAt);
                foreach (var owner in request.NewOwners)
                    await RequirePrincipalAsync(context, owner.PrincipalId, row.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                await ClosePeriodAsync(context, "ObjectOwnershipPeriods", request.OwnershipPeriodId, request.ExpectedVersion, row.Lower, request.EffectiveAt, now, token).ConfigureAwait(false);
                using var insert = context.Command("INSERT INTO [ObjectOwnershipPeriods] ([ObjectId],[OwnerState],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, row.ObjectId).Add(OleDbType.VarWChar, request.NewState.ToString(), 30).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, new StoryDate(StoryDateKind.After, request.EffectiveAt, null));
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                foreach (var owner in request.NewOwners)
                {
                    using var addOwner = context.Command("INSERT INTO [ObjectOwnershipOwners] ([OwnershipPeriodId],[PrincipalId],[SharePartsPerMillion],[Notes]) VALUES (?,?,?,?)")
                        .Add(OleDbType.Integer, id).Add(OleDbType.Integer, owner.PrincipalId)
                        .Add(OleDbType.Integer, owner.SharePartsPerMillion).Add(OleDbType.LongVarWChar, owner.Notes);
                    await addOwner.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                return new VaultMutationOutcome("ObjectOwnershipPeriod", id.ToString(), 1, "transfer", new
                {
                    closedOwnershipPeriodId = request.OwnershipPeriodId,
                    closedOwnershipPeriodVersion = request.ExpectedVersion + 1,
                    newOwnershipPeriodId = id,
                    request.EffectiveAt,
                    request.NewState,
                    owners = request.NewOwners.Count
                }, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> ReplaceOwnershipOwnersAsync(ReplaceOwnershipOwnersRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateOwners(OwnershipState.Owned, request.Owners); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.ownership", Message: exception.Message)); }
        return writes.ExecuteAsync(request.OperationId, "object.ownership.replace_owners", request, "object_ownership_replace_owners", request.ClientLabel,
            async (context, token) =>
            {
                using var period = context.Command("SELECT p.[ObjectId],c.[ContinuityId],p.[Version] FROM ([ObjectOwnershipPeriods] AS p INNER JOIN [Objects] AS o ON p.[ObjectId]=o.[EntityId]) INNER JOIN [CanonEntities] AS c ON o.[EntityId]=c.[Id] WHERE p.[Id]=? AND p.[IsDeleted]=False")
                    .Add(OleDbType.Integer, request.OwnershipPeriodId);
                var periods = await period.QueryAsync(reader => (ObjectId: reader.GetInt32(0), ContinuityId: reader.GetInt32(1), Version: reader.GetInt32(2)), token).ConfigureAwait(false);
                if (periods.Count == 0) throw new VaultCommandException("entity.not_found", "Ownership period was not found.");
                if (periods[0].Version != request.ExpectedVersion) throw new VaultCommandException("concurrency.conflict", "Ownership period changed after it was read.", "ObjectOwnershipPeriod", request.OwnershipPeriodId.ToString(), periods[0].Version);
                foreach (var owner in request.Owners) await RequirePrincipalAsync(context, owner.PrincipalId, periods[0].ContinuityId, token).ConfigureAwait(false);
                using (var delete = context.Command("DELETE FROM [ObjectOwnershipOwners] WHERE [OwnershipPeriodId]=?").Add(OleDbType.Integer, request.OwnershipPeriodId))
                    await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                foreach (var owner in request.Owners)
                {
                    using var insert = context.Command("INSERT INTO [ObjectOwnershipOwners] ([OwnershipPeriodId],[PrincipalId],[SharePartsPerMillion],[Notes]) VALUES (?,?,?,?)")
                        .Add(OleDbType.Integer, request.OwnershipPeriodId).Add(OleDbType.Integer, owner.PrincipalId)
                        .Add(OleDbType.Integer, owner.SharePartsPerMillion).Add(OleDbType.LongVarWChar, owner.Notes);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                using var bump = context.Command("UPDATE [ObjectOwnershipPeriods] SET [OwnerState]='Owned',[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                    .Add(OleDbType.Date, DateTime.UtcNow).Add(OleDbType.Integer, request.OwnershipPeriodId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await bump.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new VaultCommandException("concurrency.conflict", "Ownership period changed during replacement.");
                return new VaultMutationOutcome("ObjectOwnershipPeriod", request.OwnershipPeriodId.ToString(), request.ExpectedVersion + 1, "replace-owners", request, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddOrganizationLocationAsync(AddOrganizationLocationRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateStoryDate(request.Period); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.period", Message: exception.Message)); }
        return writes.ExecuteAsync(request.OperationId, "organization.location.add", request, "organization_location_add", request.ClientLabel,
            async (context, token) =>
            {
                var organization = await RequireEntityAsync(context, request.OrganizationId, CanonEntityType.Organization, null, token).ConfigureAwait(false);
                await RequireEntityAsync(context, request.LocationId, CanonEntityType.Location, organization.ContinuityId, token).ConfigureAwait(false);
                if (request.IsPrimary) await RejectOverlapAsync(context, "OrganizationLocations", "OrganizationId", request.OrganizationId, request.Period, token, "[IsPrimary]=True").ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [OrganizationLocations] ([OrganizationId],[LocationId],[LocationRole],[IsPrimary],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.OrganizationId).Add(OleDbType.Integer, request.LocationId)
                    .Add(OleDbType.VarWChar, request.LocationRole, 100).Add(OleDbType.Boolean, request.IsPrimary).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period); insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("OrganizationLocation", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddObjectLocationAsync(AddObjectLocationRequest request, CancellationToken cancellationToken = default)
    {
        try { ValidateStoryDate(request.Period); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.period", Message: exception.Message)); }
        return writes.ExecuteAsync(request.OperationId, "object.location.add", request, "object_location_add", request.ClientLabel,
            async (context, token) =>
            {
                var obj = await RequireEntityAsync(context, request.ObjectId, CanonEntityType.Object, null, token).ConfigureAwait(false);
                await RequireEntityAsync(context, request.LocationId, CanonEntityType.Location, obj.ContinuityId, token).ConfigureAwait(false);
                await RejectOverlapAsync(context, "ObjectLocationPeriods", "ObjectId", request.ObjectId, request.Period, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [ObjectLocationPeriods] ([ObjectId],[LocationId],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ObjectId).Add(OleDbType.Integer, request.LocationId).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period); insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("ObjectLocationPeriod", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddObjectCustodyAsync(AddObjectCustodyRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateStoryDate(request.Period);
            if (request.CustodianState == CustodyState.Known && request.PrincipalId is null)
                throw new ArgumentException("Known custody requires a principal.");
            if (request.CustodianState != CustodyState.Known && request.PrincipalId is not null)
                throw new ArgumentException("Unknown and unowned custody cannot name a principal.");
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.custody", Message: exception.Message)); }
        return writes.ExecuteAsync(request.OperationId, "object.custody.add", request, "object_custody_add", request.ClientLabel,
            async (context, token) =>
            {
                var obj = await RequireEntityAsync(context, request.ObjectId, CanonEntityType.Object, null, token).ConfigureAwait(false);
                if (request.PrincipalId is { } principalId) await RequirePrincipalAsync(context, principalId, obj.ContinuityId, token).ConfigureAwait(false);
                await RejectOverlapAsync(context, "ObjectCustodyPeriods", "ObjectId", request.ObjectId, request.Period, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [ObjectCustodyPeriods] ([ObjectId],[CustodianState],[PrincipalId],[Notes],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ObjectId).Add(OleDbType.VarWChar, request.CustodianState.ToString(), 30)
                    .Add(OleDbType.Integer, request.PrincipalId).Add(OleDbType.LongVarWChar, request.Notes);
                AddStoryDate(insert, request.Period); insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("ObjectCustodyPeriod", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    private static async Task RejectMembershipOverlapAsync(VaultWriteContext context, AddMembershipRequest request, CancellationToken token)
    {
        using var command = context.Command("SELECT [Role],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive] FROM [OrganizationMemberships] WHERE [OrganizationId]=? AND [CharacterId]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, request.OrganizationId).Add(OleDbType.Integer, request.CharacterId);
        var rows = await command.QueryAsync(reader => (
            Role: reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Lower: reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
            Upper: reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
            LowerInclusive: reader.GetBoolean(3), UpperInclusive: reader.GetBoolean(4)), token).ConfigureAwait(false);
        var role = request.Role is null ? string.Empty : TextNormalization.CanonicalKey(request.Role);
        if (rows.Any(row => (row.Role.Length == 0 ? string.Empty : TextNormalization.CanonicalKey(row.Role)) == role && Overlaps(row.Lower, row.Upper, row.LowerInclusive, row.UpperInclusive, request.Period)))
            throw new VaultCommandException("interval.overlap", "Membership overlaps an existing membership with the same role.");
    }

    private static async Task RejectOverlapAsync(VaultWriteContext context, string table, string groupColumn, int groupId, StoryDate period, CancellationToken token, string? extra = null)
    {
        var suffix = extra is null ? string.Empty : $" AND {extra}";
        using var command = context.Command($"SELECT [PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive] FROM [{table}] WHERE [{groupColumn}]=? AND [IsDeleted]=False{suffix}")
            .Add(OleDbType.Integer, groupId);
        var rows = await command.QueryAsync(reader => (
            Lower: reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0),
            Upper: reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
            LowerInclusive: reader.GetBoolean(2), UpperInclusive: reader.GetBoolean(3)), token).ConfigureAwait(false);
        if (rows.Any(row => Overlaps(row.Lower, row.Upper, row.LowerInclusive, row.UpperInclusive, period)))
            throw new VaultCommandException("interval.overlap", $"{table} contains an overlapping period.");
    }

    private static bool Overlaps(DateTime? lower, DateTime? upper, bool lowerInclusive, bool upperInclusive, StoryDate candidate) =>
        new StoryDate(StoryDateKind.Range, lower, upper, lowerInclusive, upperInclusive).Overlaps(candidate);

    private static void ValidateEffectiveAt(DateTime effectiveAt)
    {
        if (effectiveAt.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("EffectiveAt must be a timezone-free date/time with DateTimeKind.Unspecified.");
        if (effectiveAt < StoryDate.AccessMinimum)
            throw new ArgumentException("EffectiveAt is outside Access's supported date range.");
    }

    private static void ValidateTransitionBoundary(DateTime? lower, DateTime? upper, DateTime effectiveAt)
    {
        if (lower is { } lowerBound && effectiveAt <= lowerBound)
            throw new VaultCommandException("interval.invalid_transition", "EffectiveAt must be later than the prior period's lower bound.");
        if (upper is { } upperBound && effectiveAt > upperBound)
            throw new VaultCommandException("interval.invalid_transition", "EffectiveAt cannot be later than the prior period's upper bound.");
    }

    private static async Task ClosePeriodAsync(
        VaultWriteContext context,
        string table,
        int id,
        int expectedVersion,
        DateTime? lower,
        DateTime effectiveAt,
        DateTime updatedAtUtc,
        CancellationToken token)
    {
        var closedKind = lower is null ? StoryDateKind.Before : StoryDateKind.Range;
        using var update = context.Command($"UPDATE [{table}] SET [PeriodKind]=?,[PeriodUpperBound]=?,[PeriodUpperInclusive]=False,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False")
            .Add(OleDbType.VarWChar, closedKind.ToString(), 30).Add(OleDbType.Date, effectiveAt)
            .Add(OleDbType.Date, updatedAtUtc).Add(OleDbType.Integer, id).Add(OleDbType.Integer, expectedVersion);
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new VaultCommandException("concurrency.conflict", $"{table} changed during the transition.");
    }

    private static void ValidateOwners(OwnershipState state, IReadOnlyList<OwnershipOwnerInput> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (state == OwnershipState.Owned && owners.Count == 0) throw new ArgumentException("Owned periods require at least one owner.");
        if (state != OwnershipState.Owned && owners.Count != 0) throw new ArgumentException("Unknown and unowned periods cannot contain owners.");
        if (owners.Select(owner => owner.PrincipalId).Distinct().Count() != owners.Count) throw new ArgumentException("An owner cannot be repeated.");
        var specified = owners.Count(owner => owner.SharePartsPerMillion is not null);
        if (specified != 0 && (specified != owners.Count || owners.Sum(owner => owner.SharePartsPerMillion) != 1_000_000))
            throw new ArgumentException("Shares must be omitted for every owner or specified for every owner and total 1,000,000 parts.");
    }

    private static async Task RequirePrincipalAsync(VaultWriteContext context, int principalId, int continuityId, CancellationToken token)
    {
        using var command = context.Command("SELECT COUNT(*) FROM [OwnershipPrincipals] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, principalId).Add(OleDbType.Integer, continuityId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("ownership.principal_not_found", "Ownership principal was not found in the object's continuity.");
    }
}
