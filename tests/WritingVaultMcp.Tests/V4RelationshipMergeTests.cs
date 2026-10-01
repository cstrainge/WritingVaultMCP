using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4RelationshipMergeTests
{
    [Fact]
    public async Task ReviewedMergePreservesReferencesNotesAndAuditWhileMovingPeriodsAndEvents()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Merge test", "UTC"))).ResourceKey!);
        var ari = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var bo = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var source = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8)), "Original romance notes."))).ResourceKey!);
        var target = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type,
            StoryDate.ExactDate(new DateOnly(2022, 9, 8)), "Second romance notes."))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Merge test");
        var sourceRef = await references.ReferenceAsync("CharacterRelationship", source);
        var targetRef = await references.ReferenceAsync("CharacterRelationship", target);
        var app = new AccessV4ApplicationService(vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            vault.Service);
        var createdEvent = await app.RecordRelationshipEventAsync(new(
            "first-meeting", sourceRef, "First meeting",
            new(V4StoryDateKind.ExactDate, Value: "2021-09-08")), continuity, "test");
        Assert.True(createdEvent.Success, $"{createdEvent.Code}: {createdEvent.Message}");
        var oldEventRef = await references.ReferenceAsync("RelationshipEvent", int.Parse(createdEvent.ResourceKey!));
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        static string Png(SKColor color)
        {
            using var bitmap = new SKBitmap(4, 4);
            using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return Convert.ToBase64String(encoded.ToArray());
        }
        Assert.True((await images.AttachStoryAsync(new(Guid.NewGuid().ToString(),
            sourceRef, new("image/png", Png(SKColors.Red)), Title: "First romance"))).Success);
        Assert.True((await images.AttachStoryAsync(new(Guid.NewGuid().ToString(),
            targetRef, new("image/png", Png(SKColors.Blue)), Title: "Second romance"))).Success);
        Assert.True((await images.AttachStoryAsync(new(Guid.NewGuid().ToString(),
            oldEventRef, new("image/png", Png(SKColors.Green)), Title: "Meeting"))).Success);
        var sourceImageRef = Assert.Single((await images.ListAsync(new(sourceRef))).Items).Ref;
        var oldEventImageRef = Assert.Single((await images.ListAsync(new(oldEventRef))).Items).Ref;
        var claim = await vault.Service.CreateClaimAsync(new(
            Guid.NewGuid().ToString(), continuity, "Ari and Bo had a first romance.", [ari], []));
        Assert.True(claim.Success, $"{claim.Code}: {claim.Message}");
        var claimRef = await references.ReferenceAsync("Claim", int.Parse(claim.ResourceKey!));
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var link = connection.CreateCommand();
            link.CommandText = "INSERT INTO [ClaimRelationships] ([ClaimId],[RelationshipId]) VALUES (?,?)";
            link.Parameters.Add("claim", System.Data.OleDb.OleDbType.Integer).Value = int.Parse(claim.ResourceKey!);
            link.Parameters.Add("relationship", System.Data.OleDb.OleDbType.Integer).Value = source;
            Assert.Equal(1, await link.ExecuteNonQueryAsync());
        }
        var merge = new AccessV4RelationshipMergeService(vault.Factory, vault.Coordinator, references, session);
        var preview = await merge.PreviewAsync(new(sourceRef, targetRef));
        Assert.True(preview.CanMerge, string.Join("; ", preview.Conflicts));
        Assert.Equal("Original romance notes.", preview.Source.Notes);
        Assert.Equal("Second romance notes.", preview.Target.Notes);
        Assert.Equal(2, preview.Source.Periods.Count);
        Assert.Single(preview.Source.Events);
        Assert.Single(preview.Source.Images);
        Assert.Single(preview.Target.Images);
        Assert.Single(preview.Source.Claims);
        Assert.True(preview.Source.HistoryEntries > 0);
        var oldPeriodRef = preview.Source.Periods[0].Ref;
        var oldParticipantRef = (await reads.GetAsync(new(oldPeriodRef))).Sections["participant"].Items[0].Ref;

        var staleEvent = await app.RecordRelationshipEventAsync(new(
            "second-meeting", targetRef, "Second meeting",
            new(V4StoryDateKind.ExactDate, Value: "2022-09-08")), continuity, "test");
        Assert.True(staleEvent.Success);
        var stale = await merge.ApplyAsync(new("merge-stale", sourceRef, targetRef, preview.ReviewToken!));
        Assert.False(stale.Success);
        Assert.Equal("merge.preview_stale", stale.Code);
        Assert.Equal(sourceRef, (await reads.GetAsync(new(sourceRef))).Summary.Ref);

        var refreshed = await merge.PreviewAsync(new(sourceRef, targetRef));
        var periodVersion = (await reads.GetAsync(new(oldPeriodRef))).Summary.Version!.Value;
        var transitions = await new AccessV4RecordService(vault.Coordinator, references, session, vault.Service)
            .SetRelationshipMembershipTransitionsAsync(new(
                "merge-source-transitions", oldPeriodRef, periodVersion,
                new(V4StoryDateKind.ExactDate, Value: "2021-09-08"),
                new(V4StoryDateKind.Month, Value: "2021-10")),
                StoryDate.ExactDate(new DateOnly(2021, 9, 8)),
                new StoryDate(StoryDateKind.Month, new DateTime(2021, 10, 1), new DateTime(2021, 11, 1)));
        Assert.True(transitions.Success, $"{transitions.Code}: {transitions.Message}");
        var staleTransitions = await merge.ApplyAsync(new("merge-stale-transitions",
            sourceRef, targetRef, refreshed.ReviewToken!));
        Assert.False(staleTransitions.Success);
        Assert.Equal("merge.preview_stale", staleTransitions.Code);
        refreshed = await merge.PreviewAsync(new(sourceRef, targetRef));
        Assert.NotNull(refreshed.Source.Periods.Single(item => item.Ref == oldPeriodRef).Joined);
        Assert.Equal(V4StoryDateKind.Month,
            refreshed.Source.Periods.Single(item => item.Ref == oldPeriodRef).Left!.Kind);
        var beforeMerge = (await reads.GetAsync(new(targetRef))).ObservedRevision;
        var applied = await merge.ApplyAsync(new("merge-final", sourceRef, targetRef, refreshed.ReviewToken!));
        Assert.True(applied.Success, $"{applied.Code}: {applied.Message}");
        Assert.Equal(targetRef, await references.ReferenceAsync("CharacterRelationship", int.Parse(applied.ResourceKey!)));
        var replay = await merge.ApplyAsync(new("merge-final", sourceRef, targetRef, refreshed.ReviewToken!));
        Assert.True(replay.Success);
        Assert.True(replay.Replayed);

        Assert.Equal(targetRef, (await reads.GetAsync(new(sourceRef))).Summary.Ref);
        var archived = await reads.GetAsync(new(sourceRef, IncludeDeleted: true));
        Assert.True(archived.Summary.IsDeleted);
        Assert.Equal("Original romance notes.", archived.Fields["notes"].GetString());
        Assert.Equal(targetRef, Assert.Single(archived.Sections["mergedInto"].Items).Ref);
        Assert.Equal(2, archived.Sections["characters"].Items.Count);
        var survivor = await reads.GetAsync(new(targetRef));
        Assert.Equal(sourceRef, Assert.Single(survivor.Sections["mergedSources"].Items).Ref);
        Assert.Equal(4, survivor.Sections["membershipPeriods"].Items.Count);
        Assert.Equal(2, survivor.Sections["events"].Items.Count);
        Assert.Equal((await references.ResolveAsync(claimRef, continuity, default)).Id,
            (await references.ResolveAsync(Assert.Single(survivor.Sections["claims"].Items).Ref, continuity, default)).Id);
        Assert.Equal(targetRef, Assert.Single((await reads.GetAsync(new(claimRef))).Sections["targets"].Items,
            item => item.Kind == V4RecordKind.Relationship).Ref);
        Assert.Equal(targetRef, Assert.Single((await reads.GetAsync(new(oldEventRef))).Sections["relationship"].Items).Ref);
        var movedImages = (await images.ListAsync(new(targetRef))).Items;
        Assert.Equal(2, movedImages.Count);
        Assert.Single(movedImages, item => item.IsPrimary);
        Assert.Contains(movedImages, item => item.Ref == sourceImageRef);
        Assert.Equal(targetRef, (await images.ViewAsync(new(sourceImageRef))).View.Image.Owner.Ref);
        Assert.Equal(oldEventRef, (await images.ViewAsync(new(oldEventImageRef))).View.Image.Owner.Ref);
        var periodParticipant = Assert.Single((await reads.GetAsync(new(oldPeriodRef))).Sections["participant"].Items).Ref;
        Assert.Equal(targetRef, Assert.Single((await reads.GetAsync(new(oldPeriodRef)))
            .Sections["relationship"].Items).Ref);
        Assert.Equal("Month", (await reads.GetAsync(new(oldPeriodRef))).Fields["left"].GetProperty("kind").GetString());
        Assert.NotEqual(oldParticipantRef, periodParticipant);
        Assert.Equal(periodParticipant, (await reads.GetAsync(new(oldParticipantRef))).Summary.Ref);
        Assert.True((await reads.GetAsync(new(oldParticipantRef, IncludeDeleted: true))).Summary.IsDeleted);
        Assert.NotEmpty((await reads.HistoryAsync(new(Ref: sourceRef))).Items);
        Assert.NotEmpty((await reads.HistoryAsync(new(Ref: oldEventRef))).Items);
        var changed = await reads.ChangesSinceAsync(new(beforeMerge));
        Assert.Contains(changed.Changes, item => item.Ref == sourceRef);
        Assert.Contains(changed.Changes, item => item.Ref == targetRef);
        Assert.Contains(changed.Changes, item => item.Ref == oldEventRef);
        var restore = await new AccessV4RecordService(vault.Coordinator, references, session, vault.Service)
            .LifecycleAsync(new("restore-merged", sourceRef, archived.Summary.Version!.Value), true);
        Assert.False(restore.Success);
        Assert.Equal("relationship.merged", restore.Code);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task PreviewRejectsOverlappingLegacyPeriodsWithoutChangingEitherRecord()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Overlap test", "UTC"))).ResourceKey!);
        var ari = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var bo = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false, AllowsOverlappingPeriods: true))).ResourceKey!);
        var sourceResult = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type, StoryDate.Unknown(), "Ambiguous first period."));
        var targetResult = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type,
            StoryDate.ExactDate(new DateOnly(2022, 9, 8)), "Known second period."));
        Assert.True(sourceResult.Success);
        Assert.True(targetResult.Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Overlap test");
        var sourceRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(sourceResult.ResourceKey!));
        var targetRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(targetResult.ResourceKey!));
        var merge = new AccessV4RelationshipMergeService(vault.Factory, vault.Coordinator, references, session);
        var preview = await merge.PreviewAsync(new(sourceRef, targetRef));
        Assert.False(preview.CanMerge);
        Assert.Null(preview.ReviewToken);
        Assert.Contains(preview.Conflicts, conflict => conflict.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(sourceRef, (await reads.GetAsync(new(sourceRef))).Summary.Ref);
        Assert.Equal(targetRef, (await reads.GetAsync(new(targetRef))).Summary.Ref);
    }

    [Fact]
    public async Task PreviewRejectsDifferentRelationshipTypeAndDirectedRoles()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Merge roles", "UTC"))).ResourceKey!);
        var ari = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var bo = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var mentoring = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "mentors", true, "mentored by"))).ResourceKey!);
        var sibling = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "siblings", false))).ResourceKey!);
        var first = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, mentoring,
            StoryDate.ExactDate(new DateOnly(2020, 1, 1))));
        var reversed = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, bo, ari, mentoring,
            StoryDate.ExactDate(new DateOnly(2021, 1, 1))));
        var otherType = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, sibling,
            StoryDate.ExactDate(new DateOnly(2022, 1, 1))));
        Assert.True(first.Success);
        Assert.True(reversed.Success);
        Assert.True(otherType.Success);
        var (_, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Merge roles");
        var firstRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(first.ResourceKey!));
        var reversedRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(reversed.ResourceKey!));
        var otherTypeRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(otherType.ResourceKey!));
        var merge = new AccessV4RelationshipMergeService(vault.Factory, vault.Coordinator, references, session);

        var directedPreview = await merge.PreviewAsync(new(firstRef, reversedRef));
        Assert.False(directedPreview.CanMerge);
        Assert.Contains(directedPreview.Conflicts, value => value.Contains("source and target roles", StringComparison.Ordinal));
        var typePreview = await merge.PreviewAsync(new(firstRef, otherTypeRef));
        Assert.False(typePreview.CanMerge);
        Assert.Contains(typePreview.Conflicts, value => value.Contains("types differ", StringComparison.Ordinal));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }
}
