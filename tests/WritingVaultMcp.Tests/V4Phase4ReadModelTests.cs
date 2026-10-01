using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp.V4;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase4ReadModelTests
{
    [Fact]
    public async Task ContinuityListPagesPastOneHundredWithoutLosingNames()
    {
        await using var vault = await TestVault.CreateAsync();
        for (var index = 0; index < 105; index++)
            await CreateContinuity(vault, $"World {index:D3}");
        var (reads, _, _) = vault.V4();
        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = await reads.ContinuitiesAsync(new(Cursor: cursor, Limit: 37));
            names.AddRange(page.Items.Select(item => item.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(105, names.Count);
        Assert.Equal(105, names.Distinct().Count());
    }

    [Fact]
    public async Task SearchPagesPastOneHundredRecordsWithoutExposingStorageIdentity()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Large World");
        for (var index = 0; index < 112; index++)
            await CreateEntity(vault, continuity, CanonEntityType.Character, $"Person {index:D3}");
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Large World");

        var found = new List<string>();
        string? cursor = null;
        do
        {
            var page = await reads.SearchAsync(new(Kinds: [V4RecordKind.Character], Cursor: cursor, Limit: 25));
            found.AddRange(page.Items.Select(item => item.Label));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(112, found.Count);
        Assert.Equal(112, found.Distinct().Count());
        var json = System.Text.Json.JsonSerializer.Serialize(found);
        Assert.DoesNotContain("entityId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guid", json, StringComparison.OrdinalIgnoreCase);
        var relatedFirst=await reads.RelatedAsync(new(null,"entities",Limit:25));
        var relatedSecond=await reads.RelatedAsync(new(null,"entities",Cursor:relatedFirst.NextCursor,Limit:25));
        Assert.True(relatedFirst.HasMore);Assert.DoesNotContain(relatedFirst.Items.Select(item=>item.Ref),reference=>relatedSecond.Items.Any(item=>item.Ref==reference));
    }

    [Fact]
    public async Task ContentSearchFindsADeepMarkdownNoteWithoutReturningItsWholeBody()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=await CreateContinuity(vault,"Deep Notes");
        var character=await CreateEntity(vault,continuity,CanonEntityType.Character,"Archivist");
        Assert.True((await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(),character,
            new string('x',300)+"hidden brass compass","Working note"))).Success);
        var (reads,session,_)=vault.V4();session.SelectContinuity(continuity,"Deep Notes");
        var page=await reads.SearchAsync(new(Text:"brass compass",Kinds:[V4RecordKind.Note],IncludeContent:true));
        var note=Assert.Single(page.Items);
        Assert.Equal("Working note",note.Label);
        Assert.True(note.Context?.Length<=255);
    }

    [Fact]
    public async Task TimelineReturnsHistoricalAndUndatedRecordsWithUnsetClockAndStablePaging()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Clockless World");
        await CreateEntity(vault, continuity, CanonEntityType.WorldEvent, "Undated");
        await CreateWorldEvent(vault, continuity, "First", StoryDate.ExactDate(new DateOnly(2020, 1, 1)));
        await CreateWorldEvent(vault, continuity, "Second", StoryDate.ExactDate(new DateOnly(2020, 1, 1)));
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Clockless World");

        var first = await reads.TimelineAsync(new(IncludeUndated: true, Limit: 1));
        Assert.Equal("Unset", first.Clock.Status);
        Assert.True(first.HasMore);
        var second = await reads.TimelineAsync(new(IncludeUndated: true, Cursor: first.NextCursor, Limit: 10));
        Assert.DoesNotContain(first.Items.Select(x => x.Ref), x => second.Items.Any(y => y.Ref == x));
        Assert.Contains(first.Items.Concat(second.Items), item => item.Title == "First");
        Assert.Contains(first.Items.Concat(second.Items), item => item.Title == "Second");
        Assert.Contains(first.Undated.Concat(second.Undated), item => item.Label == "Undated");
    }

    [Fact]
    public async Task AggregateTimelineGroupsBeforePagingAndDoesNotSplitABucket()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Aggregate World");
        await CreateWorldEvent(vault, continuity, "Same day one", StoryDate.ExactDate(new DateOnly(2020, 1, 1)));
        await CreateWorldEvent(vault, continuity, "Same day two", StoryDate.ExactDate(new DateOnly(2020, 1, 1)));
        await CreateWorldEvent(vault, continuity, "Same day three", StoryDate.ExactDate(new DateOnly(2020, 1, 1)));
        await CreateWorldEvent(vault, continuity, "Next day", StoryDate.ExactDate(new DateOnly(2020, 1, 2)));
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Aggregate World");

        var first = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Aggregate, Limit: 1));
        Assert.True(first.Aggregated);
        var bucket = Assert.Single(first.Items);
        Assert.Equal("Aggregate", bucket.Kind);
        Assert.Equal(3, bucket.Related.Count);
        Assert.True(first.HasMore);

        var second = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Aggregate, Cursor: first.NextCursor, Limit: 1));
        Assert.Equal("Next day", Assert.Single(second.Items).Title);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task ChangesCursorCannotMissCommitAndIncludesVaultGlobalTagChanges()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=await CreateContinuity(vault,"Watcher");
        var (reads,session,_)=vault.V4(); session.SelectContinuity(continuity,"Watcher");
        var initial=await reads.ChangesSinceAsync(new());
        await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(),"Global change"));
        var changed=await reads.ChangesSinceAsync(new(initial.Cursor,1));
        Assert.Contains(changed.Changes,x=>x.Scope=="vault-global"&&x.Kind=="Tag");
        Assert.False(changed.Heartbeat);
        var source=int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(),"Shared source"))).ResourceKey!);
        var tag=int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(),"Linked global tag"))).ResourceKey!);
        var beforeLink=await reads.ChangesSinceAsync(new(changed.Cursor));
        Assert.True((await vault.Service.LinkSourceTagAsync(new(Guid.NewGuid().ToString(),source,tag))).Success);
        var linkChange=await reads.ChangesSinceAsync(new(beforeLink.Cursor,1));
        Assert.Contains(linkChange.Changes,x=>x.Scope=="vault-global"&&x.Kind=="SourceTag"&&x.FullVisibleRefreshRequired);
    }

    [Fact]
    public async Task ProjectEventsMergeBothKindsWithoutPagingLoss()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=await CreateContinuity(vault,"Projects");
        var project=await CreateEntity(vault,continuity,CanonEntityType.Project,"Novel");
        var character=await CreateEntity(vault,continuity,CanonEntityType.Character,"Hero");
        var world=await CreateWorldEvent(vault,continuity,"World Event",StoryDate.ExactDate(new DateOnly(2020,1,1)));
        var local=int.Parse((await vault.Service.AddEntityEventAsync(new(Guid.NewGuid().ToString(),character,"Local Event",StoryDate.ExactDate(new DateOnly(2020,1,2))))).ResourceKey!);
        var (reads,session,references)=vault.V4(); session.SelectContinuity(continuity,"Projects");
        var app=new AccessV4ApplicationService(vault.Coordinator,references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),vault.Service);
        var projectRef=await references.ReferenceAsync("Project",project); var worldRef=await references.ReferenceAsync("WorldEvent",world); var localRef=await references.ReferenceAsync("EntityEvent",local);
        Assert.True((await app.ApplyEventProjectsAsync(new(Guid.NewGuid().ToString(),continuity,worldRef,[projectRef],true,"backstory","Sets the world context"))).Success);
        Assert.True((await app.ApplyEventProjectsAsync(new(Guid.NewGuid().ToString(),continuity,localRef,[projectRef],true,"scene","Chapter five"))).Success);
        var first=await reads.RelatedAsync(new(projectRef,"events",Limit:1));
        var second=await reads.RelatedAsync(new(projectRef,"events",Cursor:first.NextCursor,Limit:1));
        Assert.Equal(2,first.Items.Concat(second.Items).Select(x=>x.Label).Distinct().Count());
        Assert.Contains(first.Items.Concat(second.Items),x=>x.AssociationRole=="backstory"&&x.AssociationNotes=="Sets the world context");
        Assert.Contains(first.Items.Concat(second.Items),x=>x.AssociationRole=="scene"&&x.AssociationNotes=="Chapter five");
        Assert.Equal("backstory",Assert.Single((await reads.RelatedAsync(new(worldRef,"projects"))).Items).AssociationRole);
        Assert.Equal("Chapter five",Assert.Single((await reads.RelatedAsync(new(localRef,"projects"))).Items).AssociationNotes);
    }

    [Fact]
    public async Task CharacterReverseLinksExposeOwnershipCustodyAndWorldEventParticipation()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=await CreateContinuity(vault,"Reverse Links");
        var character=await CreateEntity(vault,continuity,CanonEntityType.Character,"Owner");
        var thing=await CreateEntity(vault,continuity,CanonEntityType.Object,"Keepsake");
        var world=await CreateWorldEvent(vault,continuity,"Discovery",StoryDate.ExactDate(new DateOnly(2020,1,1)));
        var principal=int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(Guid.NewGuid().ToString(),continuity,PrincipalKind.Character,CharacterId:character))).ResourceKey!);
        var period=new StoryDate(StoryDateKind.After,new DateTime(2020,1,1),null,true,false);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(),thing,OwnershipState.Owned,period,[new(principal,1_000_000)]))).Success);
        Assert.True((await vault.Service.AddObjectCustodyAsync(new(Guid.NewGuid().ToString(),thing,period,CustodyState.Known,principal))).Success);
        Assert.True((await vault.Service.AddWorldEventParticipantAsync(new(Guid.NewGuid().ToString(),world,character,"discoverer"))).Success);
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Reverse Links");
        var characterRef=await references.ReferenceAsync("Character",character);
        var overview=await reads.GetAsync(new(characterRef));
        Assert.Single(overview.Sections["ownership"].Items);
        Assert.Single(overview.Sections["custody"].Items);
        Assert.Single(overview.Sections["eventParticipation"].Items);
        Assert.Equal("Owned",Assert.Single((await reads.RelatedAsync(new(characterRef,"ownership"))).Items).Label);
    }

    [Fact]
    public async Task DirectedRelationshipUsesInverseLabelOnTheOtherCharacterPage()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=await CreateContinuity(vault,"Directed Links");
        var parent=await CreateEntity(vault,continuity,CanonEntityType.Character,"Parent");
        var child=await CreateEntity(vault,continuity,CanonEntityType.Character,"Child");
        var type=int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(Guid.NewGuid().ToString(),"parent of",true,"child of"))).ResourceKey!);
        Assert.True((await vault.Service.CreateRelationshipAsync(new(Guid.NewGuid().ToString(),continuity,parent,child,type,StoryDate.Unknown(),"Met at the station."))).Success);
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Directed Links");
        var parentLink=Assert.Single((await reads.RelatedAsync(new(await references.ReferenceAsync("Character",parent),"relationships"))).Items);
        var childLink=Assert.Single((await reads.RelatedAsync(new(await references.ReferenceAsync("Character",child),"relationships"))).Items);
        Assert.Equal(parentLink.Ref,childLink.Ref);
        Assert.Equal(await references.ReferenceAsync("CharacterRelationship",(await vault.Service.GetCharacterRelationshipsAsync(parent)).Single().RelationshipId),parentLink.Ref);
        Assert.Equal("parent of",parentLink.Label);Assert.Equal("Child",parentLink.Context);
        Assert.Equal("child of",childLink.Label);Assert.Equal("Parent",childLink.Context);
        var relationships=await reads.SearchAsync(new(Kinds:[V4RecordKind.Relationship]));
        var listed=Assert.Single(relationships.Items);
        Assert.Equal(parentLink.Ref,listed.Ref);
        Assert.Equal("Parent — parent of — Child",listed.Label);
        Assert.Equal(listed.Ref,Assert.Single((await reads.SearchAsync(new(
            Text:"Child",Kinds:[V4RecordKind.Relationship]))).Items).Ref);
        Assert.Equal(listed.Ref,Assert.Single((await reads.SearchAsync(new(
            Text:"station",Kinds:[V4RecordKind.Relationship],IncludeContent:true))).Items).Ref);
        var details=await reads.GetAsync(new(parentLink.Ref));
        Assert.Contains(details.Sections["characters"].Items,item=>item.Label=="Parent"&&item.Context=="Source character");
        Assert.Contains(details.Sections["characters"].Items,item=>item.Label=="Child"&&item.Context=="Target character");
    }

    [Fact]
    public async Task NonEntityOverviewsExposeBoundedReverseLinksWithoutStorageKeys()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=await CreateContinuity(vault,"Pages");
        var character=await CreateEntity(vault,continuity,CanonEntityType.Character,"Reader");
        var source=int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(),"Archive",CanonicalUrl:"https://example.test/fact"))).ResourceKey!);
        var tag=int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(),"Research"))).ResourceKey!);
        await vault.Service.LinkEntityAsync("source",new(Guid.NewGuid().ToString(),character,source));
        await vault.Service.LinkSourceTagAsync(new(Guid.NewGuid().ToString(),source,tag));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Pages");
        var sourceRef=await references.ReferenceAsync("Source",source);var overview=await reads.GetAsync(new(sourceRef));
        Assert.Equal(V4RecordKind.Source,overview.Summary.Kind);
        Assert.Single(overview.Sections["entities"].Items);
        Assert.Single(overview.Sections["tags"].Items);
        var json=System.Text.Json.JsonSerializer.Serialize(overview);
        Assert.DoesNotContain("sourceId",json,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entityId",json,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relativePath",json,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalSourceReverseLinksStayInsideTheSelectedContinuity()
    {
        await using var vault=await TestVault.CreateAsync();var first=await CreateContinuity(vault,"First");var second=await CreateContinuity(vault,"Second");
        var visible=await CreateEntity(vault,first,CanonEntityType.Character,"Visible");var hidden=await CreateEntity(vault,second,CanonEntityType.Character,"Hidden");
        var source=int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(),"Shared"))).ResourceKey!);
        Assert.True((await vault.Service.LinkEntityAsync("source",new(Guid.NewGuid().ToString(),visible,source))).Success);
        Assert.True((await vault.Service.LinkEntityAsync("source",new(Guid.NewGuid().ToString(),hidden,source))).Success);
        var visibleNote=int.Parse((await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(),visible,"visible note","Visible note"))).ResourceKey!);
        var hiddenNote=int.Parse((await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(),hidden,"hidden note","Hidden note"))).ResourceKey!);
        Assert.True((await vault.Service.LinkNoteSourceAsync(new(Guid.NewGuid().ToString(),visibleNote,source))).Success);
        Assert.True((await vault.Service.LinkNoteSourceAsync(new(Guid.NewGuid().ToString(),hiddenNote,source))).Success);
        var (reads,session,references)=vault.V4();session.SelectContinuity(first,"First");
        var overview=await reads.GetAsync(new(await references.ReferenceAsync("Source",source)));
        Assert.Single(overview.Sections["entities"].Items);Assert.Equal("Visible",overview.Sections["entities"].Items[0].Label);
        Assert.Single(overview.Sections["notes"].Items);Assert.Equal("Visible note",overview.Sections["notes"].Items[0].Label);
    }

    [Fact]
    public async Task CharacterOverviewCarriesEffectiveTemporalStateThroughMcpReadTool()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=await CreateContinuity(vault,"Current");
        var character=await CreateEntity(vault,continuity,CanonEntityType.Character,"Current Person");
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Current");
        var temporal=new AccessV4TemporalService(vault.Factory,vault.Coordinator,references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads);
        var snapshots=new AccessV4SourceSnapshotService(vault.Factory,references,session,vault.Cursors,new(vault.StorageRoot),reads);
        var tools=new V4VaultReadTools(reads,snapshots,temporal,new AccessV4ImageService(vault.Factory,vault.Coordinator,references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads,vault.Cursors,vault.StorageRoot,vault.DatabasePath),
            new AccessV4RelationshipMergeService(vault.Factory,vault.Coordinator,references,session),
            references,session,vault.Schema,vault.Integrity,vault.Coordinator,new(vault.Factory,vault.Coordinator,vault.StorageRoot,3));
        var result=await tools.Get(new(await references.ReferenceAsync("Character",character)));
        Assert.Equal("Unset",result.Fields["currentTemporalState"].GetProperty("status").GetString());
        Assert.Equal("TimelineUnset",result.Fields["age"].GetProperty("status").GetString());
        Assert.Equal(17,result.Sections.Count);
    }

    [Fact]
    public async Task EntityEventCreationPersistsInitialProjectsInTheSameApplicationOperation()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=await CreateContinuity(vault,"Event Projects");
        var project=await CreateEntity(vault,continuity,CanonEntityType.Project,"Book One");
        await CreateEntity(vault,continuity,CanonEntityType.Character,"Hero");
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Event Projects");
        var app=new AccessV4ApplicationService(vault.Coordinator,references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),vault.Service);
        var result=await app.RecordEntityEventAsync(new(Guid.NewGuid().ToString(),"Hero","Inciting incident",
            new(V4StoryDateKind.ExactDate,"2020-01-01"),Projects:["Book One"]),continuity,"test");
        Assert.True(result.Success,result.Code+": "+result.Message);
        var projectRef=await references.ReferenceAsync("Project",project);
        var page=await reads.RelatedAsync(new(projectRef,"events"));
        Assert.Contains(page.Items,item=>item.Label=="Inciting incident"&&item.Kind==V4RecordKind.EntityEvent);
    }

    [Fact]
    public async Task SnapshotOverviewIsBoundedAndGenericDeletePreviewHandlesContinuity()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=await CreateContinuity(vault,"Bounded Reads");
        await CreateEntity(vault,continuity,CanonEntityType.Character,"Dependent");
        var source=int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(),"Large source"))).ResourceKey!);
        var content=new string('x',100_000);
        var snapshot=int.Parse((await vault.Service.AddSourceSnapshotAsync(new(Guid.NewGuid().ToString(),source,content))).ResourceKey!);
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Bounded Reads");
        var overview=await reads.GetAsync(new(await references.ReferenceAsync("SourceSnapshot",snapshot)));
        Assert.Equal(V4RecordKind.SourceSnapshot,overview.Summary.Kind);
        Assert.False(overview.Fields.ContainsKey("content"));
        Assert.True(overview.Fields.ContainsKey("byteSize"));
        Assert.Single(overview.Sections["source"].Items);
        var preview=await reads.DeletePreviewAsync(new("Bounded Reads"));
        Assert.False(preview.CanDelete);
        Assert.Contains(preview.Blockers,item=>item.Context!.Contains("CanonEntities",StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinuityPreviewAndOverviewReturnAReferenceThatCanBeSoftDeleted()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Disposable continuity");
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Disposable continuity");

        var preview = await reads.DeletePreviewAsync(new("Disposable continuity"));
        var overview = await reads.GetAsync(new());
        Assert.True(preview.CanDelete);
        Assert.StartsWith("continuity:", preview.Target.Ref, StringComparison.Ordinal);
        Assert.Equal(preview.Target.Ref, overview.Summary.Ref);
        Assert.Equal(continuity, (await references.ResolveAsync(preview.Target.Ref, continuity,
            CancellationToken.None, "Continuity")).Id);

        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var deleted = await records.LifecycleAsync(new(Guid.NewGuid().ToString(),
            preview.Target.Ref, overview.Summary.Version!.Value), false);
        Assert.True(deleted.Success, $"{deleted.Code}: {deleted.Message}");
        var deletedPreview = await reads.DeletePreviewAsync(new("Disposable continuity"));
        Assert.False(deletedPreview.CanDelete);
        Assert.True(deletedPreview.Target.IsDeleted);
        Assert.Equal(preview.Target.Ref, deletedPreview.Target.Ref);
    }

    [Fact]
    public async Task LocationOverviewIncludesReverseResidenceLinks()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=await CreateContinuity(vault,"Places");
        var location=await CreateEntity(vault,continuity,CanonEntityType.Location,"Town");
        var character=await CreateEntity(vault,continuity,CanonEntityType.Character,"Resident");
        Assert.True((await vault.Service.AddResidenceAsync(new(Guid.NewGuid().ToString(),character,location,
            new StoryDate(StoryDateKind.After,new DateTime(2020,1,1),null,true,false),true))).Success);
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Places");
        var overview=await reads.GetAsync(new(await references.ReferenceAsync("Location",location)));
        Assert.Single(overview.Sections["residents"].Items);
        Assert.Equal(13,overview.Sections.Count);
    }

    private static async Task<int> CreateContinuity(TestVault vault, string name) =>
        int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), name, "UTC"))).ResourceKey!);

    private static async Task<int> CreateEntity(TestVault vault, int continuity, CanonEntityType type, string name) =>
        int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, type, name,
            Occurred: type == CanonEntityType.WorldEvent ? StoryDate.Unknown() : null))).ResourceKey!);

    private static async Task<int> CreateWorldEvent(TestVault vault, int continuity, string title, StoryDate occurred) =>
        int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.WorldEvent, title, Occurred: occurred))).ResourceKey!);
}
