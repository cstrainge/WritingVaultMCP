using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>
/// Shared v4 application operations. Public names and references are resolved before
/// queueing, then every storage key and scope boundary is revalidated in the write
/// transaction so a concurrent delete or scope change cannot be committed through.
/// </summary>
public sealed partial class AccessV4ApplicationService(
    VaultWriteCoordinator writes,
    VaultReferenceService references,
    V4TargetResolver targets,
    AccessVaultService vault)
{
    public async Task<VaultMutationResult> RecordEntityEventAsync(
        V4EntityEventAddRequest request, int continuity, string? clientLabel,
        CancellationToken cancellationToken = default)
    {
        StoryDate occurred;
        try
        {
            occurred = V4StoryDateParser.Parse(new(
                Kind: Enum.Parse<StoryDateKind>(request.Occurred.Kind?.ToString() ?? "Unknown"),
                Value: request.Occurred.Value, Lower: request.Occurred.Lower, Upper: request.Occurred.Upper,
                LowerInclusive: request.Occurred.LowerInclusive, UpperInclusive: request.Occurred.UpperInclusive,
                OriginalText: request.Occurred.OriginalText, CalendarId: request.Occurred.CalendarId ?? "Gregorian"));
            if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("Title is required.");
            if (request.NarrativeOrder is { } order && !double.IsFinite(order))
                throw new ArgumentException("Narrative order must be finite.");
        }
        catch (Exception exception) when (exception is ArgumentException or VaultValidationException)
        {
            return new(false, "validation.event", Message: exception.Message);
        }

        V4ResolvedTarget owner;
        V4ResolvedTarget? worldEvent = null;
        var projects = new List<V4ResolvedTarget>();
        try
        {
            owner = await targets.EntityAsync(request.Entity, continuity, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.WorldEvent))
            {
                worldEvent = await targets.EntityAsync(request.WorldEvent, continuity, cancellationToken).ConfigureAwait(false);
                if (!worldEvent.ResourceType.Equals("WorldEvent", StringComparison.OrdinalIgnoreCase))
                    throw new V4ResolutionException("reference.type_invalid", "worldEvent must identify a WorldEvent.");
            }
            foreach (var project in request.Projects ?? [])
                projects.Add(await targets.ProjectAsync(project, continuity, cancellationToken).ConfigureAwait(false));
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }

        if (projects.Select(project => project.StorageKey).Distinct().Count() != projects.Count)
            return new(false, "validation.projects_duplicate", Message: "The same project cannot appear more than once.");

        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }

        return await writes.ExecuteAsync(
            operation, "v4.entity.event.add", request, "entity_event_add", clientLabel,
            async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, continuity, token).ConfigureAwait(false);
                await RequireCanonEntityAsync(context, owner.StorageKey, continuity, null, token).ConfigureAwait(false);
                if (worldEvent is not null)
                    await RequireCanonEntityAsync(context, worldEvent.StorageKey, continuity, "WorldEvent", token).ConfigureAwait(false);
                foreach (var project in projects)
                    await RequireCanonEntityAsync(context, project.StorageKey, continuity, "Project", token).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                using var insert = context.Command(
                        "INSERT INTO [EntityEvents] ([EntityId],[WorldEventId],[Title],[Description],[NarrativeOrder],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, owner.StorageKey)
                    .Add(OleDbType.Integer, worldEvent?.StorageKey)
                    .Add(OleDbType.VarWChar, request.Title.Trim(), 255)
                    .Add(OleDbType.LongVarWChar, request.Description)
                    .Add(OleDbType.Double, request.NarrativeOrder);
                AddDate(insert, occurred);
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);

                var eventTarget = new V4ResolvedTarget("EntityEvent", id, string.Empty, request.Title.Trim(), continuity, false);
                foreach (var project in projects)
                    await ApplyOneProjectAsync(context, eventTarget, project.StorageKey, true, null, null, token).ConfigureAwait(false);

                return new VaultMutationOutcome("EntityEvent", id.ToString(), 1, "add", new
                {
                    owner = owner.Reference,
                    worldEvent = worldEvent?.Reference,
                    projects = projects.Select(project => project.Reference).ToArray()
                });
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> RecordWorldEventAsync(V4EventRecordRequest request,int continuity,string? clientLabel,CancellationToken cancellationToken=default)
    {
        StoryDate occurred;try{occurred=V4StoryDateParser.Parse(new(Kind:Enum.Parse<StoryDateKind>(request.Occurred.Kind?.ToString()??"Unknown"),Value:request.Occurred.Value,Lower:request.Occurred.Lower,Upper:request.Occurred.Upper,LowerInclusive:request.Occurred.LowerInclusive,UpperInclusive:request.Occurred.UpperInclusive,OriginalText:request.Occurred.OriginalText,CalendarId:request.Occurred.CalendarId??"Gregorian"));if(string.IsNullOrWhiteSpace(request.Title))throw new ArgumentException("Title is required.");if(request.NarrativeOrder is{} n&&!double.IsFinite(n))throw new ArgumentException("Narrative order must be finite.");}catch(Exception e)when(e is ArgumentException or VaultValidationException){return new(false,"validation.event",Message:e.Message);}
        var participants=new List<(V4ResolvedTarget Target,V4EventParticipantInput Input)>();var locations=new List<(V4ResolvedTarget Target,V4EventLocationInput Input)>();var projects=new List<V4ResolvedTarget>();
        try
        {
            foreach(var item in request.Participants??[])participants.Add((await targets.EntityAsync(item.Entity,continuity,cancellationToken).ConfigureAwait(false),item));
            foreach(var item in request.Locations??[]){var target=await targets.EntityAsync(item.Location,continuity,cancellationToken).ConfigureAwait(false);if(target.ResourceType!="Location")throw new V4ResolutionException("reference.type_invalid","An event location must be a Location.");locations.Add((target,item));}
            foreach(var item in request.Projects??[])projects.Add(await targets.ProjectAsync(item,continuity,cancellationToken).ConfigureAwait(false));
        }catch(V4ResolutionException e){return ResolutionFailure(e);}
        if(locations.Count(x=>x.Input.IsPrimary)>1)return new(false,"validation.primary_location",Message:"At most one event location may be primary.");
        if(projects.Select(project=>project.StorageKey).Distinct().Count()!=projects.Count)return new(false,"validation.projects_duplicate",Message:"The same project cannot appear more than once.");
        var operation=references.OperationId(request.MutationToken);
        return await writes.ExecuteAsync(operation,"v4.event.record",request,"event_record",clientLabel,async(context,token)=>
        {
            await RequireSelectedContinuityAsync(context,continuity,token).ConfigureAwait(false);var now=DateTime.UtcNow;
            using var canon=context.Command("INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,'WorldEvent',?,?)").Add(OleDbType.Integer,continuity).Add(OleDbType.Date,now).Add(OleDbType.Date,now);await canon.ExecuteNonQueryAsync(token);var id=await IdentityAsync(context,token);
            using var world=context.Command("INSERT INTO [WorldEvents] ([EntityId],[Title],[Description],[NarrativeOrder],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId]) VALUES (?,?,?,?,?,?,?,?,?,?,?)").Add(OleDbType.Integer,id).Add(OleDbType.VarWChar,request.Title.Trim(),255).Add(OleDbType.LongVarWChar,request.Description).Add(OleDbType.Double,request.NarrativeOrder);AddDate(world,occurred);await world.ExecuteNonQueryAsync(token);
            foreach(var (target,item) in participants){using var add=context.Command("INSERT INTO [WorldEventParticipants] ([WorldEventId],[ParticipantEntityId],[Role],[Impact],[Outcome],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?)").Add(OleDbType.Integer,id).Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.VarWChar,item.Role,100).Add(OleDbType.LongVarWChar,item.Impact).Add(OleDbType.LongVarWChar,item.Outcome).Add(OleDbType.LongVarWChar,item.Notes).Add(OleDbType.Date,now).Add(OleDbType.Date,now);await add.ExecuteNonQueryAsync(token);}
            foreach(var (target,item) in locations){using var add=context.Command("INSERT INTO [WorldEventLocations] ([WorldEventId],[LocationId],[IsPrimary],[Role],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?)").Add(OleDbType.Integer,id).Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.Boolean,item.IsPrimary).Add(OleDbType.VarWChar,item.Role,100).Add(OleDbType.LongVarWChar,item.Notes).Add(OleDbType.Date,now).Add(OleDbType.Date,now);await add.ExecuteNonQueryAsync(token);}
            foreach(var project in projects){using var add=context.Command("INSERT INTO [ProjectEntities] ([ProjectId],[MemberEntityId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?)").Add(OleDbType.Integer,project.StorageKey).Add(OleDbType.Integer,id).Add(OleDbType.Date,now).Add(OleDbType.Date,now);await add.ExecuteNonQueryAsync(token);}
            return new VaultMutationOutcome("WorldEvent",id.ToString(),1,"create",new{request.Title,participants=participants.Count,locations=locations.Count,projects=projects.Count});
        },cancellationToken);
    }
    public async Task<VaultMutationResult> ApplyTagAsync(V4ApplyTagCommand request,CancellationToken cancellationToken=default)
    {
        if(request.Targets is null||request.Targets.Count==0||request.Targets.Count>100)return new(false,"validation.targets",Message:"Supply between 1 and 100 targets.");
        var resolved=new List<V4ResolvedTarget>();
        try
        {
            foreach(var value in request.Targets)
            {
                try{resolved.Add(await targets.EntityAsync(value,request.ContinuityKey,cancellationToken).ConfigureAwait(false));}
                catch(V4ResolutionException e) when(e.Code is "record.not_found" or "reference.type_invalid"){resolved.Add(await targets.SourceAsync(value,cancellationToken).ConfigureAwait(false));}
            }
        }
        catch(V4ResolutionException e){return ResolutionFailure(e);}
        if(resolved.Select(x=>(x.ResourceType,x.StorageKey)).Distinct().Count()!=resolved.Count)return new(false,"validation.targets_duplicate",Message:"Targets must be unique.");
        V4ResolvedTarget? existingTag=null;
        try{existingTag=await targets.TagAsync(request.Tag,cancellationToken).ConfigureAwait(false);}
        catch(V4ResolutionException e) when(e.Code=="record.not_found"&&request.CreateIfMissing){}
        catch(V4ResolutionException e){return ResolutionFailure(e);}
        string operation;try{operation=references.OperationId(request.MutationToken);}catch(ArgumentException e){return new(false,"validation.mutation_token",Message:e.Message);}
        return await writes.ExecuteAsync(operation,"v4.tag.apply",request,"tag_apply",request.ClientLabel,async(context,token)=>
        {
            await RequireSelectedContinuityAsync(context,request.ContinuityKey,token).ConfigureAwait(false);var tagId=existingTag?.StorageKey??0;var now=DateTime.UtcNow;
            if(tagId==0)
            {
                var name=TextNormalization.Required(request.Tag,255,nameof(request.Tag));var normalized=TextNormalization.CanonicalKey(name);
                using var insert=context.Command("INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?)").Add(OleDbType.VarWChar,name,255).Add(OleDbType.VarWChar,normalized,255).Add(OleDbType.Date,now).Add(OleDbType.Date,now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);tagId=await IdentityAsync(context,token).ConfigureAwait(false);
            }
            var changed=0;
            foreach(var target in resolved)
            {
                var source=target.ResourceType.Equals("Source",StringComparison.OrdinalIgnoreCase);var table=source?"SourceTags":"EntityTags";var owner=source?"SourceId":"EntityId";
                using var find=context.Command($"SELECT COUNT(*) FROM [{table}] WHERE [{owner}]=? AND [TagId]=?").Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.Integer,tagId);var exists=Convert.ToInt32(await find.ExecuteScalarAsync(token).ConfigureAwait(false))>0;
                if(request.Add&&!exists){using var add=context.Command($"INSERT INTO [{table}] ([{owner}],[TagId]) VALUES (?,?)").Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.Integer,tagId);changed+=await add.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
                else if(!request.Add&&exists){using var remove=context.Command($"DELETE FROM [{table}] WHERE [{owner}]=? AND [TagId]=?").Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.Integer,tagId);changed+=await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
            }
            return new VaultMutationOutcome("Tag",tagId.ToString(),existingTag is null?1:null,request.Add?"apply":"remove",new{tag=request.Tag,targets=resolved.Select(x=>x.Reference).ToArray(),changed});
        },cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> AddNoteAsync(
        V4AddNoteCommand request, CancellationToken cancellationToken = default)
    {
        string body;
        string? title;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Body))
                throw new ArgumentException("Body is required.", nameof(request.Body));
            if (request.Body.Length > 65_536)
                throw new ArgumentOutOfRangeException(nameof(request.Body), "Body cannot exceed 65,536 characters.");
            body = request.Body;
            title = Optional(request.Title, 255, nameof(request.Title));
            if (!request.Format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("format must be markdown.", nameof(request.Format));
        }
        catch (ArgumentException exception)
        {
            return new(false, "validation.note", Message: exception.Message);
        }

        V4ResolvedTarget target;
        try { target = await targets.NoteOwnerAsync(request.Target, request.ContinuityKey, cancellationToken).ConfigureAwait(false); }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        string operationId;
        try { operationId = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }

        return await writes.ExecuteAsync(
            operationId, "v4.note.add", request, "note_add", request.ClientLabel,
            async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, request.ContinuityKey, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                string table;
                string ownerColumn;
                string resourceType;
                if (target.ResourceType.Equals("Continuity", StringComparison.OrdinalIgnoreCase))
                {
                    if (target.StorageKey != request.ContinuityKey)
                        throw new VaultCommandException("continuity.mismatch", "The note target is outside the selected continuity.");
                    await RequireContinuityAsync(context, target.StorageKey, token).ConfigureAwait(false);
                    table = "ContinuityNotes";
                    ownerColumn = "ContinuityId";
                    resourceType = "ContinuityNote";
                }
                else
                {
                    await RequireCanonEntityAsync(context, target.StorageKey, request.ContinuityKey, null, token).ConfigureAwait(false);
                    table = "EntityNotes";
                    ownerColumn = "EntityId";
                    resourceType = "EntityNote";
                }

                using var insert = context.Command(
                        $"INSERT INTO [{table}] ([{ownerColumn}],[Title],[Body],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?)")
                    .Add(OleDbType.Integer, target.StorageKey)
                    .Add(OleDbType.VarWChar, title, 255)
                    .Add(OleDbType.LongVarWChar, body)
                    .Add(OleDbType.Date, now)
                    .Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome(resourceType, id.ToString(), 1, "add", new
                {
                    target = target.Reference,
                    title,
                    format = "markdown"
                });
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> ApplyEventProjectsAsync(
        V4ApplyEventProjectsCommand request, CancellationToken cancellationToken = default)
    {
        if (request.Projects is null || request.Projects.Count == 0)
            return new(false, "validation.projects", Message: "At least one project is required.");
        if (request.Projects.Count > 100)
            return new(false, "validation.projects", Message: "At most 100 projects may be changed at once.");
        string? role;
        string? notes;
        try
        {
            role = Optional(request.Role, 100, nameof(request.Role));
            notes = Optional(request.Notes, 65_536, nameof(request.Notes));
        }
        catch (ArgumentException exception)
        {
            return new(false, "validation.projects", Message: exception.Message);
        }

        V4ResolvedTarget eventTarget;
        V4ResolvedTarget[] projectTargets;
        try
        {
            eventTarget = await targets.EventAsync(request.Event, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
            var resolved = new List<V4ResolvedTarget>(request.Projects.Count);
            foreach (var project in request.Projects)
                resolved.Add(await targets.ProjectAsync(project, request.ContinuityKey, cancellationToken).ConfigureAwait(false));
            projectTargets = resolved.ToArray();
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        if (projectTargets.Select(project => project.StorageKey).Distinct().Count() != projectTargets.Length)
            return new(false, "validation.projects_duplicate", Message: "The same project cannot appear more than once.");

        string operationId;
        try { operationId = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }

        return await writes.ExecuteAsync(
            operationId, "v4.event.projects.apply", request, "event_project_apply", request.ClientLabel,
            async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, request.ContinuityKey, token).ConfigureAwait(false);
                if (eventTarget.ResourceType.Equals("WorldEvent", StringComparison.OrdinalIgnoreCase))
                    await RequireCanonEntityAsync(context, eventTarget.StorageKey, request.ContinuityKey, "WorldEvent", token).ConfigureAwait(false);
                else if (eventTarget.ResourceType.Equals("RelationshipEvent", StringComparison.OrdinalIgnoreCase))
                {
                    using var check = context.Command("SELECT COUNT(*) FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] WHERE e.[Id]=? AND r.[ContinuityId]=? AND e.[IsDeleted]=False AND r.[IsDeleted]=False")
                        .Add(OleDbType.Integer, eventTarget.StorageKey).Add(OleDbType.Integer, request.ContinuityKey);
                    if (Convert.ToInt32(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("record.not_found", "The relationship event is unavailable in this continuity.");
                }
                else
                    await RequireEntityEventAsync(context, eventTarget.StorageKey, request.ContinuityKey, token).ConfigureAwait(false);
                foreach (var project in projectTargets)
                    await RequireCanonEntityAsync(context, project.StorageKey, request.ContinuityKey, "Project", token).ConfigureAwait(false);

                var changed = 0;
                foreach (var project in projectTargets)
                    changed += await ApplyOneProjectAsync(context, eventTarget, project.StorageKey, request.Add, role, notes, token).ConfigureAwait(false);

                return new VaultMutationOutcome(
                    eventTarget.ResourceType, eventTarget.StorageKey.ToString(), null,
                    request.Add ? "projects-add" : "projects-remove",
                    new
                    {
                        eventRef = eventTarget.Reference,
                        projects = projectTargets.Select(project => project.Reference).ToArray(),
                        changed,
                        role
                    });
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> CreateCharacterRelationshipAsync(
        V4CreateCharacterRelationshipCommand request, CancellationToken cancellationToken = default)
    {
        if (request.Period is null)
            return new(false, "validation.period", Message: "A relationship period is required.");
        if (request.Notes?.Length > V4ContractLimits.MaximumLongTextLength)
            return new(false, "validation.notes", Message: $"Relationship notes may contain at most {V4ContractLimits.MaximumLongTextLength:N0} characters.");
        V4ResolvedTarget source;
        V4ResolvedTarget target;
        V4ResolvedTarget type;
        try
        {
            source = await ResolveCharacterAsync(request.SourceCharacter).ConfigureAwait(false);
            target = await ResolveCharacterAsync(request.TargetCharacter).ConfigureAwait(false);
            type = await targets.RelationshipTypeAsync(request.RelationshipType, cancellationToken).ConfigureAwait(false);
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        string operationId;
        try { operationId = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await vault.CreateRelationshipAsync(new(
            operationId, request.ContinuityKey, source.StorageKey, target.StorageKey,
            type.StorageKey, request.Period, request.Notes, request.ClientLabel), cancellationToken).ConfigureAwait(false);

        async Task<V4ResolvedTarget> ResolveCharacterAsync(string value)
        {
            var entity = await targets.EntityAsync(value, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
            if (!entity.ResourceType.Equals("Character", StringComparison.OrdinalIgnoreCase))
                throw new V4ResolutionException("reference.type_invalid", "A character is required.");
            return entity;
        }
    }

    public async Task<VaultMutationResult> CreateRelationshipGroupAsync(
        V4CreateRelationshipGroupCommand request, CancellationToken cancellationToken = default)
    {
        if (request.Characters is null || request.Characters.Count is < 2 or > 100)
            return new(false, "validation.participants", Message: "A relationship needs 2 to 100 characters.");
        if (request.Notes?.Length > V4ContractLimits.MaximumLongTextLength)
            return new(false, "validation.notes", Message: "Relationship notes are too long.");
        var members = new List<V4ResolvedTarget>(request.Characters.Count);
        V4ResolvedTarget type;
        try
        {
            foreach (var value in request.Characters)
            {
                var member = await targets.EntityAsync(value, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
                if (!member.ResourceType.Equals("Character", StringComparison.OrdinalIgnoreCase))
                    throw new V4ResolutionException("reference.type_invalid", "Every participant must be a character.");
                members.Add(member);
            }
            type = await targets.RelationshipTypeAsync(request.RelationshipType, cancellationToken).ConfigureAwait(false);
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        if (members.Select(member => member.StorageKey).Distinct().Count() != members.Count)
            return new(false, "validation.participants", Message: "Each character may appear only once.");
        string operationId;
        try { operationId = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await vault.CreateRelationshipGroupAsync(new(operationId, request.ContinuityKey,
            members.Select(member => member.StorageKey).ToArray(), type.StorageKey,
            request.InitialPeriod, request.Notes, request.ClientLabel), cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> AddOwnershipAsync(
        V4AddOwnershipCommand request, CancellationToken cancellationToken = default)
    {
        if (request.Period is null)
            return new(false, "validation.period", Message: "An ownership period is required.");
        if (request.Owners is null || request.Owners.Count == 0)
            return new(false, "validation.ownership", Message: "At least one owner is required.");
        if (request.Owners.Count > 100)
            return new(false, "validation.ownership", Message: "At most 100 owners may be supplied.");
        if (request.Owners.Any(owner => owner is null || string.IsNullOrWhiteSpace(owner.Principal)))
            return new(false, "validation.ownership", Message: "Every owner must name an ownership principal.");
        V4ResolvedTarget objectTarget;
        V4ResolvedTarget[] principalTargets;
        try
        {
            objectTarget = await targets.EntityAsync(request.Object, request.ContinuityKey, cancellationToken).ConfigureAwait(false);
            if (!objectTarget.ResourceType.Equals("Object", StringComparison.OrdinalIgnoreCase))
                throw new V4ResolutionException("reference.type_invalid", "An object is required.");
            var principals = new List<V4ResolvedTarget>(request.Owners.Count);
            foreach (var owner in request.Owners)
                principals.Add(await targets.OwnershipPrincipalAsync(owner.Principal, request.ContinuityKey, cancellationToken).ConfigureAwait(false));
            principalTargets = principals.ToArray();
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        string operationId;
        try { operationId = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception) { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await vault.AddOwnershipPeriodAsync(new(
            operationId, objectTarget.StorageKey, request.State, request.Period,
            request.Owners.Select((owner, index) => new OwnershipOwnerInput(
                principalTargets[index].StorageKey, owner.SharePartsPerMillion, owner.Notes)).ToArray(),
            request.Notes, request.ClientLabel), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ApplyOneProjectAsync(
        VaultWriteContext context, V4ResolvedTarget eventTarget, int projectId,
        bool add, string? role, string? notes, CancellationToken token)
    {
        var world = eventTarget.ResourceType.Equals("WorldEvent", StringComparison.OrdinalIgnoreCase);
        var relationship = eventTarget.ResourceType.Equals("RelationshipEvent", StringComparison.OrdinalIgnoreCase);
        var table = world ? "ProjectEntities" : relationship ? "RelationshipEventProjects" : "EntityEventProjects";
        var eventColumn = world ? "MemberEntityId" : relationship ? "RelationshipEventId" : "EntityEventId";
        var now = DateTime.UtcNow;
        using var lookup = context.Command(
                $"SELECT [Id],[IsDeleted],[Role],[Notes] FROM [{table}] WHERE [{eventColumn}]=? AND [ProjectId]=?")
            .Add(OleDbType.Integer, eventTarget.StorageKey)
            .Add(OleDbType.Integer, projectId);
        var existing = await lookup.QueryAsync(reader => (
            Id: reader.GetInt32(0), Deleted: reader.GetBoolean(1),
            Role: reader.IsDBNull(2) ? null : reader.GetString(2),
            Notes: reader.IsDBNull(3) ? null : reader.GetString(3)), token).ConfigureAwait(false);

        if (add)
        {
            if (existing.Count == 0)
            {
                using var insert = context.Command(
                        $"INSERT INTO [{table}] ([ProjectId],[{eventColumn}],[Role],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, projectId).Add(OleDbType.Integer, eventTarget.StorageKey)
                    .Add(OleDbType.VarWChar, role, 100).Add(OleDbType.LongVarWChar, notes)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return 1;
            }
            var row = existing.Single();
            if (!row.Deleted && string.Equals(row.Role, role, StringComparison.Ordinal) && string.Equals(row.Notes, notes, StringComparison.Ordinal))
                return 0;
            using var restore = context.Command(
                    $"UPDATE [{table}] SET [Role]=?,[Notes]=?,[IsDeleted]=False,[DeletedAtUtc]=Null,[DeletedOperationId]=Null,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=?")
                .Add(OleDbType.VarWChar, role, 100).Add(OleDbType.LongVarWChar, notes)
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, row.Id);
            return await restore.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        if (existing.Count == 0 || existing[0].Deleted) return 0;
        using var remove = context.Command(
                $"UPDATE [{table}] SET [IsDeleted]=True,[DeletedAtUtc]=?,[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [IsDeleted]=False")
            .Add(OleDbType.Date, now).Add(OleDbType.VarWChar, context.OperationId, 36)
            .Add(OleDbType.Date, now).Add(OleDbType.Integer, existing[0].Id);
        return await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task RequireSelectedContinuityAsync(VaultWriteContext context, int continuityId, CancellationToken token) =>
        await RequireContinuityAsync(context, continuityId, token).ConfigureAwait(false);

    private static async Task RequireContinuityAsync(VaultWriteContext context, int continuityId, CancellationToken token)
    {
        using var command = context.Command("SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, continuityId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("record.not_found", "The selected continuity is missing or deleted.");
    }

    private static async Task RequireCanonEntityAsync(
        VaultWriteContext context, int entityId, int continuityId, string? requiredType, CancellationToken token)
    {
        var sql = "SELECT COUNT(*) FROM [CanonEntities] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False" +
                  (requiredType is null ? string.Empty : " AND [EntityType]=?");
        using var command = context.Command(sql).Add(OleDbType.Integer, entityId).Add(OleDbType.Integer, continuityId);
        if (requiredType is not null) command.Add(OleDbType.VarWChar, requiredType, 50);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("scope.target_invalid", "A target is missing, deleted, the wrong kind, or outside the selected continuity.");
    }

    private static async Task RequireEntityEventAsync(
        VaultWriteContext context, int eventId, int continuityId, CancellationToken token)
    {
        using var command = context.Command(
                "SELECT COUNT(*) FROM [EntityEvents] AS e INNER JOIN [CanonEntities] AS c ON e.[EntityId]=c.[Id] WHERE e.[Id]=? AND c.[ContinuityId]=? AND e.[IsDeleted]=False AND c.[IsDeleted]=False")
            .Add(OleDbType.Integer, eventId).Add(OleDbType.Integer, continuityId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("scope.target_invalid", "The entity event is missing, deleted, or outside the selected continuity.");
    }

    private static async Task<int> IdentityAsync(VaultWriteContext context, CancellationToken token)
    {
        using var identity = context.Command("SELECT @@IDENTITY");
        return Convert.ToInt32(await identity.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static void AddDate(AccessCommand command,StoryDate value)=>command
        .Add(OleDbType.VarWChar,value.Kind.ToString(),30).Add(OleDbType.Date,value.LowerBound)
        .Add(OleDbType.Date,value.UpperBound).Add(OleDbType.Boolean,value.LowerInclusive)
        .Add(OleDbType.Boolean,value.UpperInclusive).Add(OleDbType.LongVarWChar,value.OriginalText)
        .Add(OleDbType.VarWChar,value.CalendarId,50);

    private static string? Optional(string? value, int maximumLength, string field)
    {
        if (value is null) return null;
        if (value.Length > maximumLength) throw new ArgumentException($"{field} cannot exceed {maximumLength} characters.", field);
        return value;
    }

    private static VaultMutationResult ResolutionFailure(V4ResolutionException exception) =>
        new(false, exception.Code, Message: exception.Message, Candidates: exception.Candidates
            .Select(candidate => new VaultMutationCandidate(
                candidate.ResourceType, candidate.Reference, candidate.Label, candidate.Context))
            .ToArray());
}
