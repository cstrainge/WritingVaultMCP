using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

public sealed class ApplicationAcceptanceTests
{
    [Fact]
    public async Task StructuredDatesBirthplaceAndNarrativeOrderAreVersionedPatchFields()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var location = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Location, "Harbor"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var patched = await vault.Service.PatchEntityAsync(new(
            Guid.NewGuid().ToString(), character, 1,
            Birth: new(true, StoryDate.Year(1987)), BirthLocationId: new(true, location),
            BirthLocationDetail: new(true, "near the old pier")));
        Assert.True(patched.Success, patched.Message);
        var details = (await vault.Service.GetEntityAsync(character))!;
        Assert.Equal("Year", details.Fields["BirthKind"]);
        Assert.Equal(new DateTime(1987, 1, 1), details.Fields["BirthLowerBound"]);
        Assert.Equal(new DateTime(1988, 1, 1), details.Fields["BirthUpperBound"]);
        Assert.Equal(location, details.Fields["BirthLocationId"]);

        var cleared = await vault.Service.PatchEntityAsync(new(
            Guid.NewGuid().ToString(), character, 2,
            Birth: new(true, null), BirthLocationId: new(true, null), BirthLocationDetail: new(true, null)));
        Assert.True(cleared.Success, cleared.Message);
        details = (await vault.Service.GetEntityAsync(character))!;
        Assert.Equal("Unknown", details.Fields["BirthKind"]);
        Assert.Null(details.Fields["BirthLocationId"]);

        var worldEvent = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, "Reveal",
            Occurred: StoryDate.Year(2000), NarrativeOrder: 10))).ResourceKey!);
        var eventPatch = await vault.Service.PatchEntityAsync(new(
            Guid.NewGuid().ToString(), worldEvent, 1,
            Occurred: new(true, StoryDate.Year(2001)), NarrativeOrder: new(true, 2.5)));
        Assert.True(eventPatch.Success, eventPatch.Message);
        var eventDetails = (await vault.Service.GetEntityAsync(worldEvent))!;
        Assert.Equal("Year", eventDetails.Fields["EventKind"]);
        Assert.Equal(2.5, eventDetails.Fields["NarrativeOrder"]);
    }

    [Fact]
    public async Task DefinitelyImpossibleBirthDeathChronologyIsRejected()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var result = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Impossible",
            Birth: StoryDate.Year(2000), Death: StoryDate.Year(1990)));
        Assert.Equal("integrity.invalid_shape", result.Code);
        Assert.Empty((await vault.Service.SearchEntitiesAsync(CanonEntityType.Character, continuity)).Items);
    }

    [Fact]
    public async Task DirectedRelationshipTypesRequireAnInverseAndUndirectedTypesShareOneLabel()
    {
        await using var vault = await TestVault.CreateAsync();
        var missingInverse = await vault.Service.CreateRelationshipTypeAsync(new(Guid.NewGuid().ToString(), "parent of", true));
        Assert.Equal("validation.name", missingInverse.Code);
        var strayInverse = await vault.Service.CreateRelationshipTypeAsync(new(Guid.NewGuid().ToString(), "sibling of", false, "sibling of"));
        Assert.Equal("validation.name", strayInverse.Code);
        Assert.True((await vault.Service.CreateRelationshipTypeAsync(new(Guid.NewGuid().ToString(), "sibling of", false))).Success);
    }

    [Fact]
    public async Task CharacterRelationshipsAreDiscoverableFromBothDirections()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var parent = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Parent"))).ResourceKey!);
        var child = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Child"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "parent of", true, "child of"))).ResourceKey!);
        var relationship = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, parent, child, type, StoryDate.Unknown()));
        Assert.True(relationship.Success, relationship.Message);

        var fromParent = Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(parent));
        Assert.Equal(child, fromParent.RelatedCharacterId);
        Assert.Equal("parent of", fromParent.Label);
        Assert.Equal("outgoing", fromParent.Perspective);

        var fromChild = Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(child));
        Assert.Equal(parent, fromChild.RelatedCharacterId);
        Assert.Equal("child of", fromChild.Label);
        Assert.Equal("incoming", fromChild.Perspective);
        Assert.Equal(fromParent.RelationshipId, fromChild.RelationshipId);

        var childGraph = await vault.Service.GetEntityGraphAsync(child);
        Assert.Contains(childGraph!.TypeSpecificRelations, relation =>
            Convert.ToInt32(relation["RelatedCharacterId"]) == parent && Convert.ToString(relation["Label"]) == "child of");
        var childDeletePreview = await vault.Service.PreviewDeleteAsync(child);
        Assert.Contains(childDeletePreview!.Blockers, item => item.ResourceType == "RelationshipTarget" && item.Count == 1);

        var relationshipId = int.Parse(relationship.ResourceKey!);
        var deleted = await vault.Service.SoftDeleteRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.CharacterRelationship, relationshipId, 1));
        Assert.True(deleted.Success, deleted.Message);
        Assert.Empty(await vault.Service.GetCharacterRelationshipsAsync(parent));
        Assert.Empty(await vault.Service.GetCharacterRelationshipsAsync(child));
        var restored = await vault.Service.RestoreRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.CharacterRelationship, relationshipId, 2));
        Assert.True(restored.Success, restored.Message);
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(parent));
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(child));
    }

    [Fact]
    public async Task UndirectedRelationshipRejectsReversedDuplicateEvenWhenOverlapsAreAllowed()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Relationship canon", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "First"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Second"))).ResourceKey!);
        var relationshipTypeId = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "knows", false, AllowsOverlappingPeriods: true))).ResourceKey!);

        var firstWrite = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuityId, first, second, relationshipTypeId, StoryDate.Year(2000)));
        Assert.True(firstWrite.Success, firstWrite.Message);
        var reversedDuplicate = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuityId, second, first, relationshipTypeId, StoryDate.Year(2000)));

        Assert.Equal("constraint.duplicate", reversedDuplicate.Code);
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(first));
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(second));
    }

    [Fact]
    public async Task VariantGroupsAndCustodyPreserveContinuityAndExplicitState()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var group = int.Parse((await vault.Service.CreateVariantGroupAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Main variants"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari", VariantGroupId: group))).ResourceKey!);
        var details = await vault.Service.GetEntityAsync(character);
        Assert.Equal(group, details!.Summary.VariantGroupId);
        Assert.Single(await vault.Service.ListVariantGroupsAsync(continuity, CanonEntityType.Character));

        var obj = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Key"))).ResourceKey!);
        var mismatched = await vault.Service.SetVariantGroupAsync(new(Guid.NewGuid().ToString(), obj, group, 1));
        Assert.False(mismatched.Success);
        Assert.Equal("variant_group.mismatch", mismatched.Code);

        var invalidCustody = await vault.Service.AddObjectCustodyAsync(new(
            Guid.NewGuid().ToString(), obj, StoryDate.Year(2020), CustodyState.Known));
        Assert.False(invalidCustody.Success);
        Assert.Equal("validation.custody", invalidCustody.Code);

        var principal = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuity, PrincipalKind.External, Label: "Museum"))).ResourceKey!);
        var custody = await vault.Service.AddObjectCustodyAsync(new(
            Guid.NewGuid().ToString(), obj, StoryDate.Year(2020), CustodyState.Known, principal));
        Assert.True(custody.Success, custody.Message);
    }

    [Fact]
    public async Task ArtificialTimeAgeIdempotencyConcurrencyAndHistoryAreSafe()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityRequest = new CreateContinuityRequest(Guid.NewGuid().ToString(), "Lostville", "America/Vancouver");
        var continuity = await vault.Service.CreateContinuityAsync(continuityRequest);
        Assert.True(continuity.Success, continuity.Message);
        var continuityId = int.Parse(continuity.ResourceKey!);
        var replay = await vault.Service.CreateContinuityAsync(continuityRequest);
        Assert.True(replay.Success);
        Assert.True(replay.Replayed);
        var reformattedReplay = await vault.Service.CreateContinuityAsync(continuityRequest with
        {
            OperationId = Guid.Parse(continuityRequest.OperationId).ToString("B").ToUpperInvariant()
        });
        Assert.True(reformattedReplay.Success);
        Assert.True(reformattedReplay.Replayed);
        var mismatch = await vault.Service.CreateContinuityAsync(continuityRequest with { Name = "Other" });
        Assert.Equal("idempotency.input_mismatch", mismatch.Code);
        var emptyOperation = await vault.Service.CreateTagAsync(new(Guid.Empty.ToString(), "Invalid operation"));
        Assert.Equal("validation.operation_id", emptyOperation.Code);

        var location = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Location, "Lostville", TimeZoneId: "America/Vancouver"));
        Assert.True(location.Success, location.Message);
        var character = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Zara",
            Birth: StoryDate.ExactDate(new DateOnly(2000, 2, 29))));
        Assert.True(character.Success, character.Message);
        var characterId = int.Parse(character.ResourceKey!);
        var residence = await vault.Service.AddResidenceAsync(new(
            Guid.NewGuid().ToString(), characterId, int.Parse(location.ResourceKey!), StoryDate.Unknown(), true));
        Assert.True(residence.Success, residence.Message);

        var clock = await vault.Service.SetClockAsync(new(
            Guid.NewGuid().ToString(), continuityId, DateTimeOffset.Parse("2025-02-28T12:00:00-08:00"), "America/Vancouver", 1));
        Assert.True(clock.Success, clock.Message);
        var local = await vault.Service.ResolveLocalCurrentTimeAsync(characterId);
        Assert.Equal("America/Vancouver", local!.TimeZoneId);
        Assert.StartsWith("location:", local.TimeZoneSource);
        var age = await vault.Service.GetCharacterAgeAsync(characterId);
        Assert.Equal(25, age!.ExactYears);
        var temporalState = await vault.Service.GetEntityTemporalStateAsync(characterId);
        Assert.NotNull(temporalState);
        Assert.Equal(new DateTime(2025, 2, 28, 12, 0, 0), temporalState!.StoryReferenceTime);
        Assert.Single(temporalState.ActiveRecords["residences"]);

        var emptyPatch = await vault.Service.PatchEntityAsync(new(Guid.NewGuid().ToString(), characterId, 1));
        Assert.Equal("patch.empty", emptyPatch.Code);
        var patch = await vault.Service.PatchEntityAsync(new(
            Guid.NewGuid().ToString(), characterId, 1,
            Description: new PatchField<string>(true, null),
            Race: new PatchField<string>(true, "Northern")));
        Assert.True(patch.Success, patch.Message);

        var deleteOne = vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), characterId, 2));
        var deleteTwo = vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), characterId, 2));
        var results = await Task.WhenAll(deleteOne, deleteTwo);
        Assert.Single(results, result => result.Success);
        Assert.Single(results, result => result.Code == "concurrency.conflict");
        var history = await vault.Service.GetHistoryAsync("CanonEntity", characterId.ToString());
        Assert.Single(history);
        var restoreOperationId = Guid.NewGuid().ToString();
        var restored = await vault.Service.RestoreEntityAsync(new(restoreOperationId, characterId, 3));
        Assert.True(restored.Success, restored.Message);
        var operationHistory = Assert.Single(await vault.Service.GetOperationHistoryAsync(restoreOperationId));
        Assert.Equal("restore", operationHistory.Action);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task CyclesCrossContinuityAndOwnershipOverlapAreRejected()
    {
        await using var vault = await TestVault.CreateAsync();
        var c1 = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "One", "UTC"))).ResourceKey!);
        var c2 = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Two", "UTC"))).ResourceKey!);
        var a = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), c1, CanonEntityType.Location, "A"))).ResourceKey!);
        var b = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), c1, CanonEntityType.Location, "B"))).ResourceKey!);
        Assert.True((await vault.Service.MoveLocationAsync(new(Guid.NewGuid().ToString(), a, b, 1))).Success);
        var cycle = await vault.Service.MoveLocationAsync(new(Guid.NewGuid().ToString(), b, a, 1));
        Assert.Equal("location.cycle", cycle.Code);

        var project = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), c1, CanonEntityType.Project, "Novel"))).ResourceKey!);
        var foreignCharacter = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), c2, CanonEntityType.Character, "Elsewhere"))).ResourceKey!);
        var cross = await vault.Service.LinkEntityAsync("project", new(Guid.NewGuid().ToString(), foreignCharacter, project));
        Assert.Equal("continuity.mismatch", cross.Code);

        var obj = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), c1, CanonEntityType.Object, "Ring"))).ResourceKey!);
        var unknown = await vault.Service.AddOwnershipPeriodAsync(new(
            Guid.NewGuid().ToString(), obj, OwnershipState.Unknown,
            new StoryDate(StoryDateKind.Range, new DateTime(2000, 1, 1), new DateTime(2010, 1, 1)), []));
        Assert.True(unknown.Success, unknown.Message);
        var overlap = await vault.Service.AddOwnershipPeriodAsync(new(
            Guid.NewGuid().ToString(), obj, OwnershipState.Unowned,
            new StoryDate(StoryDateKind.Range, new DateTime(2005, 1, 1), new DateTime(2006, 1, 1)), []));
        Assert.Equal("interval.overlap", overlap.Code);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task DeletedExclusivePeriodDoesNotInvalidateItsActiveReplacement()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Replacement canon", "UTC"))).ResourceKey!);
        var objectId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Object, "Relic"))).ResourceKey!);
        var period = new StoryDate(
            StoryDateKind.Range,
            new DateTime(2000, 1, 1),
            new DateTime(2010, 1, 1));

        var original = await vault.Service.AddOwnershipPeriodAsync(new(
            Guid.NewGuid().ToString(), objectId, OwnershipState.Unknown, period, []));
        Assert.True(original.Success, original.Message);
        var originalId = int.Parse(original.ResourceKey!);
        var deleted = await vault.Service.SoftDeleteRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.ObjectOwnershipPeriod, originalId, 1));
        Assert.True(deleted.Success, deleted.Message);

        var replacement = await vault.Service.AddOwnershipPeriodAsync(new(
            Guid.NewGuid().ToString(), objectId, OwnershipState.Unowned, period, []));
        Assert.True(replacement.Success, replacement.Message);

        var verification = await vault.Integrity.VerifyAsync();
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Issues));
    }

    [Fact]
    public async Task NamesAliasesAndMembershipsFollowTheirCanonicalPolicies()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Naming canon", "UTC"))).ResourceKey!);
        var firstCharacter = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Alex"))).ResourceKey!);
        var secondCharacter = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Alex"))).ResourceKey!);

        Assert.True((await vault.Service.AddAliasAsync(new(
            Guid.NewGuid().ToString(), firstCharacter, "  The Sparrow  "))).Success);
        var duplicateAlias = await vault.Service.AddAliasAsync(new(
            Guid.NewGuid().ToString(), firstCharacter, "the sparrow"));
        Assert.Equal("constraint.duplicate", duplicateAlias.Code);
        Assert.True((await vault.Service.AddAliasAsync(new(
            Guid.NewGuid().ToString(), secondCharacter, "THE SPARROW"))).Success);

        var organizationId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Organization, "Guild"))).ResourceKey!);
        var period = StoryDate.Year(2000);
        Assert.True((await vault.Service.AddMembershipAsync(new(
            Guid.NewGuid().ToString(), organizationId, firstCharacter, period, " Captain "))).Success);
        var sameRole = await vault.Service.AddMembershipAsync(new(
            Guid.NewGuid().ToString(), organizationId, firstCharacter, period, "captain"));
        Assert.Equal("interval.overlap", sameRole.Code);
        Assert.True((await vault.Service.AddMembershipAsync(new(
            Guid.NewGuid().ToString(), organizationId, firstCharacter, period, "Navigator"))).Success);

        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task LocalTimeUsesAncestorLocationCustodyAndContinuityFallback()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Time canon", "America/Vancouver"))).ResourceKey!);
        var parentLocation = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Location, "Parent", TimeZoneId: "America/New_York"))).ResourceKey!);
        var childLocation = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Location, "Child"))).ResourceKey!);
        Assert.True((await vault.Service.MoveLocationAsync(new(
            Guid.NewGuid().ToString(), childLocation, parentLocation, 1))).Success);
        var organizationId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Organization, "Custodian"))).ResourceKey!);
        Assert.True((await vault.Service.AddOrganizationLocationAsync(new(
            Guid.NewGuid().ToString(), organizationId, childLocation, StoryDate.Unknown(), IsPrimary: true))).Success);
        var objectId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Object, "Carried object"))).ResourceKey!);
        var principalId = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuityId, PrincipalKind.Organization, OrganizationId: organizationId))).ResourceKey!);
        Assert.True((await vault.Service.AddObjectCustodyAsync(new(
            Guid.NewGuid().ToString(), objectId, StoryDate.Unknown(), CustodyState.Known, principalId))).Success);
        var projectId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Project, "Unlocated project"))).ResourceKey!);
        Assert.True((await vault.Service.SetClockAsync(new(
            Guid.NewGuid().ToString(), continuityId,
            DateTimeOffset.Parse("2025-06-01T12:00:00-07:00"), "America/Vancouver", 1))).Success);

        var objectTime = (await vault.Service.ResolveLocalCurrentTimeAsync(objectId))!;
        Assert.Equal("America/New_York", objectTime.TimeZoneId);
        Assert.Equal(childLocation, objectTime.LocationId);
        Assert.Equal($"location:{parentLocation}", objectTime.TimeZoneSource);
        var projectTime = (await vault.Service.ResolveLocalCurrentTimeAsync(projectId))!;
        Assert.Equal("America/Vancouver", projectTime.TimeZoneId);
        Assert.Equal("continuity-default", projectTime.TimeZoneSource);
        Assert.Null(projectTime.LocationId);
    }

    [Fact]
    public async Task EventParticipationFollowsTheApprovedEntityMatrix()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Event canon", "UTC"))).ResourceKey!);
        async Task<int> Entity(CanonEntityType type, string name) => int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, type, name))).ResourceKey!);
        var worldEventId = await Entity(CanonEntityType.WorldEvent, "Storm");
        var characterId = await Entity(CanonEntityType.Character, "Witness");
        var organizationId = await Entity(CanonEntityType.Organization, "Responders");
        var objectId = await Entity(CanonEntityType.Object, "Beacon");
        var locationId = await Entity(CanonEntityType.Location, "Harbor");
        var projectId = await Entity(CanonEntityType.Project, "Novel");

        foreach (var participantId in new[] { characterId, organizationId, objectId })
            Assert.True((await vault.Service.AddWorldEventParticipantAsync(new(
                Guid.NewGuid().ToString(), worldEventId, participantId))).Success);
        Assert.Equal("world_event.unsupported_participant", (await vault.Service.AddWorldEventParticipantAsync(new(
            Guid.NewGuid().ToString(), worldEventId, locationId))).Code);
        Assert.Equal("world_event.unsupported_participant", (await vault.Service.AddWorldEventParticipantAsync(new(
            Guid.NewGuid().ToString(), worldEventId, projectId))).Code);
        Assert.True((await vault.Service.AddWorldEventLocationAsync(new(
            Guid.NewGuid().ToString(), worldEventId, locationId, true))).Success);

        foreach (var ownerId in new[] { characterId, organizationId, objectId, locationId })
            Assert.True((await vault.Service.AddEntityEventAsync(new(
                Guid.NewGuid().ToString(), ownerId, "Local effect", StoryDate.Year(2000), worldEventId))).Success);
        Assert.Equal("event.unsupported_owner", (await vault.Service.AddEntityEventAsync(new(
            Guid.NewGuid().ToString(), projectId, "Draft milestone", StoryDate.Year(2000)))).Code);
        Assert.Equal("event.unsupported_owner", (await vault.Service.AddEntityEventAsync(new(
            Guid.NewGuid().ToString(), worldEventId, "Duplicate event", StoryDate.Year(2000)))).Code);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task EveryCanonEntitySupportsItsApprovedSharedAssociations()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Association canon", "UTC"))).ResourceKey!);
        async Task<int> Entity(CanonEntityType type) => int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, type, type.ToString()))).ResourceKey!);
        var entities = new Dictionary<CanonEntityType, int>();
        foreach (var type in Enum.GetValues<CanonEntityType>()) entities[type] = await Entity(type);
        var tagId = int.Parse((await vault.Service.CreateTagAsync(new(
            Guid.NewGuid().ToString(), "Shared"))).ResourceKey!);
        var sourceId = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Shared source"))).ResourceKey!);
        var projectId = entities[CanonEntityType.Project];

        foreach (var (type, entityId) in entities)
        {
            Assert.True((await vault.Service.AddNoteAsync(new(
                Guid.NewGuid().ToString(), entityId, $"Note for {type}"))).Success);
            Assert.True((await vault.Service.LinkEntityAsync("tag", new(
                Guid.NewGuid().ToString(), entityId, tagId))).Success);
            Assert.True((await vault.Service.LinkEntityAsync("source", new(
                Guid.NewGuid().ToString(), entityId, sourceId))).Success);
            if (type != CanonEntityType.Project)
                Assert.True((await vault.Service.LinkEntityAsync("project", new(
                    Guid.NewGuid().ToString(), entityId, projectId, type.ToString(), "assignment"))).Success);
        }

        var claim = await vault.Service.CreateClaimAsync(new(
            Guid.NewGuid().ToString(), continuityId, "Shared claim",
            entities.Values.ToArray(), [new ClaimEvidenceInput(sourceId, Locator: "section 1")]));
        Assert.True(claim.Success, claim.Message);
        foreach (var entityId in entities.Values)
        {
            var graph = (await vault.Service.GetEntityGraphAsync(entityId))!;
            Assert.Single(graph.Notes);
            Assert.Single(graph.Tags);
            Assert.Single(graph.Sources);
            Assert.Single(graph.Claims);
        }
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task TemporalTransitionsClosePriorRowsAndOpenReplacementsAtomically()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Transition canon", "UTC"))).ResourceKey!);
        async Task<int> Entity(CanonEntityType type, string name) => int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, type, name))).ResourceKey!);
        var characterId = await Entity(CanonEntityType.Character, "Traveler");
        var firstLocationId = await Entity(CanonEntityType.Location, "Old home");
        var secondLocationId = await Entity(CanonEntityType.Location, "New home");
        var organizationId = await Entity(CanonEntityType.Organization, "Guild");
        var objectId = await Entity(CanonEntityType.Object, "Relic");
        var principalId = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuityId, PrincipalKind.Character, CharacterId: characterId))).ResourceKey!);
        var effectiveAt = new DateTime(2000, 1, 1);

        var residenceId = int.Parse((await vault.Service.AddResidenceAsync(new(
            Guid.NewGuid().ToString(), characterId, firstLocationId, StoryDate.Unknown()))).ResourceKey!);
        var moved = await vault.Service.TransitionResidenceAsync(new(
            Guid.NewGuid().ToString(), residenceId, 1, secondLocationId, effectiveAt));
        Assert.True(moved.Success, moved.Message);

        var membershipId = int.Parse((await vault.Service.AddMembershipAsync(new(
            Guid.NewGuid().ToString(), organizationId, characterId, StoryDate.Unknown(), "Apprentice"))).ResourceKey!);
        var promoted = await vault.Service.TransitionMembershipAsync(new(
            Guid.NewGuid().ToString(), membershipId, 1, effectiveAt, true, "Master"));
        Assert.True(promoted.Success, promoted.Message);

        var ownershipId = int.Parse((await vault.Service.AddOwnershipPeriodAsync(new(
            Guid.NewGuid().ToString(), objectId, OwnershipState.Unknown, StoryDate.Unknown(), []))).ResourceKey!);
        var transfers = await Task.WhenAll(
            vault.Service.TransferOwnershipAsync(new(
                Guid.NewGuid().ToString(), ownershipId, 1, effectiveAt,
                OwnershipState.Owned, [new OwnershipOwnerInput(principalId)])),
            vault.Service.TransferOwnershipAsync(new(
                Guid.NewGuid().ToString(), ownershipId, 1, effectiveAt,
                OwnershipState.Unowned, [])));
        Assert.Single(transfers, result => result.Success);
        Assert.Single(transfers, result => result.Code == "concurrency.conflict");

        var invalidKind = await vault.Service.TransitionMembershipAsync(new(
            Guid.NewGuid().ToString(), membershipId, 2,
            DateTime.SpecifyKind(new DateTime(2001, 1, 1), DateTimeKind.Utc), false));
        Assert.Equal("validation.effective_at", invalidKind.Code);

        Assert.True((await vault.Service.SetClockAsync(new(
            Guid.NewGuid().ToString(), continuityId,
            DateTimeOffset.Parse("2001-01-01T00:00:00+00:00"), "UTC", 1))).Success);
        var characterState = (await vault.Service.GetEntityTemporalStateAsync(characterId))!;
        Assert.Single(characterState.ActiveRecords["residences"]);
        Assert.Single(characterState.ActiveRecords["memberships"]);
        var objectState = (await vault.Service.GetEntityTemporalStateAsync(objectId))!;
        Assert.Single(objectState.ActiveRecords["ownership"]);

        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        foreach (var (table, id) in new[]
        {
            ("CharacterResidences", residenceId),
            ("OrganizationMemberships", membershipId),
            ("ObjectOwnershipPeriods", ownershipId)
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT [PeriodKind],[PeriodUpperBound],[Version] FROM [{table}] WHERE [Id]={id}";
            using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader!.ReadAsync());
            Assert.Equal("Before", reader.GetString(0));
            Assert.Equal(effectiveAt, reader.GetDateTime(1));
            Assert.Equal(2, reader.GetInt32(2));
        }
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task ContinuityDuplicationCopiesCoreFieldsWithoutSharingRelationships()
    {
        await using var vault = await TestVault.CreateAsync();
        var sourceContinuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Source canon", "UTC"))).ResourceKey!);
        var targetContinuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Target canon", "UTC"))).ResourceKey!);
        var birthplaceId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), sourceContinuityId, CanonEntityType.Location, "Birthplace"))).ResourceKey!);
        var sourceId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), sourceContinuityId, CanonEntityType.Character, "Original",
            Description: "source description", Birth: StoryDate.Year(1987, "around 1987"),
            BirthLocationId: birthplaceId, BirthLocationDetail: "near the river"))).ResourceKey!);
        Assert.True((await vault.Service.AddAliasAsync(new(
            Guid.NewGuid().ToString(), sourceId, "Source alias"))).Success);
        Assert.True((await vault.Service.AddNoteAsync(new(
            Guid.NewGuid().ToString(), sourceId, "Source-only note"))).Success);

        var duplicate = await vault.Service.DuplicateEntityAsync(new(
            Guid.NewGuid().ToString(), sourceId, targetContinuityId, "Independent copy"));
        Assert.True(duplicate.Success, duplicate.Message);
        var duplicateId = int.Parse(duplicate.ResourceKey!);
        var details = (await vault.Service.GetEntityAsync(duplicateId))!;
        Assert.Equal(targetContinuityId, details.Summary.ContinuityId);
        Assert.Equal("Independent copy", details.Summary.Name);
        Assert.Equal("Year", details.Fields["BirthKind"]);
        Assert.Equal("around 1987", details.Fields["BirthOriginalText"]);
        Assert.Null(details.Fields["BirthLocationId"]);
        Assert.Equal("near the river", details.Fields["BirthLocationDetail"]);
        var graph = (await vault.Service.GetEntityGraphAsync(duplicateId))!;
        Assert.Empty(graph.Notes);
        Assert.Empty(graph.TypeSpecificRelations);

        var patched = await vault.Service.PatchEntityAsync(new(
            Guid.NewGuid().ToString(), duplicateId, 1, Description: new(true, "target change")));
        Assert.True(patched.Success, patched.Message);
        Assert.Equal("source description", (await vault.Service.GetEntityAsync(sourceId))!.Fields["PersonalitySummary"]);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }
}
