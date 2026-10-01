using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Mcp.V4;
using WritingVault.Client;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase6ImageTests
{
    [Fact]
    public async Task FailedReplacementRollsBackRevisionAndNewOriginalAsset()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Rollback image", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Portrait owner"));
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Rollback image");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        var first = Png(5, 5, SKColors.Red);
        var second = Png(7, 7, SKColors.Blue);
        var attached = await images.AttachAsync(new(Guid.NewGuid().ToString(),
            "Portrait owner", new("image/png", Convert.ToBase64String(first))));
        Assert.True(attached.Success, attached.Code);
        var imageRef = Assert.Single((await images.ListAsync(new("Portrait owner"))).Items).Ref;
        var current = await images.ViewAsync(new(imageRef, V4ImageSize.Original));
        var originals = Path.Combine(vault.StorageRoot, "assets", "originals");
        var beforeFiles = Directory.GetFiles(originals, "*", SearchOption.AllDirectories);
        vault.Coordinator.ConfigurePageSnapshotCapture((_, _, _) =>
            throw new InvalidOperationException("Injected failure after image write."));

        var failed = await images.ReplaceAsync(new(Guid.NewGuid().ToString(), imageRef,
            current.View.Image.Version,
            new("image/png", Convert.ToBase64String(second))));
        Assert.False(failed.Success);
        Assert.Equal(1, (await images.ViewAsync(new(imageRef, V4ImageSize.Original)))
            .View.ContentRevision);
        Assert.Equal(first, (await images.ViewAsync(new(imageRef, V4ImageSize.Original))).Content);
        Assert.Equal(beforeFiles.Order(StringComparer.Ordinal), Directory
            .GetFiles(originals, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task PopulatedLegacyImageSurvivesAdditiveStoryOwnerMigration()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Legacy image migration", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character,
            "Portrait owner"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Legacy image migration");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),
            "Portrait owner", new("image/png", Convert.ToBase64String(Png(4, 4,
                SKColors.Orange))), Title: "Old portrait"))).Success);
        var original = Assert.Single((await images.ListAsync(new("Portrait owner"))).Items);
        var originalBytes = (await images.ViewAsync(new(original.Ref))).Content;
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                "DROP TABLE [StoryImageRenditions]",
                "DROP TABLE [StoryImages]",
                "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20261001_010_story_image_owners'"
            })
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }
        var migration = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System)
            .MigrateAsync(false);
        Assert.True(migration.Changed);
        Assert.True(migration.Verification.IsValid);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
        var surviving = Assert.Single((await images.ListAsync(new("Portrait owner"))).Items);
        Assert.Equal(original.Ref, surviving.Ref);
        Assert.Equal(originalBytes, (await images.ViewAsync(new(original.Ref))).Content);
        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(),
            entity, 1))).Success);
        var preview = await new AccessPurgeService().PreviewAsync(vault.DatabasePath,
            vault.StorageRoot, entity, 2);
        Assert.False(preview.CanPurge);
        Assert.Contains(preview.Blockers, blocker => blocker.ResourceType == "EntityImages");
    }

    [Fact]
    public async Task ContinuityImageUsesItsOwnOwnerWithoutCreatingAnEntity()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Continuity artwork", "UTC"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Continuity artwork");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        var owner = await references.ReferenceAsync("Continuity", continuity);
        var attached = await images.AttachStoryAsync(new(Guid.NewGuid().ToString(), owner,
            new("image/png", Convert.ToBase64String(Png(4, 4, SKColors.Goldenrod))),
            Title: "Cover", AltText: "Gold cover"));
        Assert.True(attached.Success, attached.Code + ": " + attached.Message);
        var imageRef = await references.ReferenceAsync("StoryImage", int.Parse(attached.ResourceKey!));
        Assert.StartsWith("story-image:", imageRef);
        Assert.Equal(imageRef, Assert.Single((await images.ListAsync(new(owner))).Items).Ref);
        Assert.Equal(imageRef, Assert.Single((await images.SearchAsync(new(Text: "Cover"))).Items).Ref);
        var viewed = await images.ViewAsync(new(imageRef));
        Assert.Equal("Gold cover", viewed.View.Image.AltText);
        Assert.NotNull(viewed.View.Image.CreatedAtUtc);
        Assert.NotNull(viewed.View.Image.UpdatedAtUtc);
        Assert.Equal("Continuity artwork", viewed.View.Image.Owner.ContinuityName);
        Assert.NotEmpty(viewed.Content);
        var originalView = await images.ViewAsync(new(imageRef, V4ImageSize.Original));
        Assert.Equal("image/png", originalView.View.MediaType);
        Assert.Equal(Png(4, 4, SKColors.Goldenrod), originalView.Content);
        var updated = await images.UpdateAsync(new(Guid.NewGuid().ToString(), imageRef,
            viewed.View.Image.Version,
            new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["title"] = System.Text.Json.JsonSerializer.SerializeToElement("Revised cover")
            }));
        Assert.True(updated.Success, updated.Code + ": " + updated.Message);
        var revised = (await images.ViewAsync(new(imageRef))).View.Image;
        Assert.Equal("Revised cover", revised.Title);
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        Assert.True((await records.LifecycleAsync(new(Guid.NewGuid().ToString(), imageRef,
            revised.Version), false)).Success);
        Assert.Empty((await images.ListAsync(new(owner))).Items);
        var deleted = Assert.Single((await images.ListAsync(new(owner,
            DeletionState: V4DeletionState.All))).Items);
        Assert.True((await records.LifecycleAsync(new(Guid.NewGuid().ToString(), imageRef,
            deleted.Version), true)).Success);
        Assert.Equal(imageRef.Split('~')[1], Assert.Single((await images.ListAsync(new(owner))).Items)
            .Ref.Split('~')[1]);
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM [EntityImages]";
        Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
        using var ownerRow = connection.CreateCommand();
        ownerRow.CommandText = "SELECT [OwnerKind],[ContinuityId] FROM [StoryImages]";
        await using var reader = await ownerRow.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Continuity", reader.GetString(0));
        Assert.Equal(continuity, reader.GetInt32(1));
        await reader.DisposeAsync();
        await connection.DisposeAsync();
        var backup = await new AccessBackupService().CreateAsync(vault.DatabasePath,
            vault.StorageRoot);
        var verified = await new AccessBackupService().VerifyAsync(backup.ManifestPath);
        Assert.Single(verified.Assets!);
        var restoredDatabase = Path.Combine(vault.Directory, "restored.accdb");
        var restoredAssets = Path.Combine(vault.Directory, "restored-assets");
        await new AccessBackupService().RestoreToNewPathsAsync(backup.ManifestPath,
            restoredDatabase, Path.Combine(restoredAssets, "assets"));
        var restoredFactory = new McpVaultReadClientFactory(new(TestServer.AssemblyPath,
            restoredDatabase, restoredAssets));
        await using var restoredClient = await restoredFactory.ConnectAsync("restored image reader");
        await restoredClient.SetSessionAsync(new("Continuity artwork"));
        var restoredImage = Assert.Single((await restoredClient.ImageListAsync(owner)).Items);
        Assert.Equal(imageRef.Split('~')[1], restoredImage.Ref.Split('~')[1]);
        Assert.Equal(restoredImage.Ref, Assert.Single((await restoredClient.ImageSearchAsync(
            "Revised", acrossContinuities: true)).Items).Ref);
        Assert.NotEmpty((await restoredClient.ImageViewAsync(imageRef)).Bytes);
        Assert.Equal("image/png", (await restoredClient.ImageViewAsync(imageRef,
            "Original")).MediaType);
    }

    [Fact]
    public async Task RelationshipAndBothEventKindsOwnReadableImages()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Story artwork", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            StoryDate.Unknown()))).ResourceKey!);
        var entityEvent = int.Parse((await vault.Service.AddEntityEventAsync(new(
            Guid.NewGuid().ToString(), first, "Arrival", StoryDate.Unknown()))).ResourceKey!);
        var source = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Scene archive"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Story artwork");
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(
            vault.Factory, vault.Coordinator, references));
        var application = new AccessV4ApplicationService(vault.Coordinator,
            references, targets, vault.Service);
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", relationship);
        var eventCreated = await application.RecordRelationshipEventAsync(new(
            Guid.NewGuid().ToString(), relationshipRef, "Reunited",
            new(V4StoryDateKind.Unknown)), continuity, "test");
        Assert.True(eventCreated.Success, eventCreated.Code + ": " + eventCreated.Message);
        var owners = new[]
        {
            relationshipRef,
            await references.ReferenceAsync("EntityEvent", entityEvent),
            await references.ReferenceAsync("RelationshipEvent", int.Parse(eventCreated.ResourceKey!))
        };
        var sourceRef = await references.ReferenceAsync("Source", source);
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator,
            references, targets, session, reads, vault.Cursors, vault.StorageRoot,
            vault.DatabasePath);
        var records = new AccessV4RecordService(vault.Coordinator, references, session,
            vault.Service);
        var changeCursor = (await reads.ChangesSinceAsync(new())).Cursor;
        foreach (var (owner, index) in owners.Select((value, index) => (value, index)))
        {
            var attached = await images.AttachStoryAsync(new(Guid.NewGuid().ToString(), owner,
                new("image/png", Convert.ToBase64String(Png(4, 4,
                    new SKColor((byte)(index * 60), 80, 150)))),
                Title: "Scene " + index, Source: index == 0 ? sourceRef : null));
            Assert.True(attached.Success, attached.Code + ": " + attached.Message);
            var image = Assert.Single((await images.ListAsync(new(owner))).Items);
            Assert.Equal(owner.Split('~')[1], image.Owner.Ref.Split('~')[1]);
            var viewed = await images.ViewAsync(new(image.Ref));
            Assert.NotEmpty(viewed.Content);
            if (index == 0) Assert.Equal(sourceRef, viewed.View.Image.Source?.Ref);
            var overview = await reads.GetAsync(new(image.Ref));
            Assert.Equal(V4RecordKind.Image, overview.Summary.Kind);
            Assert.Equal(owner, Assert.Single(overview.Sections["owner"].Items).Ref);
            Assert.DoesNotContain("relativePath", System.Text.Json.JsonSerializer.Serialize(overview),
                StringComparison.OrdinalIgnoreCase);
            var deleted = await records.LifecycleAsync(new(Guid.NewGuid().ToString(),
                image.Ref, image.Version), false);
            Assert.True(deleted.Success, deleted.Code + ": " + deleted.Message);
            Assert.Empty((await images.ListAsync(new(owner))).Items);
            var preserved = Assert.Single((await images.ListAsync(new(owner,
                DeletionState: V4DeletionState.All))).Items);
            Assert.True(preserved.IsDeleted);
            var restored = await records.LifecycleAsync(new(Guid.NewGuid().ToString(),
                image.Ref, preserved.Version), true);
            Assert.True(restored.Success, restored.Code + ": " + restored.Message);
            Assert.Equal(image.Ref, Assert.Single((await images.ListAsync(new(owner))).Items).Ref);
        }
        Assert.Contains((await reads.ChangesSinceAsync(new(changeCursor))).Changes,
            change => change.Kind == "StoryImage");
        Assert.Single((await reads.GetAsync(new(sourceRef))).Sections["images"].Items);
        Assert.Equal(3, (await reads.SearchAsync(new(Kinds: [V4RecordKind.Image]))).Items.Count);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task MixedOwnerImageSearchPagesAcrossContinuitiesWithoutChangingSession()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "First image world", "UTC"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Second image world", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), first,
            CanonEntityType.Character, "Portrait owner"));
        var (reads, session, references) = vault.V4();
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        session.SelectContinuity(first, "First image world");
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(), "Portrait owner",
            new("image/png", Convert.ToBase64String(Png(4, 4, SKColors.Red))),
            Title: "First portrait"))).Success);
        session.SelectContinuity(second, "Second image world");
        var secondRef = await references.ReferenceAsync("Continuity", second);
        Assert.True((await images.AttachStoryAsync(new(Guid.NewGuid().ToString(), secondRef,
            new("image/png", Convert.ToBase64String(Png(4, 4, SKColors.Blue))),
            Title: "Second portrait"))).Success);
        Assert.Single((await images.SearchAsync(new())).Items);
        var firstPage = await images.SearchAsync(new(Limit: 1, AcrossContinuities: true));
        Assert.Single(firstPage.Items);
        Assert.True(firstPage.HasMore);
        var secondPage = await images.SearchAsync(new(Limit: 1,
            Cursor: firstPage.NextCursor, AcrossContinuities: true));
        Assert.Single(secondPage.Items);
        Assert.False(secondPage.HasMore);
        Assert.Equal(new[] { "First image world", "Second image world" },
            firstPage.Items.Concat(secondPage.Items).Select(item => item.Owner.ContinuityName));
        Assert.Equal(second, session.RequireContinuityId());
        var storyImage = secondPage.Items.Single();
        session.SelectContinuity(first, "First image world");
        Assert.Equal("Second image world", (await images.ViewAsync(new(storyImage.Ref)))
            .View.Image.Owner.ContinuityName);
        var crossOwner = await images.AttachStoryAsync(new(Guid.NewGuid().ToString(), secondRef,
            new("image/png", Convert.ToBase64String(Png(4, 4, SKColors.Blue)))));
        Assert.False(crossOwner.Success);
        Assert.Equal(first, session.RequireContinuityId());
    }

    [Fact]
    public async Task DeletedStoryOwnerHidesItsImagesUntilRestored()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Deleted image owner", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            StoryDate.Unknown()))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Deleted image owner");
        var owner = await references.ReferenceAsync("CharacterRelationship", relationship);
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        Assert.True((await images.AttachStoryAsync(new(Guid.NewGuid().ToString(), owner,
            new("image/png", Convert.ToBase64String(Png(4, 4, SKColors.Gold))),
            Title: "Friends"))).Success);
        var image = Assert.Single((await images.ListAsync(new(owner))).Items);
        var records = new AccessV4RecordService(vault.Coordinator, references, session,
            vault.Service);
        var ownerVersion = (await reads.GetAsync(new(owner))).Summary.Version!.Value;
        var deleted = await records.LifecycleAsync(new(Guid.NewGuid().ToString(), owner,
            ownerVersion), false);
        Assert.True(deleted.Success, deleted.Code + ": " + deleted.Message);
        await Assert.ThrowsAsync<V4ResolutionException>(() => images.ViewAsync(new(image.Ref)));
        Assert.Empty((await images.SearchAsync(new())).Items);
        Assert.Empty((await reads.SearchAsync(new(Kinds: [V4RecordKind.Image]))).Items);
        var restored = await records.LifecycleAsync(new(Guid.NewGuid().ToString(), owner,
            deleted.Version!.Value), true);
        Assert.True(restored.Success, restored.Code + ": " + restored.Message);
        Assert.Equal(image.Ref, Assert.Single((await images.SearchAsync(new())).Items).Ref);
    }

    [Fact]
    public async Task ExplicitImageReferenceCanBeReadAcrossContinuitiesWithoutChangingSession()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Image origin", "UTC"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Note destination", "UTC"))).ResourceKey!);
        var owner = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), first,
            CanonEntityType.Character, "Cross-continuity portrait owner"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        session.SelectContinuity(first, "Image origin");
        var attached = await images.AttachAsync(new(Guid.NewGuid().ToString(), "Cross-continuity portrait owner",
            new("image/png", Convert.ToBase64String(Png(4, 4, SKColors.Green))), AltText: "Green portrait"));
        Assert.True(attached.Success, attached.Code + ": " + attached.Message);
        var imageRef = Assert.Single((await images.ListAsync(new("Cross-continuity portrait owner"))).Items).Ref;

        session.SelectContinuity(second, "Note destination");
        var viewed = await images.ViewAsync(new(imageRef));
        Assert.Equal("Cross-continuity portrait owner", viewed.View.Image.Owner.Label);
        Assert.Equal("Image origin", viewed.View.Image.Owner.ContinuityName);
        Assert.Equal(second, session.RequireContinuityId());
        await Assert.ThrowsAsync<V4ResolutionException>(() => images.ListAsync(new("Cross-continuity portrait owner")));
        Assert.Empty((await images.SearchAsync(new())).Items);
        var globallyFound = Assert.Single((await images.SearchAsync(new(Text: "portrait", AcrossContinuities: true))).Items);
        Assert.Equal(imageRef, globallyFound.Ref);
        Assert.Equal("Image origin", globallyFound.Owner.ContinuityName);
        Assert.Equal(second, session.RequireContinuityId());

        Directory.CreateDirectory(vault.StorageRoot);
        var factory = new McpVaultReadClientFactory(new(TestServer.AssemblyPath, vault.DatabasePath, vault.StorageRoot));
        await using var client = await factory.ConnectAsync("Cross-continuity image reader");
        await client.SetSessionAsync(new("Note destination"));
        var clientView = await client.ImageViewAsync(imageRef);
        Assert.Equal("Green portrait", clientView.Image.AltText);
        Assert.Equal("Image origin", clientView.Image.Owner.ContinuityName);
        Assert.Equal("Note destination", (await client.GetSessionAsync()).ContinuityName);

        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), owner, 1))).Success);
        await Assert.ThrowsAsync<V4ResolutionException>(() => images.ViewAsync(new(imageRef)));
    }

    [Fact]
    public async Task DeletedOwnerCanShowAnEmptyGalleryWithoutBreakingItsRecordPage()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Deleted gallery", "UTC"))).ResourceKey!);
        var owner = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Retired character"))).ResourceKey!);
        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), owner, 1))).Success);

        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Deleted gallery");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        await Assert.ThrowsAnyAsync<Exception>(() => images.ListAsync(new("Retired character")));
        Assert.Empty((await images.ListAsync(new("Retired character", DeletionState: V4DeletionState.All))).Items);

        Directory.CreateDirectory(vault.StorageRoot);
        var factory = new McpVaultReadClientFactory(new(TestServer.AssemblyPath, vault.DatabasePath, vault.StorageRoot));
        await using var client = await factory.ConnectAsync("Deleted gallery reader");
        await client.SetSessionAsync(new("Deleted gallery"));
        Assert.Empty((await client.ImageListAsync("Retired character", includeDeleted: true)).Items);
    }

    [Fact]
    public async Task AttachCreatesBoundedRenditionsAndOrdinaryReadsReturnMetadataOnly()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Images", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Portrait Owner"));
        var (reads, session, references) = vault.V4(); session.SelectContinuity(continuity, "Images");
        var service = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        var png = Png(80, 40, new SKColor(255, 0, 0, 120));

        var attached = await service.AttachAsync(new(Guid.NewGuid().ToString(), "Portrait Owner",
            new("image/png", Convert.ToBase64String(png)), Title: "Portrait", AltText: "Red portrait", IsPrimary: true));
        Assert.True(attached.Success, attached.Code + ": " + attached.Message);
        var listed = await service.ListAsync(new("Portrait Owner"));
        var image = Assert.Single(listed.Items);
        Assert.True(image.IsPrimary);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(png.Length, image.ByteCount);
        var viewed = await service.ViewAsync(new(image.Ref));
        Assert.Equal("image/jpeg", viewed.View.MediaType);
        Assert.InRange(viewed.Content.Length, 1, V4ContractLimits.DisplayImageMaximumBytes);
        Assert.DoesNotContain(Convert.ToBase64String(png), System.Text.Json.JsonSerializer.Serialize(listed));
        var factory=new McpVaultReadClientFactory(new(TestServer.AssemblyPath,vault.DatabasePath,vault.StorageRoot));
        await using var client=await factory.ConnectAsync("Phase 9 image reader");
        await client.SetSessionAsync(new("Images"));
        var clientImage=Assert.Single((await client.ImageListAsync("Portrait Owner")).Items);
        var clientView=await client.ImageViewAsync(clientImage.Ref);
        Assert.Equal("image/jpeg",clientView.MediaType);
        Assert.Equal(clientView.ByteCount,clientView.Bytes.LongLength);
    }

    [Fact]
    public async Task RejectsMalformedAndMediaTypeMismatchBeforeDatabaseWrite()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Bad Images", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Object"));
        var (reads, session, references) = vault.V4(); session.SelectContinuity(continuity, "Bad Images");
        var service = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)), session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        var mismatch = await service.AttachAsync(new(Guid.NewGuid().ToString(), "Object", new("image/jpeg", Convert.ToBase64String(Png(1,1,SKColors.Blue)))));
        var malformed = await service.AttachAsync(new(Guid.NewGuid().ToString(), "Object", new("image/png", Convert.ToBase64String([1,2,3,4]))));
        Assert.False(mismatch.Success); Assert.Equal("image.type_mismatch", mismatch.Code);
        Assert.False(malformed.Success); Assert.Equal("image.malformed", malformed.Code);
        Assert.Empty((await service.ListAsync(new("Object"))).Items);
    }

    [Fact]
    public async Task PrimarySelectionAndDeletionRemainDeterministic()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Primary","UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Character,"Owner"));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Primary");var targets=new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references));
        var images=new AccessV4ImageService(vault.Factory,vault.Coordinator,references,targets,session,reads,vault.Cursors,vault.StorageRoot,vault.DatabasePath);
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),"Owner",new("image/png",Convert.ToBase64String(Png(4,4,SKColors.Red))),Title:"First"))).Success);
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),"Owner",new("image/png",Convert.ToBase64String(Png(4,4,SKColors.Blue))),Title:"Second",IsPrimary:true))).Success);
        var listed=await images.ListAsync(new("Owner"));var first=listed.Items.Single(x=>x.Title=="First");var second=listed.Items.Single(x=>x.Title=="Second");Assert.True(second.IsPrimary);
        Assert.True((await images.UpdateAsync(new(Guid.NewGuid().ToString(),second.Ref,second.Version,new Dictionary<string,System.Text.Json.JsonElement>{{"isPrimary",System.Text.Json.JsonSerializer.SerializeToElement(false)}}))).Success);
        listed=await images.ListAsync(new("Owner"));first=listed.Items.Single(x=>x.Title=="First");second=listed.Items.Single(x=>x.Title=="Second");Assert.True(first.IsPrimary);Assert.False(second.IsPrimary);
        var records=new AccessV4RecordService(vault.Coordinator,references,session,vault.Service);
        Assert.True((await records.LifecycleAsync(new(Guid.NewGuid().ToString(),first.Ref,first.Version),false)).Success);
        listed=await images.ListAsync(new("Owner"));Assert.True(Assert.Single(listed.Items).IsPrimary);
    }

    [Fact]
    public async Task DuplicateContentIsRejectedAndFirstImageBecomesPrimary()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Dedup","UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Object,"Owner"));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Dedup");var images=new AccessV4ImageService(vault.Factory,vault.Coordinator,references,new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads,vault.Cursors,vault.StorageRoot,vault.DatabasePath);
        var payload=Convert.ToBase64String(Png(3,3,SKColors.Green));Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),"Owner",new("image/png",payload)))).Success);
        var duplicate=await images.AttachAsync(new(Guid.NewGuid().ToString(),"Owner",new("image/png",payload)));
        Assert.False(duplicate.Success);Assert.Equal("image.duplicate",duplicate.Code);Assert.True(Assert.Single((await images.ListAsync(new("Owner"))).Items).IsPrimary);
    }

    [Fact]
    public async Task FilteredSearchIsPagedInStorageAndTextLengthsAreRejected()
    {
        await using var vault=await TestVault.CreateAsync();var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Search Images","UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Character,"Alice"));
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Object,"Relic"));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Search Images");var images=new AccessV4ImageService(vault.Factory,vault.Coordinator,references,new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads,vault.Cursors,vault.StorageRoot,vault.DatabasePath);
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),"Alice",new("image/png",Convert.ToBase64String(Png(3,3,SKColors.Red))),Title:"Hero portrait",Role:"portrait"))).Success);
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),"Relic",new("image/png",Convert.ToBase64String(Png(3,3,SKColors.Blue))),Title:"Blue relic",Role:"prop"))).Success);
        var found=await images.SearchAsync(new(Text:"portrait",EntityKinds:[V4CanonEntityKind.Character],Roles:["portrait"],Limit:1));
        Assert.Single(found.Items);Assert.Equal("Alice",found.Items[0].Owner.Label);Assert.False(found.HasMore);
        var tooLong=await images.AttachAsync(new(Guid.NewGuid().ToString(),"Alice",new("image/png",Convert.ToBase64String(Png(1,1,SKColors.Black))),Title:new string('x',256)));
        Assert.False(tooLong.Success);Assert.Equal("validation.text_too_long",tooLong.Code);
    }

    [Fact]
    public async Task RecordLocatorFindsCrossContinuityLinksWithoutChangingTheSession()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "First story", "UTC"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Second story", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), second, CanonEntityType.Character, "Sam"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(first, "First story");
        var reference = await references.ReferenceAsync("Character", character);
        var located = await reads.LocateAsync(new(reference));
        Assert.Equal(reference, located.Ref);
        Assert.Equal("Second story", located.ContinuityName);
        Assert.Equal("First story", session.ContinuityName);
        await Assert.ThrowsAsync<V4ResolutionException>(() =>
            reads.GetAsync(new(reference)));
    }

    [Fact]
    public async Task ReplacingImagePreservesPinnedOriginalAndRenditionsAcrossBackup()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Image revisions", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Image owner"));
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Image revisions");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        var first = Png(5, 5, SKColors.Red);
        var second = Png(7, 7, SKColors.Blue);
        var attached = await images.AttachAsync(new(Guid.NewGuid().ToString(),
            "Image owner", new("image/png", Convert.ToBase64String(first)),
            Title: "Changing portrait"));
        Assert.True(attached.Success, attached.Code + ": " + attached.Message);
        var imageRef = Assert.Single((await images.ListAsync(new("Image owner"))).Items).Ref;
        var before = await images.ViewAsync(new(imageRef, V4ImageSize.Display));
        Assert.Equal(1, before.View.ContentRevision);
        var replacementToken = Guid.NewGuid().ToString();
        var replacementRequest = new V4ImageReplaceRequest(replacementToken,
            imageRef, before.View.Image.Version,
            new("image/png", Convert.ToBase64String(second)));
        var replaced = await images.ReplaceAsync(replacementRequest);
        Assert.True(replaced.Success, replaced.Code + ": " + replaced.Message);
        Assert.True((await images.ReplaceAsync(replacementRequest)).Replayed);
        var stale = await images.ReplaceAsync(new(Guid.NewGuid().ToString(),
            imageRef, before.View.Image.Version,
            new("image/png", Convert.ToBase64String(first))));
        Assert.False(stale.Success);
        Assert.Equal("concurrency.conflict", stale.Code);
        var latest = await images.ViewAsync(new(imageRef, V4ImageSize.Original));
        Assert.Equal(2, latest.View.ContentRevision);
        Assert.Equal(second, latest.Content);
        var pinned = await images.ViewAsync(new(imageRef, V4ImageSize.Original, 1));
        Assert.False(pinned.View.IsCurrentContent);
        Assert.Equal(first, pinned.Content);
        Assert.Equal(before.Content,
            (await images.ViewAsync(new(imageRef, V4ImageSize.Display, 1))).Content);
        var history = await images.RevisionHistoryAsync(new(imageRef));
        Assert.Equal(2, history.CurrentRevision);
        Assert.Equal([2, 1], history.Revisions.Select(item => item.Revision));
        Assert.False(history.HasMore);
        var firstPage = await images.RevisionHistoryAsync(new(imageRef, Limit: 1));
        Assert.True(firstPage.HasMore);
        Assert.Equal(2, firstPage.NextBeforeRevision);
        var olderPage = await images.RevisionHistoryAsync(new(imageRef,
            BeforeRevision: firstPage.NextBeforeRevision, Limit: 1));
        Assert.Equal(1, Assert.Single(olderPage.Revisions).Revision);
        Assert.False(olderPage.HasMore);
        var metadata = (await images.ViewAsync(new(imageRef))).View.Image;
        var metadataEdit = await images.UpdateAsync(new(Guid.NewGuid().ToString(),
            imageRef, metadata.Version,
            new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["title"] = System.Text.Json.JsonSerializer.SerializeToElement(
                    "Updated portrait title")
            }));
        Assert.True(metadataEdit.Success, metadataEdit.Code + ": " + metadataEdit.Message);
        Assert.Equal(2, (await images.ViewAsync(new(imageRef))).View.ContentRevision);
        Assert.Equal(first, (await images.ViewAsync(new(imageRef,
            V4ImageSize.Original, 1))).Content);
        var records = new AccessV4RecordService(vault.Coordinator,
            references, session, vault.Service);
        var currentMetadata = (await images.ViewAsync(new(imageRef))).View.Image;
        Assert.True((await records.LifecycleAsync(new(Guid.NewGuid().ToString(),
            imageRef, currentMetadata.Version), false)).Success);
        Assert.True((await images.ReplaceAsync(replacementRequest)).Replayed);
        var replaceDeleted = await images.ReplaceAsync(new(Guid.NewGuid().ToString(),
            imageRef, currentMetadata.Version + 1,
            new("image/png", Convert.ToBase64String(first))));
        Assert.False(replaceDeleted.Success);
        await Assert.ThrowsAsync<V4ResolutionException>(() => images.ViewAsync(new(
            imageRef, V4ImageSize.Original, 1)));
        var deleted = Assert.Single((await images.ListAsync(new("Image owner",
            DeletionState: V4DeletionState.All))).Items);
        Assert.True((await records.LifecycleAsync(new(Guid.NewGuid().ToString(),
            imageRef, deleted.Version), true)).Success);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
        var invalid = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            images.ViewAsync(new(imageRef, V4ImageSize.Display, 3)));
        Assert.Equal("image.revision_missing", invalid.Code);
        var backup = await new AccessBackupService().CreateAsync(vault.DatabasePath,
            vault.StorageRoot);
        var verified = await new AccessBackupService().VerifyAsync(backup.ManifestPath);
        Assert.Equal(2, verified.Assets!.Count);
        var restoredDatabase = Path.Combine(vault.Directory, "revision-restored.accdb");
        var restoredAssets = Path.Combine(vault.Directory, "revision-restored-assets");
        await new AccessBackupService().RestoreToNewPathsAsync(backup.ManifestPath,
            restoredDatabase, Path.Combine(restoredAssets, "assets"));
        var restoredFactory = new McpVaultReadClientFactory(new(TestServer.AssemblyPath,
            restoredDatabase, restoredAssets));
        await using var restoredClient = await restoredFactory.ConnectAsync("historical image reader");
        await restoredClient.SetSessionAsync(new("Image revisions"));
        Assert.Equal(first, (await restoredClient.ImageViewAsync(imageRef,
            "Original", 1)).Bytes);
        Assert.Equal(second, (await restoredClient.ImageViewAsync(imageRef,
            "Original")).Bytes);
        var tampered = before.Content.ToArray();
        tampered[^1] ^= 1;
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [ImageHistoricalContent] SET [DisplayContent]=? " +
                "WHERE [ImageKind]='EntityImage' AND [ContentRevision]=1";
            command.Parameters.Add("p0", System.Data.OleDb.OleDbType.LongVarBinary,
                tampered.Length).Value = tampered;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Contains((await vault.Integrity.VerifyAsync()).Issues,
            issue => issue.Code == "integrity.image_revision_corrupt");
        var corruption = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            images.ViewAsync(new(imageRef, V4ImageSize.Display, 1)));
        Assert.Equal("image.revision_corrupt", corruption.Code);
    }

    [Fact]
    public async Task ReplacingTransparentImageCanChangeOnlyItsRenderedBackground()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Transparent revisions", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Transparent owner"));
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Transparent revisions");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        var png = Png(8, 8, new SKColor(255, 0, 0, 120));
        var input = new V4InlineImageInput("image/png", Convert.ToBase64String(png));
        Assert.True((await images.AttachAsync(new(Guid.NewGuid().ToString(),
            "Transparent owner", input, BackgroundColor: "#FFFFFF"))).Success);
        var imageRef = Assert.Single((await images.ListAsync(new("Transparent owner"))).Items).Ref;
        var first = await images.ViewAsync(new(imageRef, V4ImageSize.Display));
        var changed = await images.ReplaceAsync(new(Guid.NewGuid().ToString(),
            imageRef, first.View.Image.Version, input, "#000000"));
        Assert.True(changed.Success, changed.Code + ": " + changed.Message);
        var second = await images.ViewAsync(new(imageRef, V4ImageSize.Display));
        Assert.Equal(2, second.View.ContentRevision);
        Assert.NotEqual(first.Content, second.Content);
        Assert.Equal(first.Content,
            (await images.ViewAsync(new(imageRef, V4ImageSize.Display, 1))).Content);
        var redundant = await images.ReplaceAsync(new(Guid.NewGuid().ToString(),
            imageRef, second.View.Image.Version, input, "#000000"));
        Assert.False(redundant.Success);
        Assert.Equal("image.same_content", redundant.Code);
    }

    [Fact]
    public async Task CrossContinuityImageReplacementInvalidatesTheViewingContinuity()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Note continuity", "UTC"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Image continuity", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), second,
            CanonEntityType.Character, "Portrait owner"));
        var (reads, session, references) = vault.V4();
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        session.SelectContinuity(first, "Note continuity");
        var cursor = (await reads.ChangesSinceAsync(new())).Cursor;
        session.SelectContinuity(second, "Image continuity");
        var attached = await images.AttachAsync(new(Guid.NewGuid().ToString(),
            "Portrait owner", new("image/png", Convert.ToBase64String(
                Png(3, 3, SKColors.Magenta)))));
        Assert.True(attached.Success, attached.Code + ": " + attached.Message);
        var imageRef = Assert.Single((await images.ListAsync(new("Portrait owner"))).Items).Ref;
        var image = await images.ViewAsync(new(imageRef));
        var replaced = await images.ReplaceAsync(new(Guid.NewGuid().ToString(),
            imageRef, image.View.Image.Version,
            new("image/png", Convert.ToBase64String(Png(3, 3, SKColors.Cyan)))));
        Assert.True(replaced.Success, replaced.Code + ": " + replaced.Message);
        session.SelectContinuity(first, "Note continuity");
        var changes = await reads.ChangesSinceAsync(new(cursor));
        Assert.Contains(changes.Changes, change => change.Kind == "EntityImage" &&
            change.Ref == imageRef && change.Scope == "vault-global");
    }

    [Fact]
    public async Task StoryImageReplacementRetainsItsEarlierContent()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Story image revisions", "UTC"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Story image revisions");
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,
                vault.Coordinator, references)), session, reads, vault.Cursors,
            vault.StorageRoot, vault.DatabasePath);
        var owner = await references.ReferenceAsync("Continuity", continuity);
        var oldBytes = Png(3, 3, SKColors.Orange);
        var newBytes = Png(4, 4, SKColors.Green);
        var attached = await images.AttachStoryAsync(new(Guid.NewGuid().ToString(),
            owner, new("image/png", Convert.ToBase64String(oldBytes))));
        Assert.True(attached.Success, attached.Code + ": " + attached.Message);
        var imageRef = Assert.Single((await images.ListAsync(new(owner))).Items).Ref;
        var version = (await images.ViewAsync(new(imageRef))).View.Image.Version;
        var changed = await images.ReplaceAsync(new(Guid.NewGuid().ToString(),
            imageRef, version, new("image/png", Convert.ToBase64String(newBytes))));
        Assert.True(changed.Success, changed.Code + ": " + changed.Message);
        Assert.Equal(oldBytes, (await images.ViewAsync(new(imageRef,
            V4ImageSize.Original, 1))).Content);
        Assert.Equal(newBytes, (await images.ViewAsync(new(imageRef,
            V4ImageSize.Original))).Content);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    private static byte[] Png(int width,int height,SKColor color)
    {
        using var bitmap=new SKBitmap(width,height); using(var canvas=new SKCanvas(bitmap)){canvas.Clear(color);canvas.Flush();}
        using var image=SKImage.FromBitmap(bitmap); using var data=image.Encode(SKEncodedImageFormat.Png,100); return data.ToArray();
    }
}
