using System.Data.OleDb;
using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class SpeciesTests
{
    private static string Token() => Guid.NewGuid().ToString();

    [Fact]
    public async Task SpeciesLinksAndRaceRoundTripThroughMcpAndRespectLifecycleAndContinuity()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Species world", "UTC"))).ResourceKey!);
        var foreign = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Other world", "UTC"))).ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Species world");
        var tools = new SemanticVaultWriteTools(vault.Service, session, refs, new VaultMcpResultMapper(refs));
        var species = await tools.CreateEntity(new(Token(), CanonEntityType.Species, "Dragon", Description: "A winged species."), default);
        Assert.True(species.Success, species.Message);
        var child = await tools.CreateEntity(new(Token(), CanonEntityType.Character, "Ember", Species: species.ResourceReference, Race: "Mountain"), default);
        Assert.True(child.Success, child.Message);
        var overview = await reads.GetAsync(new(child.ResourceReference));
        Assert.Equal(species.ResourceReference, overview.Fields["species"].GetProperty("ref").GetString());
        Assert.Equal("Mountain", overview.Fields["race"].GetString());
        Assert.DoesNotContain("speciesId", overview.Fields.Keys);
        Assert.Equal(child.ResourceReference, Assert.Single((await reads.GetAsync(new(species.ResourceReference))).Sections["characters"].Items).Ref);
        Assert.False((await reads.DeletePreviewAsync(new(species.ResourceReference!))).CanDelete);
        var speciesId = (await refs.ResolveAsync(species.ResourceReference!, world, default)).Id;
        var invalid = await vault.Service.CreateEntityAsync(new(Token(), foreign, CanonEntityType.Character, "Wrong world", SpeciesId: speciesId));
        Assert.False(invalid.Success);
        var duplicate = await tools.DuplicateEntity(new(Token(), child.ResourceReference!, "Other world"), default);
        Assert.True(duplicate.Success, duplicate.Message);
        session.SelectContinuity(foreign, "Other world");
        var copied = await reads.GetAsync(new(duplicate.ResourceReference));
        Assert.False(copied.Fields.ContainsKey("species"));
        Assert.Equal("Mountain", copied.Fields["race"].GetString());
        session.SelectContinuity(world, "Species world");
        Assert.True((await tools.PatchEntity(new(Token(), child.ResourceReference!, 1, Species: new(true, null), Race: new(true, null)), default)).Success);
        Assert.Empty((await reads.GetAsync(new(species.ResourceReference))).Sections["characters"].Items);
        Assert.True((await reads.DeletePreviewAsync(new(species.ResourceReference!))).CanDelete);
        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(Token(), speciesId, 1))).Success);
        Assert.False((await tools.PatchEntity(new(Token(), child.ResourceReference!, 2, Species: new(true, species.ResourceReference)), default)).Success);
        Assert.True((await vault.Service.RestoreEntityAsync(new(Token(), speciesId, 2))).Success);
        Assert.True((await tools.PatchEntity(new(Token(), child.ResourceReference!, 2, Species: new(true, species.ResourceReference)), default)).Success);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task SpeciesSupportsSharedRecordsSearchTimelineImagesAndSnapshots()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Species Preview", "UTC"))).ResourceKey!);
        vault.EnableAutomaticPageSnapshots();
        var species = await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Species, "Dragon", Description: "Winged creatures of the high mountains."));
        Assert.True(species.Success, species.Message);
        var id = int.Parse(species.ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Species Preview");
        var reference = await refs.ReferenceAsync("Species", id);
        Assert.True((await vault.Service.AddNoteAsync(new(Token(), id, "## Habitat\nMountain caves and warm cliffs.", "Field notes"))).Success);
        var sighting = await vault.Service.AddEntityEventAsync(new(Token(), id, "First recorded sighting", StoryDate.ExactDate(new(2025, 1, 1))));
        Assert.True(sighting.Success, $"{sighting.Code}: {sighting.Message}");
        var source = int.Parse((await vault.Service.CreateSourceAsync(new(Token(), "Field guide"))).ResourceKey!);
        var tag = int.Parse((await vault.Service.CreateTagAsync(new(Token(), "Flying"))).ResourceKey!);
        var project = int.Parse((await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Project, "Mountain tales"))).ResourceKey!);
        Assert.True((await vault.Service.LinkEntityAsync("source", new(Token(), id, source))).Success);
        Assert.True((await vault.Service.LinkEntityAsync("tag", new(Token(), id, tag))).Success);
        Assert.True((await vault.Service.LinkEntityAsync("project", new(Token(), id, project))).Success);
        Assert.True((await vault.Service.CreateClaimAsync(new(Token(), world, "Dragons have wings.", [id], [new(source)]))).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Character, "Ember", SpeciesId: id, Race: "Mountain"))).Success);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, refs));
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, refs, targets, session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var attached = await images.AttachAsync(new(Token(), reference, new("image/png", Convert.ToBase64String(png.ToArray())), Caption: "Dragon illustration"));
        Assert.True(attached.Success, attached.Code);
        var overview = await reads.GetAsync(new(reference));
        foreach (var section in new[] { "characters", "notes", "events", "sources", "tags", "projects", "claims", "images" })
            Assert.Single(overview.Sections[section].Items);
        Assert.Equal(reference, Assert.Single((await reads.SearchAsync(new(Text: "Dragon", Kinds: [V4RecordKind.Species]))).Items).Ref);
        Assert.Contains((await reads.SearchAsync(new(Text: "Dragon"))).Items, item => item.Ref == reference);
        Assert.Contains((await reads.RelatedAsync(new(null, "entities"))).Items, item => item.Ref == reference);
        Assert.Contains((await reads.RelatedAsync(new(await refs.ReferenceAsync("Tag", tag), "targets"))).Items, item => item.Ref == reference);
        Assert.Contains((await reads.RelatedAsync(new(await refs.ReferenceAsync("Project", project), "members"))).Items, item => item.Ref == reference);
        Assert.Contains((await reads.TimelineAsync(new(EntityEventKinds: [V4RecordKind.Species]))).Items,
            item => item.Title == "First recorded sighting" && item.Related!.Any(link => link.Ref == reference));
        Assert.True((await vault.Service.PatchEntityAsync(new(Token(), id, 1, Name: new(true, "Mountain dragon")))).Success);
        var saved = await reads.SnapshotAsync(new(reference, 1));
        Assert.Equal("Dragon", saved.Overview.Summary.Label);
        Assert.True((await vault.Schema.VerifyAsync()).IsValid);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
        var fixture = Environment.GetEnvironmentVariable("WRITINGVAULT_SPECIES_FIXTURE");
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            Directory.CreateDirectory(fixture);
            File.Copy(vault.DatabasePath, Path.Combine(fixture, "preview.accdb"), true);
            foreach (var file in Directory.EnumerateFiles(vault.StorageRoot, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(fixture, "backup-data", Path.GetRelativePath(vault.StorageRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
            }
        }
    }

    [Fact]
    public async Task MigrationReplacesLegacyTextWithoutInventingSpeciesAndIsRepeatable()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Before species", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Character, "Original"))).ResourceKey!);
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            foreach (var sql in new[] {
                "ALTER TABLE [Characters] DROP CONSTRAINT [FK_Characters_Species]",
                "DROP INDEX [IX_Characters_Species] ON [Characters]",
                "ALTER TABLE [Characters] DROP COLUMN [SpeciesId]", "ALTER TABLE [Characters] DROP COLUMN [Race]",
                "DROP TABLE [Species]", "ALTER TABLE [Characters] ADD COLUMN [Species] TEXT(100)",
                "UPDATE [Characters] SET [Species]='Old free text'",
                $"DELETE FROM [SchemaMigrations] WHERE [MigrationId]='{AccessSchemaDefinition.MigrationId}'" })
            { using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        }
        var migrator = new AccessSchemaMigrator(vault.Factory, TimeProvider.System);
        var result = await migrator.MigrateAsync(false);
        Assert.True(result.Verification.IsValid);
        Assert.False((await migrator.MigrateAsync(false)).Changed);
        var fields = (await vault.Service.GetEntityAsync(character))!.Fields;
        Assert.False(fields.ContainsKey("Species")); Assert.Null(fields["SpeciesId"]); Assert.Null(fields["Race"]);
        Assert.Equal("Original", fields["GivenName"]);
    }
}


