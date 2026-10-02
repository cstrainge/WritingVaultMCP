using System.ComponentModel;
using ModelContextProtocol.Server;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Mcp;

[McpServerToolType]
public sealed class SemanticVaultWriteTools(
    AccessVaultService vault,
    VaultSessionContext session,
    VaultReferenceService references,
    VaultMcpResultMapper mapper)
{
    [McpServerTool(Name = "continuity_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates a continuity. requestToken is a readable idempotency key; operation GUIDs are internal.")]
    public Task<McpMutationResult> CreateContinuity(McpCreateContinuityRequest request, CancellationToken token) =>
        Mutate(() => vault.CreateContinuityAsync(new(Op(request.RequestToken), request.Name, request.DefaultTimeZoneId, request.Description, Label), token), token);

    [McpServerTool(Name = "continuity_patch", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Patches the selected continuity using expectedVersion.")]
    public Task<McpMutationResult> PatchContinuity(McpPatchContinuityRequest request, CancellationToken token) =>
        Mutate(() => vault.PatchContinuityAsync(new(Op(request.RequestToken), Cid, request.ExpectedVersion, request.Name, request.Description, request.DefaultTimeZoneId, Label), token), token);

    [McpServerTool(Name = "variant_group_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates an alternate-version group in the selected continuity.")]
    public Task<McpMutationResult> CreateVariantGroup(McpCreateVariantGroupRequest request, CancellationToken token) =>
        Mutate(() => vault.CreateVariantGroupAsync(new(Op(request.RequestToken), Cid, request.EntityType, request.Name, request.Notes, Label), token), token);

    [McpServerTool(Name = "variant_group_patch", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Patches an alternate-version group by semantic reference.")]
    public Task<McpMutationResult> PatchVariantGroup(McpPatchVariantGroupRequest request, CancellationToken token) => Mutate(async () =>
    {
        var group = await Resolve(request.VariantGroupReference, token, "VariantGroup");
        return await vault.PatchVariantGroupAsync(new(Op(request.RequestToken), group.Id, request.ExpectedVersion, request.Name, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "entity_variant_group_set", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Assigns or clears an entity's alternate-version group by semantic reference.")]
    public Task<McpMutationResult> SetVariantGroup(McpSetVariantGroupRequest request, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        var group = request.VariantGroupReference is null ? null : await Resolve(request.VariantGroupReference, token, "VariantGroup");
        return await vault.SetVariantGroupAsync(new(Op(request.RequestToken), entity.Id, group?.Id, request.ExpectedVersion, Label), token);
    }, token);

    [McpServerTool(Name = "entity_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates an entity in the selected continuity. Related records use semantic references.")]
    public Task<McpMutationResult> CreateEntity(McpCreateEntityRequest request, CancellationToken token) => Mutate(async () =>
    {
        var species = request.Species is null ? null : await Resolve(request.Species, token, "Species");
        var birthLocation = request.BirthLocationReference is null ? null : await Resolve(request.BirthLocationReference, token, "Location");
        var group = request.VariantGroupReference is null ? null : await Resolve(request.VariantGroupReference, token, "VariantGroup");
        return await vault.CreateEntityAsync(new(Op(request.RequestToken), Cid, request.EntityType, request.Name,
            request.Description, request.SecondaryType, request.TimeZoneId, request.Birth, request.Death, request.Occurred,
            birthLocation?.Id, request.BirthLocationDetail, group?.Id, Label, request.NarrativeOrder,
            request.MiddleNames, request.FamilyName, request.PreferredName, request.Gender, request.Pronouns,
            species?.Id, request.Occupation, request.Nationality, request.PhysicalDescription, request.PersonalitySummary, request.BirthdayRecurring, request.Race), token);
    }, token);

    [McpServerTool(Name = "entity_patch", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Patches an entity by semantic reference with explicit field state and expectedVersion.")]
    public Task<McpMutationResult> PatchEntity(McpPatchEntityRequest request, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        PatchField<int?>? species = null;
        if (request.Species is { Specified: true } speciesField)
            species = new(true, speciesField.Value is null ? null : (await Resolve(speciesField.Value, token, "Species")).Id);
        PatchField<int?>? birthLocation = null;
        if (request.BirthLocationReference is { Specified: true } field)
            birthLocation = new(true, field.Value is null ? null : (await Resolve(field.Value, token, "Location")).Id);
        return await vault.PatchEntityAsync(new(Op(request.RequestToken), entity.Id, request.ExpectedVersion,
            request.Name, request.Description, request.SecondaryType, request.TimeZoneId, Label,
            request.Birth, request.Death, request.Occurred, birthLocation, request.BirthLocationDetail,
            request.NarrativeOrder, request.MiddleNames, request.FamilyName, request.PreferredName, request.Gender,
            request.Pronouns, species, request.Occupation, request.Nationality,
            request.PhysicalDescription, request.PersonalitySummary, request.BirthdayRecurring, request.Race), token);
    }, token);

    [McpServerTool(Name = "entity_duplicate_to_continuity", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Copies core fields to a named target continuity as an independent entity.")]
    public Task<McpMutationResult> DuplicateEntity(McpDuplicateEntityRequest request, CancellationToken token) => Mutate(async () =>
    {
        var source = await Entity(request.SourceEntityReference, token);
        var target = await references.ResolveContinuityNameAsync(request.TargetContinuityName, false, token);
        return await vault.DuplicateEntityAsync(new(Op(request.RequestToken), source.Id, target.Id, request.Name, Label), token);
    }, token);

    [McpServerTool(Name = "continuity_clock_set", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Sets or clears the selected continuity's shared clock. Use session_time_set for a connection-only override.")]
    public Task<McpMutationResult> SetClock(McpSetClockRequest request, CancellationToken token) =>
        Mutate(() => vault.SetClockAsync(new(Op(request.RequestToken), Cid, request.CurrentInstant, request.ReferenceTimeZoneId, request.ExpectedVersion, Label), token), token);

    [McpServerTool(Name = "tag_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates a vault-global tag.")]
    public Task<McpMutationResult> CreateTag(McpCreateTagRequest request, CancellationToken token) =>
        Mutate(() => vault.CreateTagAsync(new(Op(request.RequestToken), request.Name, request.Description, Label), token), token);

    [McpServerTool(Name = "tag_patch", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Patches a tag by semantic reference.")]
    public Task<McpMutationResult> PatchTag(McpPatchTagRequest request, CancellationToken token) => Mutate(async () =>
    {
        var tag = await Resolve(request.TagReference, token, "Tag");
        return await vault.PatchTagAsync(new(Op(request.RequestToken), tag.Id, request.ExpectedVersion, request.Name, request.Description, Label), token);
    }, token);

    [McpServerTool(Name = "source_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates a vault-global source with revisitable URLs and local citation data.")]
    public Task<McpMutationResult> CreateSource(McpCreateSourceRequest request, CancellationToken token) =>
        Mutate(() => vault.CreateSourceAsync(new(Op(request.RequestToken), request.Title, request.CanonicalUrl, request.SourceType,
            request.Citation, request.Notes, Label, request.ArchiveUrl, request.AuthorPublisher), token), token);

    [McpServerTool(Name = "source_patch", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Patches a source by semantic reference.")]
    public Task<McpMutationResult> PatchSource(McpPatchSourceRequest request, CancellationToken token) => Mutate(async () =>
    {
        var source = await Resolve(request.SourceReference, token, "Source");
        return await vault.PatchSourceAsync(new(Op(request.RequestToken), source.Id, request.ExpectedVersion, request.Title,
            request.CanonicalUrl, request.ArchiveUrl, request.SourceType, request.AuthorPublisher,
            request.Citation, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "source_snapshot_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Caches source content using a source semantic reference.")]
    public Task<McpMutationResult> AddSourceSnapshot(McpAddSourceSnapshotRequest request, CancellationToken token) => Mutate(async () =>
    {
        var source = await Resolve(request.SourceReference, token, "Source");
        return await vault.AddSourceSnapshotAsync(new(Op(request.RequestToken), source.Id, request.Content, request.MediaType, request.RetrievedAtUtc, Label), token);
    }, token);

    [McpServerTool(Name = "claim_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates a local claim in the selected continuity with semantic entity/source references.")]
    public Task<McpMutationResult> CreateClaim(McpCreateClaimRequest request, CancellationToken token) => Mutate(async () =>
    {
        var entities = new List<int>();
        foreach (var reference in request.EntityReferences) entities.Add((await Entity(reference, token)).Id);
        var evidence = new List<ClaimEvidenceInput>();
        foreach (var item in request.Evidence)
        {
            var source = await Resolve(item.SourceReference, token, "Source");
            var snapshot = item.SourceSnapshotReference is null ? null : await Resolve(item.SourceSnapshotReference, token, "SourceSnapshot");
            evidence.Add(new(source.Id, snapshot?.Id, item.EvidenceRelation, item.Locator, item.EvidenceExcerpt, item.Summary));
        }
        return await vault.CreateClaimAsync(new(Op(request.RequestToken), Cid, request.ClaimText, entities, evidence,
            request.ClaimStatus, request.Confidence, request.TargetField, request.Commentary, Label), token);
    }, token);

    [McpServerTool(Name = "entity_note_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds a note to an entity by semantic reference.")]
    public Task<McpMutationResult> AddNote(McpAddNoteRequest request, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        return await vault.AddNoteAsync(new(Op(request.RequestToken), entity.Id, request.Body, request.Title, Label), token);
    }, token);

    [McpServerTool(Name = "entity_event_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds an entity event with an optional world-event semantic reference.")]
    public Task<McpMutationResult> AddEntityEvent(McpAddEntityEventRequest request, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        var worldEvent = request.WorldEventReference is null ? null : await Resolve(request.WorldEventReference, token, "WorldEvent");
        return await vault.AddEntityEventAsync(new(Op(request.RequestToken), entity.Id, request.Title, request.Occurred,
            worldEvent?.Id, request.Description, Label, request.NarrativeOrder), token);
    }, token);

    [McpServerTool(Name = "entity_alias_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds an alias to a character or organization by semantic reference.")]
    public Task<McpMutationResult> AddAlias(McpAddAliasRequest request, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        return await vault.AddAliasAsync(new(Op(request.RequestToken), entity.Id, request.Alias, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "note_source_link", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Links a note and source by semantic reference.")]
    public Task<McpMutationResult> LinkNoteSource(McpLinkNoteSourceRequest request, CancellationToken token) => Mutate(async () =>
    {
        var note = await Resolve(request.NoteReference, token, "EntityNote");
        var source = await Resolve(request.SourceReference, token, "Source");
        return await vault.LinkNoteSourceAsync(new(Op(request.RequestToken), note.Id, source.Id, request.Locator, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "entity_tag_link", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Links an entity and tag by semantic reference.")]
    public Task<McpMutationResult> LinkTag(McpEntityTagLinkRequest request, CancellationToken token) =>
        Link(new(request.RequestToken, request.EntityReference, request.TagReference), "tag", "Tag", token);
    [McpServerTool(Name = "entity_tag_unlink", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Unlinks an entity and tag by semantic reference.")]
    public Task<McpMutationResult> UnlinkTag(McpEntityTagUnlinkRequest request, CancellationToken token) =>
        Unlink(new(request.RequestToken, request.EntityReference, request.TagReference), "tag", "Tag", token);
    [McpServerTool(Name = "entity_source_link", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Links an entity and source by semantic reference.")]
    public Task<McpMutationResult> LinkSource(McpEntitySourceLinkRequest request, CancellationToken token) =>
        Link(new(request.RequestToken, request.EntityReference, request.SourceReference), "source", "Source", token);
    [McpServerTool(Name = "entity_source_unlink", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Unlinks an entity and source by semantic reference.")]
    public Task<McpMutationResult> UnlinkSource(McpEntitySourceUnlinkRequest request, CancellationToken token) =>
        Unlink(new(request.RequestToken, request.EntityReference, request.SourceReference), "source", "Source", token);
    [McpServerTool(Name = "project_entity_link", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Assigns an entity to a project by semantic reference.")]
    public Task<McpMutationResult> LinkProject(McpProjectEntityLinkRequest request, CancellationToken token) => Mutate(async () =>
    {
        var project = await Resolve(request.ProjectReference, token, "Project");
        var entity = await Entity(request.EntityReference, token);
        return await vault.LinkEntityAsync("project", new(Op(request.RequestToken), entity.Id, project.Id, request.Role, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "source_tag_link", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Links a source and tag by semantic reference.")]
    public Task<McpMutationResult> LinkSourceTag(McpSourceTagLinkRequest request, CancellationToken token) => Mutate(async () =>
    {
        var source = await Resolve(request.SourceReference, token, "Source");
        var tag = await Resolve(request.TagReference, token, "Tag");
        return await vault.LinkSourceTagAsync(new(Op(request.RequestToken), source.Id, tag.Id, ClientLabel: Label), token);
    }, token);

    [McpServerTool(Name = "source_tag_unlink", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Unlinks a source and tag by semantic reference.")]
    public Task<McpMutationResult> UnlinkSourceTag(McpSourceTagUnlinkRequest request, CancellationToken token) => Mutate(async () =>
    {
        var source = await Resolve(request.SourceReference, token, "Source");
        var tag = await Resolve(request.TagReference, token, "Tag");
        return await vault.UnlinkEntityAsync("source-tag", new(Op(request.RequestToken), source.Id, tag.Id, Label), token);
    }, token);

    [McpServerTool(Name = "location_move", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Moves a location under an optional parent using semantic references.")]
    public Task<McpMutationResult> MoveLocation(McpMoveLocationRequest request, CancellationToken token) => Mutate(async () =>
    {
        var location = await Resolve(request.LocationReference, token, "Location");
        var parent = request.ParentLocationReference is null ? null : await Resolve(request.ParentLocationReference, token, "Location");
        return await vault.MoveLocationAsync(new(Op(request.RequestToken), location.Id, parent?.Id, request.ExpectedVersion, Label), token);
    }, token);

    [McpServerTool(Name = "character_residence_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds a residence using character and location semantic references.")]
    public Task<McpMutationResult> AddResidence(McpAddResidenceRequest request, CancellationToken token) => Mutate(async () =>
    {
        var character = await Resolve(request.CharacterReference, token, "Character");
        var location = await Resolve(request.LocationReference, token, "Location");
        return await vault.AddResidenceAsync(new(Op(request.RequestToken), character.Id, location.Id, request.Period, request.IsPrimary, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "character_residence_transition", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Atomically closes a residence and opens its replacement using semantic references.")]
    public Task<McpMutationResult> TransitionResidence(McpTransitionResidenceRequest request, CancellationToken token) => Mutate(async () =>
    {
        var residence = await Resolve(request.ResidenceReference, token, "CharacterResidence");
        var location = await Resolve(request.NewLocationReference, token, "Location");
        return await vault.TransitionResidenceAsync(new(Op(request.RequestToken), residence.Id, request.ExpectedVersion, location.Id, request.EffectiveAt, request.IsPrimary, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "organization_membership_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds a membership using organization and character semantic references.")]
    public Task<McpMutationResult> AddMembership(McpAddMembershipRequest request, CancellationToken token) => Mutate(async () =>
    {
        var organization = await Resolve(request.OrganizationReference, token, "Organization");
        var character = await Resolve(request.CharacterReference, token, "Character");
        return await vault.AddMembershipAsync(new(Op(request.RequestToken), organization.Id, character.Id, request.Period, request.Role, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "organization_membership_transition", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Ends or changes a membership using its semantic reference.")]
    public Task<McpMutationResult> TransitionMembership(McpTransitionMembershipRequest request, CancellationToken token) => Mutate(async () =>
    {
        var membership = await Resolve(request.MembershipReference, token, "OrganizationMembership");
        return await vault.TransitionMembershipAsync(new(Op(request.RequestToken), membership.Id, request.ExpectedVersion, request.EffectiveAt, request.CreateReplacement, request.NewRole, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "organization_location_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds an organization location using semantic references.")]
    public Task<McpMutationResult> AddOrganizationLocation(McpAddOrganizationLocationRequest request, CancellationToken token) => Mutate(async () =>
    {
        var organization = await Resolve(request.OrganizationReference, token, "Organization");
        var location = await Resolve(request.LocationReference, token, "Location");
        return await vault.AddOrganizationLocationAsync(new(Op(request.RequestToken), organization.Id, location.Id, request.Period, request.IsPrimary, request.LocationRole, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "relationship_type_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates a directed or undirected relationship type.")]
    public Task<McpMutationResult> CreateRelationshipType(McpCreateRelationshipTypeRequest request, CancellationToken token) =>
        Mutate(() => vault.CreateRelationshipTypeAsync(new(Op(request.RequestToken), request.Name, request.IsDirected, request.InverseName, request.AllowsOverlappingPeriods, request.Description, Label), token), token);

    [McpServerTool(Name = "character_relationship_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates one canonical relationship discoverable from both character references.")]
    public Task<McpMutationResult> CreateRelationship(McpCreateRelationshipRequest request, CancellationToken token) => Mutate(async () =>
    {
        var source = await Resolve(request.SourceCharacterReference, token, "Character");
        var target = await Resolve(request.TargetCharacterReference, token, "Character");
        var type = await Resolve(request.RelationshipTypeReference, token, "RelationshipType");
        return await vault.CreateRelationshipAsync(new(Op(request.RequestToken), Cid, source.Id, target.Id, type.Id, request.Period, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "ownership_principal_create", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Creates a character, organization, or external ownership principal in the selected continuity.")]
    public Task<McpMutationResult> CreateOwnershipPrincipal(McpCreateOwnershipPrincipalRequest request, CancellationToken token) => Mutate(async () =>
    {
        ResolvedVaultReference? entity = request.EntityReference is null ? null : await Entity(request.EntityReference, token);
        return await vault.CreateOwnershipPrincipalAsync(new(Op(request.RequestToken), Cid, request.Kind,
            request.Kind == PrincipalKind.Character ? entity?.Id : null,
            request.Kind == PrincipalKind.Organization ? entity?.Id : null,
            request.Label, Label), token);
    }, token);

    [McpServerTool(Name = "object_ownership_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds a non-overlapping ownership period using semantic references.")]
    public Task<McpMutationResult> AddOwnership(McpAddOwnershipRequest request, CancellationToken token) => Mutate(async () =>
    {
        var item = await Resolve(request.ObjectReference, token, "Object");
        return await vault.AddOwnershipPeriodAsync(new(Op(request.RequestToken), item.Id, request.State, request.Period, await Owners(request.Owners, token), request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "object_ownership_replace_owners", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Replaces all owners of an ownership period by semantic reference.")]
    public Task<McpMutationResult> ReplaceOwnershipOwners(McpReplaceOwnershipOwnersRequest request, CancellationToken token) => Mutate(async () =>
    {
        var period = await Resolve(request.OwnershipPeriodReference, token, "ObjectOwnershipPeriod");
        return await vault.ReplaceOwnershipOwnersAsync(new(Op(request.RequestToken), period.Id, request.ExpectedVersion, await Owners(request.Owners, token), Label), token);
    }, token);

    [McpServerTool(Name = "object_ownership_transfer", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Transfers ownership atomically using semantic references.")]
    public Task<McpMutationResult> TransferOwnership(McpTransferOwnershipRequest request, CancellationToken token) => Mutate(async () =>
    {
        var period = await Resolve(request.OwnershipPeriodReference, token, "ObjectOwnershipPeriod");
        return await vault.TransferOwnershipAsync(new(Op(request.RequestToken), period.Id, request.ExpectedVersion, request.EffectiveAt, request.NewState, await Owners(request.NewOwners, token), request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "object_location_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds an object location interval using semantic references.")]
    public Task<McpMutationResult> AddObjectLocation(McpAddObjectLocationRequest request, CancellationToken token) => Mutate(async () =>
    {
        var item = await Resolve(request.ObjectReference, token, "Object");
        var location = await Resolve(request.LocationReference, token, "Location");
        return await vault.AddObjectLocationAsync(new(Op(request.RequestToken), item.Id, location.Id, request.Period, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "object_custody_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds an object custody interval using semantic references.")]
    public Task<McpMutationResult> AddObjectCustody(McpAddObjectCustodyRequest request, CancellationToken token) => Mutate(async () =>
    {
        var item = await Resolve(request.ObjectReference, token, "Object");
        var principal = request.PrincipalReference is null ? null : await Resolve(request.PrincipalReference, token, "OwnershipPrincipal");
        return await vault.AddObjectCustodyAsync(new(Op(request.RequestToken), item.Id, request.Period, request.CustodianState, principal?.Id, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "world_event_participant_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Links a participant to a world event using semantic references.")]
    public Task<McpMutationResult> AddWorldEventParticipant(McpAddWorldEventParticipantRequest request, CancellationToken token) => Mutate(async () =>
    {
        var worldEvent = await Resolve(request.WorldEventReference, token, "WorldEvent");
        var participant = await Entity(request.ParticipantEntityReference, token);
        return await vault.AddWorldEventParticipantAsync(new(Op(request.RequestToken), worldEvent.Id, participant.Id, request.Role, request.Impact, request.Outcome, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "world_event_location_add", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Adds a world-event location using semantic references.")]
    public Task<McpMutationResult> AddWorldEventLocation(McpAddWorldEventLocationRequest request, CancellationToken token) => Mutate(async () =>
    {
        var worldEvent = await Resolve(request.WorldEventReference, token, "WorldEvent");
        var location = await Resolve(request.LocationReference, token, "Location");
        return await vault.AddWorldEventLocationAsync(new(Op(request.RequestToken), worldEvent.Id, location.Id, request.IsPrimary, request.Role, request.Notes, Label), token);
    }, token);

    [McpServerTool(Name = "entity_soft_delete", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Soft-deletes an entity by semantic reference and expectedVersion.")]
    public Task<McpMutationResult> SoftDelete(McpVersionedEntityRequest request, CancellationToken token) => EntityLifecycle(request, false, token);
    [McpServerTool(Name = "entity_restore", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Restores an entity by semantic reference and expectedVersion.")]
    public Task<McpMutationResult> Restore(McpVersionedEntityRequest request, CancellationToken token) => EntityLifecycle(request, true, token);

    [McpServerTool(Name = "relationship_soft_delete", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Soft-deletes a versioned relationship record by semantic reference.")]
    public Task<McpMutationResult> SoftDeleteRelationship(McpVersionedRelationshipRequest request, CancellationToken token) => RelationshipLifecycle(request, false, token);
    [McpServerTool(Name = "relationship_restore", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Restores a versioned relationship record by semantic reference.")]
    public Task<McpMutationResult> RestoreRelationship(McpVersionedRelationshipRequest request, CancellationToken token) => RelationshipLifecycle(request, true, token);
    [McpServerTool(Name = "vault_record_soft_delete", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Soft-deletes supported metadata by semantic reference.")]
    public Task<McpMutationResult> SoftDeleteVaultRecord(McpVersionedVaultRecordRequest request, CancellationToken token) => VaultLifecycle(request, false, token);
    [McpServerTool(Name = "vault_record_restore", Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true), Description("Restores supported metadata by semantic reference.")]
    public Task<McpMutationResult> RestoreVaultRecord(McpVersionedVaultRecordRequest request, CancellationToken token) => VaultLifecycle(request, true, token);

    private int Cid => session.RequireContinuityId();
    private string Label => session.ClientLabel;
    private string Op(string token) => references.OperationId(token);
    private Task<ResolvedVaultReference> Entity(string value, CancellationToken token) => references.ResolveEntityAsync(value, Cid, token);
    private Task<ResolvedVaultReference> Resolve(string value, CancellationToken token, params string[] types) => references.ResolveAsync(value, Cid, token, types);

    private Task<McpMutationResult> Link(McpLinkRequest request, string kind, string relatedType, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        var related = await Resolve(request.RelatedReference, token, relatedType);
        return await vault.LinkEntityAsync(kind, new(Op(request.RequestToken), entity.Id, related.Id, request.Role, request.Notes, Label), token);
    }, token);

    private Task<McpMutationResult> Unlink(McpUnlinkRequest request, string kind, string relatedType, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        var related = await Resolve(request.RelatedReference, token, relatedType);
        return await vault.UnlinkEntityAsync(kind, new(Op(request.RequestToken), entity.Id, related.Id, Label), token);
    }, token);

    private async Task<IReadOnlyList<OwnershipOwnerInput>> Owners(IReadOnlyList<McpOwnershipOwnerInput> values, CancellationToken token)
    {
        var result = new List<OwnershipOwnerInput>(values.Count);
        foreach (var value in values)
        {
            var principal = await Resolve(value.PrincipalReference, token, "OwnershipPrincipal");
            result.Add(new(principal.Id, value.SharePartsPerMillion, value.Notes));
        }
        return result;
    }

    private Task<McpMutationResult> EntityLifecycle(McpVersionedEntityRequest request, bool restore, CancellationToken token) => Mutate(async () =>
    {
        var entity = await Entity(request.EntityReference, token);
        var input = new VersionedEntityRequest(Op(request.RequestToken), entity.Id, request.ExpectedVersion, Label);
        return restore ? await vault.RestoreEntityAsync(input, token) : await vault.SoftDeleteEntityAsync(input, token);
    }, token);

    private Task<McpMutationResult> RelationshipLifecycle(McpVersionedRelationshipRequest request, bool restore, CancellationToken token) => Mutate(async () =>
    {
        var record = await Resolve(request.RecordReference, token, request.RecordType.ToString());
        var input = new VersionedRelationshipRequest(Op(request.RequestToken), request.RecordType, record.Id, request.ExpectedVersion, Label);
        return restore ? await vault.RestoreRelationshipAsync(input, token) : await vault.SoftDeleteRelationshipAsync(input, token);
    }, token);

    private Task<McpMutationResult> VaultLifecycle(McpVersionedVaultRecordRequest request, bool restore, CancellationToken token) => Mutate(async () =>
    {
        var recordId = request.RecordType == VaultRecordType.Continuity
            ? (await references.ResolveContinuityNameAsync(request.RecordReference, true, token)).Id
            : (await Resolve(request.RecordReference, token, request.RecordType.ToString())).Id;
        if (request.RecordType == VaultRecordType.Continuity && recordId != Cid)
            throw new ArgumentException("The continuity name must match this connection's selected continuity.");
        var input = new VersionedVaultRecordRequest(Op(request.RequestToken), request.RecordType, recordId, request.ExpectedVersion, Label);
        return restore ? await vault.RestoreVaultRecordAsync(input, token) : await vault.SoftDeleteVaultRecordAsync(input, token);
    }, token);

    private async Task<McpMutationResult> Mutate(Func<Task<VaultMutationResult>> action, CancellationToken token)
    {
        try { return await mapper.MutationAsync(await action().ConfigureAwait(false), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException exception) { return new(false, "validation.reference", Message: exception.Message); }
        catch (KeyNotFoundException exception) { return new(false, "entity.not_found", Message: exception.Message); }
        catch (Exception) { return new(false, "storage.failure", Message: "The vault could not complete the mutation."); }
    }
}
