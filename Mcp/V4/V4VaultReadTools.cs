using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Mcp.V4;

[McpServerToolType]
public sealed class V4VaultReadTools(
    AccessV4ReadService reads,
    AccessV4SourceSnapshotService snapshots,
    AccessV4TemporalService temporal,
    AccessV4ImageService images,
    AccessV4RelationshipMergeService merges,
    VaultReferenceService references,
    VaultSessionContext session,
    AccessSchemaVerifier schema,
    AccessIntegrityVerifier integrity,
    VaultWriteCoordinator coordinator,
    CoordinatedVaultBackupService backup)
{
    [McpServerTool(Name="vault_health",UseStructuredContent=true),Description("Reports v4 readiness and storage capacity without exposing paths.")]
    public Task<V4HealthResult> Health(V4EmptyRequest request,CancellationToken token=default)=>Safe(async()=>
    {
        var s=await schema.VerifyAsync(token); var i=await integrity.VerifyAsync(token); var cap=await images.CapacityAsync(token);
        var issues=s.Issues.Select(x=>new V4Error(x.Code,x.Detail)).Concat(i.Issues.Select(x=>new V4Error(x.Code,x.Detail))).ToArray();
        var lastBackup=await backup.FindLatestVerifiedAsync(token);
        return new V4HealthResult(s.IsValid&&i.IsValid,V4ContractCatalog.SurfaceVersion,AccessSchemaDefinition.MigrationId,coordinator.PendingWrites,cap.CapacityBand,cap.RenditionBytes,lastBackup?.ToString("O",System.Globalization.CultureInfo.InvariantCulture),issues,
            session.ContinuityId is null ? "unscoped" : await reads.RevisionAsync(token));
    });
    [McpServerTool(Name="continuity_list",UseStructuredContent=true),Description("Lists continuity names and clock summaries with bounded paging.")]
    public Task<V4Page<V4ContinuitySummary>> Continuities(V4ContinuityListRequest request,CancellationToken token=default)=>Safe(()=>reads.ContinuitiesAsync(request,token));
    [McpServerTool(Name="session_set",UseStructuredContent=true),Description("Selects a continuity by exact name and optionally sets an exact currentTime or date-only currentDate, or clears the session clock.")]
    public Task<V4SessionView> SessionSet(V4SessionSetRequest request,CancellationToken token=default)=>Safe(async()=>
    {
        if(request.ContinuityName is not null){var c=await references.ResolveContinuityNameAsync(request.ContinuityName,false,token);session.SelectContinuity(c.Id,c.Name);}
        _=session.RequireContinuityId();
        if(request.TimeAction==V4SessionTimeAction.Clear)session.ClearTime();
        else if(request.TimeAction==V4SessionTimeAction.Set)
        {
            if(string.IsNullOrWhiteSpace(request.ReferenceTimeZoneId) ||
                (request.CurrentTime is null)==(request.CurrentDate is null))
                throw new ArgumentException("Set exactly one of currentTime or currentDate, plus referenceTimeZoneId.");
            if(request.CurrentDate is { } date)session.SetDate(date,request.ReferenceTimeZoneId);
            else session.SetTime(request.CurrentTime!.Value,request.ReferenceTimeZoneId);
        }
        return await SessionView(token);
    });
    [McpServerTool(Name="session_get",UseStructuredContent=true),Description("Returns this connection's selected continuity and effective clock provenance.")]
    public Task<V4SessionView> SessionGet(V4EmptyRequest request,CancellationToken token=default)=>Safe(()=>SessionView(token));
    [McpServerTool(Name="search",UseStructuredContent=true),Description("Searches across selected record kinds, aliases, tags, projects, and optional content.")]
    public Task<V4Page<V4ReferenceSummary>> Search(V4SearchRequest request,CancellationToken token=default)=>Safe(()=>reads.SearchAsync(request,token));
    [McpServerTool(Name="get",UseStructuredContent=true),Description("Returns a bounded page-shaped record or continuity overview.")]
    public Task<V4RecordOverview> Get(V4GetRequest request,CancellationToken token=default)=>Safe(async()=>
    {
        var result=await reads.GetAsync(request,token);var fields=new Dictionary<string,JsonElement>(result.Fields,StringComparer.OrdinalIgnoreCase);
        if(result.Summary.Kind is V4RecordKind.Project or V4RecordKind.Location or V4RecordKind.Character or V4RecordKind.Organization or V4RecordKind.Object or V4RecordKind.WorldEvent)
            fields["currentTemporalState"]=await reads.CurrentTemporalStateAsync(result.Summary.Ref,token);
        if(result.Summary.Kind==V4RecordKind.Character)
        {
            fields["age"]=JsonSerializer.SerializeToElement(await temporal.AgeAsync(new(result.Summary.Ref),token),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            fields["temporalProfile"]=await temporal.ProfileAsync(result.Summary.Ref,token);
        }
        return result with{Fields=fields};
    });
    [McpServerTool(Name="record_locate",UseStructuredContent=true),Description("Locates one explicit semantic record reference across active continuities for safe internal links; does not change this session's continuity.")]
    public Task<V4ReferenceSummary> RecordLocate(V4RecordRefRequest request,CancellationToken token=default)=>Safe(()=>reads.LocateAsync(request,token));
    [McpServerTool(Name="record_snapshot_get",UseStructuredContent=true),Description("Reads one immutable historical record page version, including its saved notes and associations, in the selected continuity.")]
    public Task<V4RecordSnapshotResult> RecordSnapshotGet(V4RecordSnapshotGetRequest request,CancellationToken token=default)=>
        Safe(()=>reads.SnapshotAsync(request,token));
    [McpServerTool(Name="record_snapshot_list",UseStructuredContent=true),Description("Lists available immutable page-snapshot versions newest first for a record in the selected continuity. Use a returned version in record_snapshot_get or a pinned vault-record Markdown link.")]
    public Task<V4RecordSnapshotListResult> RecordSnapshotList(V4RecordSnapshotListRequest request,CancellationToken token=default)=>
        Safe(()=>reads.SnapshotListAsync(request,token));
    [McpServerTool(Name="source_snapshot_view",UseStructuredContent=true),Description("Pages verified cached source text without exposing a local file path.")]
    public Task<V4SourceSnapshotViewResult> SnapshotView(V4SourceSnapshotViewRequest request,CancellationToken token=default)=>Safe(()=>snapshots.ViewAsync(request,token));
    [McpServerTool(Name="list_related",UseStructuredContent=true),Description("Pages one named relation from a continuity or record.")]
    public Task<V4Page<V4ReferenceSummary>> Related(V4ListRelatedRequest request,CancellationToken token=default)=>Safe(()=>reads.RelatedAsync(request,token));
    [McpServerTool(Name="timeline_get",UseStructuredContent=true),Description("Returns bounded calendar or narrative timeline items; historical data does not require a clock.")]
    public Task<V4TimelineResult> Timeline(V4TimelineRequest request,CancellationToken token=default)=>Safe(()=>reads.TimelineAsync(request,token));
    [McpServerTool(Name="tag_targets",UseStructuredContent=true),Description("Pages selected-continuity and vault-global targets carrying a tag.")]
    public Task<V4Page<V4ReferenceSummary>> TagTargets(V4TagTargetsRequest request,CancellationToken token=default)=>Safe(()=>reads.TagTargetsAsync(request,token));
    [McpServerTool(Name="history_get",UseStructuredContent=true),Description("Reads real-UTC history by record reference or mutation token.")]
    public Task<V4Page<V4HistoryEntry>> History(V4HistoryRequest request,CancellationToken token=default)=>Safe(()=>reads.HistoryAsync(request,token));
    [McpServerTool(Name="changes_since",UseStructuredContent=true),Description("Waits for or lists committed changes after an opaque revision cursor.")]
    public Task<V4ChangesResult> Changes(V4ChangesSinceRequest request,CancellationToken token=default)=>Safe(()=>reads.ChangesSinceAsync(request,token));
    [McpServerTool(Name="character_age",UseStructuredContent=true),Description("Returns calendar, legal, biological, and experienced age.")]
    public Task<V4CharacterAgeResult> Age(V4CharacterAgeRequest request,CancellationToken token=default)=>Safe(()=>temporal.AgeAsync(request,token));
    [McpServerTool(Name="temporal_effect_preview",UseStructuredContent=true),Description("Validates a hypothetical temporal effect without writing.")]
    public Task<V4TemporalPreviewResult> TemporalPreview(V4TemporalEffectPreviewRequest request,CancellationToken token=default)=>Safe(()=>temporal.PreviewAsync(request,token));
    [McpServerTool(Name="entity_local_time",UseStructuredContent=true),Description("Resolves local story time and timezone provenance for one record.")]
    public Task<V4LocalTimeResult> LocalTime(V4EntityLocalTimeRequest request,CancellationToken token=default)=>Safe(()=>reads.LocalTimeAsync(request,token));
    [McpServerTool(Name="record_delete_preview",UseStructuredContent=true),Description("Reports blockers before soft deletion.")]
    public Task<V4DeletePreview> DeletePreview(V4RecordRefRequest request,CancellationToken token=default)=>Safe(()=>reads.DeletePreviewAsync(request,token));
    [McpServerTool(Name="relationship_merge_preview",UseStructuredContent=true),Description("Reviews an explicit legacy relationship merge, including periods, events, notes, claims and history counts, without writing.")]
    public Task<V4RelationshipMergePreview> RelationshipMergePreview(V4RelationshipMergePreviewRequest request,CancellationToken token=default)=>Safe(()=>merges.PreviewAsync(request,token));
    [McpServerTool(Name="image_list",UseStructuredContent=true),Description("Pages image metadata without returning bytes.")]
    public Task<V4Page<V4ImageMetadata>> ImageList(V4ImageListRequest request,CancellationToken token=default)=>Safe(()=>images.ListAsync(request,token));
    [McpServerTool(Name="image_search",UseStructuredContent=true),Description("Searches image metadata without bytes; acrossContinuities searches active continuities without changing the session.")]
    public Task<V4Page<V4ImageMetadata>> ImageSearch(V4ImageSearchRequest request,CancellationToken token=default)=>Safe(()=>images.SearchAsync(request,token));
    [McpServerTool(Name="image_view",UseStructuredContent=true),Description("Reads one explicit image reference across continuities as metadata and a bounded thumbnail, display rendition, or original.")]
    public Task<CallToolResult> ImageView(V4ImageViewRequest request,CancellationToken token=default)=>Safe(async()=>
    {
        var result=await images.ViewAsync(request,token);
        return new CallToolResult{Content=[ImageContentBlock.FromBytes(result.Content,result.View.MediaType)],StructuredContent=JsonSerializer.SerializeToElement(result.View,new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}})};
    });
    [McpServerTool(Name="image_revision_history",UseStructuredContent=true),Description("Lists retained image content revisions without returning image bytes.")]
    public Task<V4ImageRevisionHistoryResult> ImageRevisionHistory(V4ImageRevisionHistoryRequest request,CancellationToken token=default)
        =>Safe(()=>images.RevisionHistoryAsync(request,token));

    private async Task<V4SessionView> SessionView(CancellationToken token)
    {
        if(session.ContinuityId is not { } id)return new(null,new("Unset",null,null,"none"));
        return new(session.ContinuityName,await reads.EffectiveClockAsync(id,token));
    }
    private static async Task<T> Safe<T>(Func<Task<T>> action)
    {
        try{return await action();}
        catch(V4ResolutionException e)
        {
            var candidates=e.Candidates.Count==0?string.Empty:" Candidates: "+string.Join("; ",e.Candidates.Select(candidate=>$"{candidate.Reference} ({candidate.Label})"));
            var recovery=e.Code switch
            {
                "record.ambiguous"=>" Recovery: retry with exactly one candidate ref.",
                "record.not_found"=>" Recovery: use search in the selected continuity, then retry with the returned ref.",
                "scope.mismatch"=>" Recovery: select the record's continuity or choose a result from the current continuity.",
                "record.deleted"=>" Recovery: set includeDeleted when supported or restore the record before reading it.",
                _=>string.Empty
            };
            throw new McpException($"{e.Code}: {e.Message}{candidates}{recovery}");
        }
        catch(V4CursorException e){throw new McpException($"{e.Code}: {e.Message}");}
        catch(VaultValidationException e){throw new McpException($"{e.Errors[0].Code}: {e.Message}");}
        catch(ArgumentException e){throw new McpException($"INVALID_ARGUMENT: {e.Message}");}
    }
}
