using System.Data.OleDb;
using System.Globalization;
using System.Text.RegularExpressions;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Mcp.V4;

public sealed class AccessV4SemanticResolver(
    IAccessConnectionFactory connectionFactory,
    VaultWriteCoordinator coordinator,
    VaultReferenceService references) : IV4SemanticResolver
{
    private static readonly Regex SemanticReference = new(
        "^[a-z][a-z0-9-]{1,39}:[a-z0-9][a-z0-9-]{0,79}~[A-Z2-9]{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Candidate(int Id, string Type, string Label, int? ContinuityId, bool IsDeleted, string? Context = null);

    public async Task<V4ResolvedTarget> ResolveAsync(
        string value,
        int? continuityKey,
        IReadOnlyCollection<string> allowedTypes,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new V4ResolutionException("record.value_required", "A semantic reference or natural name is required.");
        if (allowedTypes.Count == 0)
            throw new ArgumentException("At least one allowed resource type is required.", nameof(allowedTypes));
        var allowed = ExpandTypes(allowedTypes);
        var trimmed = value.Trim();
        if (SemanticReference.IsMatch(trimmed))
        {
            ResolvedVaultReference resolved;
            try { resolved = await references.ResolveAsync(trimmed, continuityKey, cancellationToken, allowed.ToArray()).ConfigureAwait(false); }
            catch (ArgumentException exception) { throw new V4ResolutionException("reference.invalid", exception.Message); }
            catch (KeyNotFoundException exception) { throw new V4ResolutionException("record.not_found", exception.Message); }
            if (resolved.IsDeleted && !includeDeleted)
                throw new V4ResolutionException("record.deleted", "The referenced record is deleted. Include deleted records or restore it explicitly.");
            if (continuityKey is not null && resolved.ResourceType.Equals("Continuity", StringComparison.OrdinalIgnoreCase) &&
                resolved.Id != continuityKey)
                throw new V4ResolutionException("record.not_found", "The continuity reference is outside the selected context.");
            return new(resolved.ResourceType, resolved.Id, resolved.Reference, resolved.Label, resolved.ContinuityId, resolved.IsDeleted);
        }

        string normalized;
        try { normalized = TextNormalization.CanonicalKey(trimmed); }
        catch (ArgumentException exception) { throw new V4ResolutionException("record.value_required", exception.Message); }
        var naturalTypes = allowed.Where(SupportsNaturalName).ToArray();
        if (naturalTypes.Length == 0)
            throw new V4ResolutionException("reference.type_unsupported",
                "Natural-name resolution is not supported for this record kind; use its semantic reference.");

        var matches = await coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var candidates = new List<Candidate>();
            foreach (var type in naturalTypes.Order(StringComparer.OrdinalIgnoreCase))
                await LoadTypeAsync(connection, type, continuityKey, candidates, cancellationToken).ConfigureAwait(false);
            return candidates
                .Where(candidate => (includeDeleted || !candidate.IsDeleted) && TextNormalization.CanonicalKey(candidate.Label) == normalized)
                .GroupBy(candidate => (candidate.Type, candidate.Id))
                .Select(group => group.OrderBy(candidate => candidate.Context is null ? 0 : 1).First())
                .OrderBy(candidate => candidate.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Id)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);

        if (matches.Length == 0)
            throw new V4ResolutionException("record.not_found", "No active record with that exact name exists in the selected context.");

        var publicCandidates = new List<V4ResolutionCandidate>();
        foreach (var match in matches.Take(V4ContractLimits.MaximumAmbiguityCandidates))
        {
            var reference = await references.ReferenceAsync(match.Type, match.Id, cancellationToken).ConfigureAwait(false);
            publicCandidates.Add(new(match.Type, reference, match.Label, match.Context));
        }
        if (matches.Length != 1)
            throw new V4ResolutionException("record.ambiguous", $"{matches.Length} records have that exact name; use one returned semantic reference.", publicCandidates);
        var selected = matches[0];
        return new(selected.Type, selected.Id, publicCandidates[0].Reference, selected.Label, selected.ContinuityId, selected.IsDeleted);
    }

    private static HashSet<string> ExpandTypes(IReadOnlyCollection<string> types)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            if (type.Equals("Entity", StringComparison.OrdinalIgnoreCase))
                foreach (var entityType in Enum.GetNames<CanonEntityType>()) result.Add(entityType);
            else result.Add(type);
        }
        return result;
    }

    private static bool SupportsNaturalName(string type) =>
        Enum.TryParse<CanonEntityType>(type, true, out _) || type.ToUpperInvariant() is
        "CONTINUITY" or "SOURCE" or "TAG" or "RELATIONSHIPTYPE" or
        "ENTITYEVENT" or "RELATIONSHIPEVENT" or "CHARACTERTEMPORALEFFECT";

    private static async Task LoadTypeAsync(
        OleDbConnection connection, string type, int? continuityId,
        ICollection<Candidate> output, CancellationToken token)
    {
        if (Enum.TryParse<CanonEntityType>(type, true, out var entityType))
        {
            if (continuityId is null) return;
            var (table, labelExpression) = entityType switch
            {
                CanonEntityType.Project => ("Projects", "s.[Name]"), CanonEntityType.Location => ("Locations", "s.[Name]"),
                CanonEntityType.Character => ("Characters", "IIf(s.[PreferredName] Is Null,s.[GivenName],s.[PreferredName])"),
                CanonEntityType.Organization => ("Organizations", "s.[Name]"),
                CanonEntityType.Object => ("Objects", "s.[Name]"), CanonEntityType.WorldEvent => ("WorldEvents", "s.[Title]"),
                CanonEntityType.Species => ("Species", "s.[Name]"),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
            using (var command = new AccessCommand(connection,
                       $"SELECT s.[EntityId],{labelExpression},c.[IsDeleted] FROM [{table}] AS s INNER JOIN [CanonEntities] AS c ON s.[EntityId]=c.[Id] WHERE c.[ContinuityId]=?")
                   .Add(OleDbType.Integer, continuityId.Value))
            {
                var rows = await command.QueryAsync(reader => new Candidate(reader.GetInt32(0), entityType.ToString(), reader.GetString(1), continuityId, reader.GetBoolean(2)), token).ConfigureAwait(false);
                foreach (var row in rows) output.Add(row);
            }
            if (entityType == CanonEntityType.Character)
            {
                using var names = new AccessCommand(connection,
                        "SELECT s.[EntityId],s.[GivenName],c.[IsDeleted] FROM [Characters] AS s INNER JOIN [CanonEntities] AS c ON s.[EntityId]=c.[Id] WHERE c.[ContinuityId]=? AND s.[PreferredName] Is Not Null")
                    .Add(OleDbType.Integer, continuityId.Value);
                var rows = await names.QueryAsync(reader => new Candidate(
                    reader.GetInt32(0), "Character", reader.GetString(1), continuityId,
                    reader.GetBoolean(2), "given name"), token).ConfigureAwait(false);
                foreach (var row in rows) output.Add(row);
                await LoadAliases(connection, "CharacterAliases", "CharacterId", "Alias", "Character", continuityId.Value, output, token).ConfigureAwait(false);
            }
            if (entityType == CanonEntityType.Organization)
                await LoadAliases(connection, "OrganizationAliases", "OrganizationId", "Alias", "Organization", continuityId.Value, output, token).ConfigureAwait(false);
            return;
        }

        var (sql, global) = type.ToUpperInvariant() switch
        {
            "CONTINUITY" => ("SELECT [Id],[Name],Null AS [ContinuityId],[IsDeleted] FROM [Continuities]", true),
            "SOURCE" => ("SELECT [Id],[Title],Null AS [ContinuityId],[IsDeleted] FROM [Sources]", true),
            "TAG" => ("SELECT [Id],[Name],Null AS [ContinuityId],[IsDeleted] FROM [Tags]", true),
            "RELATIONSHIPTYPE" => ("SELECT [Id],[Name],Null AS [ContinuityId],[IsDeleted] FROM [RelationshipTypes]", true),
            "ENTITYEVENT" => ("SELECT e.[Id],e.[Title],c.[ContinuityId],(e.[IsDeleted] OR c.[IsDeleted]) FROM [EntityEvents] AS e INNER JOIN [CanonEntities] AS c ON e.[EntityId]=c.[Id]", false),
            "RELATIONSHIPEVENT" => ("SELECT e.[Id],e.[Title],r.[ContinuityId],(e.[IsDeleted] OR r.[IsDeleted]) FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id]", false),
            "CHARACTERTEMPORALEFFECT" => ("SELECT e.[Id],e.[Name],e.[ContinuityId],(e.[IsDeleted] OR c.[IsDeleted]) FROM [CharacterTemporalEffects] AS e INNER JOIN [CanonEntities] AS c ON e.[CharacterId]=c.[Id]", false),
            _ => throw new V4ResolutionException("reference.type_unsupported", $"Natural-name resolution is not supported for {type}; use its semantic reference.")
        };
        using var query = new AccessCommand(connection, sql);
        var values = await query.QueryAsync(reader => new Candidate(
            reader.GetInt32(0), type, reader.IsDBNull(1) ? type : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
        foreach (var candidate in values.Where(candidate =>
                     global
                         ? !candidate.Type.Equals("Continuity", StringComparison.OrdinalIgnoreCase) || continuityId is null || candidate.Id == continuityId
                         : continuityId is not null && candidate.ContinuityId == continuityId))
            output.Add(candidate);
    }

    private static async Task LoadAliases(
        OleDbConnection connection, string table, string ownerColumn, string labelColumn, string type,
        int continuityId, ICollection<Candidate> output, CancellationToken token)
    {
        using var command = new AccessCommand(connection,
                $"SELECT a.[{ownerColumn}],a.[{labelColumn}],(a.[IsDeleted] OR c.[IsDeleted]) FROM [{table}] AS a INNER JOIN [CanonEntities] AS c ON a.[{ownerColumn}]=c.[Id] WHERE c.[ContinuityId]=?")
            .Add(OleDbType.Integer, continuityId);
        var rows = await command.QueryAsync(reader => new Candidate(
            reader.GetInt32(0), type, reader.GetString(1), continuityId,
            Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture), "alias"), token).ConfigureAwait(false);
        foreach (var row in rows) output.Add(row);
    }
}
