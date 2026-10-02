using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Record overviews and independently paged relationship sections.</summary>
public sealed partial class AccessV4ReadService
{
    private static readonly JsonSerializerOptions TransitionDateJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public Task<V4ReferenceSummary> LocateAsync(V4RecordRefRequest request, CancellationToken token = default) =>
        MeasureAsync<V4ReferenceSummary>("reference-locate", async () =>
        {
            var resolved = await ResolveAnyAsync(request.Ref, null, request.IncludeDeleted, token)
                .ConfigureAwait(false);
            var continuityId = resolved.ResourceType == "Continuity" ? resolved.Id : resolved.ContinuityId;
            string? continuityName = null;
            if (continuityId is { } id)
            {
                await using var connection = connectionFactory.Create();
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var check = new AccessCommand(connection,
                        "SELECT [Name],[IsDeleted] FROM [Continuities] WHERE [Id]=?")
                    .Add(OleDbType.Integer, id);
                var rows = await check.QueryAsync(reader =>
                    (Name: reader.GetString(0), Deleted: reader.GetBoolean(1)), token)
                    .ConfigureAwait(false);
                if (rows.Count != 1 || (rows[0].Deleted && !request.IncludeDeleted))
                    throw new V4ResolutionException("record.deleted", "The record's continuity is unavailable.");
                continuityName = rows[0].Name;
            }
            return new(resolved.Reference, Kind(resolved.ResourceType), resolved.Label,
                ContinuityName: continuityName, IsDeleted: resolved.IsDeleted);
        });
    public Task<V4RecordOverview> GetAsync(V4GetRequest request, CancellationToken token = default) =>
        MeasureAsync<V4RecordOverview>("overview", () => coordinator.ExecuteConsistentReadAsync(async () =>
        {
            var continuity = session.RequireContinuityId();
            var revision = await RevisionAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.Ref))
                return await ContinuityOverviewAsync(continuity, request, revision, token).ConfigureAwait(false);
            var resolved = await ResolveAnyAsync(request.Ref, continuity, request.IncludeDeleted, token).ConfigureAwait(false);
            if (!Enum.TryParse<CanonEntityType>(resolved.ResourceType, true, out _))
                return await RecordOverviewAsync(resolved, request, revision, token).ConfigureAwait(false);
            var target = new V4ResolvedTarget(resolved.ResourceType,resolved.Id,resolved.Reference,resolved.Label,resolved.ContinuityId,resolved.IsDeleted);
            var details = await vault.GetEntityAsync(target.StorageKey, request.IncludeDeleted, token).ConfigureAwait(false)
                ?? throw new V4ResolutionException("record.not_found", "The record was not found.");
            var safe = await v3Mapper.DictionaryAsync(details.Fields, details.Summary.EntityType.ToString(), token).ConfigureAwait(false);
            var fields = ToJsonFields(safe, "EntityId", "ContinuityId", "DeletedOperationId").ToDictionary(pair => pair.Key, pair => pair.Value);
            if (target.ResourceType == "Project")
            {
                await using var connection = connectionFactory.Create();
                await connection.OpenAsync(token);
                using var boundaries = new AccessCommand(connection,
                    "SELECT [ProjectBoundary],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId] FROM [EntityEvents] WHERE [EntityId]=? AND [IsDeleted]=False AND [ProjectBoundary] IS NOT NULL")
                    .Add(OleDbType.Integer, target.StorageKey);
                var dates = await boundaries.QueryAsync(r => (Role: r.GetString(0), Date: ReadDate(r, 1, "Event")), token);
                if (dates.Count > 0)
                {
                    var begins = dates.SingleOrDefault(row => row.Role == "StoryBegins").Date;
                    var ends = dates.SingleOrDefault(row => row.Role == "StoryEnds").Date;
                    fields["storyRange"] = JsonSerializer.SerializeToElement(StoryDateView(ProjectSpan(begins, ends)), TransitionDateJson);
                    fields["storyBegins"] = JsonSerializer.SerializeToElement(begins is null ? null : StoryDateView(begins), TransitionDateJson);
                    fields["storyEnds"] = JsonSerializer.SerializeToElement(ends is null ? null : StoryDateView(ends), TransitionDateJson);
                }
            }
            var include = request.Include is { Count: > 0 } ? request.Include : DefaultEntitySections(target.ResourceType);
            if (include.Count > V4ContractLimits.MaximumIncludeSections)
                throw new VaultValidationException([new("include.too_many", "include", "Too many overview sections were requested.")]);
            var sections = new Dictionary<string, V4Section<V4ReferenceSummary>>(StringComparer.OrdinalIgnoreCase);
            foreach (var relation in include.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var limit = request.Limits?.TryGetValue(relation, out var configured) == true
                    ? configured : V4ContractLimits.DefaultRelationSectionSize;
                ValidateLimit(limit, V4ContractLimits.MaximumRelationSectionSize);
                var page = await RelatedEntityAsync(target, relation, request.IncludeDeleted ? V4DeletionState.All : V4DeletionState.Active, null, limit, token).ConfigureAwait(false);
                sections[relation] = new(page.Items, page.NextCursor, page.HasMore);
            }
            return new(EntitySummary(details.Summary, session.ContinuityName), fields, sections, revision);
        }, token));

    public Task<V4Page<V4ReferenceSummary>> RelatedAsync(V4ListRelatedRequest request, CancellationToken token = default) =>
        MeasureAsync<V4Page<V4ReferenceSummary>>("related", async () =>
        {
            var continuity = session.RequireContinuityId();
            ValidateLimit(request.Limit, V4ContractLimits.MaximumPageSize);
            if (string.IsNullOrWhiteSpace(request.Relation))
                throw new VaultValidationException([new("relation.required", "relation", "A relation name is required.")]);
            if (string.IsNullOrWhiteSpace(request.Ref))
                return await RelatedContinuityAsync(continuity, request, token).ConfigureAwait(false);
            var resolved = await ResolveAnyAsync(request.Ref, continuity, request.DeletionState != V4DeletionState.Active, token).ConfigureAwait(false);
            if (Enum.TryParse<CanonEntityType>(resolved.ResourceType, true, out _))
                return await RelatedEntityAsync(new(resolved.ResourceType,resolved.Id,resolved.Reference,resolved.Label,resolved.ContinuityId,resolved.IsDeleted), request.Relation, request.DeletionState, request.Cursor, request.Limit, token).ConfigureAwait(false);
            return await RelatedRecordAsync(resolved, request.Relation, request.DeletionState, request.Cursor, request.Limit, token).ConfigureAwait(false);
        });

    public Task<V4RecordSnapshotResult> SnapshotAsync(
        V4RecordSnapshotGetRequest request, CancellationToken token = default) =>
        MeasureAsync<V4RecordSnapshotResult>("record-snapshot", () =>
            coordinator.ExecuteConsistentReadAsync<V4RecordSnapshotResult>(async () =>
            {
                if (request.SnapshotVersion < 1)
                    throw new VaultValidationException([new("snapshot.version", "snapshotVersion",
                        "Snapshot version must be positive.")]);
                var continuity = session.RequireContinuityId();
                ResolvedVaultReference resolved;
                try
                {
                    resolved = await references.ResolveAsync(request.Ref, null, token)
                        .ConfigureAwait(false);
                }
                catch (ArgumentException exception)
                {
                    throw new V4ResolutionException("reference.invalid", exception.Message);
                }
                catch (KeyNotFoundException)
                {
                    throw new V4ResolutionException("record.not_found",
                        "The referenced record is unavailable.");
                }
                if (resolved.ResourceType == "Continuity" && resolved.Id != continuity ||
                    resolved.ContinuityId is { } scoped && scoped != continuity)
                    throw new V4ResolutionException("scope.mismatch",
                        "The historical page belongs to another continuity.");
                var snapshot = await new AccessV4PageSnapshotStore(connectionFactory)
                    .ReadAsync(resolved.ResourceType, resolved.Id, continuity,
                        request.SnapshotVersion, token).ConfigureAwait(false)
                    ?? throw new V4ResolutionException("snapshot.not_found",
                        "That historical page version is unavailable.");
                var latestRef = await references.ReferenceAsync(resolved.ResourceType,
                    resolved.Id, token).ConfigureAwait(false);
                return new(snapshot.Content.Overview, snapshot.Content.Notes,
                    snapshot.SnapshotVersion, snapshot.SavedAtUtc,
                    snapshot.IsBaseline, latestRef);
            }, token));

    public Task<V4RecordSnapshotListResult> SnapshotListAsync(
        V4RecordSnapshotListRequest request, CancellationToken token = default) =>
        MeasureAsync<V4RecordSnapshotListResult>("record-snapshot-list", () =>
            coordinator.ExecuteConsistentReadAsync(async () =>
            {
                if (request.Limit is < 1 or > 100 || request.BeforeVersion is < 1)
                    throw new VaultValidationException([new("snapshot.page", "limit",
                        "Limit must be 1 through 100 and beforeVersion must be positive.")]);
                var continuity = session.RequireContinuityId();
                ResolvedVaultReference resolved;
                try
                {
                    resolved = await references.ResolveAsync(request.Ref, null, token)
                        .ConfigureAwait(false);
                }
                catch (ArgumentException exception)
                {
                    throw new V4ResolutionException("reference.invalid", exception.Message);
                }
                catch (KeyNotFoundException)
                {
                    throw new V4ResolutionException("record.not_found",
                        "The referenced record is unavailable.");
                }
                if (resolved.ResourceType == "Continuity" && resolved.Id != continuity ||
                    resolved.ContinuityId is { } scoped && scoped != continuity)
                    throw new V4ResolutionException("scope.mismatch",
                        "The historical page belongs to another continuity.");
                return await new AccessV4PageSnapshotStore(connectionFactory)
                    .ListAsync(resolved.ResourceType, resolved.Id, continuity,
                        request.BeforeVersion, request.Limit, token).ConfigureAwait(false);
            }, token));

    private async Task<V4RecordOverview> ContinuityOverviewAsync(
        int continuity, V4GetRequest request, string revision, CancellationToken token)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                "SELECT c.[Name],c.[Description],c.[DefaultTimeZoneId],c.[Version],c.[IsDeleted],k.[CurrentInstantUtc],k.[ReferenceTimeZoneId],k.[Version] FROM [Continuities] AS c INNER JOIN [ContinuityClocks] AS k ON c.[Id]=k.[ContinuityId] WHERE c.[Id]=?")
            .Add(OleDbType.Integer, continuity);
        var rows = await command.QueryAsync(reader => new
        {
            Name = reader.GetString(0), Description = reader.IsDBNull(1) ? null : reader.GetString(1), Zone = reader.GetString(2),
            Version = reader.GetInt32(3), Deleted = reader.GetBoolean(4), Instant = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
            ReferenceZone = reader.IsDBNull(6) ? null : reader.GetString(6), ClockVersion = reader.GetInt32(7)
        }, token).ConfigureAwait(false);
        if (rows.Count != 1) throw new V4ResolutionException("record.not_found", "The selected continuity no longer exists.");
        var row = rows[0];
        var fields = ToJsonFields(new Dictionary<string, object?>
        {
            ["description"] = row.Description, ["defaultTimeZoneId"] = row.Zone,
            ["clock"] = Clock(row.Instant, row.ReferenceZone ?? row.Zone, "continuity"), ["clockVersion"] = row.ClockVersion
        });
        var sections = new Dictionary<string, V4Section<V4ReferenceSummary>>(StringComparer.OrdinalIgnoreCase);
        var include = request.Include is { Count: > 0 } ? request.Include : ["notes", "entities"];
        foreach (var relation in include.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var limit = request.Limits?.TryGetValue(relation, out var configured) == true ? configured : V4ContractLimits.DefaultRelationSectionSize;
            var page = await RelatedContinuityAsync(continuity, new(null, relation,
                request.IncludeDeleted ? V4DeletionState.All : V4DeletionState.Active,
                null, limit), token).ConfigureAwait(false);
            sections[relation] = new(page.Items, page.NextCursor, page.HasMore);
        }
        return new(new(references.ReferenceFromKnownRecord("Continuity", continuity, row.Name),
            V4RecordKind.Continuity, row.Name, ContinuityName: row.Name,
            Version: row.Version, IsDeleted: row.Deleted), fields, sections, revision);
    }

    private async Task<ResolvedVaultReference> ResolveAnyAsync(string reference,int? continuity,bool includeDeleted,CancellationToken token)
    {
        ResolvedVaultReference resolved;
        try { resolved=await references.ResolveAsync(reference,continuity,token).ConfigureAwait(false); }
        catch(ArgumentException e){throw new V4ResolutionException("reference.invalid",e.Message);}
        catch(KeyNotFoundException e){throw new V4ResolutionException("record.not_found",e.Message);}
        if(resolved.IsDeleted&&!includeDeleted&&resolved.ResourceType=="CharacterRelationship")
        {
            await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);
            using var redirect=new AccessCommand(connection,"SELECT [TargetRelationshipId] FROM [RelationshipMergeRedirects] WHERE [SourceRelationshipId]=?")
                .Add(OleDbType.Integer,resolved.Id);
            var target=await redirect.ExecuteScalarAsync(token).ConfigureAwait(false);
            if(target is not null and not DBNull)
            {
                var targetRef=await references.ReferenceAsync("CharacterRelationship",Convert.ToInt32(target),token).ConfigureAwait(false);
                resolved=await references.ResolveAsync(targetRef,continuity,token,"CharacterRelationship").ConfigureAwait(false);
            }
        }
        if (resolved.IsDeleted && !includeDeleted && resolved.ResourceType == "RelationshipParticipant")
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var merged = new AccessCommand(connection,
                "SELECT m.[TargetRelationshipId],s.[CharacterId] FROM [RelationshipParticipants] AS s " +
                "INNER JOIN [RelationshipMergeRedirects] AS m ON s.[RelationshipId]=m.[SourceRelationshipId] " +
                "WHERE s.[Id]=?")
                .Add(OleDbType.Integer, resolved.Id);
            var destinations = await merged.QueryAsync(reader =>
                (Relationship: reader.GetInt32(0), Character: reader.GetInt32(1)), token).ConfigureAwait(false);
            if (destinations.Count == 1)
            {
                using var replacement = new AccessCommand(connection,
                    "SELECT [Id] FROM [RelationshipParticipants] " +
                    "WHERE [RelationshipId]=? AND [CharacterId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, destinations[0].Relationship)
                    .Add(OleDbType.Integer, destinations[0].Character);
                var target = await replacement.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (target is not null and not DBNull)
                {
                    var targetRef = await references.ReferenceAsync("RelationshipParticipant", Convert.ToInt32(target), token)
                        .ConfigureAwait(false);
                    resolved = await references.ResolveAsync(targetRef, continuity, token, "RelationshipParticipant")
                        .ConfigureAwait(false);
                }
            }
        }
        if(resolved.IsDeleted&&!includeDeleted)throw new V4ResolutionException("record.deleted","The referenced record is deleted. Include deleted records to read it.");
        if(continuity is not null && resolved.ContinuityId is { } recordContinuity&&recordContinuity!=continuity)throw new V4ResolutionException("scope.mismatch","The record belongs to another continuity.");
        return resolved;
    }

    private async Task<V4RecordOverview> RecordOverviewAsync(ResolvedVaultReference target,V4GetRequest request,string revision,CancellationToken token)
    {
        var descriptor=target.ResourceType switch
        {
            "VariantGroup"=>("VariantGroups","Id"),"Source"=>("Sources","Id"),"SourceSnapshot"=>("SourceSnapshots","Id"),
            "Tag"=>("Tags","Id"),"EntityNote"=>("EntityNotes","Id"),"ContinuityNote"=>("ContinuityNotes","Id"),
            "EntityEvent"=>("EntityEvents","Id"),"Claim"=>("Claims","Id"),"EntityImage"=>("EntityImages","Id"),"StoryImage"=>("StoryImages","Id"),
            "RelationshipEvent"=>("RelationshipEvents","Id"),
            "RelationshipParticipant"=>("RelationshipParticipants","Id"),
            "RelationshipMembershipPeriod"=>("RelationshipMembershipPeriods","Id"),
            "RelationshipEventProject"=>("RelationshipEventProjects","Id"),
            "RelationshipType"=>("RelationshipTypes","Id"),"OwnershipPrincipal"=>("OwnershipPrincipals","Id"),
            "CharacterRelationship"=>("CharacterRelationships","Id"),"CharacterResidence"=>("CharacterResidences","Id"),
            "OrganizationMembership"=>("OrganizationMemberships","Id"),"OrganizationLocation"=>("OrganizationLocations","Id"),
            "ObjectOwnershipPeriod"=>("ObjectOwnershipPeriods","Id"),"ObjectCustodyPeriod"=>("ObjectCustodyPeriods","Id"),
            "ObjectLocationPeriod"=>("ObjectLocationPeriods","Id"),"WorldEventParticipant"=>("WorldEventParticipants","Id"),
            "WorldEventLocation"=>("WorldEventLocations","Id"),"CharacterTemporalEffect"=>("CharacterTemporalEffects","Id"),
            _=>throw new V4ResolutionException("record.overview_unsupported","That record kind does not have a public overview.")
        };
        await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);
        using var command=new AccessCommand(connection,$"SELECT * FROM [{descriptor.Item1}] WHERE [{descriptor.Item2}]=?").Add(OleDbType.Integer,target.Id);
        var rows=await command.QueryAsync(ReadFields,token).ConfigureAwait(false);
        if(rows.Count!=1)throw new V4ResolutionException("record.not_found","The record was not found.");
        var mapped=await v3Mapper.DictionaryAsync(rows[0],target.ResourceType,token).ConfigureAwait(false);
        var fields=mapped.Where(pair=>IsPublicField(target.ResourceType,pair.Key)).ToDictionary(
            pair=>char.ToLowerInvariant(pair.Key[0])+pair.Key[1..],pair=>JsonSerializer.SerializeToElement(pair.Value),StringComparer.OrdinalIgnoreCase);
        if (target.ResourceType == "RelationshipMembershipPeriod")
        {
            using var descriptions = new AccessCommand(connection,
                "SELECT d.[TransitionId],d.[Description] FROM " +
                "[RelationshipTransitionDescriptions] AS d INNER JOIN " +
                "[RelationshipMembershipTransitions] AS t ON d.[TransitionId]=t.[Id] " +
                "WHERE t.[MembershipPeriodId]=?").Add(OleDbType.Integer, target.Id);
            var descriptionsById = (await descriptions.QueryAsync(reader =>
                (Id: reader.GetInt32(0), Text: reader.GetString(1)), token).ConfigureAwait(false))
                .ToDictionary(item => item.Id, item => item.Text);
            using var transitions = new AccessCommand(connection,
                "SELECT [Id],[TransitionKind],[OccurredKind],[OccurredLowerBound],[OccurredUpperBound]," +
                "[OccurredLowerInclusive],[OccurredUpperInclusive],[OccurredOriginalText],[OccurredCalendarId] " +
                "FROM [RelationshipMembershipTransitions] WHERE [MembershipPeriodId]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, target.Id);
            var dates = await transitions.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Kind = reader.GetString(1),
                Date = new StoryDate(Enum.Parse<StoryDateKind>(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    reader.GetBoolean(5), reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8))
            }, token).ConfigureAwait(false);
            foreach (var date in dates)
            {
                fields[date.Kind == "Join" ? "joined" : "left"] =
                    JsonSerializer.SerializeToElement(StoryDateView(date.Date), TransitionDateJson);
                if (descriptionsById.TryGetValue(date.Id, out var text))
                    fields[date.Kind == "Join" ? "joinDescription" : "leaveDescription"] =
                        JsonSerializer.SerializeToElement(text);
            }
        }
        if (target.ResourceType == "OrganizationMembership")
        {
            using var transitions = new AccessCommand(connection,
                "SELECT [TransitionKind],[Description],[OccurredKind],[OccurredLowerBound]," +
                "[OccurredUpperBound],[OccurredLowerInclusive],[OccurredUpperInclusive]," +
                "[OccurredOriginalText],[OccurredCalendarId] " +
                "FROM [OrganizationMembershipTransitions] WHERE [MembershipId]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, target.Id);
            var dates = await transitions.QueryAsync(reader => new
            {
                Kind = reader.GetString(0), Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                Date = new StoryDate(Enum.Parse<StoryDateKind>(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    reader.GetBoolean(5), reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8))
            }, token).ConfigureAwait(false);
            foreach (var date in dates)
            {
                fields[date.Kind == "Join" ? "joined" : "left"] =
                    JsonSerializer.SerializeToElement(StoryDateView(date.Date), TransitionDateJson);
                if (date.Description is not null)
                    fields[date.Kind == "Join" ? "joinDescription" : "leaveDescription"] =
                        JsonSerializer.SerializeToElement(date.Description);
            }
        }
        var include=request.Include is {Count:>0}?request.Include:DefaultRecordSections(target.ResourceType);
        if(include.Count>V4ContractLimits.MaximumIncludeSections)throw new VaultValidationException([new("include.too_many","include","Too many overview sections were requested.")]);
        var sections=new Dictionary<string,V4Section<V4ReferenceSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach(var relation in include.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var limit=request.Limits?.TryGetValue(relation,out var configured)==true?configured:V4ContractLimits.DefaultRelationSectionSize;
            ValidateLimit(limit,V4ContractLimits.MaximumRelationSectionSize);
            var page=await RelatedRecordAsync(target,relation,request.IncludeDeleted?V4DeletionState.All:V4DeletionState.Active,null,limit,token).ConfigureAwait(false);
            sections[relation]=new(page.Items,page.NextCursor,page.HasMore);
        }
        var displayLabel = target.ResourceType == "CharacterRelationship"
            ? await RelationshipOverviewLabelAsync(connection, target.Id, target.Label, target.IsDeleted, token)
                .ConfigureAwait(false)
            : target.ResourceType == "RelationshipMembershipPeriod"
                ? await MembershipOverviewLabelAsync(connection, target.Id, target.Label, token)
                    .ConfigureAwait(false)
            : target.Label;
        return new(new(target.Reference,Kind(target.ResourceType),displayLabel,ContinuityName:target.ContinuityId is null?null:session.ContinuityName,
            Version:TryInteger(rows[0],"Version"),IsDeleted:target.IsDeleted),fields,sections,revision);
    }

    private static async Task<string> RelationshipOverviewLabelAsync(
        OleDbConnection connection, int relationshipId, string fallback, bool includeDeletedMembers,
        CancellationToken token)
    {
        using var typeQuery = new AccessCommand(connection,
            "SELECT t.[Name] FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t " +
            "ON r.[RelationshipTypeId]=t.[Id] WHERE r.[Id]=?")
            .Add(OleDbType.Integer, relationshipId);
        var type = await typeQuery.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        using var members = new AccessCommand(connection,
            "SELECT ch.[GivenName],ch.[PreferredName] FROM [RelationshipParticipants] AS p " +
            "INNER JOIN [Characters] AS ch ON p.[CharacterId]=ch.[EntityId] " +
            $"WHERE p.[RelationshipId]=?{(includeDeletedMembers ? "" : " AND p.[IsDeleted]=False")} ORDER BY p.[Id]")
            .Add(OleDbType.Integer, relationshipId);
        var names = await members.QueryAsync(reader => reader.IsDBNull(1)
            ? reader.GetString(0) : reader.GetString(1), token).ConfigureAwait(false);
        if (type is null || names.Count < 2) return fallback;
        if (names.Count == 2) return $"{names[0]} — {type} — {names[1]}";
        return $"{string.Join(", ", names.Take(3))}{(names.Count > 3 ? $" +{names.Count - 3} more" : "")} — {type}";
    }

    private static async Task<string> MembershipOverviewLabelAsync(
        OleDbConnection connection, int periodId, string fallback, CancellationToken token)
    {
        using var command = new AccessCommand(connection,
            "SELECT ch.[GivenName],ch.[PreferredName] " +
            "FROM ([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p " +
            "ON m.[ParticipantId]=p.[Id]) INNER JOIN [Characters] AS ch " +
            "ON p.[CharacterId]=ch.[EntityId] WHERE m.[Id]=?")
            .Add(OleDbType.Integer, periodId);
        var names = await command.QueryAsync(reader =>
            reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1), token).ConfigureAwait(false);
        return names.Count == 1 ? $"{names[0]}'s membership" : fallback;
    }

    private async Task<V4Page<V4ReferenceSummary>> RelatedRecordAsync(ResolvedVaultReference target,string requestedRelation,V4DeletionState deletion,string? cursor,int limit,CancellationToken token)
    {
        var relation=requestedRelation.Trim().ToUpperInvariant();var revision=await RevisionAsync(token).ConfigureAwait(false);
        var scope=$"r:{target.Reference}:{relation}:{deletion}";var after=cursor is null?0:cursors.Decode(cursor,"related",scope).Position;
        await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);
        var rows=new List<RelationRow>();
        async Task Add(string type,string sql,params (OleDbType Type,object? Value)[] parameters)
        {
            using var query=new AccessCommand(connection,sql);foreach(var parameter in parameters)query.Add(parameter.Type,parameter.Value);
            rows.AddRange(await query.QueryAsync(r=>new RelationRow(r.GetInt32(0),type,r.IsDBNull(1)?type:r.GetString(1),null,
                r.FieldCount>2&&!r.IsDBNull(2)?r.GetInt32(2):null,r.FieldCount>3&&r.GetBoolean(3),
                Role:r.FieldCount>4&&!r.IsDBNull(4)?r.GetString(4):null,
                Notes:r.FieldCount>5&&!r.IsDBNull(5)?r.GetString(5):null),token).ConfigureAwait(false));
        }
        var state=DeletionSql("r",deletion);
        switch((target.ResourceType.ToUpperInvariant(),relation))
        {
            case ("SOURCE","TAGS"):await Add("Tag","SELECT r.[Id],r.[Name],r.[Version],r.[IsDeleted] FROM [SourceTags] AS x INNER JOIN [Tags] AS r ON x.[TagId]=r.[Id] WHERE x.[SourceId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("SOURCE","SNAPSHOTS"):await Add("SourceSnapshot","SELECT r.[Id],IIf(r.[RetrievedAtUtc] Is Null,'snapshot',CStr(r.[RetrievedAtUtc])),r.[Version],r.[IsDeleted] FROM [SourceSnapshots] AS r WHERE r.[SourceId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("SOURCE","NOTES"):
                await Add("EntityNote","SELECT r.[Id],IIf(r.[Title] Is Null,'note',r.[Title]),r.[Version],r.[IsDeleted] FROM ([NoteSources] AS x INNER JOIN [EntityNotes] AS r ON x.[NoteId]=r.[Id]) INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id] WHERE x.[SourceId]=? AND c.[ContinuityId]=?"+state,(OleDbType.Integer,target.Id),(OleDbType.Integer,session.RequireContinuityId()));
                await Add("ContinuityNote","SELECT r.[Id],IIf(r.[Title] Is Null,'note',r.[Title]),r.[Version],r.[IsDeleted] FROM [ContinuityNoteSources] AS x INNER JOIN [ContinuityNotes] AS r ON x.[ContinuityNoteId]=r.[Id] WHERE x.[SourceId]=? AND r.[ContinuityId]=?"+state,(OleDbType.Integer,target.Id),(OleDbType.Integer,session.RequireContinuityId()));break;
            case ("SOURCE","CLAIMS"):await Add("Claim","SELECT r.[Id],Left(r.[ClaimText],255),r.[Version],r.[IsDeleted] FROM [ClaimSources] AS x INNER JOIN [Claims] AS r ON x.[ClaimId]=r.[Id] WHERE x.[SourceId]=? AND r.[ContinuityId]=?"+state,(OleDbType.Integer,target.Id),(OleDbType.Integer,session.RequireContinuityId()));break;
            case ("SOURCE","IMAGES"):
                await Add("EntityImage","SELECT r.[Id],IIf(r.[Title] Is Null,'image',r.[Title]),r.[Version],r.[IsDeleted] FROM [EntityImages] AS r INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id] WHERE r.[SourceId]=? AND c.[ContinuityId]=? AND c.[IsDeleted]=False"+state,(OleDbType.Integer,target.Id),(OleDbType.Integer,session.RequireContinuityId()));
                await Add("StoryImage","SELECT r.[Id],IIf(r.[Title] Is Null,'image',r.[Title]),r.[Version],r.[IsDeleted] FROM [StoryImages] AS r WHERE r.[SourceId]=? AND r.[ContinuityId]=? AND " +
                    "(r.[OwnerKind]='Continuity' OR " +
                    "(r.[OwnerKind]='Relationship' AND EXISTS (SELECT * FROM [CharacterRelationships] AS owner WHERE owner.[Id]=r.[RelationshipId] AND owner.[IsDeleted]=False)) OR " +
                    "(r.[OwnerKind]='EntityEvent' AND EXISTS (SELECT * FROM [EntityEvents] AS owner INNER JOIN [CanonEntities] AS entity ON owner.[EntityId]=entity.[Id] WHERE owner.[Id]=r.[EntityEventId] AND owner.[IsDeleted]=False AND entity.[IsDeleted]=False)) OR " +
                    "(r.[OwnerKind]='RelationshipEvent' AND EXISTS (SELECT * FROM [RelationshipEvents] AS owner INNER JOIN [CharacterRelationships] AS parent ON owner.[RelationshipId]=parent.[Id] WHERE owner.[Id]=r.[RelationshipEventId] AND owner.[IsDeleted]=False AND parent.[IsDeleted]=False)))"+state,(OleDbType.Integer,target.Id),(OleDbType.Integer,session.RequireContinuityId()));break;
            case ("SOURCE","ENTITIES"):
            {
                using var ids=new AccessCommand(connection,"SELECT [EntityId] FROM [EntitySources] WHERE [SourceId]=?").Add(OleDbType.Integer,target.Id);
                rows.AddRange(await EntityRowsAsync(connection,(await ids.QueryAsync(r=>r.GetInt32(0),token)).Distinct().ToArray(),deletion,token).ConfigureAwait(false));break;
            }
            case ("TAG","TARGETS"):
            {
                using var entities=new AccessCommand(connection,"SELECT [EntityId] FROM [EntityTags] WHERE [TagId]=?").Add(OleDbType.Integer,target.Id);
                rows.AddRange(await EntityRowsAsync(connection,(await entities.QueryAsync(r=>r.GetInt32(0),token)).Distinct().ToArray(),deletion,token).ConfigureAwait(false));
                await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [SourceTags] AS x INNER JOIN [Sources] AS r ON x.[SourceId]=r.[Id] WHERE x.[TagId]=?"+state,(OleDbType.Integer,target.Id));break;
            }
            case ("ENTITYNOTE","SOURCES"):await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [NoteSources] AS x INNER JOIN [Sources] AS r ON x.[SourceId]=r.[Id] WHERE x.[NoteId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("CONTINUITYNOTE","SOURCES"):await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [ContinuityNoteSources] AS x INNER JOIN [Sources] AS r ON x.[SourceId]=r.[Id] WHERE x.[ContinuityNoteId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("CLAIM","SOURCES"):await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [ClaimSources] AS x INNER JOIN [Sources] AS r ON x.[SourceId]=r.[Id] WHERE x.[ClaimId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("CLAIM","TARGETS"):
            {
                using var ids=new AccessCommand(connection,"SELECT [EntityId] FROM [ClaimEntities] WHERE [ClaimId]=?").Add(OleDbType.Integer,target.Id);
                rows.AddRange(await EntityRowsAsync(connection,(await ids.QueryAsync(r=>r.GetInt32(0),token)).Distinct().ToArray(),deletion,token).ConfigureAwait(false));
                await Add("CharacterRelationship","SELECT r.[Id],t.[Name],r.[Version],r.[IsDeleted] " +
                    "FROM ([ClaimRelationships] AS x INNER JOIN [CharacterRelationships] AS r " +
                    "ON x.[RelationshipId]=r.[Id]) INNER JOIN [RelationshipTypes] AS t " +
                    "ON r.[RelationshipTypeId]=t.[Id] WHERE x.[ClaimId]=?"+state,
                    (OleDbType.Integer,target.Id));
                await Add("CharacterRelationship","SELECT r.[Id],t.[Name],r.[Version],r.[IsDeleted] " +
                    "FROM (([ClaimRelationships] AS x INNER JOIN [RelationshipMergeRedirects] AS m " +
                    "ON x.[RelationshipId]=m.[SourceRelationshipId]) INNER JOIN [CharacterRelationships] AS r " +
                    "ON m.[TargetRelationshipId]=r.[Id]) INNER JOIN [RelationshipTypes] AS t " +
                    "ON r.[RelationshipTypeId]=t.[Id] WHERE x.[ClaimId]=?"+state,
                    (OleDbType.Integer,target.Id));break;
            }
            case ("ENTITYIMAGE","OWNER"):
            {
                using var id=new AccessCommand(connection,"SELECT [EntityId] FROM [EntityImages] WHERE [Id]=?").Add(OleDbType.Integer,target.Id);var value=await id.ExecuteScalarAsync(token).ConfigureAwait(false);
                if(value is not null and not DBNull)rows.AddRange(await EntityRowsAsync(connection,[Convert.ToInt32(value,CultureInfo.InvariantCulture)],deletion,token).ConfigureAwait(false));break;
            }
            case ("ENTITYIMAGE","SOURCE"):await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [EntityImages] AS i INNER JOIN [Sources] AS r ON i.[SourceId]=r.[Id] WHERE i.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("STORYIMAGE","OWNER"):
            {
                using var owner = new AccessCommand(connection,
                    "SELECT [OwnerKind],[ContinuityId],[RelationshipId],[EntityEventId],[RelationshipEventId] FROM [StoryImages] WHERE [Id]=?")
                    .Add(OleDbType.Integer,target.Id);
                var owners = await owner.QueryAsync(r => (
                    Kind:r.GetString(0), Continuity:r.GetInt32(1),
                    Relationship:r.IsDBNull(2)?(int?)null:r.GetInt32(2),
                    EntityEvent:r.IsDBNull(3)?(int?)null:r.GetInt32(3),
                    RelationshipEvent:r.IsDBNull(4)?(int?)null:r.GetInt32(4)),token).ConfigureAwait(false);
                if (owners.Count != 1) break;
                var imageOwner = owners[0];
                switch (imageOwner.Kind)
                {
                    case "Continuity":
                        await Add("Continuity","SELECT [Id],[Name],[Version],[IsDeleted] FROM [Continuities] WHERE [Id]=?",(OleDbType.Integer,imageOwner.Continuity));break;
                    case "Relationship":
                        await Add("CharacterRelationship","SELECT r.[Id],t.[Name],r.[Version],r.[IsDeleted] FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] WHERE r.[Id]=?",(OleDbType.Integer,imageOwner.Relationship));break;
                    case "EntityEvent":
                        await Add("EntityEvent","SELECT [Id],[Title],[Version],[IsDeleted] FROM [EntityEvents] WHERE [Id]=?",(OleDbType.Integer,imageOwner.EntityEvent));break;
                    case "RelationshipEvent":
                        await Add("RelationshipEvent","SELECT [Id],[Title],[Version],[IsDeleted] FROM [RelationshipEvents] WHERE [Id]=?",(OleDbType.Integer,imageOwner.RelationshipEvent));break;
                }
                break;
            }
            case ("STORYIMAGE","SOURCE"):await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [StoryImages] AS i INNER JOIN [Sources] AS r ON i.[SourceId]=r.[Id] WHERE i.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("CHARACTERRELATIONSHIP","CHARACTERS"):
            {
                using var ids=new AccessCommand(connection,"SELECT [CharacterId] FROM [RelationshipParticipants] WHERE [RelationshipId]=?"+
                    (target.IsDeleted&&deletion==V4DeletionState.All?"":" AND [IsDeleted]=False")+" ORDER BY [Id]")
                    .Add(OleDbType.Integer,target.Id);
                var values=await ids.QueryAsync(r=>r.GetInt32(0),token);
                using var anchors=new AccessCommand(connection,"SELECT [SourceCharacterId],[TargetCharacterId] FROM [CharacterRelationships] WHERE [Id]=?")
                    .Add(OleDbType.Integer,target.Id);
                var pair=await anchors.QueryAsync(r=>(Source:r.GetInt32(0),Target:r.GetInt32(1)),token);
                if(values.Count>0)rows.AddRange((await EntityRowsAsync(connection,values.Distinct().ToArray(),deletion,token).ConfigureAwait(false))
                    .Select(row=>row with{Context=pair.Count==0?"Participant":row.Id==pair[0].Source?"Source character":row.Id==pair[0].Target?"Target character":"Participant"}));break;
            }
            case ("CHARACTERRELATIONSHIP","TYPE"):await Add("RelationshipType","SELECT r.[Id],r.[Name],r.[Version],r.[IsDeleted] FROM [CharacterRelationships] AS x INNER JOIN [RelationshipTypes] AS r ON x.[RelationshipTypeId]=r.[Id] WHERE x.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("CHARACTERRELATIONSHIP","EVENTS"):
                await Add("RelationshipEvent","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [RelationshipEvents] AS r WHERE r.[RelationshipId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("CHARACTERRELATIONSHIP","MEMBERSHIPPERIODS"):
                await Add("RelationshipMembershipPeriod","SELECT m.[Id],IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]) & ' membership',m.[Version],m.[IsDeleted] FROM ([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) INNER JOIN [Characters] AS ch ON p.[CharacterId]=ch.[EntityId] WHERE p.[RelationshipId]=?"+DeletionSql("m",deletion),(OleDbType.Integer,target.Id));break;
            case ("CHARACTERRELATIONSHIP","MERGEDINTO"):
                await Add("CharacterRelationship","SELECT r.[Id],t.[Name],r.[Version],r.[IsDeleted] " +
                    "FROM ([RelationshipMergeRedirects] AS x INNER JOIN [CharacterRelationships] AS r " +
                    "ON x.[TargetRelationshipId]=r.[Id]) INNER JOIN [RelationshipTypes] AS t " +
                    "ON r.[RelationshipTypeId]=t.[Id] WHERE x.[SourceRelationshipId]=?",
                    (OleDbType.Integer,target.Id));break;
            case ("CHARACTERRELATIONSHIP","MERGEDSOURCES"):
                await Add("CharacterRelationship","SELECT r.[Id],t.[Name],r.[Version],r.[IsDeleted] " +
                    "FROM ([RelationshipMergeRedirects] AS x INNER JOIN [CharacterRelationships] AS r " +
                    "ON x.[SourceRelationshipId]=r.[Id]) INNER JOIN [RelationshipTypes] AS t " +
                    "ON r.[RelationshipTypeId]=t.[Id] WHERE x.[TargetRelationshipId]=?",
                    (OleDbType.Integer,target.Id));break;
            case ("CHARACTERRELATIONSHIP","CLAIMS"):
                await Add("Claim","SELECT r.[Id],Left(r.[ClaimText],255),r.[Version],r.[IsDeleted] " +
                    "FROM [ClaimRelationships] AS x INNER JOIN [Claims] AS r ON x.[ClaimId]=r.[Id] " +
                    "WHERE x.[RelationshipId]=?"+state,(OleDbType.Integer,target.Id));
                await Add("Claim","SELECT r.[Id],Left(r.[ClaimText],255),r.[Version],r.[IsDeleted] " +
                    "FROM ([RelationshipMergeRedirects] AS m INNER JOIN [ClaimRelationships] AS x " +
                    "ON m.[SourceRelationshipId]=x.[RelationshipId]) INNER JOIN [Claims] AS r " +
                    "ON x.[ClaimId]=r.[Id] WHERE m.[TargetRelationshipId]=?"+state,
                    (OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPEVENT","RELATIONSHIP"):
                await Add("CharacterRelationship","SELECT r.[Id],'relationship',r.[Version],r.[IsDeleted] FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] WHERE e.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPEVENT","WORLDEVENT"):
                await Add("WorldEvent","SELECT r.[EntityId],r.[Title],c.[Version],c.[IsDeleted] FROM ([RelationshipEvents] AS e INNER JOIN [WorldEvents] AS r ON e.[WorldEventId]=r.[EntityId]) INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id] WHERE e.[Id]=?"+DeletionSql("c",deletion),(OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPEVENT","PROJECTS"):
                await Add("Project","SELECT r.[EntityId],r.[Name],c.[Version],c.[IsDeleted],x.[Role],x.[Notes] FROM (([RelationshipEventProjects] AS x INNER JOIN [Projects] AS r ON x.[ProjectId]=r.[EntityId]) INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id]) WHERE x.[RelationshipEventId]=? AND x.[IsDeleted]=False"+DeletionSql("c",deletion),(OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPPARTICIPANT","RELATIONSHIP"):
                await Add("CharacterRelationship","SELECT r.[Id],'relationship',r.[Version],r.[IsDeleted] FROM [RelationshipParticipants] AS p INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id] WHERE p.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPPARTICIPANT","CHARACTER"):
            {
                using var member=new AccessCommand(connection,"SELECT [CharacterId] FROM [RelationshipParticipants] WHERE [Id]=?").Add(OleDbType.Integer,target.Id);
                var value=await member.ExecuteScalarAsync(token).ConfigureAwait(false);
                if(value is not null and not DBNull)rows.AddRange(await EntityRowsAsync(connection,[Convert.ToInt32(value,CultureInfo.InvariantCulture)],deletion,token).ConfigureAwait(false));break;
            }
            case ("RELATIONSHIPPARTICIPANT","MEMBERSHIPPERIODS"):
                await Add("RelationshipMembershipPeriod","SELECT r.[Id],'membership period',r.[Version],r.[IsDeleted] FROM [RelationshipMembershipPeriods] AS r WHERE r.[ParticipantId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPMEMBERSHIPPERIOD","PARTICIPANT"):
                await Add("RelationshipParticipant","SELECT p.[Id],IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]),p.[Version],p.[IsDeleted] FROM ([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) INNER JOIN [Characters] AS ch ON p.[CharacterId]=ch.[EntityId] WHERE m.[Id]=?"+DeletionSql("p",deletion),(OleDbType.Integer,target.Id));break;
            case ("RELATIONSHIPMEMBERSHIPPERIOD","RELATIONSHIP"):
            {
                using var query = new AccessCommand(connection,
                    "SELECT r.[Id],r.[Version],r.[IsDeleted] FROM " +
                    "([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p " +
                    "ON m.[ParticipantId]=p.[Id]) INNER JOIN [CharacterRelationships] AS r " +
                    "ON p.[RelationshipId]=r.[Id] WHERE m.[Id]=?" + DeletionSql("r", deletion))
                    .Add(OleDbType.Integer, target.Id);
                var parents = await query.QueryAsync(reader => new
                {
                    Id = reader.GetInt32(0), Version = reader.GetInt32(1),
                    Deleted = reader.GetBoolean(2)
                }, token).ConfigureAwait(false);
                if (parents.Count == 1)
                    rows.Add(new RelationRow(parents[0].Id, "CharacterRelationship",
                        await RelationshipOverviewLabelAsync(connection, parents[0].Id,
                            "Relationship", parents[0].Deleted, token).ConfigureAwait(false),
                        null, parents[0].Version, parents[0].Deleted));
                break;
            }
            case ("SOURCESNAPSHOT","SOURCE"):await Add("Source","SELECT r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [SourceSnapshots] AS x INNER JOIN [Sources] AS r ON x.[SourceId]=r.[Id] WHERE x.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("ENTITYNOTE","OWNER"):
            case ("ENTITYEVENT","OWNER"):
            case ("CHARACTERTEMPORALEFFECT","CHARACTER"):
            {
                var table=target.ResourceType.Equals("EntityNote",StringComparison.OrdinalIgnoreCase)?"EntityNotes":target.ResourceType.Equals("EntityEvent",StringComparison.OrdinalIgnoreCase)?"EntityEvents":"CharacterTemporalEffects";
                var column=target.ResourceType.Equals("CharacterTemporalEffect",StringComparison.OrdinalIgnoreCase)?"CharacterId":"EntityId";
                using var id=new AccessCommand(connection,$"SELECT [{column}] FROM [{table}] WHERE [Id]=?").Add(OleDbType.Integer,target.Id);var value=await id.ExecuteScalarAsync(token).ConfigureAwait(false);
                if(value is not null and not DBNull)rows.AddRange(await EntityRowsAsync(connection,[Convert.ToInt32(value,CultureInfo.InvariantCulture)],deletion,token).ConfigureAwait(false));break;
            }
            case ("ENTITYEVENT","WORLDEVENT"):await Add("WorldEvent","SELECT r.[EntityId],r.[Title],c.[Version],c.[IsDeleted] FROM ([EntityEvents] AS x INNER JOIN [WorldEvents] AS r ON x.[WorldEventId]=r.[EntityId]) INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id] WHERE x.[Id]=?"+DeletionSql("c",deletion),(OleDbType.Integer,target.Id));break;
            case ("ENTITYEVENT","PROJECTS"):await Add("Project","SELECT r.[EntityId],r.[Name],c.[Version],c.[IsDeleted],x.[Role],x.[Notes] FROM (([EntityEventProjects] AS x INNER JOIN [Projects] AS r ON x.[ProjectId]=r.[EntityId]) INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id]) WHERE x.[EntityEventId]=? AND x.[IsDeleted]=False"+DeletionSql("c",deletion),(OleDbType.Integer,target.Id));break;
            case ("CHARACTERTEMPORALEFFECT","WORLDEVENT"):await Add("WorldEvent","SELECT r.[EntityId],r.[Title],c.[Version],c.[IsDeleted] FROM ([CharacterTemporalEffects] AS x INNER JOIN [WorldEvents] AS r ON x.[WorldEventId]=r.[EntityId]) INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id] WHERE x.[Id]=?"+DeletionSql("c",deletion),(OleDbType.Integer,target.Id));break;
            case ("CHARACTERRESIDENCE","CHARACTER"):
            case ("CHARACTERRESIDENCE","LOCATION"):
            case ("ORGANIZATIONMEMBERSHIP","ORGANIZATION"):
            case ("ORGANIZATIONMEMBERSHIP","CHARACTER"):
            case ("ORGANIZATIONLOCATION","ORGANIZATION"):
            case ("ORGANIZATIONLOCATION","LOCATION"):
            case ("OBJECTOWNERSHIPPERIOD","OBJECT"):
            case ("OBJECTCUSTODYPERIOD","OBJECT"):
            case ("OBJECTLOCATIONPERIOD","OBJECT"):
            case ("OBJECTLOCATIONPERIOD","LOCATION"):
            case ("WORLDEVENTPARTICIPANT","WORLDEVENT"):
            case ("WORLDEVENTPARTICIPANT","PARTICIPANT"):
            case ("WORLDEVENTLOCATION","WORLDEVENT"):
            case ("WORLDEVENTLOCATION","LOCATION"):
            {
                var link=RecordEntityLink(target.ResourceType,relation);
                using var id=new AccessCommand(connection,$"SELECT [{link.Column}] FROM [{link.Table}] WHERE [Id]=?").Add(OleDbType.Integer,target.Id);var value=await id.ExecuteScalarAsync(token).ConfigureAwait(false);
                if(value is not null and not DBNull)rows.AddRange(await EntityRowsAsync(connection,[Convert.ToInt32(value,CultureInfo.InvariantCulture)],deletion,token).ConfigureAwait(false));break;
            }
            case ("OBJECTOWNERSHIPPERIOD","OWNERS"):await Add("OwnershipPrincipal","SELECT r.[Id],IIf(r.[Label] Is Null,r.[PrincipalKind],r.[Label]),r.[Version],r.[IsDeleted] FROM [ObjectOwnershipOwners] AS x INNER JOIN [OwnershipPrincipals] AS r ON x.[PrincipalId]=r.[Id] WHERE x.[OwnershipPeriodId]=?"+state,(OleDbType.Integer,target.Id));break;
            case ("OBJECTCUSTODYPERIOD","PRINCIPAL"):await Add("OwnershipPrincipal","SELECT r.[Id],IIf(r.[Label] Is Null,r.[PrincipalKind],r.[Label]),r.[Version],r.[IsDeleted] FROM [ObjectCustodyPeriods] AS x INNER JOIN [OwnershipPrincipals] AS r ON x.[PrincipalId]=r.[Id] WHERE x.[Id]=?"+state,(OleDbType.Integer,target.Id));break;
            default:throw new V4ResolutionException("relation.unsupported",$"Relation '{relation}' is not supported for {target.ResourceType}.");
        }
        var ordered=rows.GroupBy(row=>(row.Type,row.Id)).Select(group=>group.First()).OrderBy(row=>SearchKey(Kind(row.Type),row.Id)).Where(row=>SearchKey(Kind(row.Type),row.Id)>after).Take(limit+1).ToArray();
        var page=ordered.Take(limit).Select(row=>new V4ReferenceSummary(references.ReferenceFromKnownRecord(row.Type,row.Id,row.Label),Kind(row.Type),row.Label,row.Context,
            row.Type is "Source" or "Tag"?null:session.ContinuityName,row.Version,row.Deleted,row.Role,row.Notes)).ToArray();
        return new(page,ordered.Length>limit?cursors.Encode("related",SearchKey(Kind(ordered[limit-1].Type),ordered[limit-1].Id),scope):null,ordered.Length>limit,revision);
    }

    private async Task<IReadOnlyList<RelationRow>> EntityRowsAsync(OleDbConnection connection,IReadOnlyList<int> ids,V4DeletionState deletion,CancellationToken token)
    {
        if(ids.Count==0)return [];
        var placeholders=string.Join(',',ids.Select(_=>"?"));
        var sql=$"SELECT c.[Id],c.[EntityType],"+EntityLabelExpression("c")+",c.[Version],c.[IsDeleted] FROM (((((([CanonEntities] AS c LEFT JOIN [Projects] AS p ON c.[Id]=p.[EntityId]) LEFT JOIN [Locations] AS l ON c.[Id]=l.[EntityId]) LEFT JOIN [Characters] AS ch ON c.[Id]=ch.[EntityId]) LEFT JOIN [Organizations] AS o ON c.[Id]=o.[EntityId]) LEFT JOIN [Objects] AS ob ON c.[Id]=ob.[EntityId]) LEFT JOIN [WorldEvents] AS w ON c.[Id]=w.[EntityId]) WHERE c.[Id] IN ("+placeholders+") AND c.[ContinuityId]=?"+DeletionSql("c",deletion);
        using var command=new AccessCommand(connection,sql);foreach(var id in ids)command.Add(OleDbType.Integer,id);command.Add(OleDbType.Integer,session.RequireContinuityId());
        return await command.QueryAsync(r=>new RelationRow(r.GetInt32(0),r.GetString(1),r.GetString(2),null,r.GetInt32(3),r.GetBoolean(4)),token).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> DefaultEntitySections(string type)=>type.ToUpperInvariant() switch
    {
        "CHARACTER"=>["notes","events","relationshipEvents","tags","projects","images","sources","claims","aliases","relationships","relationshipMembershipPeriods","residences","memberships","temporalEffects","ownership","custody","eventParticipation"],
        "PROJECT"=>["notes","events","tags","images","sources","claims","members","eventParticipation"],
        "WORLDEVENT"=>["notes","events","contextEvents","relationshipEvents","tags","projects","images","sources","claims","participants","locations"],
        "ORGANIZATION"=>["notes","events","tags","projects","images","sources","claims","aliases","memberships","organizationLocations","ownership","custody","eventParticipation"],
        "OBJECT"=>["notes","events","tags","projects","images","sources","claims","ownership","custody","objectLocations","eventParticipation"],
        "LOCATION"=>["notes","events","tags","projects","images","sources","claims","children","residents","organizations","objects","worldEvents","eventParticipation"],
        _=>["notes","events","tags","projects","images","sources","claims"]
    };
    private static IReadOnlyList<string> DefaultRecordSections(string type)=>type.ToUpperInvariant() switch
    {
        "SOURCE"=>["entities","notes","claims","tags","snapshots","images"],"TAG"=>["targets"],
        "SOURCESNAPSHOT"=>["source"],"ENTITYNOTE"=>["owner","sources"],"CONTINUITYNOTE"=>["sources"],"CLAIM"=>["targets","sources"],
        "ENTITYEVENT"=>["owner","worldEvent","projects"],"ENTITYIMAGE" or "STORYIMAGE"=>["owner","source"],"CHARACTERRELATIONSHIP"=>["characters","type","membershipPeriods","events","claims","mergedInto","mergedSources"],
        "RELATIONSHIPEVENT"=>["relationship","worldEvent","projects"],
        "RELATIONSHIPPARTICIPANT"=>["relationship","character","membershipPeriods"],
        "RELATIONSHIPMEMBERSHIPPERIOD"=>["participant","relationship"],
        "CHARACTERRESIDENCE"=>["character","location"],"ORGANIZATIONMEMBERSHIP"=>["organization","character"],
        "ORGANIZATIONLOCATION"=>["organization","location"],"OBJECTOWNERSHIPPERIOD"=>["object","owners"],
        "OBJECTCUSTODYPERIOD"=>["object","principal"],"OBJECTLOCATIONPERIOD"=>["object","location"],
        "WORLDEVENTPARTICIPANT"=>["worldEvent","participant"],"WORLDEVENTLOCATION"=>["worldEvent","location"],
        "CHARACTERTEMPORALEFFECT"=>["character","worldEvent"],_=>[]
    };
    private static bool IsPublicField(string type,string key)=>!(type.Equals("SourceSnapshot",StringComparison.OrdinalIgnoreCase)&&key.Equals("Content",StringComparison.OrdinalIgnoreCase))&&
        !key.Equals("Reference",StringComparison.OrdinalIgnoreCase)&&
        !key.Equals("NormalizedName",StringComparison.OrdinalIgnoreCase)&&!key.EndsWith("Path",StringComparison.OrdinalIgnoreCase)&&
        !key.EndsWith("Sha256",StringComparison.OrdinalIgnoreCase)&&
        (!key.EndsWith("Id",StringComparison.OrdinalIgnoreCase)||key.EndsWith("TimeZoneId",StringComparison.OrdinalIgnoreCase)||key.EndsWith("CalendarId",StringComparison.OrdinalIgnoreCase));
    private static (string Table,string Column) RecordEntityLink(string type,string relation)=>(type.ToUpperInvariant(),relation.ToUpperInvariant()) switch
    {
        ("CHARACTERRESIDENCE","CHARACTER")=>("CharacterResidences","CharacterId"),("CHARACTERRESIDENCE","LOCATION")=>("CharacterResidences","LocationId"),
        ("ORGANIZATIONMEMBERSHIP","ORGANIZATION")=>("OrganizationMemberships","OrganizationId"),("ORGANIZATIONMEMBERSHIP","CHARACTER")=>("OrganizationMemberships","CharacterId"),
        ("ORGANIZATIONLOCATION","ORGANIZATION")=>("OrganizationLocations","OrganizationId"),("ORGANIZATIONLOCATION","LOCATION")=>("OrganizationLocations","LocationId"),
        ("OBJECTOWNERSHIPPERIOD","OBJECT")=>("ObjectOwnershipPeriods","ObjectId"),("OBJECTCUSTODYPERIOD","OBJECT")=>("ObjectCustodyPeriods","ObjectId"),
        ("OBJECTLOCATIONPERIOD","OBJECT")=>("ObjectLocationPeriods","ObjectId"),("OBJECTLOCATIONPERIOD","LOCATION")=>("ObjectLocationPeriods","LocationId"),
        ("WORLDEVENTPARTICIPANT","WORLDEVENT")=>("WorldEventParticipants","WorldEventId"),("WORLDEVENTPARTICIPANT","PARTICIPANT")=>("WorldEventParticipants","ParticipantEntityId"),
        ("WORLDEVENTLOCATION","WORLDEVENT")=>("WorldEventLocations","WorldEventId"),("WORLDEVENTLOCATION","LOCATION")=>("WorldEventLocations","LocationId"),
        _=>throw new InvalidOperationException("Unsupported record entity link.")
    };
    private static int? TryInteger(IReadOnlyDictionary<string,object?> row,string key)=>row.TryGetValue(key,out var value)&&value is not null?Convert.ToInt32(value,CultureInfo.InvariantCulture):null;
    private static IReadOnlyDictionary<string,object?> ReadFields(DbDataReader reader){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);return row;}

    private async Task<V4Page<V4ReferenceSummary>> RelatedContinuityAsync(
        int continuity, V4ListRelatedRequest request, CancellationToken token)
    {
        ValidateLimit(request.Limit, V4ContractLimits.MaximumPageSize);
        var revision = await RevisionAsync(token).ConfigureAwait(false);
        var relation = request.Relation.Trim().ToLowerInvariant();
        var scope = $"c:{continuity}:{relation}:{request.DeletionState}";
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        string sql;
        string type;
        if (relation == "notes")
        {
            var after = request.Cursor is null ? 0 : checked((int)cursors.Decode(request.Cursor, "related", scope).Position);
            type = "ContinuityNote";
            sql = $"SELECT TOP {request.Limit + 1} n.[Id],IIf(n.[Title] Is Null,'note',n.[Title]),n.[Version],n.[IsDeleted] FROM [ContinuityNotes] AS n WHERE n.[ContinuityId]=? AND n.[Id]>?" + DeletionSql("n", request.DeletionState) + " ORDER BY n.[Id]";
            using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, continuity).Add(OleDbType.Integer, after);
            var rows = await command.QueryAsync(reader => new RelationRow(reader.GetInt32(0), type,
                reader.GetString(1), null, reader.GetInt32(2), reader.GetBoolean(3)), token).ConfigureAwait(false);
            return RelationPage(rows, request.Limit, scope, session.ContinuityName, revision);
        }
        else if (relation is "entities" or "records")
        {
            var search = await SearchAsync(new(Kinds: [V4RecordKind.Project, V4RecordKind.Location, V4RecordKind.Character,
                V4RecordKind.Organization, V4RecordKind.Object, V4RecordKind.WorldEvent], DeletionState: request.DeletionState,
                Cursor: request.Cursor, Limit: request.Limit), token).ConfigureAwait(false);
            return search;
        }
        else throw new V4ResolutionException("relation.unsupported", "That continuity relation is not supported.");
        throw new InvalidOperationException("Unreachable continuity relation branch.");
    }

    private async Task<V4Page<V4ReferenceSummary>> RelatedEntityAsync(
        V4ResolvedTarget target, string requestedRelation, V4DeletionState deletion,
        string? cursor, int limit, CancellationToken token)
    {
        var relation = requestedRelation.Trim().ToLowerInvariant();
        var revision = await RevisionAsync(token).ConfigureAwait(false);
        var scope = $"e:{target.Reference}:{relation}:{deletion}";
        var after = cursor is null ? 0 : cursors.Decode(cursor, "related", scope).Position;
        var rows = await RelationRowsAsync(target, relation, deletion, after, limit + 1, token).ConfigureAwait(false);
        return RelationPage(rows, limit, scope, session.ContinuityName, revision);
    }

    private V4Page<V4ReferenceSummary> RelationPage(
        IReadOnlyList<RelationRow> rows, int limit, string scope, string? continuityName, string revision)
    {
        var page = rows.Take(limit).Select(row => new V4ReferenceSummary(
            references.ReferenceFromKnownRecord(row.Type, row.Id, row.Label), Kind(row.Type), row.Label,
            row.Context, continuityName, row.Version, row.Deleted, row.Role, row.Notes)).ToArray();
        return new(page, rows.Count > limit ? cursors.Encode("related", rows[limit - 1].Key, scope) : null,
            rows.Count > limit, revision);
    }

    private async Task<IReadOnlyList<RelationRow>> RelationRowsAsync(
        V4ResolvedTarget target, string relation, V4DeletionState deletion, long after, int take, CancellationToken token)
    {
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        string sql;
        string type;
        var owner = target.StorageKey;
        switch (relation)
        {
            case "notes": type = "EntityNote"; sql = $"SELECT TOP {take} r.[Id],IIf(r.[Title] Is Null,'note',r.[Title]),r.[Version],r.[IsDeleted] FROM [EntityNotes] AS r WHERE r.[EntityId]=? AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "events":
                if (target.ResourceType.Equals("Project", StringComparison.OrdinalIgnoreCase))
                    return await ProjectEventsAsync(connection, owner, deletion, after, take, token).ConfigureAwait(false);
                type = "EntityEvent"; sql = $"SELECT TOP {take} r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [EntityEvents] AS r WHERE r.[EntityId]=? AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "relationshipevents" when target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase):
                type="RelationshipEvent";
                sql=$"SELECT DISTINCT TOP {take} r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM ([RelationshipEvents] AS r INNER JOIN [CharacterRelationships] AS rel ON r.[RelationshipId]=rel.[Id]) INNER JOIN [RelationshipParticipants] AS p ON rel.[Id]=p.[RelationshipId] WHERE p.[CharacterId]=? AND p.[IsDeleted]=False AND rel.[IsDeleted]=False AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "relationshipevents" when target.ResourceType.Equals("WorldEvent",StringComparison.OrdinalIgnoreCase):
                type="RelationshipEvent";
                sql=$"SELECT TOP {take} r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [RelationshipEvents] AS r INNER JOIN [CharacterRelationships] AS rel ON r.[RelationshipId]=rel.[Id] WHERE r.[WorldEventId]=? AND rel.[IsDeleted]=False AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "relationshipmembershipperiods" when target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase):
                type="RelationshipMembershipPeriod";
                sql=$"SELECT TOP {take} m.[Id],'membership in ' & t.[Name],m.[Version],m.[IsDeleted] " +
                    "FROM (([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p " +
                    "ON m.[ParticipantId]=p.[Id]) INNER JOIN [CharacterRelationships] AS rel " +
                    "ON p.[RelationshipId]=rel.[Id]) INNER JOIN [RelationshipTypes] AS t " +
                    "ON rel.[RelationshipTypeId]=t.[Id] WHERE p.[CharacterId]=? AND p.[IsDeleted]=False " +
                    "AND rel.[IsDeleted]=False AND m.[Id]>?"+DeletionSql("m",deletion)+" ORDER BY m.[Id]";break;
            case "tags": type = "Tag"; sql = $"SELECT TOP {take} t.[Id],t.[Name],t.[Version],t.[IsDeleted] FROM [EntityTags] AS x INNER JOIN [Tags] AS t ON x.[TagId]=t.[Id] WHERE x.[EntityId]=? AND t.[Id]>?" + DeletionSql("t", deletion) + " ORDER BY t.[Id]"; break;
            case "sources": type = "Source"; sql = $"SELECT TOP {take} s.[Id],s.[Title],s.[Version],s.[IsDeleted] FROM [EntitySources] AS x INNER JOIN [Sources] AS s ON x.[SourceId]=s.[Id] WHERE x.[EntityId]=? AND s.[Id]>?" + DeletionSql("s", deletion) + " ORDER BY s.[Id]"; break;
            case "projects":
            {
                using var projects=new AccessCommand(connection,$"SELECT TOP {take} p.[EntityId],p.[Name],c.[Version],c.[IsDeleted],x.[Role],x.[Notes] FROM ([ProjectEntities] AS x INNER JOIN [Projects] AS p ON x.[ProjectId]=p.[EntityId]) INNER JOIN [CanonEntities] AS c ON p.[EntityId]=c.[Id] WHERE x.[MemberEntityId]=? AND p.[EntityId]>? AND x.[IsDeleted]=False"+DeletionSql("c",deletion)+" ORDER BY p.[EntityId]")
                    .Add(OleDbType.Integer,owner).Add(OleDbType.Integer,checked((int)after));
                return await projects.QueryAsync(r=>new RelationRow(r.GetInt32(0),"Project",r.GetString(1),null,r.GetInt32(2),r.GetBoolean(3),Role:r.IsDBNull(4)?null:r.GetString(4),Notes:r.IsDBNull(5)?null:r.GetString(5)),token).ConfigureAwait(false);
            }
            case "images": type = "EntityImage"; sql = $"SELECT TOP {take} r.[Id],IIf(r.[Title] Is Null,'image',r.[Title]),r.[Version],r.[IsDeleted] FROM [EntityImages] AS r WHERE r.[EntityId]=? AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "claims": type = "Claim"; sql = $"SELECT TOP {take} r.[Id],Left(r.[ClaimText],255),r.[Version],r.[IsDeleted] FROM [ClaimEntities] AS x INNER JOIN [Claims] AS r ON x.[ClaimId]=r.[Id] WHERE x.[EntityId]=? AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "aliases" when target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase): type="CharacterAlias";sql=$"SELECT TOP {take} r.[Id],r.[Alias],r.[Version],r.[IsDeleted] FROM [CharacterAliases] AS r WHERE r.[CharacterId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "aliases" when target.ResourceType.Equals("Organization",StringComparison.OrdinalIgnoreCase): type="OrganizationAlias";sql=$"SELECT TOP {take} r.[Id],r.[Alias],r.[Version],r.[IsDeleted] FROM [OrganizationAliases] AS r WHERE r.[OrganizationId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "temporaleffects" or "temporal-effects": type = "CharacterTemporalEffect"; sql = $"SELECT TOP {take} r.[Id],r.[Name],r.[Version],r.[IsDeleted] FROM [CharacterTemporalEffects] AS r WHERE r.[CharacterId]=? AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "relationships":
            {
                using var query=new AccessCommand(connection,$"SELECT TOP {take} r.[Id],t.[Name],t.[InverseName],t.[IsDirected],r.[SourceCharacterId],r.[TargetCharacterId],r.[Version],r.[IsDeleted] FROM ([CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id]) INNER JOIN [RelationshipParticipants] AS p ON r.[Id]=p.[RelationshipId] WHERE p.[CharacterId]=? AND p.[IsDeleted]=False AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]")
                    .Add(OleDbType.Integer,owner).Add(OleDbType.Integer,checked((int)after));
                var links=await query.QueryAsync(r=>new{Id=r.GetInt32(0),Name=r.GetString(1),Inverse=r.IsDBNull(2)?null:r.GetString(2),Directed=r.GetBoolean(3),Source=r.GetInt32(4),Target=r.GetInt32(5),Version=r.GetInt32(6),Deleted=r.GetBoolean(7)},token).ConfigureAwait(false);
                if (links.Count == 0) return [];
                var ids=string.Join(",",links.Select(link=>link.Id));
                using var memberQuery=new AccessCommand(connection,$"SELECT [RelationshipId],[CharacterId] FROM [RelationshipParticipants] WHERE [RelationshipId] IN ({ids}) AND [IsDeleted]=False");
                var members=await memberQuery.QueryAsync(r=>(Relationship:r.GetInt32(0),Character:r.GetInt32(1)),token).ConfigureAwait(false);
                var labels=(await EntityRowsAsync(connection,members.Select(member=>member.Character).Distinct().ToArray(),deletion,token).ConfigureAwait(false))
                    .ToDictionary(row=>row.Id,row=>row.Label);
                return links.Select(link=>new RelationRow(link.Id,"CharacterRelationship",
                    link.Target==owner&&link.Directed&&link.Inverse is not null?link.Inverse:link.Name,
                    string.Join(", ",members.Where(member=>member.Relationship==link.Id&&member.Character!=owner)
                        .Select(member=>labels.GetValueOrDefault(member.Character)).Where(label=>label is not null)),link.Version,link.Deleted)).ToArray();
            }
            case "residences": type="CharacterResidence";sql=$"SELECT TOP {take} r.[Id],'residence',r.[Version],r.[IsDeleted] FROM [CharacterResidences] AS r WHERE r.[CharacterId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "memberships": type = "OrganizationMembership"; sql = $"SELECT TOP {take} r.[Id],IIf(r.[Role] Is Null,'membership',r.[Role]),r.[Version],r.[IsDeleted] FROM [OrganizationMemberships] AS r WHERE (r.[OrganizationId]=? OR r.[CharacterId]=?) AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "organizationlocations" or "organization-locations": type="OrganizationLocation";sql=$"SELECT TOP {take} r.[Id],IIf(r.[LocationRole] Is Null,'organization location',r.[LocationRole]),r.[Version],r.[IsDeleted] FROM [OrganizationLocations] AS r WHERE r.[OrganizationId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "ownership" when target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase) || target.ResourceType.Equals("Organization",StringComparison.OrdinalIgnoreCase):
            {
                var column=target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase)?"CharacterId":"OrganizationId";
                type="ObjectOwnershipPeriod";
                sql=$"SELECT DISTINCT TOP {take} r.[Id],r.[OwnerState],r.[Version],r.[IsDeleted] FROM ([ObjectOwnershipPeriods] AS r INNER JOIN [ObjectOwnershipOwners] AS x ON r.[Id]=x.[OwnershipPeriodId]) INNER JOIN [OwnershipPrincipals] AS p ON x.[PrincipalId]=p.[Id] WHERE p.[{column}]=? AND r.[Id]>? AND p.[IsDeleted]=False"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";
                break;
            }
            case "ownership": type="ObjectOwnershipPeriod";sql=$"SELECT TOP {take} r.[Id],r.[OwnerState],r.[Version],r.[IsDeleted] FROM [ObjectOwnershipPeriods] AS r WHERE r.[ObjectId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "custody" when target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase) || target.ResourceType.Equals("Organization",StringComparison.OrdinalIgnoreCase):
            {
                var column=target.ResourceType.Equals("Character",StringComparison.OrdinalIgnoreCase)?"CharacterId":"OrganizationId";
                type="ObjectCustodyPeriod";
                sql=$"SELECT TOP {take} r.[Id],r.[CustodianState],r.[Version],r.[IsDeleted] FROM [ObjectCustodyPeriods] AS r INNER JOIN [OwnershipPrincipals] AS p ON r.[PrincipalId]=p.[Id] WHERE p.[{column}]=? AND r.[Id]>? AND p.[IsDeleted]=False"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";
                break;
            }
            case "custody": type="ObjectCustodyPeriod";sql=$"SELECT TOP {take} r.[Id],r.[CustodianState],r.[Version],r.[IsDeleted] FROM [ObjectCustodyPeriods] AS r WHERE r.[ObjectId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "eventparticipation" or "event-participation": type="WorldEventParticipant";sql=$"SELECT TOP {take} r.[Id],IIf(r.[Role] Is Null,'participant',r.[Role]),r.[Version],r.[IsDeleted] FROM [WorldEventParticipants] AS r WHERE r.[ParticipantEntityId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "objectlocations" or "object-locations": type="ObjectLocationPeriod";sql=$"SELECT TOP {take} r.[Id],'object location',r.[Version],r.[IsDeleted] FROM [ObjectLocationPeriods] AS r WHERE r.[ObjectId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "participants": type = "WorldEventParticipant"; sql = $"SELECT TOP {take} r.[Id],IIf(r.[Role] Is Null,'participant',r.[Role]),r.[Version],r.[IsDeleted] FROM [WorldEventParticipants] AS r WHERE (r.[WorldEventId]=? OR r.[ParticipantEntityId]=?) AND r.[Id]>?" + DeletionSql("r", deletion) + " ORDER BY r.[Id]"; break;
            case "locations" when target.ResourceType.Equals("WorldEvent",StringComparison.OrdinalIgnoreCase): type="WorldEventLocation";sql=$"SELECT TOP {take} r.[Id],IIf(r.[Role] Is Null,'event location',r.[Role]),r.[Version],r.[IsDeleted] FROM [WorldEventLocations] AS r WHERE r.[WorldEventId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "contextevents" or "context-events" when target.ResourceType.Equals("WorldEvent",StringComparison.OrdinalIgnoreCase): type="EntityEvent";sql=$"SELECT TOP {take} r.[Id],r.[Title],r.[Version],r.[IsDeleted] FROM [EntityEvents] AS r WHERE r.[WorldEventId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "children" when target.ResourceType.Equals("Location",StringComparison.OrdinalIgnoreCase):
            case "birthcharacters" or "birth-characters" when target.ResourceType.Equals("Location",StringComparison.OrdinalIgnoreCase):
            {
                var table=relation=="children"?"Locations":"Characters";var column=relation=="children"?"ParentLocationId":"BirthLocationId";
                using var ids=new AccessCommand(connection,$"SELECT TOP {take} r.[EntityId] FROM [{table}] AS r INNER JOIN [CanonEntities] AS c ON r.[EntityId]=c.[Id] WHERE r.[{column}]=? AND r.[EntityId]>?"+DeletionSql("c",deletion)+" ORDER BY r.[EntityId]").Add(OleDbType.Integer,owner).Add(OleDbType.Integer,checked((int)after));
                return await EntityRowsAsync(connection,await ids.QueryAsync(r=>r.GetInt32(0),token),deletion,token).ConfigureAwait(false);
            }
            case "residents" when target.ResourceType.Equals("Location",StringComparison.OrdinalIgnoreCase): type="CharacterResidence";sql=$"SELECT TOP {take} r.[Id],'residence',r.[Version],r.[IsDeleted] FROM [CharacterResidences] AS r WHERE r.[LocationId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "organizations" when target.ResourceType.Equals("Location",StringComparison.OrdinalIgnoreCase): type="OrganizationLocation";sql=$"SELECT TOP {take} r.[Id],IIf(r.[LocationRole] Is Null,'organization location',r.[LocationRole]),r.[Version],r.[IsDeleted] FROM [OrganizationLocations] AS r WHERE r.[LocationId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "objects" when target.ResourceType.Equals("Location",StringComparison.OrdinalIgnoreCase): type="ObjectLocationPeriod";sql=$"SELECT TOP {take} r.[Id],'object location',r.[Version],r.[IsDeleted] FROM [ObjectLocationPeriods] AS r WHERE r.[LocationId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "worldevents" or "world-events" when target.ResourceType.Equals("Location",StringComparison.OrdinalIgnoreCase): type="WorldEventLocation";sql=$"SELECT TOP {take} r.[Id],IIf(r.[Role] Is Null,'event location',r.[Role]),r.[Version],r.[IsDeleted] FROM [WorldEventLocations] AS r WHERE r.[LocationId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]";break;
            case "members" when target.ResourceType.Equals("Project",StringComparison.OrdinalIgnoreCase):
            {
                using var ids=new AccessCommand(connection,$"SELECT TOP {take} r.[MemberEntityId],r.[Id],r.[Role],r.[Notes] FROM [ProjectEntities] AS r WHERE r.[ProjectId]=? AND r.[Id]>?"+DeletionSql("r",deletion)+" ORDER BY r.[Id]").Add(OleDbType.Integer,owner).Add(OleDbType.Integer,checked((int)after));
                var values=await ids.QueryAsync(r=>(Entity:r.GetInt32(0),Assignment:r.GetInt32(1),Role:r.IsDBNull(2)?null:r.GetString(2),Notes:r.IsDBNull(3)?null:r.GetString(3)),token).ConfigureAwait(false);
                var members=await EntityRowsAsync(connection,values.Select(x=>x.Entity).ToArray(),deletion,token).ConfigureAwait(false);
                return members.Select(row=>{var assignment=values.First(x=>x.Entity==row.Id);return row with{SortKey=assignment.Assignment,Role=assignment.Role,Notes=assignment.Notes};}).ToArray();
            }
            default: throw new V4ResolutionException("relation.unsupported", $"Relation '{relation}' is not supported for this record.");
        }
        using var command = new AccessCommand(connection, sql);
        command.Add(OleDbType.Integer, owner);
        if (relation is "relationships" or "memberships" or "participants") command.Add(OleDbType.Integer, owner);
        command.Add(OleDbType.Integer, checked((int)after));
        return await command.QueryAsync(reader => new RelationRow(reader.GetInt32(0), type,
            reader.GetString(1), null, reader.GetInt32(2), reader.GetBoolean(3)), token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RelationRow>> ProjectEventsAsync(
        OleDbConnection connection, int project, V4DeletionState deletion, long after, int take, CancellationToken token)
    {
        var rows = new List<RelationRow>();
        var afterKind = (int)(after >> 32);
        var afterId = unchecked((int)(after & uint.MaxValue));
        if (afterKind < 2)
        using (var worlds = new AccessCommand(connection,
                   $"SELECT TOP {take} w.[EntityId],w.[Title],c.[Version],c.[IsDeleted],x.[Role],x.[Notes] FROM ([ProjectEntities] AS x INNER JOIN [WorldEvents] AS w ON x.[MemberEntityId]=w.[EntityId]) INNER JOIN [CanonEntities] AS c ON w.[EntityId]=c.[Id] WHERE x.[ProjectId]=? AND w.[EntityId]>? AND x.[IsDeleted]=False" + DeletionSql("c", deletion) + " ORDER BY w.[EntityId]")
               .Add(OleDbType.Integer, project).Add(OleDbType.Integer, afterKind == 1 ? afterId : 0))
            rows.AddRange(await worlds.QueryAsync(r => new RelationRow(r.GetInt32(0), "WorldEvent", r.GetString(1), "world event", r.GetInt32(2), r.GetBoolean(3), ((long)1 << 32) | (uint)r.GetInt32(0), r.IsDBNull(4)?null:r.GetString(4), r.IsDBNull(5)?null:r.GetString(5)), token).ConfigureAwait(false));
        if (afterKind < 3)
        using (var locals = new AccessCommand(connection,
                   $"SELECT TOP {take} e.[Id],e.[Title],e.[Version],e.[IsDeleted],x.[Role],x.[Notes] FROM [EntityEventProjects] AS x INNER JOIN [EntityEvents] AS e ON x.[EntityEventId]=e.[Id] WHERE x.[ProjectId]=? AND e.[Id]>? AND x.[IsDeleted]=False" + DeletionSql("e", deletion) + " ORDER BY e.[Id]")
               .Add(OleDbType.Integer, project).Add(OleDbType.Integer, afterKind == 2 ? afterId : 0))
            rows.AddRange(await locals.QueryAsync(r => new RelationRow(r.GetInt32(0), "EntityEvent", r.GetString(1), "entity event", r.GetInt32(2), r.GetBoolean(3), ((long)2 << 32) | (uint)r.GetInt32(0), r.IsDBNull(4)?null:r.GetString(4), r.IsDBNull(5)?null:r.GetString(5)), token).ConfigureAwait(false));
        if (afterKind < 3)
        using (var owned = new AccessCommand(connection,
                   $"SELECT TOP {take} e.[Id],e.[Title],e.[Version],e.[IsDeleted],e.[ProjectBoundary] FROM [EntityEvents] AS e WHERE e.[EntityId]=? AND e.[Id]>?" + DeletionSql("e", deletion) + " ORDER BY e.[Id]")
               .Add(OleDbType.Integer, project).Add(OleDbType.Integer, afterKind == 2 ? afterId : 0))
            rows.AddRange(await owned.QueryAsync(r => new RelationRow(r.GetInt32(0), "EntityEvent", r.GetString(1), "project story event", r.GetInt32(2), r.GetBoolean(3), ((long)2 << 32) | (uint)r.GetInt32(0), r.IsDBNull(4) ? null : r.GetString(4)), token));
        using (var relationshipEvents = new AccessCommand(connection,
                   $"SELECT TOP {take} e.[Id],e.[Title],e.[Version],e.[IsDeleted],x.[Role],x.[Notes] FROM ([RelationshipEventProjects] AS x INNER JOIN [RelationshipEvents] AS e ON x.[RelationshipEventId]=e.[Id]) INNER JOIN [CharacterRelationships] AS rel ON e.[RelationshipId]=rel.[Id] WHERE x.[ProjectId]=? AND e.[Id]>? AND x.[IsDeleted]=False AND rel.[IsDeleted]=False" + DeletionSql("e", deletion) + " ORDER BY e.[Id]")
               .Add(OleDbType.Integer, project).Add(OleDbType.Integer, afterKind == 3 ? afterId : 0))
            rows.AddRange(await relationshipEvents.QueryAsync(r => new RelationRow(r.GetInt32(0), "RelationshipEvent", r.GetString(1), "relationship event", r.GetInt32(2), r.GetBoolean(3), ((long)3 << 32) | (uint)r.GetInt32(0), r.IsDBNull(4)?null:r.GetString(4), r.IsDBNull(5)?null:r.GetString(5)), token).ConfigureAwait(false));
        return rows.Where(row => row.Key > after).DistinctBy(row => row.Key).OrderBy(row => row.Key).Take(take).ToArray();
    }

    public Task<V4Page<V4ReferenceSummary>> TagTargetsAsync(V4TagTargetsRequest request, CancellationToken token = default) =>
        MeasureAsync<V4Page<V4ReferenceSummary>>("tag-targets", async () =>
        {
            var continuity = session.RequireContinuityId();
            ValidateLimit(request.Limit, V4ContractLimits.MaximumPageSize);
            var tag = await targets.TagAsync(request.Tag, token).ConfigureAwait(false);
            var scope = $"tag:{tag.Reference}:c:{continuity}:d:{request.DeletionState}:k:{string.Join(',', request.Kinds ?? [])}";
            var after = request.Cursor is null ? -1L : cursors.Decode(request.Cursor, "tag-targets", scope).Position;
            var revision = await RevisionAsync(token).ConfigureAwait(false);
            var rows = new List<(long Key, V4ReferenceSummary Summary)>();
            await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
            using (var entities = new AccessCommand(connection,
                       "SELECT c.[Id],c.[EntityType],c.[Version],c.[IsDeleted]," + EntityLabelExpression("c") +
                       " FROM (((((( [CanonEntities] AS c LEFT JOIN [Projects] AS p ON c.[Id]=p.[EntityId]) LEFT JOIN [Locations] AS l ON c.[Id]=l.[EntityId]) LEFT JOIN [Characters] AS ch ON c.[Id]=ch.[EntityId]) LEFT JOIN [Organizations] AS o ON c.[Id]=o.[EntityId]) LEFT JOIN [Objects] AS ob ON c.[Id]=ob.[EntityId]) LEFT JOIN [WorldEvents] AS w ON c.[Id]=w.[EntityId]) INNER JOIN [EntityTags] AS x ON c.[Id]=x.[EntityId] " +
                       "WHERE x.[TagId]=? AND c.[ContinuityId]=?" + DeletionSql("c", request.DeletionState) + " ORDER BY c.[Id]")
                   .Add(OleDbType.Integer, tag.StorageKey).Add(OleDbType.Integer, continuity))
            {
                var entityRows = await entities.QueryAsync(r => new
                {
                    Id = r.GetInt32(0), Type = Enum.Parse<CanonEntityType>(r.GetString(1)), Version = r.GetInt32(2),
                    Deleted = r.GetBoolean(3), Label = r.GetString(4)
                }, token).ConfigureAwait(false);
                foreach (var row in entityRows)
                {
                    var kind = Kind(row.Type.ToString());
                    if (request.Kinds is { Count: > 0 } && !request.Kinds.Contains(kind)) continue;
                    var key = SearchKey(kind, row.Id); if (key <= after) continue;
                    rows.Add((key, new(references.ReferenceFromKnownRecord(row.Type.ToString(), row.Id, row.Label), kind,
                        row.Label, ContinuityName: session.ContinuityName, Version: row.Version, IsDeleted: row.Deleted)));
                }
            }
            if (request.Kinds is not { Count: > 0 } || request.Kinds.Contains(V4RecordKind.Source))
            {
                using var sources = new AccessCommand(connection,
                        "SELECT s.[Id],s.[Title],s.[Version],s.[IsDeleted] FROM [SourceTags] AS x INNER JOIN [Sources] AS s ON x.[SourceId]=s.[Id] WHERE x.[TagId]=?" + DeletionSql("s", request.DeletionState) + " ORDER BY s.[Id]")
                    .Add(OleDbType.Integer, tag.StorageKey);
                foreach (var row in await sources.QueryAsync(r => new { Id = r.GetInt32(0), Label = r.GetString(1), Version = r.GetInt32(2), Deleted = r.GetBoolean(3) }, token).ConfigureAwait(false))
                {
                    var key = SearchKey(V4RecordKind.Source, row.Id); if (key <= after) continue;
                    rows.Add((key, new(references.ReferenceFromKnownRecord("Source", row.Id, row.Label), V4RecordKind.Source,
                        row.Label, Version: row.Version, IsDeleted: row.Deleted)));
                }
            }
            var ordered = rows.OrderBy(row => row.Key).Take(request.Limit + 1).ToArray();
            return new(ordered.Take(request.Limit).Select(row => row.Summary).ToArray(),
                ordered.Length > request.Limit ? cursors.Encode("tag-targets", ordered[request.Limit - 1].Key, scope) : null,
                ordered.Length > request.Limit, revision);
        });

}
