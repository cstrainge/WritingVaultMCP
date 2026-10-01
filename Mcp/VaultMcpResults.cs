using System.Collections;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Mcp;

public sealed record VaultHealthResult(
    bool Ready, string SchemaMigration, IReadOnlyList<SchemaIssue> SchemaIssues,
    IReadOnlyList<IntegrityIssue> IntegrityIssues, int PendingWrites, DateTime? LastSuccessfulBackupUtc,
    string ToolSurfaceVersion = "3.0");

public sealed record McpMutationResult(
    bool Success, string Code, string? ResourceReference = null, string? ResourceType = null,
    int? Version = null, bool Replayed = false, string? Message = null, bool Retryable = false);

public sealed record McpEntitySummary(
    string Reference, string EntityType, string Name, int Version, bool IsDeleted, DateTime UpdatedAtUtc,
    string? VariantGroupReference = null);

public sealed record McpEntityDetails(McpEntitySummary Summary, IReadOnlyDictionary<string, object?> Fields);
public sealed record McpPage<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record McpContinuitySummary(
    string Name, string DefaultTimeZoneId, int Version, bool IsDeleted,
    DateTime? ArtificialInstantUtc, string? ReferenceTimeZoneId, int ClockVersion);
public sealed record McpVariantGroupSummary(
    string Reference, string EntityType, string? Name, string? Notes, int Version, bool IsDeleted, bool NotesTruncated);
public sealed record McpSourceSummary(string Reference, string Title, string? CanonicalUrl, int Version, bool IsDeleted);
public sealed record McpTagSummary(string Reference, string Name, int Version, bool IsDeleted);
public sealed record McpCharacterRelationship(
    string Reference, string CharacterReference, string RelatedCharacterReference,
    string RelationshipTypeReference, string Label, string Perspective, StoryDate Period,
    string? Notes, int Version, bool NotesTruncated);
public sealed record McpHistoryEntry(
    DateTime ChangedAtUtc, string? ClientLabel, string ToolName, string Action,
    string RecordType, string? RecordReference, int? VersionBefore, int? VersionAfter,
    IReadOnlyDictionary<string, object?>? Change);
public sealed record McpDeletePreview(string EntityReference, IReadOnlyList<BlockingReference> Blockers, bool CanSoftDelete);
public sealed record McpEntityGraph(
    string EntityReference,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Notes,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Events,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Tags,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Sources,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Projects,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> TypeSpecificRelations,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Claims,
    bool Truncated);
public sealed record McpSourceGraph(
    McpSourceSummary Source,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Entities,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Notes,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Claims,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Tags,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Snapshots,
    bool Truncated);
public sealed record McpLocalCurrentTime(
    DateTimeOffset Instant, string TimeZoneId, string TimeZoneSource,
    string? LocationReference, int? ClockVersion, string TimeSource);
public sealed record McpTemporalState(
    string EntityReference, DateTime StoryReferenceTime, DateTime ArtificialInstantUtc,
    string ReferenceTimeZoneId,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> ActiveRecords,
    string TimeSource);

public sealed class VaultMcpResultMapper(VaultReferenceService references)
{
    private static readonly IReadOnlyDictionary<string, string> IdTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EntityId"] = "Entity", ["CharacterId"] = "Entity", ["RelatedCharacterId"] = "Entity",
        ["SourceCharacterId"] = "Entity", ["TargetCharacterId"] = "Entity", ["ParticipantEntityId"] = "Entity",
        ["MemberEntityId"] = "Entity", ["WorldEventId"] = "Entity", ["LocationId"] = "Entity",
        ["ParentLocationId"] = "Entity", ["BirthLocationId"] = "Entity", ["OrganizationId"] = "Entity",
        ["ObjectId"] = "Entity", ["ProjectId"] = "Entity", ["SourceEntityId"] = "Entity",
        ["SourceId"] = "Source", ["TagId"] = "Tag", ["ClaimId"] = "Claim", ["NoteId"] = "EntityNote",
        ["SourceSnapshotId"] = "SourceSnapshot", ["PrincipalId"] = "OwnershipPrincipal",
        ["RelationshipTypeId"] = "RelationshipType", ["VariantGroupId"] = "VariantGroup",
        ["ResidenceId"] = "CharacterResidence", ["MembershipId"] = "OrganizationMembership",
        ["OwnershipPeriodId"] = "ObjectOwnershipPeriod",
        ["RelationshipId"] = "CharacterRelationship", ["RelationshipEventId"] = "RelationshipEvent",
        ["ParticipantId"] = "RelationshipParticipant"
    };

    public async Task<McpMutationResult> MutationAsync(VaultMutationResult result, CancellationToken token)
    {
        string? reference = null;
        if (result.Success && result.ResourceKey is { } key && int.TryParse(key, out var id) && result.ResourceType is { } type)
            reference = type.Equals("Continuity", StringComparison.OrdinalIgnoreCase) || type.Equals("ContinuityClock", StringComparison.OrdinalIgnoreCase)
                ? await references.ContinuityNameAsync(id, token).ConfigureAwait(false)
                : await references.ReferenceAsync(type, id, token).ConfigureAwait(false);
        return new(result.Success, result.Code, reference, result.ResourceType, result.Version, result.Replayed, SafeMessage(result), result.Retryable);
    }

    public async Task<McpEntitySummary> EntitySummaryAsync(EntitySummary value, CancellationToken token) => new(
        await references.ReferenceAsync(value.EntityType.ToString(), value.Id, token).ConfigureAwait(false),
        value.EntityType.ToString(), value.Name, value.Version, value.IsDeleted, value.UpdatedAtUtc,
        value.VariantGroupId is { } group ? await references.ReferenceAsync("VariantGroup", group, token).ConfigureAwait(false) : null);

    public async Task<McpEntityDetails> EntityDetailsAsync(EntityDetails value, CancellationToken token) =>
        new(await EntitySummaryAsync(value.Summary, token).ConfigureAwait(false),
            await DictionaryAsync(value.Fields, null, token).ConfigureAwait(false));

    public async Task<McpPage<McpEntitySummary>> EntityPageAsync(PageResult<EntitySummary> page, CancellationToken token)
    {
        var items = new List<McpEntitySummary>(page.Items.Count);
        foreach (var item in page.Items) items.Add(await EntitySummaryAsync(item, token).ConfigureAwait(false));
        var cursor = page.NextAfterId is { } id && page.Items.Count > 0
            ? await references.ReferenceAsync(page.Items[^1].EntityType.ToString(), id, token).ConfigureAwait(false)
            : null;
        return new(items, cursor);
    }

    public async Task<IReadOnlyDictionary<string, object?>> DictionaryAsync(
        IReadOnlyDictionary<string, object?> source, string? defaultType, CancellationToken token)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (pair.Key.Equals("OperationId", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("DeletedOperationId", StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Value is not null && TryId(pair.Key, defaultType, out var resourceType) && TryInteger(pair.Value, out var id))
            {
                if (resourceType.Equals("Continuity", StringComparison.OrdinalIgnoreCase))
                    result["ContinuityName"] = await references.ContinuityNameAsync(id, token).ConfigureAwait(false);
                else
                    result[ReferenceKey(pair.Key)] = await references.ReferenceAsync(resourceType, id, token).ConfigureAwait(false);
                continue;
            }
            result[pair.Key] = await ValueAsync(pair.Value, token).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> DictionariesAsync(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string defaultType, CancellationToken token)
    {
        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var effectiveType = defaultType.Equals("Record", StringComparison.OrdinalIgnoreCase) ? InferRecordType(row) : defaultType;
            result.Add(await DictionaryAsync(row, effectiveType, token).ConfigureAwait(false));
        }
        return result;
    }

    private async Task<object?> ValueAsync(object? value, CancellationToken token)
    {
        if (value is System.Text.Json.JsonElement json)
        {
            if (json.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var jsonDictionary = json.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
                return await DictionaryAsync(jsonDictionary, null, token).ConfigureAwait(false);
            }
            if (json.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var result = new List<object?>();
                foreach (var item in json.EnumerateArray()) result.Add(await ValueAsync(item.Clone(), token).ConfigureAwait(false));
                return result;
            }
            return json.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => json.GetString(),
                System.Text.Json.JsonValueKind.Number when json.TryGetInt64(out var integer) => integer,
                System.Text.Json.JsonValueKind.Number => json.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => null
            };
        }
        if (value is IReadOnlyDictionary<string, object?> dictionary)
            return await DictionaryAsync(dictionary, null, token).ConfigureAwait(false);
        if (value is IDictionary untyped)
        {
            var converted = untyped.Keys.Cast<object>().ToDictionary(key => Convert.ToString(key)!, key => untyped[key]);
            return await DictionaryAsync(converted, null, token).ConfigureAwait(false);
        }
        if (value is IEnumerable enumerable and not string)
        {
            var result = new List<object?>();
            foreach (var item in enumerable) result.Add(await ValueAsync(item, token).ConfigureAwait(false));
            return result;
        }
        return value;
    }

    private static bool TryId(string key, string? defaultType, out string resourceType)
    {
        if (key.Equals("Id", StringComparison.OrdinalIgnoreCase) && defaultType is not null)
        {
            resourceType = defaultType;
            return true;
        }
        if (key.Equals("ContinuityId", StringComparison.OrdinalIgnoreCase))
        {
            resourceType = "Continuity";
            return true;
        }
        return IdTypes.TryGetValue(key, out resourceType!);
    }

    private static string ReferenceKey(string key) => key.Equals("Id", StringComparison.OrdinalIgnoreCase)
        ? "Reference"
        : key[..^2] + "Reference";

    private static bool TryInteger(object value, out int id)
    {
        if (value is System.Text.Json.JsonElement json && json.ValueKind == System.Text.Json.JsonValueKind.Number && json.TryGetInt32(out id))
            return id > 0;
        try { id = Convert.ToInt32(value); return id > 0; }
        catch { id = 0; return false; }
    }

    private static string InferRecordType(IReadOnlyDictionary<string, object?> row)
    {
        if (row.ContainsKey("RelatedCharacterId")) return "CharacterRelationship";
        if (row.ContainsKey("OrganizationId") || row.ContainsKey("CharacterId")) return "OrganizationMembership";
        if (row.ContainsKey("OwnerState")) return "ObjectOwnershipPeriod";
        if (row.ContainsKey("ParticipantEntityId")) return "WorldEventParticipant";
        if (row.ContainsKey("MemberEntityId")) return "ProjectAssignment";
        if (row.ContainsKey("EntityId") && row.ContainsKey("LocationType")) return "Location";
        return "EntityNote";
    }

    private static string? SafeMessage(VaultMutationResult result)
    {
        if (result.Success) return null;
        if (result.Code.StartsWith("validation.", StringComparison.OrdinalIgnoreCase))
            return "One or more supplied values are invalid.";
        return result.Code switch
        {
            "entity.not_found" => "A referenced resource was not found or is unavailable.",
            "entity.type_mismatch" => "A referenced resource has the wrong type.",
            "concurrency.conflict" => "The record changed after it was read.",
            "idempotency.input_mismatch" => "The request token was already used with different input.",
            "storage.failure" or "schema.not_ready" => "The vault could not complete the mutation.",
            _ when result.Code.StartsWith("delete.", StringComparison.OrdinalIgnoreCase) ||
                   result.Code.StartsWith("constraint.", StringComparison.OrdinalIgnoreCase) ||
                   result.Code.StartsWith("continuity.", StringComparison.OrdinalIgnoreCase) ||
                   result.Code.StartsWith("interval.", StringComparison.OrdinalIgnoreCase) ||
                   result.Code.StartsWith("location.", StringComparison.OrdinalIgnoreCase) => result.Message,
            _ => "The vault could not complete the mutation."
        };
    }
}
