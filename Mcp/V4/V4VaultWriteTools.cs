using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;

namespace WritingVaultMcp.Mcp.V4;

[McpServerToolType]
public sealed class V4VaultWriteTools(
    SemanticVaultWriteTools legacy, V4ResultMapper mapper, AccessV4ApplicationService application,
    AccessV4TemporalService temporal, AccessV4ImageService images, CoordinatedVaultBackupService backup,
    AccessV4RecordService records, AccessV4RelationshipMergeService merges, VaultSessionContext session,
    AccessV4MemoryService memories)
{
    [McpServerTool(Name="memory_save",Destructive=true,Idempotent=true,UseStructuredContent=true)]
    public Task<V4MutationResult> MemorySave(V4MemorySaveRequest request,CancellationToken token)=>memories.SaveAsync(request,token);
    // Keep these adapters thin: each public method declares one stable MCP operation
    // and translates its v4 request into the existing application command.
    [McpServerTool(Name="continuity_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ContinuityCreate(V4ContinuityCreateRequest request,CancellationToken t)=>Map(legacy.CreateContinuity(new(request.MutationToken,request.Name,request.DefaultTimeZoneId,request.Description),t),t);
    [McpServerTool(Name="vault_backup_create",Idempotent=true,UseStructuredContent=true)] public async Task<V4MutationResult> Backup(V4BackupCreateRequest request,CancellationToken t)
    {
        try{return V4ResultMapper.Backup(await backup.CreateAsync(Operation(request.MutationToken),request.Purpose,t));}
        catch(OperationCanceledException){throw;}
        catch(ArgumentException e){return new(false,"validation.backup",[],false,e.Message,new("validation.backup",e.Message));}
        catch(InvalidOperationException e) when(e.Message.Contains("no longer retained",StringComparison.OrdinalIgnoreCase))
        {return new(false,"backup.not_retained",[],false,"The backup created by this mutation token is no longer retained.",new("backup.not_retained","The prior backup is no longer retained.",Recovery:"Retry with a new mutationToken to create a new backup."));}
        catch(InvalidOperationException e) when(e.Message.Contains("mutation token",StringComparison.OrdinalIgnoreCase))
        {return new(false,"mutation_token.input_mismatch",[],false,"The backup mutation token cannot be used for this request.",new("mutation_token.input_mismatch","Use a new mutationToken for different backup input.",Recovery:"Use a new mutationToken."));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Data.OleDb.OleDbException)
        {VaultDiagnostics.Write("backup.failed","error",code:"backup.failed");return new(false,"backup.failed",[],false,"The backup could not be created and verified.",new("backup.failed","The backup could not be created and verified.",true,Recovery:"Check vault_health and the configured backup storage, then retry with the same mutationToken."));}
    }
    [McpServerTool(Name="continuity_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ContinuityUpdate(V4ContinuityUpdateRequest request,CancellationToken t)=>Map(legacy.PatchContinuity(new(request.MutationToken,request.ExpectedVersion,S(request.Changes,"name"),S(request.Changes,"description"),S(request.Changes,"defaultTimeZoneId")),t),t);
    [McpServerTool(Name="continuity_clock_set",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ClockSet(V4ContinuityClockSetRequest request,CancellationToken t)=>Map(legacy.SetClock(new(request.MutationToken,request.CurrentTime,request.ReferenceTimeZoneId,request.ExpectedVersion),t),t);
    [McpServerTool(Name="variant_group_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> VariantCreate(V4VariantGroupCreateRequest request,CancellationToken t)=>Map(legacy.CreateVariantGroup(new(request.MutationToken,Enum.Parse<CanonEntityType>(request.EntityKind.ToString()),request.Name,request.Notes),t),t);
    [McpServerTool(Name="variant_group_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> VariantUpdate(V4VariantGroupUpdateRequest request,CancellationToken t)=>Map(legacy.PatchVariantGroup(new(request.MutationToken,request.VariantGroupRef,request.ExpectedVersion,S(request.Changes,"name"),S(request.Changes,"notes")),t),t);
    [McpServerTool(Name="entity_variant_group_set",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> VariantSet(V4EntityVariantGroupSetRequest request,CancellationToken t)=>Map(legacy.SetVariantGroup(new(request.MutationToken,request.EntityRef,request.VariantGroupRef,request.ExpectedVersion),t),t);
    [McpServerTool(Name="entity_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EntityCreate(V4EntityCreateRequest request,CancellationToken t)
    {var f=request.Fields??new();return Map(legacy.CreateEntity(new(request.MutationToken,Enum.Parse<CanonEntityType>(request.EntityKind.ToString()),request.Name,f.Description,f.SecondaryType,f.TimeZoneId,Date(f.Birth),Date(f.Death),Date(f.Occurred),f.BirthLocation,f.BirthLocationDetail,request.VariantGroupRef,f.NarrativeOrder,f.MiddleNames,f.FamilyName,f.PreferredName,f.Gender,f.Pronouns,f.Species,f.Occupation,f.Nationality,f.PhysicalDescription,f.PersonalitySummary,f.BirthdayRecurring,f.Race),t),t);}
    [McpServerTool(Name="entity_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EntityUpdate(V4EntityUpdateRequest request,CancellationToken t)=>Map(legacy.PatchEntity(new(
        request.MutationToken,request.Ref,request.ExpectedVersion,S(request.Changes,"name"),S(request.Changes,"description"),S(request.Changes,"secondaryType"),S(request.Changes,"timeZoneId"),
        SD(request.Changes,"birth"),SD(request.Changes,"death"),SD(request.Changes,"occurred"),S(request.Changes,"birthLocation"),S(request.Changes,"birthLocationDetail"),
        DN(request.Changes,"narrativeOrder"),S(request.Changes,"middleNames"),S(request.Changes,"familyName"),S(request.Changes,"preferredName"),S(request.Changes,"gender"),S(request.Changes,"pronouns"),S(request.Changes,"species"),S(request.Changes,"occupation"),S(request.Changes,"nationality"),S(request.Changes,"physicalDescription"),S(request.Changes,"personalitySummary"),B(request.Changes,"birthdayRecurring"),S(request.Changes,"race")),t),t);
    [McpServerTool(Name="entity_duplicate_to_continuity",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Duplicate(V4EntityDuplicateRequest request,CancellationToken t)=>Map(legacy.DuplicateEntity(new(request.MutationToken,request.SourceRef,request.TargetContinuityName,request.Name),t),t);
    [McpServerTool(Name="tag_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> TagCreate(V4TagCreateRequest request,CancellationToken t)=>Map(legacy.CreateTag(new(request.MutationToken,request.Name,request.Description),t),t);
    [McpServerTool(Name="tag_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> TagUpdate(V4TagUpdateRequest request,CancellationToken t)=>Map(legacy.PatchTag(new(request.MutationToken,request.TagRef,request.ExpectedVersion,S(request.Changes,"name"),S(request.Changes,"description")),t),t);
    [McpServerTool(Name="tag_apply",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> TagApply(V4TagApplyRequest request,CancellationToken t)=>Map(application.ApplyTagAsync(new(request.MutationToken,session.RequireContinuityId(),request.Tag,request.Targets,request.Action==V4TagAction.Add,request.CreateIfMissing,session.ClientLabel),t),t);
    [McpServerTool(Name="source_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> SourceCreate(V4SourceCreateRequest request,CancellationToken t)=>Map(legacy.CreateSource(new(request.MutationToken,request.Title,request.CanonicalUrl,request.SourceType,request.Citation,request.Notes,request.ArchiveUrl,request.AuthorPublisher),t),t);
    [McpServerTool(Name="source_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> SourceUpdate(V4SourceUpdateRequest request,CancellationToken t)=>Map(legacy.PatchSource(new(request.MutationToken,request.SourceRef,request.ExpectedVersion,S(request.Changes,"title"),S(request.Changes,"canonicalUrl"),S(request.Changes,"archiveUrl"),S(request.Changes,"sourceType"),S(request.Changes,"authorPublisher"),S(request.Changes,"citation"),S(request.Changes,"notes")),t),t);
    [McpServerTool(Name="source_snapshot_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Snapshot(V4SourceSnapshotAddRequest request,CancellationToken t)=>Map(legacy.AddSourceSnapshot(new(request.MutationToken,request.SourceRef,request.Content,request.MediaType,request.RetrievedAtUtc),t),t);
    [McpServerTool(Name="claim_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ClaimCreate(V4ClaimCreateRequest request,CancellationToken t)=>Map(legacy.CreateClaim(new(request.MutationToken,request.ClaimText,request.Targets,request.Evidence.Select(x=>new McpClaimEvidenceInput(x.Source,x.SnapshotRef,x.Relation,x.Locator,x.EvidenceExcerpt,x.Summary)).ToArray(),request.Status,request.Confidence,request.TargetField,request.Commentary),t),t);
    [McpServerTool(Name="claim_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ClaimUpdate(V4ClaimUpdateRequest request,CancellationToken t)=>Map(records.UpdateClaimAsync(request,t),t);
    [McpServerTool(Name="note_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> NoteAdd(V4NoteAddRequest request,CancellationToken t)=>Map(application.AddNoteAsync(new(request.MutationToken,session.RequireContinuityId(),request.Target,request.Body,request.Title,request.Format,session.ClientLabel),t),t);
    [McpServerTool(Name="note_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> NoteUpdate(V4NoteUpdateRequest request,CancellationToken t)=>Map(records.UpdateNoteAsync(request,t),t);
    [McpServerTool(Name="event_record",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EventRecord(V4EventRecordRequest request,CancellationToken t)=>Map(application.RecordWorldEventAsync(request,session.RequireContinuityId(),session.ClientLabel,t),t);
    [McpServerTool(Name="event_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EventUpdate(V4EventUpdateRequest request,CancellationToken t)=>Map(application.UpdateEventAsync(request,session.RequireContinuityId(),session.ClientLabel,t),t);
    [McpServerTool(Name="entity_event_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EntityEvent(V4EntityEventAddRequest request,CancellationToken t)=>Map(application.RecordEntityEventAsync(request,session.RequireContinuityId(),session.ClientLabel,t),t);
    [McpServerTool(Name="event_project_apply",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EventProjects(V4EventProjectApplyRequest request,CancellationToken t)=>Map(application.ApplyEventProjectsAsync(new(request.MutationToken,session.RequireContinuityId(),request.Event,request.Projects,request.Action==V4TagAction.Add,request.Role,request.Notes,session.ClientLabel),t),t);
    [McpServerTool(Name="entity_alias_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Alias(V4AliasAddRequest request,CancellationToken t)=>Map(legacy.AddAlias(new(request.MutationToken,request.Entity,request.Alias,request.Notes),t),t);
    [McpServerTool(Name="note_source_link",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> NoteSource(V4NoteSourceLinkRequest request,CancellationToken t)=>Map(legacy.LinkNoteSource(new(request.MutationToken,request.Note,request.Source,request.Locator,request.Notes),t),t);
    [McpServerTool(Name="entity_source_link",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EntitySourceLink(V4EntitySourceLinkRequest request,CancellationToken t)=>Map(legacy.LinkSource(new(request.MutationToken,request.Entity,request.Source),t),t);
    [McpServerTool(Name="entity_source_unlink",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EntitySourceUnlink(V4EntitySourceLinkRequest request,CancellationToken t)=>Map(legacy.UnlinkSource(new(request.MutationToken,request.Entity,request.Source),t),t);
    [McpServerTool(Name="project_entity_link",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ProjectLink(V4ProjectEntityLinkRequest request,CancellationToken t)=>Map(legacy.LinkProject(new(request.MutationToken,request.Project,request.Entity,request.Role,request.Notes),t),t);
    [McpServerTool(Name="location_move",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> LocationMove(V4LocationMoveRequest request,CancellationToken t)=>Map(legacy.MoveLocation(new(request.MutationToken,request.Location,request.ParentLocation,request.ExpectedVersion),t),t);
    [McpServerTool(Name="character_residence_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Residence(V4ResidenceAddRequest request,CancellationToken t)=>Map(legacy.AddResidence(new(request.MutationToken,request.Character,request.Location,Date(request.Period)!,request.IsPrimary,request.Notes),t),t);
    [McpServerTool(Name="character_residence_transition",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ResidenceTransition(V4ResidenceTransitionRequest request,CancellationToken t)=>Map(legacy.TransitionResidence(new(request.MutationToken,request.ResidenceRef,request.ExpectedVersion,request.NewLocation,ParseInstant(request.EffectiveAt),request.IsPrimary,request.Notes),t),t);
    [McpServerTool(Name="organization_membership_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Membership(V4MembershipAddRequest request,CancellationToken t)=>Map(application.AddOrganizationMembershipAsync(new(request.MutationToken,session.RequireContinuityId(),request.Organization,request.Character,Date(request.Period),request.Role,request.Notes,Date(request.Joined),Date(request.Left),request.JoinDescription,request.LeaveDescription,session.ClientLabel),t),t);
    [McpServerTool(Name="organization_membership_transition",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> MembershipTransition(V4MembershipTransitionRequest request,CancellationToken t)=>Map(records.TransitionOrganizationMembershipAsync(request,ParseInstant(request.EffectiveAt),t),t);
    [McpServerTool(Name="organization_membership_transitions_set",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> MembershipTransitionsSet(V4OrganizationMembershipTransitionsSetRequest request,CancellationToken t)=>Map(records.SetOrganizationMembershipTransitionsAsync(request,Date(request.Joined)!,Date(request.Left)!,t),t);
    [McpServerTool(Name="organization_location_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> OrganizationLocation(V4OrganizationLocationAddRequest request,CancellationToken t)=>Map(legacy.AddOrganizationLocation(new(request.MutationToken,request.Organization,request.Location,Date(request.Period)!,request.IsPrimary,request.LocationRole,request.Notes),t),t);
    [McpServerTool(Name="relationship_type_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipType(V4RelationshipTypeCreateRequest request,CancellationToken t)=>Map(legacy.CreateRelationshipType(new(request.MutationToken,request.Name,request.IsDirected,request.InverseName,request.AllowsOverlappingPeriods,request.Description),t),t);
    [McpServerTool(Name="character_relationship_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Relationship(V4CharacterRelationshipCreateRequest request,CancellationToken t)=>Map(application.CreateCharacterRelationshipAsync(new(request.MutationToken,session.RequireContinuityId(),request.SourceCharacter,request.TargetCharacter,request.RelationshipType,Date(request.Period)!,request.Notes,session.ClientLabel),t),t);
    [McpServerTool(Name="relationship_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipCreate(V4RelationshipCreateRequest request,CancellationToken t)=>Map(application.CreateRelationshipGroupAsync(new(request.MutationToken,session.RequireContinuityId(),request.Characters,request.RelationshipType,Date(request.InitialPeriod),request.Notes,session.ClientLabel),t),t);
    [McpServerTool(Name="relationship_participant_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipParticipantAdd(V4RelationshipParticipantAddRequest request,CancellationToken t)=>Map(application.AddRelationshipParticipantAsync(new(request.MutationToken,session.RequireContinuityId(),request.RelationshipRef,request.Character,request.ExpectedVersion,Date(request.InitialPeriod),session.ClientLabel),t),t);
    [McpServerTool(Name="relationship_membership_period_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipPeriodAdd(V4RelationshipMembershipPeriodAddRequest request,CancellationToken t)=>Map(application.AddRelationshipMembershipPeriodAsync(new(request.MutationToken,session.RequireContinuityId(),request.RelationshipRef,request.Character,request.ExpectedVersion,Date(request.Period),request.Notes,session.ClientLabel,Date(request.Joined),Date(request.Left),request.JoinDescription,request.LeaveDescription),t),t);
    [McpServerTool(Name="relationship_membership_period_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipPeriodUpdate(V4RelationshipMembershipPeriodUpdateRequest request,CancellationToken t)=>Map(records.UpdateRelationshipMembershipPeriodAsync(request,Date(request.Period)!,t),t);
    [McpServerTool(Name="relationship_membership_transitions_set",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipTransitionsSet(V4RelationshipMembershipTransitionsSetRequest request,CancellationToken t)=>Map(records.SetRelationshipMembershipTransitionsAsync(request,Date(request.Joined)!,Date(request.Left)!,t),t);
    [McpServerTool(Name="relationship_event_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipEventAdd(V4RelationshipEventAddRequest request,CancellationToken t)=>Map(application.RecordRelationshipEventAsync(request,session.RequireContinuityId(),session.ClientLabel,t),t);
    [McpServerTool(Name="relationship_notes_set",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipNotesSet(V4RelationshipNotesSetRequest request,CancellationToken t)=>Map(records.SetRelationshipNotesAsync(request,t),t);
    [McpServerTool(Name="relationship_merge_apply",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> RelationshipMergeApply(V4RelationshipMergeApplyRequest request,CancellationToken t)=>Map(merges.ApplyAsync(request,t),t);
    [McpServerTool(Name="ownership_principal_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Principal(V4OwnershipPrincipalCreateRequest request,CancellationToken t)=>Map(legacy.CreateOwnershipPrincipal(new(request.MutationToken,Enum.Parse<PrincipalKind>(request.Kind,true),request.Entity,request.Label),t),t);
    [McpServerTool(Name="object_ownership_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Ownership(V4ObjectOwnershipAddRequest request,CancellationToken t)=>Map(application.AddOwnershipAsync(new(request.MutationToken,session.RequireContinuityId(),request.Object,Enum.Parse<OwnershipState>(request.State,true),Date(request.Period)!,request.Owners.Select(x=>new V4OwnershipOwnerTarget(x.Principal,x.SharePartsPerMillion,x.Notes)).ToArray(),request.Notes,session.ClientLabel),t),t);
    [McpServerTool(Name="object_ownership_replace_owners",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> OwnershipReplace(V4ObjectOwnershipReplaceOwnersRequest request,CancellationToken t)=>Map(legacy.ReplaceOwnershipOwners(new(request.MutationToken,request.OwnershipRef,request.ExpectedVersion,request.Owners.Select(x=>new McpOwnershipOwnerInput(x.Principal,x.SharePartsPerMillion,x.Notes)).ToArray()),t),t);
    [McpServerTool(Name="object_ownership_transfer",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> OwnershipTransfer(V4ObjectOwnershipTransferRequest request,CancellationToken t)=>Map(legacy.TransferOwnership(new(request.MutationToken,request.OwnershipRef,request.ExpectedVersion,ParseInstant(request.EffectiveAt),Enum.Parse<OwnershipState>(request.NewState,true),request.NewOwners.Select(x=>new McpOwnershipOwnerInput(x.Principal,x.SharePartsPerMillion,x.Notes)).ToArray(),request.Notes),t),t);
    [McpServerTool(Name="object_location_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ObjectLocation(V4ObjectLocationAddRequest request,CancellationToken t)=>Map(legacy.AddObjectLocation(new(request.MutationToken,request.Object,request.Location,Date(request.Period)!,request.Notes),t),t);
    [McpServerTool(Name="object_custody_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Custody(V4ObjectCustodyAddRequest request,CancellationToken t)=>Map(legacy.AddObjectCustody(new(request.MutationToken,request.Object,Date(request.Period)!,Enum.Parse<CustodyState>(request.CustodianState,true),request.Principal,request.Notes),t),t);
    [McpServerTool(Name="world_event_participant_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Participant(V4WorldEventParticipantAddRequest request,CancellationToken t)=>Map(legacy.AddWorldEventParticipant(new(request.MutationToken,request.WorldEvent,request.Participant,request.Role,request.Impact,request.Outcome,request.Notes),t),t);
    [McpServerTool(Name="world_event_location_add",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EventLocation(V4WorldEventLocationAddRequest request,CancellationToken t)=>Map(legacy.AddWorldEventLocation(new(request.MutationToken,request.WorldEvent,request.Location,request.IsPrimary,request.Role,request.Notes),t),t);
    [McpServerTool(Name="character_temporal_profile_set",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Profile(V4TemporalProfileSetRequest request,CancellationToken t)=>Map(temporal.SetProfileAsync(request,t),t);
    [McpServerTool(Name="character_temporal_effect_create",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Effect(V4TemporalEffectCreateRequest request,CancellationToken t)=>Map(temporal.CreateEffectAsync(request,t),t);
    [McpServerTool(Name="character_temporal_effect_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> EffectUpdate(V4TemporalEffectUpdateRequest request,CancellationToken t)=>Map(temporal.UpdateEffectAsync(request,t),t);
    [McpServerTool(Name="record_soft_delete",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Delete(V4VersionedRecordRequest request,CancellationToken t)=>Map(records.LifecycleAsync(request,false,t),t);
    [McpServerTool(Name="record_restore",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> Restore(V4VersionedRecordRequest request,CancellationToken t)=>Map(records.LifecycleAsync(request,true,t),t);
    [McpServerTool(Name="image_attach",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ImageAttach(V4ImageAttachRequest request,CancellationToken t)=>Map(images.AttachAsync(request,t),t);
    [McpServerTool(Name="story_image_attach",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> StoryImageAttach(V4StoryImageAttachRequest request,CancellationToken t)=>Map(images.AttachStoryAsync(request,t),t);
    [McpServerTool(Name="image_replace",Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ImageReplace(V4ImageReplaceRequest request,CancellationToken t)=>Map(images.ReplaceAsync(request,t),t);
    [McpServerTool(Name="image_update",Destructive=true,Idempotent=true,UseStructuredContent=true)] public Task<V4MutationResult> ImageUpdate(V4ImageUpdateRequest request,CancellationToken t)=>Map(images.UpdateAsync(request,t),t);

    private async Task<V4MutationResult> Map(Task<McpMutationResult> task, CancellationToken token)
    {
        var result = await task;
        if (!result.Success)
            return new(false, result.Code, [], result.Replayed, result.Message,
                new(result.Code, result.Message ?? "The operation failed.", result.Retryable));

        IReadOnlyList<V4ReferenceSummary> affected = result.ResourceReference is null
            ? []
            : new V4ReferenceSummary[]
            {
                new(result.ResourceReference, Kind(result.ResourceType),
                    result.ResourceType ?? "record", Version: result.Version)
            };
        return new(true, result.Code, affected, result.Replayed, result.Message);
    }

    private Task<V4MutationResult> Map(Task<VaultMutationResult> task, CancellationToken token) =>
        MapStorage(task, token);

    private async Task<V4MutationResult> MapStorage(
        Task<VaultMutationResult> task, CancellationToken token) =>
        await mapper.MutationAsync(await task, token);

    private static string Operation(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("mutationToken is required.");
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("writing-vault-operation-v2\0" + token.Trim()));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 15) | 80);
        bytes[8] = (byte)((bytes[8] & 63) | 128);
        return new Guid(bytes).ToString("D");
    }

    private static PatchField<string>? S(
        IReadOnlyDictionary<string, JsonElement> changes, string key) =>
        !changes.TryGetValue(key, out var value) ? null :
        new(true, value.ValueKind == JsonValueKind.Null ? null : value.GetString());

    private static PatchField<StoryDate>? SD(
        IReadOnlyDictionary<string, JsonElement> changes, string key) =>
        !changes.TryGetValue(key, out var value) ? null :
        new(true, value.ValueKind == JsonValueKind.Null ? null :
            Date(value.Deserialize<V4StoryDateInput>()));

    private static PatchField<bool>? B(IReadOnlyDictionary<string, JsonElement> changes, string key) =>
        !changes.TryGetValue(key, out var value) ? null : new(true, value.GetBoolean());

    private static PatchField<double?>? DN(
        IReadOnlyDictionary<string, JsonElement> changes, string key) =>
        !changes.TryGetValue(key, out var value) ? null :
        new(true, value.ValueKind == JsonValueKind.Null ? null : value.GetDouble());

    private static StoryDate? Date(V4StoryDateInput? input) =>
        input is null ? null : V4StoryDateParser.Parse(new(
            Kind: Enum.Parse<StoryDateKind>(input.Kind?.ToString() ?? "Unknown"),
            Value: input.Value, Lower: input.Lower, Upper: input.Upper,
            LowerInclusive: input.LowerInclusive, UpperInclusive: input.UpperInclusive,
            OriginalText: input.OriginalText, CalendarId: input.CalendarId ?? "Gregorian"));

    private static DateTime ParseInstant(string value) => DateTime.SpecifyKind(
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None), DateTimeKind.Unspecified);

    private static V4RecordKind Kind(string? resourceType) =>
        Enum.TryParse<V4RecordKind>(resourceType, true, out var kind)
            ? kind : V4RecordKind.Relationship;
}
