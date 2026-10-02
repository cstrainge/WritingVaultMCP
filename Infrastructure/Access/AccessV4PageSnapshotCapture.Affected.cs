using System.Data.OleDb;
using System.Globalization;
using System.Text.Json;
using WritingVaultMcp.Mcp;

namespace WritingVaultMcp.Infrastructure.Access;

internal sealed partial class AccessV4PageSnapshotCapture
{
    private static readonly HashSet<string> OverviewTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Continuity", "Project", "Location", "Character", "Organization", "Object", "WorldEvent", "Species",
        "VariantGroup", "Source", "SourceSnapshot", "Tag", "EntityNote", "ContinuityNote",
        "EntityEvent", "Claim", "EntityImage", "StoryImage", "RelationshipEvent",
        "RelationshipParticipant", "RelationshipMembershipPeriod", "RelationshipEventProject",
        "RelationshipType", "OwnershipPrincipal", "CharacterRelationship", "CharacterResidence",
        "OrganizationMembership", "OrganizationLocation", "ObjectOwnershipPeriod", "ObjectCustodyPeriod",
        "ObjectLocationPeriod", "WorldEventParticipant", "WorldEventLocation", "CharacterTemporalEffect"
    };

    private static readonly Dictionary<string, (string Table, (string Column, string Type)[] Owners)>
        OwnerColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EntityNote"] = ("EntityNotes", [("EntityId", "CanonEntity")]),
            ["EntityEvent"] = ("EntityEvents", [("EntityId", "CanonEntity"), ("WorldEventId", "WorldEvent")]),
            ["EntityImage"] = ("EntityImages", [("EntityId", "CanonEntity")]),
            ["CharacterAlias"] = ("CharacterAliases", [("CharacterId", "Character")]),
            ["OrganizationAlias"] = ("OrganizationAliases", [("OrganizationId", "Organization")]),
            ["ContinuityNote"] = ("ContinuityNotes", [("ContinuityId", "Continuity")]),
            ["SourceSnapshot"] = ("SourceSnapshots", [("SourceId", "Source")]),
            ["CharacterResidence"] = ("CharacterResidences", [("CharacterId", "Character"), ("LocationId", "Location")]),
            ["OrganizationMembership"] = ("OrganizationMemberships", [("OrganizationId", "Organization"), ("CharacterId", "Character")]),
            ["OrganizationLocation"] = ("OrganizationLocations", [("OrganizationId", "Organization"), ("LocationId", "Location")]),
            ["ObjectOwnershipPeriod"] = ("ObjectOwnershipPeriods", [("ObjectId", "Object")]),
            ["ObjectCustodyPeriod"] = ("ObjectCustodyPeriods", [("ObjectId", "Object")]),
            ["ObjectLocationPeriod"] = ("ObjectLocationPeriods", [("ObjectId", "Object"), ("LocationId", "Location")]),
            ["WorldEventParticipant"] = ("WorldEventParticipants", [("WorldEventId", "WorldEvent"), ("ParticipantEntityId", "CanonEntity")]),
            ["WorldEventLocation"] = ("WorldEventLocations", [("WorldEventId", "WorldEvent"), ("LocationId", "Location")]),
            ["RelationshipParticipant"] = ("RelationshipParticipants", [("RelationshipId", "CharacterRelationship"), ("CharacterId", "Character")]),
            ["RelationshipMembershipPeriod"] = ("RelationshipMembershipPeriods", [("ParticipantId", "RelationshipParticipant")]),
            ["RelationshipEvent"] = ("RelationshipEvents", [("RelationshipId", "CharacterRelationship"), ("WorldEventId", "WorldEvent")]),
            ["RelationshipEventProject"] = ("RelationshipEventProjects", [("RelationshipEventId", "RelationshipEvent"), ("ProjectId", "Project")]),
            ["EntityEventProject"] = ("EntityEventProjects", [("EntityEventId", "EntityEvent"), ("ProjectId", "Project")]),
            ["CharacterTemporalEffect"] = ("CharacterTemporalEffects", [("CharacterId", "Character")]),
            ["NoteSource"] = ("NoteSources", [("NoteId", "EntityNote"), ("SourceId", "Source")]),
            ["ContinuityNoteSource"] = ("ContinuityNoteSources", [("ContinuityNoteId", "ContinuityNote"), ("SourceId", "Source")]),
            ["ClaimEvidence"] = ("ClaimSources", [("ClaimId", "Claim"), ("SourceId", "Source")])
        };

    internal Task CaptureAffectedAsync(VaultWriteContext context,
        VaultMutationOutcome outcome, CancellationToken token) =>
        coordinator.ReadPendingWriteAsync(context, () => CaptureAffectedCoreAsync(context, outcome, token));

    private async Task CaptureAffectedCoreAsync(VaultWriteContext context,
        VaultMutationOutcome outcome, CancellationToken token)
    {
        var candidates = new HashSet<(string Type, int Key, int ContinuityId)>();
        var inspected = new HashSet<(string Type, int Key)>();
        var allContinuities = await ContinuityIdsAsync(context, token).ConfigureAwait(false);

        async Task AddAsync(string type, int key)
        {
            if (key < 1 || !inspected.Add((type, key))) return;
            if (type.Equals("ContinuityClock", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Continuity", StringComparison.OrdinalIgnoreCase))
            {
                // Character age and temporal state are materialized in the saved
                // page. A clock change therefore changes every character page
                // in this continuity, even when no character row was updated.
                using var characters = context.Command(
                    "SELECT [Id] FROM [CanonEntities] WHERE [ContinuityId]=? " +
                    "AND [EntityType]='Character'")
                    .Add(OleDbType.Integer, key);
                foreach (var character in await characters.QueryAsync(reader => reader.GetInt32(0), token)
                             .ConfigureAwait(false))
                    await AddAsync("Character", character).ConfigureAwait(false);
                type = "Continuity";
            }
            string? reference = null;
            ResolvedVaultReference? resolved = null;
            try
            {
                reference = await references.ReferenceAsync(type, key, token).ConfigureAwait(false);
                resolved = await references.ResolveAsync(reference, null, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
            {
                // Some journal resources are join-table operations rather than
                // navigable pages. Their owner columns below still identify pages.
            }
            if (resolved is not null)
            {
                if (OverviewTypes.Contains(resolved.ResourceType))
                {
                    var contexts = resolved.ResourceType == "Continuity"
                        ? [resolved.Id]
                        : resolved.ContinuityId is { } scoped ? [scoped] : allContinuities;
                    foreach (var continuity in contexts) candidates.Add((resolved.ResourceType, resolved.Id, continuity));
                }
                if (reference is not null && reference.Contains('~'))
                {
                    foreach (var page in await store.FindDependentPagesAsync(context, reference, token)
                                 .ConfigureAwait(false))
                        candidates.Add(page);
                }
                if (resolved.ResourceType == "Continuity")
                    candidates.Add(("Continuity", resolved.Id, resolved.Id));
                else if (resolved.ContinuityId is { } continuityId)
                    candidates.Add(("Continuity", continuityId, continuityId));
            }

            if (OwnerColumns.TryGetValue(type, out var owner))
            {
                var columns = string.Join(',', owner.Owners.Select(value => $"[{value.Column}]"));
                using var query = context.Command($"SELECT {columns} FROM [{owner.Table}] WHERE [Id]=?")
                    .Add(OleDbType.Integer, key);
                var owners = await query.QueryAsync(reader => owner.Owners
                    .Select((value, index) => (value.Type,
                        Id: reader.IsDBNull(index) ? 0 : Convert.ToInt32(reader.GetValue(index),
                            CultureInfo.InvariantCulture)))
                    .Where(value => value.Id > 0).ToArray(), token).ConfigureAwait(false);
                foreach (var row in owners)
                    foreach (var (ownerType, ownerKey) in row)
                        await AddAsync(ownerType, ownerKey).ConfigureAwait(false);
            }
            if (type.Equals("Character", StringComparison.OrdinalIgnoreCase))
            {
                using var species = context.Command("SELECT [SpeciesId] FROM [Characters] WHERE [EntityId]=?").Add(OleDbType.Integer, key);
                var linked = await species.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (linked is not null and not DBNull) await AddAsync("Species", Convert.ToInt32(linked, CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
            if (type.Equals("CharacterRelationship", StringComparison.OrdinalIgnoreCase))
            {
                using var members = context.Command(
                    "SELECT [CharacterId] FROM [RelationshipParticipants] WHERE [RelationshipId]=?")
                    .Add(OleDbType.Integer, key);
                foreach (var character in await members.QueryAsync(reader => reader.GetInt32(0), token)
                             .ConfigureAwait(false))
                    await AddAsync("Character", character).ConfigureAwait(false);
            }
            if (type.Equals("EntityEvent", StringComparison.OrdinalIgnoreCase))
            {
                using var projects = context.Command(
                    "SELECT [ProjectId] FROM [EntityEventProjects] WHERE [EntityEventId]=?")
                    .Add(OleDbType.Integer, key);
                foreach (var project in await projects.QueryAsync(reader => reader.GetInt32(0), token)
                             .ConfigureAwait(false))
                    await AddAsync("Project", project).ConfigureAwait(false);
            }
            if (type.Equals("RelationshipType", StringComparison.OrdinalIgnoreCase))
            {
                using var relationships = context.Command(
                    "SELECT [Id] FROM [CharacterRelationships] WHERE [RelationshipTypeId]=?")
                    .Add(OleDbType.Integer, key);
                foreach (var relationship in await relationships.QueryAsync(reader => reader.GetInt32(0), token)
                             .ConfigureAwait(false))
                    await AddAsync("CharacterRelationship", relationship).ConfigureAwait(false);
            }
            if (type.Equals("VariantGroup", StringComparison.OrdinalIgnoreCase))
            {
                using var members = context.Command(
                    "SELECT [Id] FROM [CanonEntities] WHERE [VariantGroupId]=?")
                    .Add(OleDbType.Integer, key);
                foreach (var entity in await members.QueryAsync(reader => reader.GetInt32(0), token)
                             .ConfigureAwait(false))
                    await AddAsync("CanonEntity", entity).ConfigureAwait(false);
            }
            if (type.Equals("StoryImage", StringComparison.OrdinalIgnoreCase))
            {
                using var imageOwner = context.Command(
                    "SELECT [OwnerKind],[OwnerId] FROM [StoryImages] WHERE [Id]=?")
                    .Add(OleDbType.Integer, key);
                var rows = await imageOwner.QueryAsync(reader =>
                    (Kind: reader.GetString(0), Id: reader.GetInt32(1)), token).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var ownerType = row.Kind switch
                    {
                        "Relationship" => "CharacterRelationship",
                        "Continuity" => "Continuity",
                        "EntityEvent" => "EntityEvent",
                        "RelationshipEvent" => "RelationshipEvent",
                        _ => null
                    };
                    if (ownerType is not null) await AddAsync(ownerType, row.Id).ConfigureAwait(false);
                }
            }
        }

        if (int.TryParse(outcome.ResourceKey, NumberStyles.None, CultureInfo.InvariantCulture,
                out var primary))
            await AddAsync(outcome.ResourceType, primary).ConfigureAwait(false);
        else
        {
            var parts = outcome.ResourceKey.Split(':');
            if (outcome.ResourceType is "EntityLink" or "EntityTag" or "EntitySource" &&
                parts.Length >= 2 &&
                int.TryParse(parts[0], out var entity))
                await AddAsync("CanonEntity", entity).ConfigureAwait(false);
            if (outcome.ResourceType == "EntityTag" && parts.Length == 2 &&
                int.TryParse(parts[1], out var linkedTag))
                await AddAsync("Tag", linkedTag).ConfigureAwait(false);
            if (outcome.ResourceType == "EntitySource" && parts.Length == 2 &&
                int.TryParse(parts[1], out var linkedSource))
                await AddAsync("Source", linkedSource).ConfigureAwait(false);
            if (outcome.ResourceType == "SourceTag" && parts.Length == 2 &&
                int.TryParse(parts[0], out var source) && int.TryParse(parts[1], out var tag))
            {
                await AddAsync("Source", source).ConfigureAwait(false);
                await AddAsync("Tag", tag).ConfigureAwait(false);
            }
        }

        if (outcome.Action is "merge" or "apply" or "remove" or
                "projects-add" or "projects-remove" && outcome.Change is not null)
        {
            var change = JsonSerializer.SerializeToElement(outcome.Change);
            if (outcome.Action == "merge" && change.TryGetProperty("source", out var sourceRef))
            {
                var source = await references.ResolveAsync(sourceRef.GetString()!, null, token)
                    .ConfigureAwait(false);
                await AddAsync(source.ResourceType, source.Id).ConfigureAwait(false);
            }
            if (change.TryGetProperty("targets", out var targetsJson) &&
                targetsJson.ValueKind == JsonValueKind.Array)
                foreach (var targetJson in targetsJson.EnumerateArray())
                    if (targetJson.ValueKind == JsonValueKind.String)
                    {
                        var target = await references.ResolveAsync(targetJson.GetString()!, null, token)
                            .ConfigureAwait(false);
                        await AddAsync(target.ResourceType, target.Id).ConfigureAwait(false);
                    }
            if (change.TryGetProperty("projects", out var projectsJson) &&
                projectsJson.ValueKind == JsonValueKind.Array)
                foreach (var projectJson in projectsJson.EnumerateArray())
                    if (projectJson.ValueKind == JsonValueKind.String)
                    {
                        var project = await references.ResolveAsync(projectJson.GetString()!, null, token)
                            .ConfigureAwait(false);
                        await AddAsync(project.ResourceType, project.Id).ConfigureAwait(false);
                    }
        }

        foreach (var (type, key, continuity) in candidates
                     .OrderBy(value => value.ContinuityId).ThenBy(value => value.Type, StringComparer.Ordinal)
                     .ThenBy(value => value.Key))
            await CaptureAsync(context, type, key, continuity, false, token).ConfigureAwait(false);
    }

    private static async Task<int[]> ContinuityIdsAsync(VaultWriteContext context, CancellationToken token)
    {
        using var query = context.Command("SELECT [Id] FROM [Continuities] ORDER BY [Id]");
        return (await query.QueryAsync(reader => reader.GetInt32(0), token).ConfigureAwait(false)).ToArray();
    }
}
