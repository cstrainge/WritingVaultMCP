using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Mcp;

public sealed class VaultSessionContext
{
    public string ClientLabel { get; init; } = "mcp-client";
    public int? ContinuityId { get; private set; }
    public string? ContinuityName { get; private set; }
    public DateTimeOffset? CurrentTimeOverride { get; private set; }
    public DateOnly? CurrentDateOverride { get; private set; }
    public string? CurrentTimeZoneId { get; private set; }

    public void SelectContinuity(int id, string name)
    {
        ContinuityId = id;
        ContinuityName = name;
        ClearTime();
    }

    public int RequireContinuityId() => ContinuityId ??
        throw new McpException(
            "No continuity is selected for this connection. Call continuity_list, then select the chosen continuityName " +
            "with session_set on v4 or session_continuity_set on v3 before using continuity-scoped tools.");

    public void SetTime(DateTimeOffset instant, string timeZoneId)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        CurrentTimeOverride = instant;
        CurrentDateOverride = null;
        CurrentTimeZoneId = timeZoneId;
    }

    public void SetDate(DateOnly date, string timeZoneId)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        if (date.Year is < 1753 or > 9998)
            throw new ArgumentOutOfRangeException(nameof(date), "The story date must be between 1753 and 9998.");
        CurrentTimeOverride = null;
        CurrentDateOverride = date;
        CurrentTimeZoneId = timeZoneId;
    }

    public void ClearTime()
    {
        CurrentTimeOverride = null;
        CurrentDateOverride = null;
        CurrentTimeZoneId = null;
    }
}

public sealed record VaultSessionView(
    string? ContinuityName,
    DateTimeOffset? CurrentTimeOverride,
    string? CurrentTimeZoneId,
    string TimeSource);

public sealed record ResolvedVaultReference(
    string ResourceType, int Id, string Reference,
    string Label, int? ContinuityId, bool IsDeleted);

public sealed class VaultReferenceService(IAccessConnectionFactory connectionFactory, VaultWriteCoordinator writes)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly Regex ReferencePattern = new(
        @"^(?<prefix>[a-z][a-z0-9-]{1,39}):(?<slug>[a-z0-9][a-z0-9-]{0,79})~(?<token>[A-Z2-9]{10})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Descriptor(string Type, string Prefix, string Sql, string IdColumn, string LabelColumn, string? ContinuityColumn = null);

    private static readonly IReadOnlyDictionary<string, Descriptor> ByType = BuildDescriptors()
        .ToDictionary(value => value.Type, StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, Descriptor> ByPrefix = BuildDescriptors()
        .ToDictionary(value => value.Prefix, StringComparer.OrdinalIgnoreCase);

    public string OperationId(string requestToken)
    {
        if (string.IsNullOrWhiteSpace(requestToken) || requestToken.Length > 100)
            throw new ArgumentException("requestToken is required and cannot exceed 100 characters.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("writing-vault-operation-v2\0" + requestToken.Trim()));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    public async Task<(int Id, string Name)> ResolveContinuityNameAsync(string name, bool includeDeleted, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("continuityName is required.");
        var normalized = TextNormalization.CanonicalKey(name);
        return await writes.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new AccessCommand(connection,
                    "SELECT [Id],[Name] FROM [Continuities] WHERE [NormalizedName]=?" + (includeDeleted ? string.Empty : " AND [IsDeleted]=False"))
                .Add(OleDbType.VarWChar, normalized, 255);
            var rows = await command.QueryAsync(reader => (reader.GetInt32(0), reader.GetString(1)), token).ConfigureAwait(false);
            return rows.Count switch
            {
                1 => rows[0],
                _ => throw new KeyNotFoundException("The requested continuity name was not found.")
            };
        }, token).ConfigureAwait(false);
    }

    public async Task<string> ReferenceAsync(string resourceType, int id, CancellationToken token = default)
    {
        if (resourceType.Equals("CanonEntity", StringComparison.OrdinalIgnoreCase) ||
            resourceType.Equals("Entity", StringComparison.OrdinalIgnoreCase))
        {
            resourceType = await EntityTypeAsync(id, token).ConfigureAwait(false);
        }
        if (resourceType.Equals("ContinuityClock", StringComparison.OrdinalIgnoreCase)) resourceType = "Continuity";
        if (!ByType.TryGetValue(resourceType, out var descriptor))
            return $"{Slug(resourceType)}:record~{Token(resourceType, id)}";
        var row = await FindByIdAsync(descriptor, id, token).ConfigureAwait(false);
        var label = row?.Label ?? descriptor.Prefix;
        return $"{descriptor.Prefix}:{Slug(label)}~{Token(descriptor.Type, id)}";
    }

    internal string ReferenceFromKnownRecord(string resourceType, int id, string label)
    {
        if (resourceType.Equals("CanonEntity", StringComparison.OrdinalIgnoreCase) ||
            resourceType.Equals("Entity", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A concrete entity type is required for a known record.", nameof(resourceType));
        if (!ByType.TryGetValue(resourceType, out var descriptor))
            return $"{Slug(resourceType)}:record~{Token(resourceType, id)}";
        // These records have context-dependent display labels, but each has one canonical reference.
        if (resourceType.Equals("CharacterRelationship", StringComparison.OrdinalIgnoreCase)) label = "relationship";
        if (resourceType.Equals("RelationshipParticipant", StringComparison.OrdinalIgnoreCase)) label = "participant";
        if (resourceType.Equals("RelationshipMembershipPeriod", StringComparison.OrdinalIgnoreCase)) label = "membership period";
        return $"{descriptor.Prefix}:{Slug(label)}~{Token(descriptor.Type, id)}";
    }

    public async Task<string> ContinuityNameAsync(int id, CancellationToken token = default)
    {
        var row = await FindByIdAsync(ByType["Continuity"], id, token).ConfigureAwait(false);
        return row?.Label ?? throw new KeyNotFoundException("The continuity was not found.");
    }

    public async Task<ResolvedVaultReference> ResolveAsync(
        string reference,
        int? continuityId,
        CancellationToken token,
        params string[] allowedTypes)
    {
        var match = ReferencePattern.Match(reference ?? string.Empty);
        if (!match.Success) throw new ArgumentException("The resource reference is malformed.");
        if (!ByPrefix.TryGetValue(match.Groups["prefix"].Value, out var descriptor))
            throw new ArgumentException("The resource reference type is not supported.");
        if (allowedTypes.Length > 0 && !allowedTypes.Contains(descriptor.Type, StringComparer.OrdinalIgnoreCase) &&
            !(allowedTypes.Contains("Entity", StringComparer.OrdinalIgnoreCase) && IsEntity(descriptor.Type)))
            throw new ArgumentException("The resource reference has the wrong type for this field.");

        var expected = match.Groups["token"].Value;
        var candidates = await ReadCandidatesAsync(descriptor, token).ConfigureAwait(false);
        var matches = candidates.Where(candidate =>
                Token(descriptor.Type, candidate.Id).Equals(expected, StringComparison.Ordinal) &&
                (continuityId is null || candidate.ContinuityId is null || candidate.ContinuityId == continuityId))
            .ToArray();
        if (matches.Length != 1) throw new KeyNotFoundException("The resource reference was not found in the selected continuity.");
        var resolved = matches[0];
        return new ResolvedVaultReference(descriptor.Type, resolved.Id, reference!, resolved.Label, resolved.ContinuityId, resolved.IsDeleted);
    }

    public Task<ResolvedVaultReference> ResolveEntityAsync(string reference, int continuityId, CancellationToken token) =>
        ResolveAsync(reference, continuityId, token, "Entity");

    internal async Task<IReadOnlyList<(string Type, int Id, int? ContinuityId)>>
        EnumerateSnapshotRecordsAsync(IReadOnlySet<string> supportedTypes,
            CancellationToken token = default)
    {
        var result = new List<(string Type, int Id, int? ContinuityId)>();
        foreach (var descriptor in ByType.Values.OrderBy(value => value.Type, StringComparer.Ordinal))
        {
            if (!supportedTypes.Contains(descriptor.Type)) continue;
            foreach (var row in await ReadCandidatesAsync(descriptor, token).ConfigureAwait(false))
                result.Add((descriptor.Type, row.Id, row.ContinuityId));
        }
        return result;
    }

    private async Task<string> EntityTypeAsync(int id, CancellationToken token)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, "SELECT [EntityType] FROM [CanonEntities] WHERE [Id]=?")
            .Add(OleDbType.Integer, id);
        return Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture)
               ?? throw new KeyNotFoundException("The entity reference was not found.");
    }

    private async Task<(string Label, int? ContinuityId)?> FindByIdAsync(Descriptor descriptor, int id, CancellationToken token)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, descriptor.Sql);
        var rows = await command.QueryAsync(reader => (
            Id: Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            Label: reader.IsDBNull(1) ? descriptor.Prefix : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!,
            ContinuityId: descriptor.ContinuityColumn is null || reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
        var row = rows.SingleOrDefault(value => value.Id == id);
        return row == default ? null : (row.Label, row.ContinuityId);
    }

    private async Task<IReadOnlyList<(int Id, string Label, int? ContinuityId, bool IsDeleted)>> ReadCandidatesAsync(Descriptor descriptor, CancellationToken token)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, descriptor.Sql);
        return await command.QueryAsync(reader => (
            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? descriptor.Prefix : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!,
            descriptor.ContinuityColumn is null || reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
    }

    private static string Token(string type, int id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("writing-vault-reference-v2\0" + type.ToUpperInvariant() + ":" + id.ToString(CultureInfo.InvariantCulture)));
        ulong value = 0;
        for (var index = 0; index < 7; index++) value = (value << 8) | bytes[index];
        var chars = new char[10];
        for (var index = chars.Length - 1; index >= 0; index--)
        {
            chars[index] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return new string(chars);
    }

    private static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var character in normalized)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length == 80) break;
        }
        return builder.ToString().Trim('-') is { Length: > 0 } slug ? slug : "record";
    }

    private static bool IsEntity(string type) => Enum.TryParse<CanonEntityType>(type, true, out _);

    private static IEnumerable<Descriptor> BuildDescriptors()
    {
        foreach (var type in Enum.GetValues<CanonEntityType>())
        {
            var (table, labelExpression) = type switch
            {
                CanonEntityType.Project => ("Projects", "s.[Name]"), CanonEntityType.Location => ("Locations", "s.[Name]"),
                CanonEntityType.Character => ("Characters", "IIf(s.[PreferredName] Is Null,s.[GivenName],s.[PreferredName])"),
                CanonEntityType.Organization => ("Organizations", "s.[Name]"),
                CanonEntityType.Object => ("Objects", "s.[Name]"), CanonEntityType.WorldEvent => ("WorldEvents", "s.[Title]"),
                _ => throw new ArgumentOutOfRangeException()
            };
            yield return new(type.ToString(), Slug(type.ToString()),
                $"SELECT s.[EntityId] AS [Id],{labelExpression} AS [Label],c.[ContinuityId],c.[IsDeleted] FROM [{table}] AS s INNER JOIN [CanonEntities] AS c ON s.[EntityId]=c.[Id]",
                "Id", "Label", "ContinuityId");
        }
        yield return new("Continuity", "continuity", "SELECT [Id],[Name],Null AS [ContinuityId],[IsDeleted] FROM [Continuities]", "Id", "Name");
        yield return new("VariantGroup", "variant-group", "SELECT [Id],[Name] AS [Label],[ContinuityId],[IsDeleted] FROM [VariantGroups]", "Id", "Label", "ContinuityId");
        yield return new("Source", "source", "SELECT [Id],[Title],Null AS [ContinuityId],[IsDeleted] FROM [Sources]", "Id", "Title");
        yield return new("Tag", "tag", "SELECT [Id],[Name],Null AS [ContinuityId],[IsDeleted] FROM [Tags]", "Id", "Name");
        yield return new("RelationshipType", "relationship-type", "SELECT [Id],[Name],Null AS [ContinuityId],[IsDeleted] FROM [RelationshipTypes]", "Id", "Name");
        yield return new("OwnershipPrincipal", "ownership-principal", "SELECT [Id],[Label],[ContinuityId],[IsDeleted] FROM [OwnershipPrincipals]", "Id", "Label", "ContinuityId");
        static Descriptor Scoped(string type, string prefix, string table, string? label, string owner) => new(
            type, prefix,
            $"SELECT r.[Id]," + (label is null ? $"'{prefix}'" : $"r.[{label}]") + $" AS [Label],c.[ContinuityId],(r.[IsDeleted] OR c.[IsDeleted]) FROM [{table}] AS r INNER JOIN [CanonEntities] AS c ON r.[{owner}]=c.[Id]",
            "Id", "Label", "ContinuityId");
        yield return Scoped("EntityNote", "note", "EntityNotes", "Title", "EntityId");
        yield return Scoped("EntityEvent", "entity-event", "EntityEvents", "Title", "EntityId");
        yield return Scoped("ProjectAssignment", "project-assignment", "ProjectEntities", "Role", "ProjectId");
        yield return new("ContinuityNote", "continuity-note", "SELECT n.[Id],n.[Title] AS [Label],n.[ContinuityId],(n.[IsDeleted] OR c.[IsDeleted]) FROM [ContinuityNotes] AS n INNER JOIN [Continuities] AS c ON n.[ContinuityId]=c.[Id]", "Id", "Label", "ContinuityId");
        yield return new("ContinuityNoteSource", "continuity-note-source", "SELECT r.[Id],r.[Locator] AS [Label],n.[ContinuityId],(r.[IsDeleted] OR n.[IsDeleted] OR c.[IsDeleted] OR s.[IsDeleted]) FROM (([ContinuityNoteSources] AS r INNER JOIN [ContinuityNotes] AS n ON r.[ContinuityNoteId]=n.[Id]) INNER JOIN [Continuities] AS c ON n.[ContinuityId]=c.[Id]) INNER JOIN [Sources] AS s ON r.[SourceId]=s.[Id]", "Id", "Label", "ContinuityId");
        yield return new("EntityEventProject", "event-project", "SELECT r.[Id],r.[Role] AS [Label],c.[ContinuityId],(r.[IsDeleted] OR e.[IsDeleted] OR c.[IsDeleted] OR p.[IsDeleted]) FROM (([EntityEventProjects] AS r INNER JOIN [EntityEvents] AS e ON r.[EntityEventId]=e.[Id]) INNER JOIN [CanonEntities] AS c ON e.[EntityId]=c.[Id]) INNER JOIN [CanonEntities] AS p ON r.[ProjectId]=p.[Id]", "Id", "Label", "ContinuityId");
        yield return new("CharacterTemporalProfile", "temporal-profile", "SELECT p.[CharacterId] AS [Id],'temporal profile' AS [Label],c.[ContinuityId],(p.[IsDeleted] OR c.[IsDeleted]) FROM [CharacterTemporalProfiles] AS p INNER JOIN [CanonEntities] AS c ON p.[CharacterId]=c.[Id]", "Id", "Label", "ContinuityId");
        yield return new("CharacterTemporalEffect", "temporal-effect", "SELECT e.[Id],e.[Name] AS [Label],e.[ContinuityId],(e.[IsDeleted] OR c.[IsDeleted]) FROM [CharacterTemporalEffects] AS e INNER JOIN [CanonEntities] AS c ON e.[CharacterId]=c.[Id]", "Id", "Label", "ContinuityId");
        yield return Scoped("EntityImage", "image", "EntityImages", "Title", "EntityId");
        yield return new("StoryImage", "story-image",
            "SELECT [Id],IIf([Title] Is Null,'image',[Title]) AS [Label],[ContinuityId],[IsDeleted] FROM [StoryImages]",
            "Id", "Label", "ContinuityId");
        yield return Scoped("CharacterAlias", "character-alias", "CharacterAliases", "Alias", "CharacterId");
        yield return Scoped("CharacterResidence", "residence", "CharacterResidences", null, "CharacterId");
        yield return Scoped("OrganizationAlias", "organization-alias", "OrganizationAliases", "Alias", "OrganizationId");
        yield return Scoped("OrganizationMembership", "membership", "OrganizationMemberships", "Role", "OrganizationId");
        yield return Scoped("OrganizationLocation", "organization-location", "OrganizationLocations", "LocationRole", "OrganizationId");
        yield return Scoped("ObjectOwnershipPeriod", "ownership-period", "ObjectOwnershipPeriods", null, "ObjectId");
        yield return Scoped("ObjectCustodyPeriod", "custody-period", "ObjectCustodyPeriods", null, "ObjectId");
        yield return Scoped("ObjectLocationPeriod", "object-location", "ObjectLocationPeriods", null, "ObjectId");
        yield return Scoped("WorldEventParticipant", "event-participant", "WorldEventParticipants", "Role", "WorldEventId");
        yield return Scoped("WorldEventLocation", "event-location", "WorldEventLocations", "Role", "WorldEventId");
        yield return new("CharacterRelationship", "character-relationship", "SELECT [Id],'relationship' AS [Label],[ContinuityId],[IsDeleted] FROM [CharacterRelationships]", "Id", "Label", "ContinuityId");
        yield return new("RelationshipParticipant", "relationship-participant", "SELECT p.[Id],'participant' AS [Label],r.[ContinuityId],(p.[IsDeleted] OR r.[IsDeleted]) FROM [RelationshipParticipants] AS p INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id]", "Id", "Label", "ContinuityId");
        yield return new("RelationshipMembershipPeriod", "relationship-period", "SELECT m.[Id],'membership period' AS [Label],r.[ContinuityId],(m.[IsDeleted] OR p.[IsDeleted] OR r.[IsDeleted]) FROM ([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id]", "Id", "Label", "ContinuityId");
        yield return new("RelationshipEvent", "relationship-event", "SELECT e.[Id],e.[Title] AS [Label],r.[ContinuityId],(e.[IsDeleted] OR r.[IsDeleted]) FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id]", "Id", "Label", "ContinuityId");
        yield return new("RelationshipEventProject", "relationship-event-project", "SELECT x.[Id],x.[Role] AS [Label],r.[ContinuityId],(x.[IsDeleted] OR e.[IsDeleted] OR r.[IsDeleted]) FROM ([RelationshipEventProjects] AS x INNER JOIN [RelationshipEvents] AS e ON x.[RelationshipEventId]=e.[Id]) INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id]", "Id", "Label", "ContinuityId");
        yield return new("Claim", "claim", "SELECT [Id],'claim' AS [Label],[ContinuityId],[IsDeleted] FROM [Claims]", "Id", "Label", "ContinuityId");
        yield return new("NoteSource", "note-source", "SELECT r.[Id],r.[Locator] AS [Label],c.[ContinuityId],r.[IsDeleted] FROM ([NoteSources] AS r INNER JOIN [EntityNotes] AS n ON r.[NoteId]=n.[Id]) INNER JOIN [CanonEntities] AS c ON n.[EntityId]=c.[Id]", "Id", "Label", "ContinuityId");
        yield return new("ClaimEvidence", "claim-evidence", "SELECT r.[Id],r.[Locator] AS [Label],c.[ContinuityId],r.[IsDeleted] FROM [ClaimSources] AS r INNER JOIN [Claims] AS c ON r.[ClaimId]=c.[Id]", "Id", "Label", "ContinuityId");
        yield return new("SourceSnapshot", "source-snapshot", "SELECT [Id],[ExtractionStatus] AS [Label],Null AS [ContinuityId],[IsDeleted] FROM [SourceSnapshots]", "Id", "Label");
    }
}
