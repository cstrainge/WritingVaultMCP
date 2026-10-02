using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Bounded cross-kind search and content-filter projection.</summary>
public sealed partial class AccessV4ReadService
{
    public Task<V4Page<V4ReferenceSummary>> SearchAsync(V4SearchRequest request, CancellationToken token = default) =>
        MeasureAsync<V4Page<V4ReferenceSummary>>("search", async () =>
        {
            var continuity = session.RequireContinuityId();
            ValidateLimit(request.Limit, V4ContractLimits.MaximumPageSize);
            var revision = await RevisionAsync(token).ConfigureAwait(false);
            if (request.Text?.Length > V4ContractLimits.MaximumSearchTextLength)
                throw new VaultValidationException([new("search.text_too_long", "text", "Search text is too long.")]);
            var kinds = request.Kinds is { Count: > 0 }
                ? request.Kinds.Distinct().ToArray()
                : Enum.GetValues<V4RecordKind>().Where(kind => kind is V4RecordKind.Project or V4RecordKind.Location or
                    V4RecordKind.Character or V4RecordKind.Organization or V4RecordKind.Object or V4RecordKind.WorldEvent or
                    V4RecordKind.Source or V4RecordKind.Tag).ToArray();
            var scope = SearchScope(continuity, request, kinds);
            var after = request.Cursor is null ? -1L : cursors.Decode(request.Cursor, "search", scope).Position;
            var candidates = new List<(long Key, V4ReferenceSummary Summary)>();
            var eligible = await LoadSearchEligibleEntityIdsAsync(continuity, request, token).ConfigureAwait(false);
            foreach (var kind in kinds)
            {
                if (EntityType(kind) is { } entityType)
                {
                    if (SearchKey(kind, int.MaxValue) <= after) continue;
                    var contentMatches = request.IncludeContent && !string.IsNullOrWhiteSpace(request.Text)
                        ? await LoadContentMatchesAsync(entityType, continuity, request.Text, token).ConfigureAwait(false)
                        : null;
                    var entityAfter = after >= SearchKey(kind, 0)
                        ? checked((int)(after & 0xFFFFFFFFL)) : 0;
                    var accepted = 0;
                    while (true)
                    {
                        var page = await vault.SearchEntitiesAsync(entityType, continuity, request.IncludeContent ? null : request.Text, entityAfter, 100,
                            request.DeletionState != V4DeletionState.Active,
                            request.DeletionState == V4DeletionState.Deleted, token).ConfigureAwait(false);
                        foreach (var row in page.Items)
                        {
                            if (eligible is not null && !eligible.Contains(row.Id)) continue;
                            if (request.IncludeContent && !string.IsNullOrWhiteSpace(request.Text) &&
                                !row.Name.Contains(request.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                !(contentMatches?.Contains(row.Id) ?? false)) continue;
                            var key = SearchKey(kind, row.Id);
                            if (key <= after) continue;
                            candidates.Add((key, EntitySummary(row, session.ContinuityName)));
                            accepted++;
                        }
                        if (accepted > request.Limit || page.NextAfterId is null) break;
                        entityAfter = page.NextAfterId.Value;
                    }
                    foreach (var alias in await AliasMatchesAsync(entityType, continuity, request.Text, request.DeletionState, token).ConfigureAwait(false))
                    {
                        var key = SearchKey(kind, alias.Id);
                        if (key <= after || candidates.Any(item => item.Key == key)) continue;
                        if (eligible is not null && !eligible.Contains(alias.Id)) continue;
                        candidates.Add((key, new(references.ReferenceFromKnownRecord(entityType.ToString(), alias.Id, alias.Label), kind,
                            alias.Label, "alias", session.ContinuityName, alias.Version, alias.Deleted)));
                    }
                }
                else if (kind == V4RecordKind.Source)
                {
                    if(!string.IsNullOrWhiteSpace(request.Project))continue;
                    var eligibleSources=await LoadEligibleSourceIdsAsync(request.Tags,token).ConfigureAwait(false);
                    foreach (var row in await LoadSourceSearchAsync(request,token).ConfigureAwait(false))
                    {
                        if(eligibleSources is not null&&!eligibleSources.Contains(row.Id))continue;
                        var key = SearchKey(kind, row.Id); if (key <= after) continue;
                        candidates.Add((key, new(references.ReferenceFromKnownRecord("Source", row.Id, row.Title), kind,
                            row.Title, Version: row.Version, IsDeleted: row.IsDeleted)));
                    }
                }
                else if (kind == V4RecordKind.Tag)
                {
                    foreach (var row in await LoadTagSearchAsync(request,token).ConfigureAwait(false))
                    {
                        var key = SearchKey(kind, row.Id); if (key <= after) continue;
                        candidates.Add((key, new(references.ReferenceFromKnownRecord("Tag", row.Id, row.Name), kind,
                            row.Name, Version: row.Version, IsDeleted: row.IsDeleted)));
                    }
                }
                else if(string.IsNullOrWhiteSpace(request.Project)&&request.Tags is not {Count:>0})
                {
                    foreach(var row in await LoadSearchRecordRowsAsync(kind,continuity,request,token).ConfigureAwait(false))
                    {
                        var key=SearchKey(kind,row.Id,row.Subtype);if(key<=after)continue;
                        candidates.Add((key,new(references.ReferenceFromKnownRecord(row.Type,row.Id,row.Label),kind,row.Label,row.Context,
                            row.Global?null:session.ContinuityName,row.Version,row.Deleted)));
                    }
                }
            }
            var ordered = candidates.OrderBy(item => item.Key).Take(request.Limit + 1).ToArray();
            return new(ordered.Take(request.Limit).Select(item => item.Summary).ToArray(),
                ordered.Length > request.Limit ? cursors.Encode("search", ordered[request.Limit - 1].Key, scope) : null,
                ordered.Length > request.Limit, revision);
        });
    private async Task<IReadOnlyList<(int Id, string Label, int Version, bool Deleted)>> AliasMatchesAsync(
        CanonEntityType type, int continuity, string? text, V4DeletionState deletion, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || type is not (CanonEntityType.Character or CanonEntityType.Organization)) return [];
        var table = type == CanonEntityType.Character ? "CharacterAliases" : "OrganizationAliases";
        var owner = type == CanonEntityType.Character ? "CharacterId" : "OrganizationId";
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                $"SELECT a.[{owner}],a.[Alias],c.[Version],c.[IsDeleted] FROM [{table}] AS a INNER JOIN [CanonEntities] AS c ON a.[{owner}]=c.[Id] WHERE c.[ContinuityId]=? AND a.[Alias] LIKE ? AND a.[IsDeleted]=False" + DeletionSql("c", deletion) + $" ORDER BY a.[{owner}]")
            .Add(OleDbType.Integer, continuity).Add(OleDbType.VarWChar, $"%{EscapeLike(text.Trim())}%", 255);
        return await command.QueryAsync(r => (r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3)), token).ConfigureAwait(false);
    }

    private sealed record SearchRecordRow(int Id,string Type,string Label,string? Context,int Version,bool Deleted,int Subtype=0,bool Global=false);
    private async Task<IReadOnlyList<SearchRecordRow>> LoadSearchRecordRowsAsync(V4RecordKind kind,int continuity,V4SearchRequest request,CancellationToken token)
    {
        await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);var rows=new List<SearchRecordRow>();
        async Task Add(string type,string table,string label,string? context,int subtype,bool global,string? ownerColumn=null,string? searchable=null,string? ownerPredicate=null)
        {
            var scope=global?"":ownerColumn is null?" AND r.[ContinuityId]=?":$" AND EXISTS (SELECT 1 FROM [CanonEntities] AS c WHERE c.[Id]=r.[{ownerColumn}] AND c.[ContinuityId]=?)";
            var extra=request.IncludeContent&&!string.IsNullOrWhiteSpace(request.Text)?searchable??"Null":"Null";
            var sql=$"SELECT r.[Id],{label},"+(context??"Null")+$",r.[Version],r.[IsDeleted],{extra} FROM [{table}] AS r WHERE 1=1"+scope+(ownerPredicate is null?"":" AND "+ownerPredicate)+DeletionSql("r",request.DeletionState)+" ORDER BY r.[Id]";
            using var command=new AccessCommand(connection,sql);if(!global)command.Add(OleDbType.Integer,continuity);
            var found=await command.QueryAsync(r=>new{Row=new SearchRecordRow(r.GetInt32(0),type,r.IsDBNull(1)?type:r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetInt32(3),r.GetBoolean(4),subtype,global),Body=r.IsDBNull(5)?null:r.GetString(5)},token).ConfigureAwait(false);
            foreach(var candidate in found)
            {
                var row=candidate.Row;
                if(string.IsNullOrWhiteSpace(request.Text)||row.Label.Contains(request.Text.Trim(),StringComparison.OrdinalIgnoreCase)||
                   (row.Context?.Contains(request.Text.Trim(),StringComparison.OrdinalIgnoreCase)??false)||
                   (candidate.Body?.Contains(request.Text.Trim(),StringComparison.OrdinalIgnoreCase)??false))
                    rows.Add(row with{Context=row.Context is {Length:>255}?row.Context[..255]:row.Context});
            }
        }
        async Task AddCharacterRelationships()
        {
            var searchNotes = request.IncludeContent && !string.IsNullOrWhiteSpace(request.Text);
            var notesColumn = searchNotes ? "r.[Notes]" : "Left(r.[Notes],255)";
            var sql = "SELECT r.[Id],t.[Name],src.[GivenName],src.[PreferredName]," +
                $"dst.[GivenName],dst.[PreferredName],{notesColumn},r.[Version],r.[IsDeleted] " +
                "FROM (([CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t " +
                "ON r.[RelationshipTypeId]=t.[Id]) INNER JOIN [Characters] AS src " +
                "ON r.[SourceCharacterId]=src.[EntityId]) INNER JOIN [Characters] AS dst " +
                "ON r.[TargetCharacterId]=dst.[EntityId] WHERE r.[ContinuityId]=?";
            using var command = new AccessCommand(connection,
                sql + DeletionSql("r", request.DeletionState) + " ORDER BY r.[Id]")
                .Add(OleDbType.Integer, continuity);
            var found = await command.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Type = reader.GetString(1),
                Source = reader.IsDBNull(3) ? reader.GetString(2) : reader.GetString(3),
                Target = reader.IsDBNull(5) ? reader.GetString(4) : reader.GetString(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6),
                Version = reader.GetInt32(7), Deleted = reader.GetBoolean(8)
            }, token).ConfigureAwait(false);
            using var members = new AccessCommand(connection,
                "SELECT p.[RelationshipId],ch.[GivenName],ch.[PreferredName] " +
                "FROM ([RelationshipParticipants] AS p INNER JOIN [CharacterRelationships] AS r " +
                "ON p.[RelationshipId]=r.[Id]) INNER JOIN [Characters] AS ch " +
                "ON p.[CharacterId]=ch.[EntityId] " +
                "WHERE r.[ContinuityId]=? AND p.[IsDeleted]=False " +
                "ORDER BY p.[RelationshipId],p.[Id]")
                .Add(OleDbType.Integer, continuity);
            var participantNames = (await members.QueryAsync(reader => new
            {
                Relationship = reader.GetInt32(0),
                Name = reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2)
            }, token).ConfigureAwait(false))
                .GroupBy(member => member.Relationship)
                .ToDictionary(group => group.Key,
                    group => group.Select(member => member.Name).ToArray());
            foreach (var relationship in found)
            {
                var names = participantNames.GetValueOrDefault(relationship.Id) ??
                    [relationship.Source, relationship.Target];
                var legacyPair = names.Length == 2 &&
                    names.Contains(relationship.Source, StringComparer.Ordinal) &&
                    names.Contains(relationship.Target, StringComparer.Ordinal);
                var label = legacyPair
                    ? $"{relationship.Source} — {relationship.Type} — {relationship.Target}"
                    : $"{string.Join(", ", names)} — {relationship.Type}";
                var text = request.Text?.Trim();
                if (!string.IsNullOrEmpty(text) &&
                    !label.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                    !(searchNotes && (relationship.Notes?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)))
                    continue;
                rows.Add(new(relationship.Id, "CharacterRelationship", label,
                    relationship.Notes is { Length: > 255 } ? relationship.Notes[..255] : relationship.Notes,
                    relationship.Version, relationship.Deleted));
            }
        }
        async Task AddRelationshipEvents()
        {
            var deletion = request.DeletionState switch
            {
                V4DeletionState.Active => " AND e.[IsDeleted]=False AND r.[IsDeleted]=False",
                V4DeletionState.Deleted => " AND (e.[IsDeleted]=True OR r.[IsDeleted]=True)",
                _ => string.Empty
            };
            using var command = new AccessCommand(connection,
                "SELECT e.[Id],e.[Title],e.[Description],e.[Version]," +
                "(e.[IsDeleted] OR r.[IsDeleted]) " +
                "FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] " +
                "WHERE r.[ContinuityId]=?" + deletion + " ORDER BY e.[Id]")
                .Add(OleDbType.Integer, continuity);
            var found = await command.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Title = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Version = reader.GetInt32(3), Deleted = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture)
            }, token).ConfigureAwait(false);
            foreach (var item in found)
            {
                var text = request.Text?.Trim();
                if (!string.IsNullOrEmpty(text) && !item.Title.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                    !(request.IncludeContent && item.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) == true))
                    continue;
                rows.Add(new(item.Id, "RelationshipEvent", item.Title,
                    item.Description is { Length: > 255 } ? item.Description[..255] : item.Description,
                    item.Version, item.Deleted));
            }
        }
        async Task AddRelationshipMembershipRecords(bool periods)
        {
            using var characterQuery = new AccessCommand(connection,
                "SELECT ch.[EntityId],IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName])," +
                "c.[IsDeleted] FROM [Characters] AS ch INNER JOIN [CanonEntities] AS c " +
                "ON ch.[EntityId]=c.[Id] WHERE c.[ContinuityId]=?")
                .Add(OleDbType.Integer, continuity);
            var characters = (await characterQuery.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Name = reader.GetString(1), Deleted = reader.GetBoolean(2)
            }, token).ConfigureAwait(false)).ToDictionary(character => character.Id);
            var sql = periods
                ? "SELECT m.[Id],p.[CharacterId],t.[Name],m.[Version],m.[IsDeleted]," +
                  "p.[IsDeleted],r.[IsDeleted] FROM (([RelationshipMembershipPeriods] AS m " +
                  "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) " +
                  "INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id]) " +
                  "INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] " +
                  "WHERE r.[ContinuityId]=? ORDER BY m.[Id]"
                : "SELECT p.[Id],p.[CharacterId],t.[Name],p.[Version],p.[IsDeleted]," +
                  "False,r.[IsDeleted] FROM ([RelationshipParticipants] AS p " +
                  "INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id]) " +
                  "INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] " +
                  "WHERE r.[ContinuityId]=? ORDER BY p.[Id]";
            using var query = new AccessCommand(connection, sql).Add(OleDbType.Integer, continuity);
            foreach (var item in await query.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Character = reader.GetInt32(1),
                Type = reader.GetString(2), Version = reader.GetInt32(3),
                Deleted = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture) ||
                    Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture) ||
                    Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture)
            }, token).ConfigureAwait(false))
            {
                if (!characters.TryGetValue(item.Character, out var character)) continue;
                var deleted = item.Deleted || character.Deleted;
                if (request.DeletionState == V4DeletionState.Active && deleted ||
                    request.DeletionState == V4DeletionState.Deleted && !deleted) continue;
                var label = periods
                    ? $"{character.Name} — membership period in {item.Type}"
                    : $"{character.Name} — participant in {item.Type}";
                if (!string.IsNullOrWhiteSpace(request.Text) &&
                    !label.Contains(request.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new(item.Id,
                    periods ? "RelationshipMembershipPeriod" : "RelationshipParticipant",
                    label, item.Type, item.Version, deleted));
            }
        }
        switch(kind)
        {
            case V4RecordKind.Continuity:await Add("Continuity","Continuities","r.[Name]","r.[Description]",0,true);break;
            case V4RecordKind.SourceSnapshot:await Add("SourceSnapshot","SourceSnapshots","IIf(r.[RetrievedAtUtc] Is Null,'snapshot',CStr(r.[RetrievedAtUtc]))","r.[ExtractionStatus]",0,true);break;
            case V4RecordKind.Claim:await Add("Claim","Claims","Left(r.[ClaimText],255)","r.[ClaimStatus]",0,false,searchable:"r.[ClaimText]");break;
            case V4RecordKind.Note:
                await Add("ContinuityNote","ContinuityNotes","IIf(r.[Title] Is Null,'note',r.[Title])","Left(r.[Body],255)",1,false,searchable:"r.[Body]");await Add("EntityNote","EntityNotes","IIf(r.[Title] Is Null,'note',r.[Title])","Left(r.[Body],255)",2,false,"EntityId","r.[Body]");break;
            case V4RecordKind.Image:
                await Add("EntityImage","EntityImages","IIf(r.[Title] Is Null,'image',r.[Title])","IIf(r.[Caption] Is Null,r.[AltText],r.[Caption])",0,false,"EntityId","r.[Caption]",
                    "EXISTS (SELECT * FROM [CanonEntities] AS owner WHERE owner.[Id]=r.[EntityId] AND owner.[IsDeleted]=False)");
                await Add("StoryImage","StoryImages","IIf(r.[Title] Is Null,'image',r.[Title])","IIf(r.[Caption] Is Null,r.[AltText],r.[Caption])",1,false,searchable:"r.[Caption]",
                    ownerPredicate:"EXISTS (SELECT * FROM [Continuities] AS c WHERE c.[Id]=r.[ContinuityId] AND c.[IsDeleted]=False) AND " +
                    "(r.[OwnerKind]='Continuity' OR " +
                    "(r.[OwnerKind]='Relationship' AND EXISTS (SELECT * FROM [CharacterRelationships] AS owner WHERE owner.[Id]=r.[RelationshipId] AND owner.[IsDeleted]=False)) OR " +
                    "(r.[OwnerKind]='EntityEvent' AND EXISTS (SELECT * FROM [EntityEvents] AS owner INNER JOIN [CanonEntities] AS entity ON owner.[EntityId]=entity.[Id] WHERE owner.[Id]=r.[EntityEventId] AND owner.[IsDeleted]=False AND entity.[IsDeleted]=False)) OR " +
                    "(r.[OwnerKind]='RelationshipEvent' AND EXISTS (SELECT * FROM [RelationshipEvents] AS owner INNER JOIN [CharacterRelationships] AS parent ON owner.[RelationshipId]=parent.[Id] WHERE owner.[Id]=r.[RelationshipEventId] AND owner.[IsDeleted]=False AND parent.[IsDeleted]=False)))");break;
            case V4RecordKind.VariantGroup:await Add("VariantGroup","VariantGroups","IIf(r.[Name] Is Null,'variant group',r.[Name])","Left(r.[Notes],255)",0,false,searchable:"r.[Notes]");break;
            case V4RecordKind.Relationship:await AddCharacterRelationships();break;
            case V4RecordKind.RelationshipType:await Add("RelationshipType","RelationshipTypes","r.[Name]","r.[Description]",0,true);break;
            case V4RecordKind.Residence:await Add("CharacterResidence","CharacterResidences","'residence'","Left(r.[Notes],255)",0,false,"CharacterId");break;
            case V4RecordKind.Membership:await Add("OrganizationMembership","OrganizationMemberships","IIf(r.[Role] Is Null,'membership',r.[Role])","Left(r.[Notes],255)",0,false,"OrganizationId");break;
            case V4RecordKind.OrganizationLocation:await Add("OrganizationLocation","OrganizationLocations","IIf(r.[LocationRole] Is Null,'organization location',r.[LocationRole])","Left(r.[Notes],255)",0,false,"OrganizationId");break;
            case V4RecordKind.Ownership:await Add("ObjectOwnershipPeriod","ObjectOwnershipPeriods","r.[OwnerState]","Left(r.[Notes],255)",0,false,"ObjectId");break;
            case V4RecordKind.OwnershipPrincipal:await Add("OwnershipPrincipal","OwnershipPrincipals","IIf(r.[Label] Is Null,r.[PrincipalKind],r.[Label])","r.[PrincipalKind]",0,false);break;
            case V4RecordKind.Custody:await Add("ObjectCustodyPeriod","ObjectCustodyPeriods","r.[CustodianState]","Left(r.[Notes],255)",0,false,"ObjectId");break;
            case V4RecordKind.ObjectLocation:await Add("ObjectLocationPeriod","ObjectLocationPeriods","'object location'","Left(r.[Notes],255)",0,false,"ObjectId");break;
            case V4RecordKind.EntityEvent:await Add("EntityEvent","EntityEvents","r.[Title]","Left(r.[Description],255)",0,false,"EntityId","r.[Description]");break;
            case V4RecordKind.RelationshipEvent:await AddRelationshipEvents();break;
            case V4RecordKind.RelationshipParticipant:await AddRelationshipMembershipRecords(false);break;
            case V4RecordKind.RelationshipMembershipPeriod:await AddRelationshipMembershipRecords(true);break;
            case V4RecordKind.TemporalEffect:await Add("CharacterTemporalEffect","CharacterTemporalEffects","r.[Name]","Left(r.[Notes],255)",0,false,"CharacterId");break;
        }
        return kind==V4RecordKind.Continuity?rows.Where(row=>row.Id==continuity).ToArray():rows;
    }

    private async Task<HashSet<int>?> LoadEligibleSourceIdsAsync(IReadOnlyList<string>? tags,CancellationToken token)
    {
        if(tags is not {Count:>0})return null;var tagIds=await ResolveTagFilterAsync(tags,token).ConfigureAwait(false);
        await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);using var command=new AccessCommand(connection,"SELECT [SourceId],[TagId] FROM [SourceTags]");
        var rows=await command.QueryAsync(r=>(Source:r.GetInt32(0),Tag:r.GetInt32(1)),token).ConfigureAwait(false);
        return rows.GroupBy(x=>x.Source).Where(group=>tagIds.All(tag=>group.Any(x=>x.Tag==tag))).Select(group=>group.Key).ToHashSet();
    }

    private async Task<IReadOnlyList<SourceSummary>> LoadSourceSearchAsync(V4SearchRequest request,CancellationToken token)
    {
        await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);
        var content=request.IncludeContent&&!string.IsNullOrWhiteSpace(request.Text);
        var sql="SELECT [Id],[Title],[CanonicalUrl],[Version],[IsDeleted] FROM [Sources] AS s WHERE 1=1"+DeletionSql("s",request.DeletionState)+
            (string.IsNullOrWhiteSpace(request.Text)?"":content?" AND ([Title] LIKE ? OR [Citation] LIKE ? OR [Notes] LIKE ? OR [AuthorPublisher] LIKE ?)":" AND [Title] LIKE ?")+" ORDER BY [Id]";
        using var command=new AccessCommand(connection,sql);if(!string.IsNullOrWhiteSpace(request.Text)){var pattern=$"%{EscapeLike(request.Text.Trim())}%";command.Add(OleDbType.VarWChar,pattern,255);if(content)command.Add(OleDbType.LongVarWChar,pattern).Add(OleDbType.LongVarWChar,pattern).Add(OleDbType.VarWChar,pattern,255);}
        return await command.QueryAsync(r=>new SourceSummary(r.GetInt32(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetInt32(3),r.GetBoolean(4)),token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TagSummary>> LoadTagSearchAsync(V4SearchRequest request,CancellationToken token)
    {
        await using var connection=connectionFactory.Create();await connection.OpenAsync(token).ConfigureAwait(false);
        var sql="SELECT [Id],[Name],[Version],[IsDeleted] FROM [Tags] AS t WHERE 1=1"+DeletionSql("t",request.DeletionState)+(string.IsNullOrWhiteSpace(request.Text)?"":" AND ([Name] LIKE ? OR [Description] LIKE ?)")+" ORDER BY [Id]";
        using var command=new AccessCommand(connection,sql);if(!string.IsNullOrWhiteSpace(request.Text)){var pattern=$"%{EscapeLike(request.Text.Trim())}%";command.Add(OleDbType.VarWChar,pattern,255).Add(OleDbType.LongVarWChar,pattern);}
        return await command.QueryAsync(r=>new TagSummary(r.GetInt32(0),r.GetString(1),r.GetInt32(2),r.GetBoolean(3)),token).ConfigureAwait(false);
    }

    private async Task<HashSet<int>?> LoadSearchEligibleEntityIdsAsync(int continuity, V4SearchRequest request, CancellationToken token)
    {
        if (request.Tags is not {Count:>0} && string.IsNullOrWhiteSpace(request.Project)) return null;
        HashSet<int>? eligible=null;
        await using var connection=connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.Project))
        {
            var project=await targets.ProjectAsync(request.Project,continuity,token).ConfigureAwait(false);
            using var command=new AccessCommand(connection,"SELECT [MemberEntityId] FROM [ProjectEntities] WHERE [ProjectId]=? AND [IsDeleted]=False").Add(OleDbType.Integer,project.StorageKey);
            eligible=(await command.QueryAsync(r=>r.GetInt32(0),token).ConfigureAwait(false)).ToHashSet();
        }
        if(request.Tags is {Count:>0})
        {
            var tagIds=new List<int>(); foreach(var tag in request.Tags.Distinct(StringComparer.OrdinalIgnoreCase)) tagIds.Add((await targets.TagAsync(tag,token).ConfigureAwait(false)).StorageKey);
            using var command=new AccessCommand(connection,"SELECT x.[EntityId],x.[TagId] FROM [EntityTags] AS x INNER JOIN [CanonEntities] AS c ON x.[EntityId]=c.[Id] WHERE c.[ContinuityId]=?").Add(OleDbType.Integer,continuity);
            var rows=await command.QueryAsync(r=>(Entity:r.GetInt32(0),Tag:r.GetInt32(1)),token).ConfigureAwait(false);
            var tagged=rows.GroupBy(x=>x.Entity).Where(g=>tagIds.All(tag=>g.Any(x=>x.Tag==tag))).Select(g=>g.Key).ToHashSet();
            if(eligible is null)eligible=tagged;else eligible.IntersectWith(tagged);
        }
        return eligible;
    }

    private async Task<HashSet<int>> LoadContentMatchesAsync(CanonEntityType type,int continuity,string text,CancellationToken token)
    {
        var (table,_)=Subtype(type); await using var connection=connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        var character = type == CanonEntityType.Character;
        var predicate = character
            ? "(s.[PhysicalDescription] LIKE ? OR s.[PersonalitySummary] LIKE ? OR s.[PreferredName] LIKE ?)"
            : "s.[Description] LIKE ?";
        using var command=new AccessCommand(connection,$"SELECT s.[EntityId] FROM [{table}] AS s INNER JOIN [CanonEntities] AS c ON s.[EntityId]=c.[Id] WHERE c.[ContinuityId]=? AND {predicate}")
            .Add(OleDbType.Integer,continuity);
        var pattern=$"%{EscapeLike(text.Trim())}%";
        command.Add(OleDbType.LongVarWChar,pattern);
        if(character)
        {
            command.Add(OleDbType.LongVarWChar,pattern);
            command.Add(OleDbType.VarWChar,pattern,255);
        }
        return (await command.QueryAsync(r=>r.GetInt32(0),token).ConfigureAwait(false)).ToHashSet();
    }

    private async Task<bool> EntityMatchesFiltersAsync(int entity, CanonEntityType type, V4SearchRequest request, CancellationToken token)
    {
        if (request.Tags is not { Count: > 0 } && string.IsNullOrWhiteSpace(request.Project) &&
            (!request.IncludeContent || string.IsNullOrWhiteSpace(request.Text))) return true;
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.Project))
        {
            var project = await targets.ProjectAsync(request.Project, session.RequireContinuityId(), token).ConfigureAwait(false);
            using var membership = new AccessCommand(connection,
                "SELECT COUNT(*) FROM [ProjectEntities] WHERE [ProjectId]=? AND [MemberEntityId]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, project.StorageKey).Add(OleDbType.Integer, entity);
            if (Convert.ToInt32(await membership.ExecuteScalarAsync(token).ConfigureAwait(false)) == 0) return false;
        }
        if (request.Tags is { Count: > 0 })
        {
            foreach (var tagName in request.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tag = await targets.TagAsync(tagName, token).ConfigureAwait(false);
                using var tagged = new AccessCommand(connection, "SELECT COUNT(*) FROM [EntityTags] WHERE [EntityId]=? AND [TagId]=?")
                    .Add(OleDbType.Integer, entity).Add(OleDbType.Integer, tag.StorageKey);
                if (Convert.ToInt32(await tagged.ExecuteScalarAsync(token).ConfigureAwait(false)) == 0) return false;
            }
        }
        if (request.IncludeContent && !string.IsNullOrWhiteSpace(request.Text))
        {
            var (table, _) = Subtype(type);
            using var content = new AccessCommand(connection, $"SELECT COUNT(*) FROM [{table}] WHERE [EntityId]=? AND [Description] LIKE ?")
                .Add(OleDbType.Integer, entity).Add(OleDbType.LongVarWChar, $"%{EscapeLike(request.Text.Trim())}%");
            var contentMatch = Convert.ToInt32(await content.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0;
            if (!contentMatch)
            {
                // A name or alias match already admitted the row; content is an additional searchable field, not a mandatory filter.
            }
        }
        return true;
    }

}
