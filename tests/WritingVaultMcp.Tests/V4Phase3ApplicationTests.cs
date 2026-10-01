using System.Data.OleDb;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase3ApplicationTests
{
    [Fact]
    public async Task SharedNoteServiceSupportsContinuitiesAndEntitiesWithIdempotentTokens()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Notes world");
        _ = await CreateEntity(vault, continuity, CanonEntityType.Character, "Note owner");
        var app = CreateApplication(vault);

        var token = "note-on-continuity";
        var continuityNote = await app.AddNoteAsync(new(
            token, continuity, "Notes world", "# Rules\n\nTime is local.", "World rules"));
        var replay = await app.AddNoteAsync(new(
            token, continuity, "Notes world", "# Rules\n\nTime is local.", "World rules"));
        var entityNote = await app.AddNoteAsync(new(
            "note-on-character", continuity, "Note owner", "Character note"));

        Assert.True(continuityNote.Success);
        Assert.True(replay.Success);
        Assert.True(replay.Replayed);
        Assert.True(entityNote.Success);
        Assert.Equal(1, await Count(vault, "ContinuityNotes"));
        Assert.Equal(1, await Count(vault, "EntityNotes"));

        var mismatchedReplay = await app.AddNoteAsync(new(
            token, continuity, "Notes world", "Different body", "World rules"));
        Assert.False(mismatchedReplay.Success);
        Assert.Equal("idempotency.input_mismatch", mismatchedReplay.Code);
    }

    [Fact]
    public async Task OneServiceAddsRemovesAndRestoresProjectsForBothEventKinds()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Events world");
        var projectA = await CreateEntity(vault, continuity, CanonEntityType.Project, "Book A");
        _ = await CreateEntity(vault, continuity, CanonEntityType.Project, "Book B");
        var worldEvent = await CreateEntity(vault, continuity, CanonEntityType.WorldEvent, "The storm");
        var character = await CreateEntity(vault, continuity, CanonEntityType.Character, "Witness");
        var entityEvent = int.Parse((await vault.Service.AddEntityEventAsync(new(
            Guid.NewGuid().ToString(), character, "Saw the storm", StoryDate.ExactDate(new DateOnly(2026, 9, 28))))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var entityEventRef = await references.ReferenceAsync("EntityEvent", entityEvent);
        var app = CreateApplication(vault, references);

        var worldAdd = await app.ApplyEventProjectsAsync(new(
            "world-projects", continuity, "The storm", ["Book A", "Book B"], true, "Major"));
        var entityAdd = await app.ApplyEventProjectsAsync(new(
            "entity-event-project", continuity, entityEventRef, ["Book A"], true));
        Assert.True(worldAdd.Success);
        Assert.True(entityAdd.Success);
        Assert.Equal(2, await Count(vault, "ProjectEntities", "[MemberEntityId]=? AND [IsDeleted]=False", worldEvent));
        Assert.Equal(1, await Count(vault, "EntityEventProjects", "[EntityEventId]=? AND [IsDeleted]=False", entityEvent));

        var remove = await app.ApplyEventProjectsAsync(new(
            "world-remove", continuity, "The storm", ["Book A"], false));
        Assert.True(remove.Success);
        Assert.Equal(1, await Count(vault, "ProjectEntities", "[MemberEntityId]=? AND [IsDeleted]=False", worldEvent));

        var restore = await app.ApplyEventProjectsAsync(new(
            "world-restore", continuity, "The storm", ["Book A"], true, "Revised"));
        Assert.True(restore.Success);
        Assert.Equal(2, await Count(vault, "ProjectEntities", "[MemberEntityId]=? AND [IsDeleted]=False", worldEvent));
        Assert.Equal(1, await Count(vault, "ProjectEntities", "[MemberEntityId]=? AND [ProjectId]=" + projectA, worldEvent));
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task EventProjectBatchRejectsCrossContinuityBeforeAnyWrite()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = await CreateContinuity(vault, "First events");
        var second = await CreateContinuity(vault, "Second events");
        _ = await CreateEntity(vault, first, CanonEntityType.WorldEvent, "Shared event");
        _ = await CreateEntity(vault, first, CanonEntityType.Project, "Valid project");
        var foreignProject = await CreateEntity(vault, second, CanonEntityType.Project, "Foreign project");
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var foreignRef = await references.ReferenceAsync("Project", foreignProject);
        var app = CreateApplication(vault, references);

        var result = await app.ApplyEventProjectsAsync(new(
            "cross-continuity", first, "Shared event", ["Valid project", foreignRef], true));
        Assert.False(result.Success);
        Assert.Equal("record.not_found", result.Code);
        Assert.Equal(0, await Count(vault, "ProjectEntities"));
    }

    [Fact]
    public async Task CommonResultEnvelopeMapsStorageIdentityToSemanticReferencesOnly()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Result world");
        var character = await CreateEntity(vault, continuity, CanonEntityType.Character, "Public person");
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var mapper = new V4ResultMapper(references);

        var mapped = await mapper.MutationAsync(new VaultMutationResult(
            true, "ok", "Character", character.ToString(), 4));
        var json = JsonSerializer.Serialize(mapped);
        var affected = Assert.Single(mapped.Affected);
        Assert.StartsWith("character:", affected.Ref, StringComparison.Ordinal);
        Assert.Equal("Public person", affected.Label);
        Assert.Equal("Result world", affected.ContinuityName);
        Assert.Equal(4, affected.Version);
        Assert.DoesNotContain($"\"{character}\"", json, StringComparison.Ordinal);

        var failure = await mapper.MutationAsync(new VaultMutationResult(
            false, "concurrency.conflict", "Character", character.ToString(), 7,
            Message: "The record changed."));
        Assert.Empty(failure.Affected);
        Assert.Equal("version.conflict", failure.Error?.Code);
        Assert.Equal("version.conflict", failure.Code);
        Assert.Contains("7", Assert.Single(failure.Error!.Details!).Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(character.ToString(), JsonSerializer.Serialize(failure), StringComparison.Ordinal);

        var scopeFailure = await mapper.MutationAsync(new VaultMutationResult(
            false, "continuity.mismatch", Message: "Targets belong to different continuities."));
        Assert.Equal("scope.mismatch", scopeFailure.Code);

        var ambiguous = await mapper.MutationAsync(new VaultMutationResult(
            false, "record.ambiguous", Message: "Choose one.", Candidates:
            [new("Character", affected.Ref, "Public person", "alias")]));
        Assert.Equal(affected.Ref, Assert.Single(ambiguous.Error!.Candidates!).Ref);

        var validation = V4ResultMapper.Validation(new VaultValidationException(
            [new ValidationError("date.shape", "occurred.lower", "This field does not apply.")]));
        Assert.Equal("occurred.lower", Assert.Single(validation.Error!.Details!).Field);
    }

    [Fact]
    public async Task SemanticRelationshipAndOwnershipPathsPreserveBidirectionalAndGroupInvariants()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Invariant world");
        var parent = await CreateEntity(vault, continuity, CanonEntityType.Character, "Parent");
        var child = await CreateEntity(vault, continuity, CanonEntityType.Character, "Child");
        var objectId = await CreateEntity(vault, continuity, CanonEntityType.Object, "Keepsake");
        Assert.True((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "parent of", true, "child of"))).Success);
        var principal = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuity, PrincipalKind.Character, CharacterId: parent))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var principalRef = await references.ReferenceAsync("OwnershipPrincipal", principal);
        var app = CreateApplication(vault, references);

        var relationship = await app.CreateCharacterRelationshipAsync(new(
            "relationship-v4", continuity, "Parent", "Child", "parent of", StoryDate.Unknown()));
        Assert.True(relationship.Success, relationship.Message);
        Assert.Equal("parent of", Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(parent)).Label);
        Assert.Equal("child of", Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(child)).Label);

        var ownership = await app.AddOwnershipAsync(new(
            "ownership-v4", continuity, "Keepsake", OwnershipState.Owned,
            StoryDate.Year(2026), [new(principalRef, 1_000_000)]));
        Assert.True(ownership.Success, ownership.Message);

        var invalid = await app.AddOwnershipAsync(new(
            "ownership-invalid-v4", continuity, "Keepsake", OwnershipState.Owned,
            StoryDate.Year(2027), [new(principalRef, 900_000)]));
        Assert.False(invalid.Success);
        Assert.Equal("validation.ownership", invalid.Code);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
        Assert.Equal(1, await Count(vault, "ObjectOwnershipPeriods", "[ObjectId]=?", objectId));
    }

    [Fact]
    public async Task RelationshipNotesCanBeEditedAndClearedFromEitherCharacterWithoutLosingTheSharedLink()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Relationship notes world");
        var foreignContinuity = await CreateContinuity(vault, "Other world");
        var parent = await CreateEntity(vault, continuity, CanonEntityType.Character, "Parent");
        var child = await CreateEntity(vault, continuity, CanonEntityType.Character, "Child");
        Assert.True((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "parent of", true, "child of"))).Success);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var app = CreateApplication(vault, references);
        var created = await app.CreateCharacterRelationshipAsync(new(
            "relationship-note-create", continuity, "Parent", "Child", "parent of",
            StoryDate.Unknown(), "Initial **context**"));
        Assert.True(created.Success, created.Message);
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(created.ResourceKey!));
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Relationship notes world");
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);

        var initial = await reads.GetAsync(new(relationshipRef));
        Assert.Equal("Initial **context**", initial.Fields["notes"].GetString());
        var edit = new V4RelationshipNotesSetRequest(
            "relationship-note-edit", relationshipRef, initial.Summary.Version!.Value,
            "# Shared history\n\nThey met at the station.");
        var updated = await records.SetRelationshipNotesAsync(edit);
        Assert.True(updated.Success, updated.Message);
        Assert.True((await records.SetRelationshipNotesAsync(edit)).Replayed);
        var current = await reads.GetAsync(new(relationshipRef));
        Assert.Equal("# Shared history\n\nThey met at the station.", current.Fields["notes"].GetString());
        var parentLink = Assert.Single((await reads.RelatedAsync(new(
            await references.ReferenceAsync("Character", parent), "relationships"))).Items);
        var childLink = Assert.Single((await reads.RelatedAsync(new(
            await references.ReferenceAsync("Character", child), "relationships"))).Items);
        Assert.Equal(relationshipRef, parentLink.Ref);
        Assert.Equal(relationshipRef, childLink.Ref);

        var stale = await records.SetRelationshipNotesAsync(new(
            "relationship-note-stale", relationshipRef, initial.Summary.Version!.Value, "Stale"));
        Assert.False(stale.Success);
        Assert.Equal("concurrency.conflict", stale.Code);
        session.SelectContinuity(foreignContinuity, "Other world");
        var wrongScope = await records.SetRelationshipNotesAsync(new(
            "relationship-note-wrong-scope", relationshipRef, current.Summary.Version!.Value, "Foreign edit"));
        Assert.False(wrongScope.Success);
        session.SelectContinuity(continuity, "Relationship notes world");
        var cleared = await records.SetRelationshipNotesAsync(new(
            "relationship-note-clear", relationshipRef, current.Summary.Version!.Value, null));
        Assert.True(cleared.Success, cleared.Message);
        var afterClear = await reads.GetAsync(new(relationshipRef));
        Assert.Equal(JsonValueKind.Null, afterClear.Fields["notes"].ValueKind);
        var tooLong = await records.SetRelationshipNotesAsync(new(
            "relationship-note-too-long", relationshipRef, afterClear.Summary.Version!.Value,
            new string('x', 65_537)));
        Assert.False(tooLong.Success);
        Assert.Equal("validation.notes", tooLong.Code);

        var deleted = await records.LifecycleAsync(new(
            "relationship-note-delete", relationshipRef, afterClear.Summary.Version!.Value), false);
        Assert.True(deleted.Success, deleted.Message);
        var editDeleted = await records.SetRelationshipNotesAsync(new(
            "relationship-note-edit-deleted", relationshipRef, deleted.Version!.Value, "No"));
        Assert.False(editDeleted.Success);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task RelationshipNotesSetAlsoWorksForWorldEventParticipants()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Participant notes world");
        var worldEvent = await CreateEntity(vault, continuity, CanonEntityType.WorldEvent, "The storm");
        var witness = await CreateEntity(vault, continuity, CanonEntityType.Character, "Witness");
        var created = await vault.Service.AddWorldEventParticipantAsync(new(
            Guid.NewGuid().ToString(), worldEvent, witness, "witness"));
        Assert.True(created.Success, created.Message);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Participant notes world");
        var linkRef = await references.ReferenceAsync("WorldEventParticipant", int.Parse(created.ResourceKey!));
        var record = await reads.GetAsync(new(linkRef));
        var notes = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);

        var written = await notes.SetRelationshipNotesAsync(new(
            "participant-notes-set", linkRef, record.Summary.Version!.Value,
            "**Witnessed** from the tower."));

        Assert.True(written.Success, written.Message);
        Assert.Equal("**Witnessed** from the tower.",
            (await reads.GetAsync(new(linkRef))).Fields["notes"].GetString());
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task OwnershipRejectsMalformedEmptyInputWithoutThrowingOrWriting()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Malformed ownership");
        var objectId = await CreateEntity(vault, continuity, CanonEntityType.Object, "Unowned thing");
        var app = CreateApplication(vault);

        var result = await app.AddOwnershipAsync(new(
            "empty-owners", continuity, "Unowned thing", OwnershipState.Owned,
            StoryDate.Unknown(), []));

        Assert.False(result.Success);
        Assert.Equal("validation.ownership", result.Code);
        Assert.Equal(0, await Count(vault, "ObjectOwnershipPeriods", "[ObjectId]=?", objectId));
    }

    private static AccessV4ApplicationService CreateApplication(TestVault vault, VaultReferenceService? references = null)
    {
        references ??= new VaultReferenceService(vault.Factory, vault.Coordinator);
        var resolver = new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references);
        return new(vault.Coordinator, references, new V4TargetResolver(resolver), vault.Service);
    }

    private static async Task<int> CreateContinuity(TestVault vault, string name) =>
        int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), name, "UTC"))).ResourceKey!);

    private static async Task<int> CreateEntity(TestVault vault, int continuity, CanonEntityType type, string name) =>
        int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, type, name,
            Occurred: type == CanonEntityType.WorldEvent ? StoryDate.Unknown() : null))).ResourceKey!);

    private static async Task<int> Count(TestVault vault, string table, string? where = null, int? parameter = null)
    {
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}]" + (where is null ? string.Empty : " WHERE " + where);
        if (parameter is not null) command.Parameters.Add("?", OleDbType.Integer).Value = parameter.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
