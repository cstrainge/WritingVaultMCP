using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase9PreviewFixtureTests
{
    [Fact]
    public async Task BuildDisposableRepresentativeRecordPages()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),
            "Lostville Preview","America/Vancouver","A town with odd history and ordinary paperwork."))).ResourceKey!);
        var chloe=await Entity(CanonEntityType.Character,"Chloë Bell");
        var mara=await Entity(CanonEntityType.Character,"Mara Vale");
        var project=await Entity(CanonEntityType.Project,"The Long September");
        var world=await Entity(CanonEntityType.WorldEvent,"The Night the Clocks Paused",StoryDate.ExactDate(new DateOnly(2021,9,8)),narrative:1);
        var district=await Entity(CanonEntityType.Location,"The Lantern District");
        await Entity(CanonEntityType.Organization,"Lostville Historical Society");
        await Entity(CanonEntityType.Object,"The Brass Key");
        await Entity(CanonEntityType.WorldEvent,"The fog week",new StoryDate(
            StoryDateKind.UncertainRange,new DateTime(2021,9,3),new DateTime(2021,9,7),true,true,
            "sometime during the foggy first week"),narrative:3);
        await Entity(CanonEntityType.WorldEvent,"Rumors before the pause",new StoryDate(
            StoryDateKind.Before,null,new DateTime(2021,9,1),false,false),narrative:2);
        await Entity(CanonEntityType.WorldEvent,"What followed",new StoryDate(
            StoryDateKind.After,new DateTime(2021,9,10),null,true,false));
        await Entity(CanonEntityType.WorldEvent,"An undated account",StoryDate.Unknown("no reliable date"));
        async Task<int> Entity(CanonEntityType type,string name,StoryDate? date=null,double? narrative=null)=>int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(),continuity,type,name,Occurred:date ?? (type==CanonEntityType.WorldEvent?StoryDate.Unknown():null),NarrativeOrder:narrative))).ResourceKey!);

        Assert.True((await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(),chloe,
            "## The key\nChloë kept the brass key after the **clock pause**. [Archive source](https://example.org/lostville) records the date.\n\n> This is a working note.","What she remembers"))).Success);
        Assert.True((await vault.Service.AddEntityEventAsync(new(Guid.NewGuid().ToString(),chloe,"Found the brass key",
            StoryDate.ExactDate(new DateOnly(2021,9,9)),world,"The morning after the clocks paused."))).Success);
        Assert.True((await vault.Service.CreateRelationshipTypeAsync(new(Guid.NewGuid().ToString(),"friend of",false))).Success);
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Lostville Preview");
        var targets=new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references));
        var app=new AccessV4ApplicationService(vault.Coordinator,references,targets,vault.Service);
        Assert.True((await app.AddNoteAsync(new(Guid.NewGuid().ToString(),continuity,"Lostville Preview",
            "# Time in Lostville\nThe town has **one shared continuity clock**. Local clocks can still disagree.","Timeline rule"))).Success);
        var friendship=await app.CreateCharacterRelationshipAsync(new(Guid.NewGuid().ToString(),continuity,
            "Chloë Bell","Mara Vale","friend of",StoryDate.Unknown()));
        Assert.True(friendship.Success);
        var friendshipRef=await references.ReferenceAsync("CharacterRelationship",int.Parse(friendship.ResourceKey!));
        var membershipRefs=(await reads.GetAsync(new(friendshipRef))).Sections["membershipPeriods"].Items
            .Select(item=>item.Ref).ToArray();
        Assert.Equal(2,membershipRefs.Length);
        var recordEdits=new AccessV4RecordService(vault.Coordinator,references,session,vault.Service);
        for(var index=0;index<membershipRefs.Length;index++)
        {
            var membershipRef=membershipRefs[index];
            var version=(await reads.GetAsync(new(membershipRef))).Summary.Version!.Value;
            var left=index==0?StoryDate.ExactDate(new DateOnly(2021,9,10)):
                new StoryDate(StoryDateKind.Month,new DateTime(2021,10,1),new DateTime(2021,11,1));
            var updated=await recordEdits.SetRelationshipMembershipTransitionsAsync(new(
                Guid.NewGuid().ToString(),membershipRef,version,
                new(V4StoryDateKind.ExactDate,Value:"2021-09-08"),
                index==0?new(V4StoryDateKind.ExactDate,Value:"2021-09-10"):
                    new(V4StoryDateKind.Month,Value:"2021-10")),
                StoryDate.ExactDate(new DateOnly(2021,9,8)),left);
            Assert.True(updated.Success,$"{updated.Code}: {updated.Message}");
        }
        Assert.True((await vault.Service.AddWorldEventParticipantAsync(new(
            Guid.NewGuid().ToString(),world,chloe,"witness"))).Success);
        Assert.True((await vault.Service.AddWorldEventLocationAsync(new(
            Guid.NewGuid().ToString(),world,district,true,"scene"))).Success);
        var projectRef=await references.ReferenceAsync("Project",project);
        var worldRef=await references.ReferenceAsync("WorldEvent",world);
        Assert.True((await app.ApplyEventProjectsAsync(new(Guid.NewGuid().ToString(),continuity,worldRef,[projectRef],true,
            "turning point","The clock pause changes the story's direction."))).Success);

        var source=int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(),"Lostville archive ledger",
            CanonicalUrl:"https://example.org/lostville",Notes:"A fictional external reference for this disposable preview."))).ResourceKey!);
        var snapshot=int.Parse((await vault.Service.AddSourceSnapshotAsync(new(Guid.NewGuid().ToString(),source,
            "Archive entry, September 8, 2021. At 22:14 the town clocks ceased for eleven minutes.","text/plain"))).ResourceKey!);
        Assert.True((await vault.Service.LinkEntityAsync("source",new(Guid.NewGuid().ToString(),chloe,source))).Success);
        Assert.True((await vault.Service.CreateClaimAsync(new(Guid.NewGuid().ToString(),continuity,
            "The town clocks stopped for eleven minutes.",[chloe],[new(source,snapshot)]))).Success);
        var tag=int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(),"Clock lore"))).ResourceKey!);
        Assert.True((await vault.Service.LinkEntityAsync("tag",new(Guid.NewGuid().ToString(),chloe,tag))).Success);
        Assert.True((await vault.Service.LinkSourceTagAsync(new(Guid.NewGuid().ToString(),source,tag))).Success);
        Assert.True((await vault.Service.PatchEntityAsync(new(Guid.NewGuid().ToString(),chloe,1,
            FamilyName:new(true,"Bell"),PreferredName:new(true,"Chloë")))).Success);
        Assert.True((await vault.Service.AddAliasAsync(new(Guid.NewGuid().ToString(),chloe,"The Clockkeeper"))).Success);
        var characterOverview=await reads.GetAsync(new(await references.ReferenceAsync("Character",chloe)));
        Assert.Equal("Chloë Bell",characterOverview.Fields["givenName"].GetString());
        Assert.Equal("Bell",characterOverview.Fields["familyName"].GetString());
        Assert.Equal("Chloë",characterOverview.Fields["preferredName"].GetString());
        Assert.Contains(characterOverview.Sections["aliases"].Items,item=>item.Label=="The Clockkeeper");
        var deletedTag=int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(),"Retired draft tag"))).ResourceKey!);
        Assert.True((await vault.Service.SoftDeleteVaultRecordAsync(new(Guid.NewGuid().ToString(),VaultRecordType.Tag,deletedTag,1))).Success);
        using(var bitmap=new SKBitmap(160,100))
        {
            using var canvas=new SKCanvas(bitmap);
            canvas.Clear(new SKColor(115,82,65));
            using var ink=new SKPaint{Color=new SKColor(235,190,130),StrokeWidth=6,IsAntialias=true};
            canvas.DrawCircle(80,50,28,ink);canvas.Flush();
            using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);
            var images=new AccessV4ImageService(vault.Factory,vault.Coordinator,references,targets,session,reads,
                vault.Cursors,vault.StorageRoot,vault.DatabasePath);
            Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),"Chloë Bell",
                new("image/png",Convert.ToBase64String(data.ToArray())),Title:"Portrait study",AltText:"A warm circle on a brown background",IsPrimary:true))).Success);
            var imageRef=Assert.Single((await images.ListAsync(new("Chloë Bell"))).Items).Ref;
            var characterRef=await references.ReferenceAsync("Character",chloe);
            var linkedNote=$"## Portrait links\n![Current portrait](vault-image:{imageRef})\n"+
                $"![First portrait](vault-image:{imageRef}?v=1){{width=50%}}\n"+
                $"[Earlier character page](vault-record:{characterRef}?v=1)";
            Assert.True((await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(),chloe,
                linkedNote,"Portrait links"))).Success);
            using var replacement=new SKBitmap(160,100);
            using(var paint=new SKCanvas(replacement))paint.Clear(new SKColor(88,116,91));
            using var replacementImage=SKImage.FromBitmap(replacement);
            using var replacementData=replacementImage.Encode(SKEncodedImageFormat.Png,100);
            var firstView=await images.ViewAsync(new(imageRef,V4ImageSize.Thumbnail));
            Assert.True((await images.ReplaceAsync(new(Guid.NewGuid().ToString(),imageRef,
                firstView.View.Image.Version,new("image/png",Convert.ToBase64String(replacementData.ToArray()))))).Success);
        }

        var output=Path.Combine(RepositoryRoot(),"artifacts","phase9-preview");
        Directory.CreateDirectory(output);
        File.Copy(vault.DatabasePath,Path.Combine(output,"WritingVault.Phase9Preview.accdb"),true);
        var backup=Path.Combine(output,"backup");Directory.CreateDirectory(backup);
        foreach(var file in Directory.GetFiles(vault.StorageRoot,"*",SearchOption.AllDirectories))
        {
            var destination=Path.Combine(backup,Path.GetRelativePath(vault.StorageRoot,file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,true);
        }
        Assert.True(File.Exists(Path.Combine(output,"WritingVault.Phase9Preview.accdb")));
    }

    private static string RepositoryRoot()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"WritingVaultMcp.csproj")))directory=directory.Parent;
        return directory?.FullName??throw new InvalidOperationException("Could not locate repository root.");
    }
}
