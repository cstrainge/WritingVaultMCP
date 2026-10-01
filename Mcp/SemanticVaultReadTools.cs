using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Infrastructure;

namespace WritingVaultMcp.Mcp;

[McpServerToolType]
public sealed class SemanticVaultReadTools(
    AccessVaultService vault,
    AccessSchemaVerifier schema,
    AccessIntegrityVerifier integrity,
    VaultWriteCoordinator coordinator,
    WritingVaultStorageOptions storage,
    VaultSessionContext session,
    VaultReferenceService references,
    VaultMcpResultMapper mapper)
{
    [McpServerTool(Name = "session_continuity_set", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Selects this connection's continuity by its unique name. Later continuity-scoped calls use it implicitly and never require a database ID.")]
    public async Task<VaultSessionView> SetContinuity(string continuityName, CancellationToken cancellationToken = default)
    {
        var selected = await SafeRead(() => references.ResolveContinuityNameAsync(continuityName, false, cancellationToken), "continuity selection").ConfigureAwait(false);
        session.SelectContinuity(selected.Id, selected.Name);
        return View();
    }

    [McpServerTool(Name = "session_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Returns this connection's selected continuity name and optional artificial-time override.")]
    public VaultSessionView GetSession() => View();

    [McpServerTool(Name = "session_time_set", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Sets an artificial current-time override for this client connection only. It does not change the shared continuity clock.")]
    public VaultSessionView SetSessionTime(DateTimeOffset currentInstant, string referenceTimeZoneId)
    {
        _ = session.RequireContinuityId();
        session.SetTime(currentInstant, referenceTimeZoneId);
        return View();
    }

    [McpServerTool(Name = "session_time_clear", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Clears this connection's artificial-time override so temporal reads use the selected continuity's persisted clock.")]
    public VaultSessionView ClearSessionTime()
    {
        session.ClearTime();
        return View();
    }

    [McpServerTool(Name = "vault_health", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Checks exact schema compatibility and persisted integrity without exposing database paths, SQL, IDs, or connection details.")]
    public Task<VaultHealthResult> Health(CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var schemaResult = await schema.VerifyAsync(cancellationToken).ConfigureAwait(false);
        var integrityResult = schemaResult.IsValid
            ? await integrity.VerifyAsync(cancellationToken).ConfigureAwait(false)
            : new IntegrityVerificationResult([]);
        return new VaultHealthResult(schemaResult.IsValid && integrityResult.IsValid,
            AccessSchemaDefinition.MigrationId, schemaResult.Issues, integrityResult.Issues,
            coordinator.PendingWrites, FindLastSuccessfulBackup(storage.BackupRoot));
    }, "vault health check");

    [McpServerTool(Name = "continuity_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Lists continuity names and clock states. Storage IDs are never returned.")]
    public Task<IReadOnlyList<McpContinuitySummary>> ListContinuities(
        bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
        (IReadOnlyList<McpContinuitySummary>)(await vault.ListContinuitiesAsync(includeDeleted, onlyDeleted, cancellationToken).ConfigureAwait(false))
            .Select(value => new McpContinuitySummary(value.Name, value.DefaultTimeZoneId, value.Version, value.IsDeleted,
                value.ArtificialInstantUtc, value.ReferenceTimeZoneId, value.ClockVersion)).ToArray(), "continuity listing");

    [McpServerTool(Name = "entity_search", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Searches the selected continuity with opaque semantic-reference keyset pagination. Returns at most 100 entities.")]
    public Task<McpPage<McpEntitySummary>> SearchEntities(
        CanonEntityType entityType, string? text = null, string? after = null, int limit = 50,
        bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var continuityId = session.RequireContinuityId();
        var afterId = after is null ? 0 : (await references.ResolveAsync(after, continuityId, cancellationToken, entityType.ToString()).ConfigureAwait(false)).Id;
        var page = await vault.SearchEntitiesAsync(entityType, continuityId, text, afterId, limit, includeDeleted, onlyDeleted, cancellationToken).ConfigureAwait(false);
        return await mapper.EntityPageAsync(page, cancellationToken).ConfigureAwait(false);
    }, "entity search");

    [McpServerTool(Name = "entity_get", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Gets one entity by semantic reference within the selected continuity.")]
    public Task<McpEntityDetails?> GetEntity(string entityReference, bool includeDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var resolved = await references.ResolveEntityAsync(entityReference, session.RequireContinuityId(), cancellationToken).ConfigureAwait(false);
        var value = await vault.GetEntityAsync(resolved.Id, includeDeleted, cancellationToken).ConfigureAwait(false);
        return value is null ? null : await mapper.EntityDetailsAsync(value, cancellationToken).ConfigureAwait(false);
    }, "entity read");

    [McpServerTool(Name = "variant_group_list", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Lists alternate-version groups in the selected continuity using semantic references.")]
    public Task<IReadOnlyList<McpVariantGroupSummary>> ListVariantGroups(
        CanonEntityType? entityType = null, bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var rows = await vault.ListVariantGroupsAsync(session.RequireContinuityId(), entityType, includeDeleted, onlyDeleted, cancellationToken).ConfigureAwait(false);
        var result = new List<McpVariantGroupSummary>(rows.Count);
        foreach (var row in rows) result.Add(new(await references.ReferenceAsync("VariantGroup", row.Id, cancellationToken).ConfigureAwait(false),
            row.EntityType.ToString(), row.Name, row.Notes, row.Version, row.IsDeleted, row.NotesTruncated));
        return (IReadOnlyList<McpVariantGroupSummary>)result;
    }, "variant group listing");

    [McpServerTool(Name = "source_search", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Searches vault-global sources and returns semantic references.")]
    public Task<IReadOnlyList<McpSourceSummary>> SearchSources(string? text = null, bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var rows = await vault.SearchSourcesAsync(text, includeDeleted, onlyDeleted, cancellationToken).ConfigureAwait(false);
        var result = new List<McpSourceSummary>(rows.Count);
        foreach (var row in rows) result.Add(new(await references.ReferenceAsync("Source", row.Id, cancellationToken).ConfigureAwait(false), row.Title, row.CanonicalUrl, row.Version, row.IsDeleted));
        return (IReadOnlyList<McpSourceSummary>)result;
    }, "source search");

    [McpServerTool(Name = "tag_search", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Searches vault-global tags and returns semantic references.")]
    public Task<IReadOnlyList<McpTagSummary>> SearchTags(string? text = null, bool includeDeleted = false, bool onlyDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var rows = await vault.SearchTagsAsync(text, includeDeleted, onlyDeleted, cancellationToken).ConfigureAwait(false);
        var result = new List<McpTagSummary>(rows.Count);
        foreach (var row in rows) result.Add(new(await references.ReferenceAsync("Tag", row.Id, cancellationToken).ConfigureAwait(false), row.Name, row.Version, row.IsDeleted));
        return (IReadOnlyList<McpTagSummary>)result;
    }, "tag search");

    [McpServerTool(Name = "entity_graph", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Returns bounded related records using semantic references. Each collection is capped at 50.")]
    public Task<McpEntityGraph?> GetEntityGraph(string entityReference, int relationLimit = 50, bool includeDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var entity = await references.ResolveEntityAsync(entityReference, session.RequireContinuityId(), cancellationToken).ConfigureAwait(false);
        var graph = await vault.GetEntityGraphAsync(entity.Id, relationLimit, includeDeleted, cancellationToken).ConfigureAwait(false);
        if (graph is null) return null;
        return new McpEntityGraph(entityReference,
            await mapper.DictionariesAsync(graph.Notes, "EntityNote", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Events, "EntityEvent", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Tags, "Tag", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Sources, "Source", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Projects, "ProjectAssignment", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.TypeSpecificRelations, "Record", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Claims, "Claim", cancellationToken).ConfigureAwait(false), graph.Truncated);
    }, "entity graph read");

    [McpServerTool(Name = "source_graph", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Returns bounded reverse links from a source semantic reference.")]
    public Task<McpSourceGraph?> GetSourceGraph(string sourceReference, int relationLimit = 50, bool includeDeleted = false, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var source = await references.ResolveAsync(sourceReference, null, cancellationToken, "Source").ConfigureAwait(false);
        var graph = await vault.GetSourceGraphAsync(source.Id, relationLimit, includeDeleted, cancellationToken).ConfigureAwait(false);
        if (graph is null) return null;
        return new McpSourceGraph(new(sourceReference, graph.Source.Title, graph.Source.CanonicalUrl, graph.Source.Version, graph.Source.IsDeleted),
            await mapper.DictionariesAsync(graph.Entities, "Entity", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Notes, "NoteSource", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Claims, "ClaimEvidence", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Tags, "Tag", cancellationToken).ConfigureAwait(false),
            await mapper.DictionariesAsync(graph.Snapshots, "SourceSnapshot", cancellationToken).ConfigureAwait(false), graph.Truncated);
    }, "source graph read");

    [McpServerTool(Name = "character_relationships", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Lists relationships from either endpoint using semantic references and perspective-correct labels.")]
    public Task<IReadOnlyList<McpCharacterRelationship>> GetCharacterRelationships(string characterReference, int limit = 100, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var character = await references.ResolveAsync(characterReference, session.RequireContinuityId(), cancellationToken, "Character").ConfigureAwait(false);
        var rows = await vault.GetCharacterRelationshipsAsync(character.Id, limit, cancellationToken).ConfigureAwait(false);
        var result = new List<McpCharacterRelationship>(rows.Count);
        foreach (var row in rows) result.Add(new(
            await references.ReferenceAsync("CharacterRelationship", row.RelationshipId, cancellationToken).ConfigureAwait(false),
            await references.ReferenceAsync("Character", row.CharacterId, cancellationToken).ConfigureAwait(false),
            await references.ReferenceAsync("Character", row.RelatedCharacterId, cancellationToken).ConfigureAwait(false),
            await references.ReferenceAsync("RelationshipType", row.RelationshipTypeId, cancellationToken).ConfigureAwait(false),
            row.Label, row.Perspective, row.Period, row.Notes, row.Version, row.NotesTruncated));
        return (IReadOnlyList<McpCharacterRelationship>)result;
    }, "character relationship read");

    [McpServerTool(Name = "character_age", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Calculates age from this connection's override when set, otherwise from the selected continuity clock.")]
    public Task<StoryAge?> GetCharacterAge(string characterReference, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var character = await references.ResolveAsync(characterReference, session.RequireContinuityId(), cancellationToken, "Character").ConfigureAwait(false);
        return session.CurrentTimeOverride is { } instant
            ? await vault.GetCharacterAgeAtAsync(character.Id, instant, session.CurrentTimeZoneId!, cancellationToken).ConfigureAwait(false)
            : await vault.GetCharacterAgeAsync(character.Id, cancellationToken).ConfigureAwait(false);
    }, "character age read");

    [McpServerTool(Name = "entity_local_time", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Resolves current local time for an entity and states whether the connection override or persisted continuity clock supplied the instant.")]
    public Task<McpLocalCurrentTime?> GetLocalTime(string entityReference, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var entity = await references.ResolveEntityAsync(entityReference, session.RequireContinuityId(), cancellationToken).ConfigureAwait(false);
        var value = session.CurrentTimeOverride is { } instant
            ? await vault.ResolveLocalCurrentTimeAtAsync(entity.Id, instant, session.CurrentTimeZoneId!, cancellationToken).ConfigureAwait(false)
            : await vault.ResolveLocalCurrentTimeAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        if (value is null) return null;
        return new McpLocalCurrentTime(value.Instant, value.TimeZoneId, value.TimeZoneSource,
            value.LocationId is { } location ? await references.ReferenceAsync("Location", location, cancellationToken).ConfigureAwait(false) : null,
            session.CurrentTimeOverride is null ? value.ClockVersion : null,
            session.CurrentTimeOverride is null ? "continuity-clock" : "client-override");
    }, "local time read");

    [McpServerTool(Name = "entity_temporal_state", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Returns records active at this connection's override or the persisted continuity clock, using semantic references.")]
    public Task<McpTemporalState?> GetTemporalState(string entityReference, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var entity = await references.ResolveEntityAsync(entityReference, session.RequireContinuityId(), cancellationToken).ConfigureAwait(false);
        var state = session.CurrentTimeOverride is { } instant
            ? await vault.GetEntityTemporalStateAtAsync(entity.Id, instant, session.CurrentTimeZoneId!, cancellationToken).ConfigureAwait(false)
            : await vault.GetEntityTemporalStateAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        if (state is null) return null;
        var records = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in state.ActiveRecords)
        {
            var type = category.Key.ToLowerInvariant() switch
            {
                "residences" => "CharacterResidence", "memberships" => "OrganizationMembership",
                "relationships" => "CharacterRelationship", "ownership" => "ObjectOwnershipPeriod",
                "custody" => "ObjectCustodyPeriod",
                "locations" when entity.ResourceType.Equals("Organization", StringComparison.OrdinalIgnoreCase) => "OrganizationLocation",
                "locations" => "ObjectLocationPeriod",
                "owners" => "OwnershipPrincipal", _ => "Record"
            };
            records[category.Key] = await mapper.DictionariesAsync(category.Value, type, cancellationToken).ConfigureAwait(false);
        }
        return new McpTemporalState(entityReference, state.StoryReferenceTime, state.ArtificialInstantUtc,
            state.ReferenceTimeZoneId, records,
            session.CurrentTimeOverride is null ? "continuity-clock" : "client-override");
    }, "temporal state read");

    [McpServerTool(Name = "entity_delete_preview", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Previews soft deletion by semantic reference and makes no changes.")]
    public Task<McpDeletePreview?> PreviewDelete(string entityReference, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var entity = await references.ResolveEntityAsync(entityReference, session.RequireContinuityId(), cancellationToken).ConfigureAwait(false);
        var preview = await vault.PreviewDeleteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        return preview is null ? null : new McpDeletePreview(entityReference, preview.Blockers, preview.CanSoftDelete);
    }, "delete preview");

    [McpServerTool(Name = "record_history", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Returns bounded history for a semantic record reference without operation GUIDs or storage keys.")]
    public Task<IReadOnlyList<McpHistoryEntry>> GetHistory(string recordReference, int limit = 100, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var record = recordReference.Contains(':', StringComparison.Ordinal)
            ? await references.ResolveAsync(recordReference, session.ContinuityId, cancellationToken).ConfigureAwait(false)
            : new ResolvedVaultReference(
                "Continuity",
                (await references.ResolveContinuityNameAsync(recordReference, true, cancellationToken).ConfigureAwait(false)).Id,
                recordReference,
                recordReference,
                null,
                false);
        var rows = (await vault.GetHistoryAsync(record.ResourceType, record.Id.ToString(), limit, cancellationToken).ConfigureAwait(false)).ToList();
        if (Enum.TryParse<CanonEntityType>(record.ResourceType, true, out _))
            rows.AddRange(await vault.GetHistoryAsync("CanonEntity", record.Id.ToString(), limit, cancellationToken).ConfigureAwait(false));
        if (record.ResourceType.Equals("Continuity", StringComparison.OrdinalIgnoreCase))
            rows.AddRange(await vault.GetHistoryAsync("ContinuityClock", record.Id.ToString(), limit, cancellationToken).ConfigureAwait(false));
        rows = rows.OrderByDescending(row => row.ChangedAtUtc).ThenByDescending(row => row.Id).Take(Math.Clamp(limit, 1, 200)).ToList();
        return await MapHistoryAsync(rows, cancellationToken).ConfigureAwait(false);
    }, "record history read");

    [McpServerTool(Name = "operation_history", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Returns bounded history for a readable mutation request token. Internal operation GUIDs are never accepted or returned.")]
    public Task<IReadOnlyList<McpHistoryEntry>> GetOperationHistory(string requestToken, int limit = 100, CancellationToken cancellationToken = default) => SafeRead(async () =>
    {
        var rows = await vault.GetOperationHistoryAsync(references.OperationId(requestToken), limit, cancellationToken).ConfigureAwait(false);
        return await MapHistoryAsync(rows, cancellationToken).ConfigureAwait(false);
    }, "operation history read");

    private async Task<IReadOnlyList<McpHistoryEntry>> MapHistoryAsync(IReadOnlyList<HistoryEntry> rows, CancellationToken token)
    {
        var result = new List<McpHistoryEntry>(rows.Count);
        foreach (var row in rows)
        {
            string? reference = null;
            if (int.TryParse(row.RecordKey, out var id)) reference = await references.ReferenceAsync(row.RecordType, id, token).ConfigureAwait(false);
            IReadOnlyDictionary<string, object?>? change = null;
            if (!string.IsNullOrWhiteSpace(row.ChangeJson))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.ChangeJson!);
                if (raw is not null) change = await mapper.DictionaryAsync(raw, null, token).ConfigureAwait(false);
            }
            result.Add(new(row.ChangedAtUtc, row.ClientLabel, row.ToolName, row.Action, row.RecordType,
                reference, row.VersionBefore, row.VersionAfter, change));
        }
        return result;
    }

    private VaultSessionView View() => new(session.ContinuityName, session.CurrentTimeOverride,
        session.CurrentTimeZoneId, session.CurrentTimeOverride is null ? "continuity-clock" : "client-override");

    private static DateTime? FindLastSuccessfulBackup(string backupRoot)
    {
        try
        {
            var directory = Path.Combine(Path.GetFullPath(backupRoot), "backups");
            if (!Directory.Exists(directory)) return null;
            foreach (var path in Directory.GetFiles(directory, "WritingVault.*.manifest.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<VaultBackupManifest>(File.ReadAllText(path));
                    if (manifest is not { SchemaValid: true, IntegrityValid: true } || manifest.Purpose != "Regular") continue;
                    var backup = Path.Combine(directory, manifest.BackupFileName);
                    if (File.Exists(backup) && new FileInfo(backup).Length == manifest.BackupBytes) return manifest.CreatedAtUtc;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static async Task<T> SafeRead<T>(Func<Task<T>> action, string operation)
    {
        VaultDiagnostics.Write("read.started", commandType: operation);
        try
        {
            var result = await action().ConfigureAwait(false);
            VaultDiagnostics.Write("read.completed", commandType: operation);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (McpException)
        {
            VaultDiagnostics.Write("read.failed", "warning", commandType: operation, code: "client.action_required");
            throw;
        }
        catch (VaultReadException)
        {
            VaultDiagnostics.Write("read.failed", "warning", commandType: operation, code: "read.failure");
            throw;
        }
        catch (Exception exception)
        {
            VaultDiagnostics.Write("read.failed", "warning", commandType: operation, code: "read.failure");
            throw AccessErrorClassifier.ToClientReadException(exception, operation);
        }
    }
}
