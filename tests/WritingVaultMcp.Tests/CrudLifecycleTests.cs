using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

public sealed class CrudLifecycleTests
{
    [Fact]
    public async Task SharedWriteBoundaryRejectsInvalidIdsAndOversizedShortText()
    {
        await using var vault = await TestVault.CreateAsync();
        var invalidId = await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(), -1, "body"));
        Assert.Equal("validation.failed", invalidId.Code);

        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Person"))).ResourceKey!);
        var oversized = await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(), character, "body", new string('x', 256)));
        Assert.Equal("validation.failed", oversized.Code);
        Assert.Empty((await vault.Service.GetEntityGraphAsync(character))!.Notes);
    }

    [Fact]
    public async Task SharedWriteBoundaryRejectsUndefinedEnumsNonFiniteNumbersAndUtcUnderflow()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Validation canon", "UTC"))).ResourceKey!);
        var invalidEntityType = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, (CanonEntityType)999, "Invalid"));
        Assert.Equal("validation.failed", invalidEntityType.Code);
        var invalidRecordType = await vault.Service.SoftDeleteRelationshipAsync(new(
            Guid.NewGuid().ToString(), (RelationshipRecordType)999, 1, 1));
        Assert.Equal("validation.failed", invalidRecordType.Code);

        var characterId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Subject"))).ResourceKey!);
        var sourceId = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Source"))).ResourceKey!);
        var nonFinite = await vault.Service.CreateClaimAsync(new(
            Guid.NewGuid().ToString(), continuityId, "Claim", [characterId],
            [new ClaimEvidenceInput(sourceId)], Confidence: double.NaN));
        Assert.Equal("validation.failed", nonFinite.Code);

        var utcUnderflow = new DateTimeOffset(new DateTime(100, 1, 1), TimeSpan.FromHours(14));
        var clock = await vault.Service.SetClockAsync(new(
            Guid.NewGuid().ToString(), continuityId, utcUnderflow, null, 1));
        Assert.Equal("validation.timezone", clock.Code);

        var longOriginalText = new string('x', 300);
        var longFormDate = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Long wording",
            Birth: StoryDate.Year(2000, longOriginalText)));
        Assert.True(longFormDate.Success, longFormDate.Message);
    }

    [Fact]
    public async Task MetadataLinksAndDependentRecordsHaveVersionedLifecycle()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "O'Brien 100%_[] 雪"))).ResourceKey!);
        var literalSearch = await vault.Service.SearchEntitiesAsync(CanonEntityType.Character, continuity, "%_[]");
        Assert.Single(literalSearch.Items);

        var tag = int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Draft"))).ResourceKey!);
        var patchTag = await vault.Service.PatchTagAsync(new(
            Guid.NewGuid().ToString(), tag, 1, Name: new PatchField<string>(true, "Final %_")));
        Assert.True(patchTag.Success, patchTag.Message);
        Assert.Single(await vault.Service.SearchTagsAsync("%_"));

        Assert.True((await vault.Service.LinkEntityAsync("tag", new(Guid.NewGuid().ToString(), character, tag))).Success);
        Assert.Single((await vault.Service.GetEntityGraphAsync(character))!.Tags);
        var unlink = new UnlinkEntityRequest(Guid.NewGuid().ToString(), character, tag);
        Assert.True((await vault.Service.UnlinkEntityAsync("tag", unlink)).Success);
        Assert.True((await vault.Service.UnlinkEntityAsync("tag", unlink)).Replayed);
        Assert.Empty((await vault.Service.GetEntityGraphAsync(character))!.Tags);

        var body = "Apostrophe ' and Unicode 雪 — " + new string('x', 10_000);
        var note = await vault.Service.AddNoteAsync(new(Guid.NewGuid().ToString(), character, body, "Evidence"));
        Assert.True(note.Success, note.Message);
        var noteId = int.Parse(note.ResourceKey!);
        Assert.Single((await vault.Service.GetEntityGraphAsync(character))!.Notes);
        var missingSourceLink = await vault.Service.LinkNoteSourceAsync(new(
            Guid.NewGuid().ToString(), noteId, 999_999));
        Assert.Equal("entity.not_found", missingSourceLink.Code);
        Assert.True((await vault.Service.SoftDeleteRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.EntityNote, noteId, 1))).Success);
        Assert.Empty((await vault.Service.GetEntityGraphAsync(character))!.Notes);
        Assert.True((await vault.Service.RestoreRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.EntityNote, noteId, 2))).Success);
        var restoredNote = Assert.Single((await vault.Service.GetEntityGraphAsync(character))!.Notes);
        Assert.Equal(body, restoredNote["Body"]);

        var source = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Reference", "https://example.com/fact", Citation: "p. 1",
            ArchiveUrl: "https://archive.example/fact", AuthorPublisher: "Archivist"))).ResourceKey!);
        var patchSource = await vault.Service.PatchSourceAsync(new(
            Guid.NewGuid().ToString(), source, 1,
            Title: new PatchField<string>(true, "Reference Revised"),
            CanonicalUrl: new PatchField<string>(true, null)));
        Assert.True(patchSource.Success, patchSource.Message);
        Assert.Single(await vault.Service.SearchSourcesAsync("Revised"));

        Assert.True((await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Tag, tag, 2))).Success);
        Assert.Empty(await vault.Service.SearchTagsAsync("Final"));
        Assert.Single(await vault.Service.SearchTagsAsync("Final", includeDeleted: true));
        Assert.True((await vault.Service.RestoreVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Tag, tag, 3))).Success);

        var blocked = await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Continuity, continuity, 1));
        Assert.Equal("delete.blocked", blocked.Code);
    }

    [Fact]
    public async Task DeletedContinuityCannotHaveItsClockMutated()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Disposable", "UTC"))).ResourceKey!);
        var deleted = await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Continuity, continuityId, 1));
        Assert.True(deleted.Success, deleted.Message);

        var clock = await vault.Service.SetClockAsync(new(
            Guid.NewGuid().ToString(), continuityId, DateTimeOffset.Parse("2025-01-01T00:00:00+00:00"), "UTC", 1));
        Assert.Equal("entity.not_found", clock.Code);
    }
}
