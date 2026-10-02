using System.Data.Common;
using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessVaultService
{
    private const int MaximumReadTextCharacters = 65_536;
    private const int MaximumGraphRelations = 50;

    public async Task<IReadOnlyList<ContinuitySummary>> ListContinuitiesAsync(bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new AccessCommand(connection, "SELECT TOP 100 c.[Id],c.[Name],c.[DefaultTimeZoneId],c.[Version],c.[IsDeleted],k.[CurrentInstantUtc],k.[ReferenceTimeZoneId],k.[Version] FROM [Continuities] AS c INNER JOIN [ContinuityClocks] AS k ON c.[Id]=k.[ContinuityId]" + (onlyDeleted ? " WHERE c.[IsDeleted]=True" : includeDeleted ? string.Empty : " WHERE c.[IsDeleted]=False") + " ORDER BY c.[Id]");
        return await command.QueryAsync(reader => new ContinuitySummary(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VariantGroupSummary>> ListVariantGroupsAsync(
        int continuityId,
        CanonEntityType? entityType = null,
        bool includeDeleted = false,
        bool onlyDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = "SELECT TOP 200 [Id],[ContinuityId],[EntityType],[Name],[Notes],[Version],[IsDeleted] FROM [VariantGroups] WHERE [ContinuityId]=?" +
                  (entityType is null ? string.Empty : " AND [EntityType]=?") +
                  (onlyDeleted ? " AND [IsDeleted]=True" : includeDeleted ? string.Empty : " AND [IsDeleted]=False") + " ORDER BY [Id]";
        using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, continuityId);
        if (entityType is not null) command.Add(OleDbType.VarWChar, entityType.Value.ToString(), 30);
        return await command.QueryAsync(reader => new VariantGroupSummary(
            reader.GetInt32(0), reader.GetInt32(1), Enum.Parse<CanonEntityType>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : Truncate(reader.GetString(4)),
            reader.GetInt32(5), reader.GetBoolean(6), !reader.IsDBNull(4) && reader.GetString(4).Length > MaximumReadTextCharacters), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(string? text = null, bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = "SELECT TOP 100 [Id],[Title],[CanonicalUrl],[Version],[IsDeleted] FROM [Sources] WHERE 1=1" +
                  (onlyDeleted ? " AND [IsDeleted]=True" : includeDeleted ? string.Empty : " AND [IsDeleted]=False") + (string.IsNullOrWhiteSpace(text) ? string.Empty : " AND [Title] LIKE ?") + " ORDER BY [Id]";
        using var command = new AccessCommand(connection, sql);
        if (!string.IsNullOrWhiteSpace(text)) command.Add(OleDbType.VarWChar, $"%{EscapeLike(text.Trim())}%", 255);
        return await command.QueryAsync(reader => new SourceSummary(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TagSummary>> SearchTagsAsync(string? text = null, bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.Create(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = "SELECT TOP 100 [Id],[Name],[Version],[IsDeleted] FROM [Tags] WHERE 1=1" +
                  (onlyDeleted ? " AND [IsDeleted]=True" : includeDeleted ? string.Empty : " AND [IsDeleted]=False") + (string.IsNullOrWhiteSpace(text) ? string.Empty : " AND [Name] LIKE ?") + " ORDER BY [Id]";
        using var command = new AccessCommand(connection, sql);
        if (!string.IsNullOrWhiteSpace(text)) command.Add(OleDbType.VarWChar, $"%{EscapeLike(text.Trim())}%", 100);
        return await command.QueryAsync(reader => new TagSummary(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)), cancellationToken).ConfigureAwait(false);
    }

    public Task<EntityGraph?> GetEntityGraphAsync(
        int entityId,
        int relationLimit = 100,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => GetEntityGraphCoreAsync(entityId, relationLimit, includeDeleted, cancellationToken), cancellationToken);

    private async Task<EntityGraph?> GetEntityGraphCoreAsync(int entityId, int relationLimit, bool includeDeleted, CancellationToken cancellationToken)
    {
        if (await GetEntityAsync(entityId, includeDeleted, cancellationToken).ConfigureAwait(false) is not { } entity) return null;
        relationLimit = Math.Clamp(relationLimit, 1, MaximumGraphRelations);
        await using var connection = _connectionFactory.Create(); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string sql)
        {
            using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, entityId);
            return (await command.QueryAsync(reader => (IReadOnlyDictionary<string, object?>)ReadFields(reader), cancellationToken).ConfigureAwait(false)).ToArray();
        }
        var notes = await Query($"SELECT TOP {relationLimit + 1} [Id],[Title],[Body],[Version],[IsDeleted] FROM [EntityNotes] WHERE [EntityId]=? AND [IsDeleted]=False ORDER BY [Id]");
        var events = await Query($"SELECT TOP {relationLimit + 1} [Id],[WorldEventId],[Title],[EventKind],[EventLowerBound],[EventUpperBound],[Version],[IsDeleted] FROM [EntityEvents] WHERE [EntityId]=? AND [IsDeleted]=False ORDER BY [Id]");
        var tags = await Query($"SELECT TOP {relationLimit + 1} t.[Id],t.[Name],t.[IsDeleted] FROM [EntityTags] AS x INNER JOIN [Tags] AS t ON x.[TagId]=t.[Id] WHERE x.[EntityId]=? AND t.[IsDeleted]=False ORDER BY t.[Id]");
        var sources = await Query($"SELECT TOP {relationLimit + 1} s.[Id],s.[Title],s.[CanonicalUrl],s.[IsDeleted] FROM [EntitySources] AS x INNER JOIN [Sources] AS s ON x.[SourceId]=s.[Id] WHERE x.[EntityId]=? AND s.[IsDeleted]=False ORDER BY s.[Id]");
        var projects = await Query($"SELECT TOP {relationLimit + 1} x.[Id],x.[ProjectId],p.[Name],x.[Role],x.[Notes],x.[IsDeleted] FROM ([ProjectEntities] AS x INNER JOIN [Projects] AS p ON x.[ProjectId]=p.[EntityId]) INNER JOIN [CanonEntities] AS pc ON p.[EntityId]=pc.[Id] WHERE x.[MemberEntityId]=? AND x.[IsDeleted]=False AND pc.[IsDeleted]=False ORDER BY x.[Id]");
        var claims = await Query($"SELECT TOP {relationLimit + 1} c.[Id],c.[ClaimText],c.[ClaimStatus],c.[Confidence],c.[TargetField],c.[Version] FROM [ClaimEntities] AS x INNER JOIN [Claims] AS c ON x.[ClaimId]=c.[Id] WHERE x.[EntityId]=? AND c.[IsDeleted]=False ORDER BY c.[Id]");
        var characterRelationships = entity.Summary.EntityType == CanonEntityType.Character
            ? await QueryCharacterRelationshipsAsync(connection, entityId, relationLimit + 1, cancellationToken).ConfigureAwait(false)
            : [];
        var relationshipFields = characterRelationships.Select(view => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Id"] = view.RelationshipId,
            ["RelatedCharacterId"] = view.RelatedCharacterId,
            ["RelationshipTypeId"] = view.RelationshipTypeId,
            ["Label"] = view.Label,
            ["Perspective"] = view.Perspective,
            ["PeriodKind"] = view.Period.Kind.ToString(),
            ["PeriodLowerBound"] = view.Period.LowerBound,
            ["PeriodUpperBound"] = view.Period.UpperBound,
            ["Notes"] = view.Notes,
            ["Version"] = view.Version
        }).ToArray();
        var typeSpecific = entity.Summary.EntityType switch
        {
            CanonEntityType.Character => (await Query($"SELECT TOP {relationLimit + 1} m.[Id],m.[OrganizationId],m.[Role],m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound],m.[IsDeleted] FROM [OrganizationMemberships] AS m INNER JOIN [CanonEntities] AS o ON m.[OrganizationId]=o.[Id] WHERE m.[CharacterId]=? AND m.[IsDeleted]=False AND o.[IsDeleted]=False ORDER BY m.[Id]")).Concat(relationshipFields).ToArray(),
            CanonEntityType.Location => await Query($"SELECT TOP {relationLimit + 1} l.[EntityId],l.[Name],l.[LocationType] FROM [Locations] AS l INNER JOIN [CanonEntities] AS c ON l.[EntityId]=c.[Id] WHERE l.[ParentLocationId]=? AND c.[IsDeleted]=False ORDER BY l.[EntityId]"),
            CanonEntityType.Organization => await Query($"SELECT TOP {relationLimit + 1} m.[Id],m.[CharacterId],m.[Role],m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound],m.[IsDeleted] FROM [OrganizationMemberships] AS m INNER JOIN [CanonEntities] AS c ON m.[CharacterId]=c.[Id] WHERE m.[OrganizationId]=? AND m.[IsDeleted]=False AND c.[IsDeleted]=False ORDER BY m.[Id]"),
            CanonEntityType.Object => await Query($"SELECT TOP {relationLimit + 1} [Id],[OwnerState],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[Version],[IsDeleted] FROM [ObjectOwnershipPeriods] WHERE [ObjectId]=? AND [IsDeleted]=False ORDER BY [Id]"),
            CanonEntityType.WorldEvent => await Query($"SELECT TOP {relationLimit + 1} w.[Id],w.[ParticipantEntityId],w.[Role],w.[Impact],w.[Outcome],w.[IsDeleted] FROM [WorldEventParticipants] AS w INNER JOIN [CanonEntities] AS c ON w.[ParticipantEntityId]=c.[Id] WHERE w.[WorldEventId]=? AND w.[IsDeleted]=False AND c.[IsDeleted]=False ORDER BY w.[Id]"),
            CanonEntityType.Project => await Query($"SELECT TOP {relationLimit + 1} p.[Id],p.[MemberEntityId],p.[Role],p.[Notes],p.[IsDeleted] FROM [ProjectEntities] AS p INNER JOIN [CanonEntities] AS c ON p.[MemberEntityId]=c.[Id] WHERE p.[ProjectId]=? AND p.[IsDeleted]=False AND c.[IsDeleted]=False ORDER BY p.[Id]"),
            _ => []
        };
        var collections = new[] { notes, events, tags, sources, projects, typeSpecific, claims };
        return new EntityGraph(entityId,
            notes.Take(relationLimit).ToArray(), events.Take(relationLimit).ToArray(), tags.Take(relationLimit).ToArray(),
            sources.Take(relationLimit).ToArray(), projects.Take(relationLimit).ToArray(), typeSpecific.Take(relationLimit).ToArray(),
            claims.Take(relationLimit).ToArray(),
            collections.Any(items => items.Count > relationLimit));
    }

    public Task<SourceGraph?> GetSourceGraphAsync(
        int sourceId,
        int relationLimit = 100,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(async () =>
        {
            relationLimit = Math.Clamp(relationLimit, 1, MaximumGraphRelations);
            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var sourceCommand = new AccessCommand(connection, "SELECT [Id],[Title],[CanonicalUrl],[Version],[IsDeleted] FROM [Sources] WHERE [Id]=?" + (includeDeleted ? string.Empty : " AND [IsDeleted]=False"))
                .Add(OleDbType.Integer, sourceId);
            var sourceRows = await sourceCommand.QueryAsync(reader => new SourceSummary(
                reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4)), cancellationToken).ConfigureAwait(false);
            if (sourceRows.Count == 0) return null;
            async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string sql)
            {
                using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, sourceId);
                return (await command.QueryAsync(reader => (IReadOnlyDictionary<string, object?>)ReadFields(reader), cancellationToken).ConfigureAwait(false)).ToArray();
            }
            var directEntities = await Query($"SELECT TOP {relationLimit + 1} c.[Id] AS [EntityId],c.[ContinuityId],c.[EntityType],c.[Version],c.[IsDeleted] FROM [EntitySources] AS x INNER JOIN [CanonEntities] AS c ON x.[EntityId]=c.[Id] WHERE x.[SourceId]=? AND c.[IsDeleted]=False ORDER BY c.[Id]");
            var claimEntities = await Query($"SELECT TOP {relationLimit + 1} c.[Id] AS [EntityId],c.[ContinuityId],c.[EntityType],c.[Version],c.[IsDeleted] FROM (([ClaimSources] AS s INNER JOIN [ClaimEntities] AS x ON s.[ClaimId]=x.[ClaimId]) INNER JOIN [Claims] AS q ON x.[ClaimId]=q.[Id]) INNER JOIN [CanonEntities] AS c ON x.[EntityId]=c.[Id] WHERE s.[SourceId]=? AND s.[IsDeleted]=False AND q.[IsDeleted]=False AND c.[IsDeleted]=False ORDER BY c.[Id]");
            var entities = directEntities.Concat(claimEntities)
                .GroupBy(row => Convert.ToInt32(row["EntityId"]))
                .Select(group => group.First()).OrderBy(row => Convert.ToInt32(row["EntityId"]))
                .Take(relationLimit + 1).ToArray();
            var notes = await Query($"SELECT TOP {relationLimit + 1} x.[Id],n.[Id] AS [NoteId],n.[EntityId],x.[Locator],x.[Notes],x.[Version],x.[IsDeleted] FROM ([NoteSources] AS x INNER JOIN [EntityNotes] AS n ON x.[NoteId]=n.[Id]) INNER JOIN [CanonEntities] AS c ON n.[EntityId]=c.[Id] WHERE x.[SourceId]=? AND x.[IsDeleted]=False AND n.[IsDeleted]=False AND c.[IsDeleted]=False ORDER BY x.[Id]");
            var claims = await Query($"SELECT TOP {relationLimit + 1} x.[Id],c.[Id] AS [ClaimId],c.[ContinuityId],c.[ClaimText],x.[EvidenceRelation],x.[Locator],x.[EvidenceExcerpt],x.[Summary],x.[Version],x.[IsDeleted] FROM [ClaimSources] AS x INNER JOIN [Claims] AS c ON x.[ClaimId]=c.[Id] WHERE x.[SourceId]=? AND x.[IsDeleted]=False AND c.[IsDeleted]=False ORDER BY x.[Id]");
            var tags = await Query($"SELECT TOP {relationLimit + 1} t.[Id],t.[Name],t.[Version],t.[IsDeleted] FROM [SourceTags] AS x INNER JOIN [Tags] AS t ON x.[TagId]=t.[Id] WHERE x.[SourceId]=? AND t.[IsDeleted]=False ORDER BY t.[Id]");
            var snapshots = await Query($"SELECT TOP {relationLimit + 1} [Id],[RetrievedAtUtc],[MediaType],[ByteSize],[ContentSha256],[ExtractionStatus],[Version],[IsDeleted] FROM [SourceSnapshots] WHERE [SourceId]=? AND [IsDeleted]=False ORDER BY [Id]");
            var collections = new[] { entities, notes, claims, tags, snapshots };
            return new SourceGraph(sourceRows[0], entities.Take(relationLimit).ToArray(), notes.Take(relationLimit).ToArray(),
                claims.Take(relationLimit).ToArray(), tags.Take(relationLimit).ToArray(), snapshots.Take(relationLimit).ToArray(),
                collections.Any(items => items.Count > relationLimit));
        }, cancellationToken);

    public Task<IReadOnlyList<CharacterRelationshipView>> GetCharacterRelationshipsAsync(
        int characterId,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await QueryCharacterRelationshipsAsync(connection, characterId, Math.Clamp(limit, 1, 100), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task<IReadOnlyList<CharacterRelationshipView>> QueryCharacterRelationshipsAsync(
        OleDbConnection connection,
        int characterId,
        int limit,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(connection,
            $"SELECT TOP {limit} r.[Id],r.[SourceCharacterId],r.[TargetCharacterId],r.[RelationshipTypeId],t.[Name],t.[InverseName],t.[IsDirected],r.[PeriodKind],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[PeriodLowerInclusive],r.[PeriodUpperInclusive],r.[PeriodOriginalText],r.[PeriodCalendarId],r.[Notes],r.[Version] " +
            "FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] " +
            "WHERE (r.[SourceCharacterId]=? OR r.[TargetCharacterId]=?) AND r.[IsDeleted]=False " +
            "AND EXISTS (SELECT 1 FROM [CanonEntities] AS cs WHERE cs.[Id]=r.[SourceCharacterId] AND cs.[IsDeleted]=False) " +
            "AND EXISTS (SELECT 1 FROM [CanonEntities] AS ct WHERE ct.[Id]=r.[TargetCharacterId] AND ct.[IsDeleted]=False) ORDER BY r.[Id]")
            .Add(OleDbType.Integer, characterId).Add(OleDbType.Integer, characterId);
        return await command.QueryAsync(reader =>
        {
            var source = reader.GetInt32(1);
            var target = reader.GetInt32(2);
            var directed = reader.GetBoolean(6);
            var fromSource = source == characterId;
            var label = !directed || fromSource || reader.IsDBNull(5) ? reader.GetString(4) : reader.GetString(5);
            return new CharacterRelationshipView(
                reader.GetInt32(0), characterId, fromSource ? target : source, reader.GetInt32(3), label,
                directed ? (fromSource ? "outgoing" : "incoming") : "undirected",
                new StoryDate(Enum.Parse<StoryDateKind>(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Unspecified),
                    reader.IsDBNull(9) ? null : DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Unspecified),
                    reader.GetBoolean(10), reader.GetBoolean(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetString(13)),
                reader.IsDBNull(14) ? null : Truncate(reader.GetString(14)), reader.GetInt32(15),
                !reader.IsDBNull(14) && reader.GetString(14).Length > MaximumReadTextCharacters);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<EntityTemporalState?> GetEntityTemporalStateAsync(int entityId, CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => GetEntityTemporalStateCoreAsync(entityId, null, null, cancellationToken), cancellationToken);

    public Task<EntityTemporalState?> GetEntityTemporalStateAtAsync(
        int entityId, DateTimeOffset currentInstant, string referenceTimeZoneId,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => GetEntityTemporalStateCoreAsync(entityId, currentInstant, referenceTimeZoneId, cancellationToken), cancellationToken);

    private async Task<EntityTemporalState?> GetEntityTemporalStateCoreAsync(
        int entityId, DateTimeOffset? overrideInstant, string? overrideZoneId, CancellationToken cancellationToken)
        {
            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var header = new AccessCommand(connection,
                "SELECT c.[ContinuityId],c.[EntityType],k.[CurrentInstantUtc],k.[ReferenceTimeZoneId],v.[DefaultTimeZoneId] FROM ([CanonEntities] AS c INNER JOIN [ContinuityClocks] AS k ON c.[ContinuityId]=k.[ContinuityId]) INNER JOIN [Continuities] AS v ON c.[ContinuityId]=v.[Id] WHERE c.[Id]=? AND c.[IsDeleted]=False")
                .Add(OleDbType.Integer, entityId);
            var headers = await header.QueryAsync(reader => new
            {
                ContinuityId = reader.GetInt32(0), Type = Enum.Parse<CanonEntityType>(reader.GetString(1)),
                Instant = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                Zone = reader.IsDBNull(3) ? reader.GetString(4) : reader.GetString(3)
            }, cancellationToken).ConfigureAwait(false);
            if (headers.Count == 0) return null;
            var h = headers[0];
            if (overrideInstant is null && h.Instant is not DateTime) return null;
            var effectiveZoneId = overrideZoneId ?? h.Zone;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(effectiveZoneId);
            var instantUtc = overrideInstant?.UtcDateTime ?? DateTime.SpecifyKind(h.Instant!.Value, DateTimeKind.Utc);
            var reference = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(new DateTimeOffset(instantUtc), zone).DateTime, DateTimeKind.Unspecified);
            var categories = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

            async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Active(string table, string ownerColumn)
            {
                using var command = new AccessCommand(connection, $"SELECT * FROM [{table}] WHERE [{ownerColumn}]=? AND [IsDeleted]=False ORDER BY [Id]")
                    .Add(OleDbType.Integer, entityId);
                var rows = await command.QueryAsync(reader => (IReadOnlyDictionary<string, object?>)ReadFields(reader), cancellationToken).ConfigureAwait(false);
                return rows.Where(row => ReadPeriod(row).Contains(reference)).ToArray();
            }

            switch (h.Type)
            {
                case CanonEntityType.Character:
                    categories["residences"] = await Active("CharacterResidences", "CharacterId");
                    categories["memberships"] = await Active("OrganizationMemberships", "CharacterId");
                    categories["relationships"] = (await GetCharacterRelationshipsAsync(entityId, 200, cancellationToken))
                        .Where(view => view.Period.Contains(reference))
                        .Select(view => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            ["Id"] = view.RelationshipId, ["RelatedCharacterId"] = view.RelatedCharacterId,
                            ["RelationshipTypeId"] = view.RelationshipTypeId, ["Label"] = view.Label,
                            ["Perspective"] = view.Perspective, ["Version"] = view.Version
                        }).ToArray();
                    break;
                case CanonEntityType.Organization:
                    categories["memberships"] = await Active("OrganizationMemberships", "OrganizationId");
                    categories["locations"] = await Active("OrganizationLocations", "OrganizationId");
                    break;
                case CanonEntityType.Object:
                    var ownership = await Active("ObjectOwnershipPeriods", "ObjectId");
                    categories["ownership"] = ownership;
                    categories["custody"] = await Active("ObjectCustodyPeriods", "ObjectId");
                    categories["locations"] = await Active("ObjectLocationPeriods", "ObjectId");
                    var activeIds = ownership.Select(row => Convert.ToInt32(row["Id"])).ToHashSet();
                    using (var owners = new AccessCommand(connection,
                               "SELECT x.[OwnershipPeriodId],x.[PrincipalId],x.[SharePartsPerMillion],x.[Notes],p.[PrincipalKind],p.[CharacterId],p.[OrganizationId],p.[Label] FROM ([ObjectOwnershipOwners] AS x INNER JOIN [ObjectOwnershipPeriods] AS q ON x.[OwnershipPeriodId]=q.[Id]) INNER JOIN [OwnershipPrincipals] AS p ON x.[PrincipalId]=p.[Id] WHERE q.[ObjectId]=? ORDER BY x.[OwnershipPeriodId],x.[PrincipalId]")
                           .Add(OleDbType.Integer, entityId))
                    {
                        var ownerRows = await owners.QueryAsync(reader => (IReadOnlyDictionary<string, object?>)ReadFields(reader), cancellationToken).ConfigureAwait(false);
                        categories["owners"] = ownerRows.Where(row => activeIds.Contains(Convert.ToInt32(row["OwnershipPeriodId"]))).ToArray();
                    }
                    break;
            }
            return new EntityTemporalState(entityId, h.ContinuityId, reference, instantUtc, effectiveZoneId, categories);
        }

    private static StoryDate ReadPeriod(IReadOnlyDictionary<string, object?> row) => new(
        Enum.Parse<StoryDateKind>(Convert.ToString(row["PeriodKind"])!),
        row["PeriodLowerBound"] is DateTime lower ? DateTime.SpecifyKind(lower, DateTimeKind.Unspecified) : null,
        row["PeriodUpperBound"] is DateTime upper ? DateTime.SpecifyKind(upper, DateTimeKind.Unspecified) : null,
        Convert.ToBoolean(row["PeriodLowerInclusive"]), Convert.ToBoolean(row["PeriodUpperInclusive"]),
        Convert.ToString(row["PeriodOriginalText"]), Convert.ToString(row["PeriodCalendarId"])!);

    public async Task<PageResult<EntitySummary>> SearchEntitiesAsync(
        CanonEntityType entityType,
        int? continuityId = null,
        string? text = null,
        int afterId = 0,
        int limit = 50,
        bool includeDeleted = false,
        bool onlyDeleted = false,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var (table, nameColumn) = Subtype(entityType);
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT TOP {limit + 1} c.[Id],c.[ContinuityId],c.[EntityType],s.[{nameColumn}],c.[Version],c.[IsDeleted],c.[UpdatedAtUtc],c.[VariantGroupId] " +
                  $"FROM [CanonEntities] AS c INNER JOIN [{table}] AS s ON c.[Id]=s.[EntityId] " +
                  "WHERE c.[EntityType]=? AND c.[Id]>?" +
                  (continuityId is null ? string.Empty : " AND c.[ContinuityId]=?") +
                  (onlyDeleted ? " AND c.[IsDeleted]=True" : includeDeleted ? string.Empty : " AND c.[IsDeleted]=False") +
                  (string.IsNullOrWhiteSpace(text) ? string.Empty : entityType == CanonEntityType.Character
                      ? $" AND (s.[{nameColumn}] LIKE ? OR s.[PreferredName] LIKE ?)"
                      : $" AND s.[{nameColumn}] LIKE ?") +
                  " ORDER BY c.[Id]";
        using var command = new AccessCommand(connection, sql)
            .Add(OleDbType.VarWChar, entityType.ToString(), 30)
            .Add(OleDbType.Integer, afterId);
        if (continuityId is not null) command.Add(OleDbType.Integer, continuityId);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{EscapeLike(text.Trim())}%";
            command.Add(OleDbType.VarWChar, pattern, 255);
            if (entityType == CanonEntityType.Character) command.Add(OleDbType.VarWChar, pattern, 255);
        }
        var rows = await command.QueryAsync(reader => new EntitySummary(
            reader.GetInt32(0), reader.GetInt32(1), Enum.Parse<CanonEntityType>(reader.GetString(2)),
            reader.GetString(3), reader.GetInt32(4), reader.GetBoolean(5), reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7)), cancellationToken).ConfigureAwait(false);
        var items = rows.Take(limit).ToArray();
        return new PageResult<EntitySummary>(items, rows.Count > limit ? items[^1].Id : null);
    }

    public Task<EntityDetails?> GetEntityAsync(int entityId, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => GetEntityCoreAsync(entityId, includeDeleted, cancellationToken), cancellationToken);

    private async Task<EntityDetails?> GetEntityCoreAsync(int entityId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var header = new AccessCommand(connection, "SELECT [ContinuityId],[EntityType],[Version],[IsDeleted],[UpdatedAtUtc],[VariantGroupId] FROM [CanonEntities] WHERE [Id]=?" + (includeDeleted ? string.Empty : " AND [IsDeleted]=False"))
            .Add(OleDbType.Integer, entityId);
        var headers = await header.QueryAsync(reader => (
            ContinuityId: reader.GetInt32(0), Type: Enum.Parse<CanonEntityType>(reader.GetString(1)),
            Version: reader.GetInt32(2), Deleted: reader.GetBoolean(3), Updated: reader.GetDateTime(4),
            VariantGroupId: reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5)), cancellationToken).ConfigureAwait(false);
        if (headers.Count == 0) return null;
        var h = headers[0];
        var (table, nameColumn) = Subtype(h.Type);
        using var detail = new AccessCommand(connection, $"SELECT * FROM [{table}] WHERE [EntityId]=?").Add(OleDbType.Integer, entityId);
        var fields = await detail.QueryAsync(ReadFields, cancellationToken).ConfigureAwait(false);
        if (fields.Count == 0) return null;
        var name = Convert.ToString(fields[0][nameColumn]) ?? string.Empty;
        return new EntityDetails(new EntitySummary(entityId, h.ContinuityId, h.Type, name, h.Version, h.Deleted, h.Updated, h.VariantGroupId), fields[0]);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string recordType, string recordKey, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        if (recordType.Length > 100 || recordKey.Length > 100)
            throw new ArgumentException("History record type and key must each be at most 100 characters.");
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new AccessCommand(connection, $"SELECT TOP {limit} [Id],[OperationId],[ChangedAtUtc],[ClientLabel],[ToolName],[Action],[RecordType],[RecordKey],[VersionBefore],[VersionAfter],[ChangeJson] FROM [ChangeLog] WHERE [RecordType]=? AND [RecordKey]=? ORDER BY [Id] DESC")
            .Add(OleDbType.VarWChar, recordType, 100).Add(OleDbType.VarWChar, recordKey, 100);
        return await command.QueryAsync(reader => new HistoryEntry(
            reader.GetInt32(0), reader.GetString(1), reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetOperationHistoryAsync(string operationId, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(operationId, out var operationGuid) || operationGuid == Guid.Empty)
            throw new ArgumentException("operationId must be a GUID.", nameof(operationId));
        operationId = operationGuid.ToString("D");
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new AccessCommand(connection, $"SELECT TOP {limit} [Id],[OperationId],[ChangedAtUtc],[ClientLabel],[ToolName],[Action],[RecordType],[RecordKey],[VersionBefore],[VersionAfter],[ChangeJson] FROM [ChangeLog] WHERE [OperationId]=? ORDER BY [Id] DESC")
            .Add(OleDbType.VarWChar, operationId, 36);
        return await command.QueryAsync(reader => new HistoryEntry(
            reader.GetInt32(0), reader.GetString(1), reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)), cancellationToken).ConfigureAwait(false);
    }

    public Task<DeletePreview?> PreviewDeleteAsync(int entityId, CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => PreviewDeleteCoreAsync(entityId, cancellationToken), cancellationToken);

    private async Task<DeletePreview?> PreviewDeleteCoreAsync(int entityId, CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(entityId, true, cancellationToken).ConfigureAwait(false);
        if (entity is null) return null;
        var blockers = new List<BlockingReference>();
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (name, table, column) in new[]
        {
            ("ProjectAssignmentsAsProject","ProjectEntities","ProjectId"), ("ProjectAssignmentsAsMember","ProjectEntities","MemberEntityId"),
            ("Notes","EntityNotes","EntityId"), ("EntityEvents","EntityEvents","EntityId"), ("EntityEventContext","EntityEvents","WorldEventId"),
            ("Tags","EntityTags","EntityId"), ("Sources","EntitySources","EntityId"), ("Claims","ClaimEntities","EntityId"),
            ("CharacterAliases","CharacterAliases","CharacterId"), ("CharacterResidences","CharacterResidences","CharacterId"),
            ("ResidenceLocation","CharacterResidences","LocationId"), ("OrganizationMembershipAsOrganization","OrganizationMemberships","OrganizationId"),
            ("OrganizationMembershipAsCharacter","OrganizationMemberships","CharacterId"), ("OrganizationAliases","OrganizationAliases","OrganizationId"),
            ("OrganizationLocations","OrganizationLocations","OrganizationId"), ("OrganizationLocationTarget","OrganizationLocations","LocationId"),
            ("RelationshipSource","CharacterRelationships","SourceCharacterId"), ("RelationshipTarget","CharacterRelationships","TargetCharacterId"),
            ("LocationChildren","Locations","ParentLocationId"), ("BirthLocation","Characters","BirthLocationId"), ("SpeciesCharacters","Characters","SpeciesId"),
            ("OwnershipPrincipalCharacter","OwnershipPrincipals","CharacterId"), ("OwnershipPrincipalOrganization","OwnershipPrincipals","OrganizationId"),
            ("OwnershipPeriods","ObjectOwnershipPeriods","ObjectId"), ("CustodyPeriods","ObjectCustodyPeriods","ObjectId"),
            ("ObjectLocations","ObjectLocationPeriods","ObjectId"),
            ("ObjectLocationTarget","ObjectLocationPeriods","LocationId"), ("WorldEventParticipation","WorldEventParticipants","ParticipantEntityId"),
            ("WorldEventParticipants","WorldEventParticipants","WorldEventId"), ("WorldEventLocations","WorldEventLocations","WorldEventId"),
            ("WorldEventLocationTarget","WorldEventLocations","LocationId")
        })
        {
            using var command = new AccessCommand(connection, $"SELECT COUNT(*) FROM [{table}] WHERE [{column}]=?").Add(OleDbType.Integer, entityId);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count > 0) blockers.Add(new BlockingReference(name, count));
        }
        if (entity.Summary.EntityType is CanonEntityType.Character or CanonEntityType.Organization)
        {
            var principalColumn = entity.Summary.EntityType == CanonEntityType.Character ? "CharacterId" : "OrganizationId";
            foreach (var (name, sql) in new[]
            {
                ("OwnershipThroughPrincipal", $"SELECT COUNT(*) FROM [ObjectOwnershipOwners] AS o INNER JOIN [OwnershipPrincipals] AS p ON o.[PrincipalId]=p.[Id] WHERE p.[{principalColumn}]=?"),
                ("CustodyThroughPrincipal", $"SELECT COUNT(*) FROM [ObjectCustodyPeriods] AS c INNER JOIN [OwnershipPrincipals] AS p ON c.[PrincipalId]=p.[Id] WHERE p.[{principalColumn}]=?")
            })
            {
                using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, entityId);
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                if (count > 0) blockers.Add(new BlockingReference(name, count));
            }
        }
        return new DeletePreview(entityId, blockers, !entity.Summary.IsDeleted);
    }

    public Task<LocalCurrentTime?> ResolveLocalCurrentTimeAsync(int entityId, CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => ResolveLocalCurrentTimeCoreAsync(entityId, cancellationToken), cancellationToken);

    public Task<LocalCurrentTime?> ResolveLocalCurrentTimeAtAsync(
        int entityId, DateTimeOffset currentInstant, string referenceTimeZoneId,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(async () =>
        {
            var referenceZone = TimeZoneInfo.FindSystemTimeZoneById(referenceTimeZoneId);
            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var entityCommand = new AccessCommand(connection,
                    "SELECT [ContinuityId],[EntityType] FROM [CanonEntities] WHERE [Id]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, entityId);
            var entities = await entityCommand.QueryAsync(reader => (
                ContinuityId: reader.GetInt32(0),
                Type: Enum.Parse<CanonEntityType>(reader.GetString(1))), cancellationToken).ConfigureAwait(false);
            if (entities.Count == 0) return null;
            var storyReference = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(currentInstant, referenceZone).DateTime, DateTimeKind.Unspecified);
            var location = await ResolveContextLocationAsync(connection, entityId, entities[0].Type, storyReference, cancellationToken).ConfigureAwait(false);
            var inherited = location is null ? null : await InheritTimeZoneAsync(connection, location.Value.Id, cancellationToken).ConfigureAwait(false);
            var outputZone = inherited is null ? referenceZone : TimeZoneInfo.FindSystemTimeZoneById(inherited.Value.Zone);
            return new LocalCurrentTime(
                entities[0].ContinuityId,
                TimeZoneInfo.ConvertTime(currentInstant, outputZone),
                outputZone.Id,
                inherited is null ? "client-reference" : $"location:{inherited.Value.SourceLocationId}",
                location?.Id,
                0);
        }, cancellationToken);

    private async Task<LocalCurrentTime?> ResolveLocalCurrentTimeCoreAsync(int entityId, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var entityCommand = new AccessCommand(connection, "SELECT [ContinuityId],[EntityType] FROM [CanonEntities] WHERE [Id]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, entityId);
        var entities = await entityCommand.QueryAsync(reader => (ContinuityId: reader.GetInt32(0), Type: Enum.Parse<CanonEntityType>(reader.GetString(1))), cancellationToken).ConfigureAwait(false);
        if (entities.Count == 0) return null;
        using var continuityCommand = new AccessCommand(connection, "SELECT c.[Name],c.[Description],c.[DefaultTimeZoneId],c.[Version],c.[IsDeleted],c.[CreatedAtUtc],c.[UpdatedAtUtc],k.[CurrentInstantUtc],k.[ReferenceTimeZoneId],k.[Version],k.[UpdatedAtUtc] FROM [Continuities] AS c INNER JOIN [ContinuityClocks] AS k ON c.[Id]=k.[ContinuityId] WHERE c.[Id]=?")
            .Add(OleDbType.Integer, entities[0].ContinuityId);
        var data = await continuityCommand.QueryAsync(reader => new
        {
            Continuity = new Continuity(entities[0].ContinuityId, reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4), reader.GetDateTime(5), reader.GetDateTime(6)),
            Clock = new ContinuityClock(entities[0].ContinuityId, reader.IsDBNull(7) ? null : reader.GetDateTime(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), reader.GetDateTime(10))
        }, cancellationToken).ConfigureAwait(false);
        if (data.Count == 0 || data[0].Clock.CurrentInstantUtc is not { } currentInstant) return null;
        var referenceZoneId = data[0].Clock.ReferenceTimeZoneId ?? data[0].Continuity.DefaultTimeZoneId;
        var referenceZone = TimeZoneInfo.FindSystemTimeZoneById(referenceZoneId);
        var storyReference = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(currentInstant, DateTimeKind.Utc)), referenceZone).DateTime,
            DateTimeKind.Unspecified);
        var location = await ResolveContextLocationAsync(connection, entityId, entities[0].Type, storyReference, cancellationToken).ConfigureAwait(false);
        var zone = location is null ? null : await InheritTimeZoneAsync(connection, location.Value.Id, cancellationToken).ConfigureAwait(false);
        return ZonedClock.Resolve(data[0].Continuity, data[0].Clock, zone?.Zone, location?.Id,
            zone is null ? "continuity-default" : $"location:{zone.Value.SourceLocationId}");
    }

    public Task<StoryAge?> GetCharacterAgeAsync(int characterId, CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(() => GetCharacterAgeCoreAsync(characterId, cancellationToken), cancellationToken);

    public Task<StoryAge?> GetCharacterAgeAtAsync(
        int characterId, DateTimeOffset currentInstant, string referenceTimeZoneId,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteConsistentReadAsync(async () =>
        {
            var local = await ResolveLocalCurrentTimeAtAsync(characterId, currentInstant, referenceTimeZoneId, cancellationToken).ConfigureAwait(false);
            if (local is null) return null;
            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = new AccessCommand(connection, "SELECT [BirthKind],[BirthLowerBound],[BirthUpperBound],[BirthLowerInclusive],[BirthUpperInclusive],[BirthOriginalText],[BirthCalendarId],[DeathKind],[DeathLowerBound],[DeathUpperBound],[DeathLowerInclusive],[DeathUpperInclusive],[DeathOriginalText],[DeathCalendarId] FROM [Characters] WHERE [EntityId]=?")
                .Add(OleDbType.Integer, characterId);
            var rows = await command.QueryAsync(reader => (Birth: ReadStoryDate(reader, 0), Death: ReadStoryDate(reader, 7)), cancellationToken).ConfigureAwait(false);
            return rows.Count == 0 ? null : StoryAge.Calculate(rows[0].Birth, DateOnly.FromDateTime(local.Instant.DateTime), rows[0].Death);
        }, cancellationToken);

    private async Task<StoryAge?> GetCharacterAgeCoreAsync(int characterId, CancellationToken cancellationToken)
    {
        var local = await ResolveLocalCurrentTimeAsync(characterId, cancellationToken).ConfigureAwait(false);
        if (local is null) return null;
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new AccessCommand(connection, "SELECT [BirthKind],[BirthLowerBound],[BirthUpperBound],[BirthLowerInclusive],[BirthUpperInclusive],[BirthOriginalText],[BirthCalendarId],[DeathKind],[DeathLowerBound],[DeathUpperBound],[DeathLowerInclusive],[DeathUpperInclusive],[DeathOriginalText],[DeathCalendarId] FROM [Characters] WHERE [EntityId]=?")
            .Add(OleDbType.Integer, characterId);
        var rows = await command.QueryAsync(reader => (Birth: ReadStoryDate(reader, 0), Death: ReadStoryDate(reader, 7)), cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : StoryAge.Calculate(rows[0].Birth, DateOnly.FromDateTime(local.Instant.DateTime), rows[0].Death);
    }

    private static async Task<(int Id, string Source)?> ResolveContextLocationAsync(OleDbConnection connection, int entityId, CanonEntityType type, DateTime storyReference, CancellationToken token)
    {
        if (type == CanonEntityType.Location) return (entityId, "location-self");
        if (type == CanonEntityType.WorldEvent)
        {
            using var eventLocation = new AccessCommand(connection, "SELECT TOP 1 [LocationId] FROM [WorldEventLocations] WHERE [WorldEventId]=? AND [IsDeleted]=False ORDER BY [IsPrimary] DESC,[Id]").Add(OleDbType.Integer, entityId);
            var eventValue = await eventLocation.ExecuteScalarAsync(token).ConfigureAwait(false);
            return eventValue is null or DBNull ? null : (Convert.ToInt32(eventValue), "world-event-location");
        }
        var (table, owner, roleFilter) = type switch
        {
            CanonEntityType.Character => ("CharacterResidences", "CharacterId", " AND [IsPrimary]=True"),
            CanonEntityType.Organization => ("OrganizationLocations", "OrganizationId", " AND [IsPrimary]=True"),
            CanonEntityType.Object => ("ObjectLocationPeriods", "ObjectId", string.Empty),
            _ => (string.Empty, string.Empty, string.Empty)
        };
        if (table.Length == 0) return null;
        using var command = new AccessCommand(connection, $"SELECT [LocationId],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive] FROM [{table}] WHERE [{owner}]=? AND [IsDeleted]=False{roleFilter} ORDER BY [PeriodLowerBound] DESC,[Id] DESC")
            .Add(OleDbType.Integer, entityId);
        var locations = await command.QueryAsync(reader => (
            Id: reader.GetInt32(0),
            Period: new StoryDate(StoryDateKind.Range,
                reader.IsDBNull(1) ? null : reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.GetBoolean(3), reader.GetBoolean(4))), token).ConfigureAwait(false);
        var activeLocation = locations.FirstOrDefault(row => row.Period.Contains(storyReference));
        if (activeLocation != default) return (activeLocation.Id, table);
        if (type != CanonEntityType.Object) return null;

        using var custody = new AccessCommand(connection,
            "SELECT p.[CharacterId],p.[OrganizationId],c.[PeriodLowerBound],c.[PeriodUpperBound],c.[PeriodLowerInclusive],c.[PeriodUpperInclusive] FROM [ObjectCustodyPeriods] AS c INNER JOIN [OwnershipPrincipals] AS p ON c.[PrincipalId]=p.[Id] " +
            "WHERE c.[ObjectId]=? AND c.[IsDeleted]=False ORDER BY c.[PeriodLowerBound] DESC,c.[Id] DESC")
            .Add(OleDbType.Integer, entityId);
        var custodians = await custody.QueryAsync(reader => (
            CharacterId: reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0),
            OrganizationId: reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
            Period: new StoryDate(StoryDateKind.Range,
                reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.GetBoolean(4), reader.GetBoolean(5))), token).ConfigureAwait(false);
        var custodian = custodians.FirstOrDefault(row => row.Period.Contains(storyReference));
        if (custodian == default) return null;
        if (custodian.CharacterId is { } characterId)
            return await ResolveContextLocationAsync(connection, characterId, CanonEntityType.Character, storyReference, token).ConfigureAwait(false);
        if (custodian.OrganizationId is { } organizationId)
            return await ResolveContextLocationAsync(connection, organizationId, CanonEntityType.Organization, storyReference, token).ConfigureAwait(false);
        return null;
    }

    private static async Task<(string Zone, int SourceLocationId)?> InheritTimeZoneAsync(OleDbConnection connection, int locationId, CancellationToken token)
    {
        int? cursor = locationId;
        var seen = new HashSet<int>();
        while (cursor is { } id && seen.Add(id))
        {
            using var command = new AccessCommand(connection, "SELECT [ParentLocationId],[TimeZoneId] FROM [Locations] WHERE [EntityId]=?").Add(OleDbType.Integer, id);
            var rows = await command.QueryAsync(reader => (
                Parent: reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0),
                Zone: reader.IsDBNull(1) ? null : reader.GetString(1)), token).ConfigureAwait(false);
            if (rows.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(rows[0].Zone)) return (rows[0].Zone!, id);
            cursor = rows[0].Parent;
        }
        return null;
    }

    private static StoryDate ReadStoryDate(DbDataReader reader, int offset) => new(
        Enum.Parse<StoryDateKind>(reader.GetString(offset)),
        reader.IsDBNull(offset + 1) ? null : DateTime.SpecifyKind(reader.GetDateTime(offset + 1), DateTimeKind.Unspecified),
        reader.IsDBNull(offset + 2) ? null : DateTime.SpecifyKind(reader.GetDateTime(offset + 2), DateTimeKind.Unspecified),
        reader.GetBoolean(offset + 3), reader.GetBoolean(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5), reader.GetString(offset + 6));

    private static Dictionary<string, object?> ReadFields(DbDataReader reader)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var name = reader.GetName(index);
            if (reader.IsDBNull(index))
            {
                result[name] = null;
                continue;
            }
            var value = reader.GetValue(index);
            if (value is string text && text.Length > MaximumReadTextCharacters)
            {
                result[name] = Truncate(text);
                result[name + "Truncated"] = true;
            }
            else result[name] = value;
        }
        return result;
    }

    private static string Truncate(string value) => value.Length <= MaximumReadTextCharacters
        ? value
        : value[..MaximumReadTextCharacters];

    private static (string Table, string NameColumn) Subtype(CanonEntityType type) => type switch
    {
        CanonEntityType.Project => ("Projects", "Name"), CanonEntityType.Location => ("Locations", "Name"),
        CanonEntityType.Character => ("Characters", "GivenName"), CanonEntityType.Organization => ("Organizations", "Name"),
        CanonEntityType.Object => ("Objects", "Name"), CanonEntityType.WorldEvent => ("WorldEvents", "Title"),
        CanonEntityType.Species => ("Species", "Name"),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string EscapeLike(string value) => value
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);
}
