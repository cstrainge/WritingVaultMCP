using System.Text.Json;
using System.Text.RegularExpressions;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;

namespace WritingVaultMcp.Tests;

public sealed class Phase7McpTests
{
    [Fact]
    public async Task EverySemanticReferenceDescriptorIsValidForTheAccessProvider()
    {
        await using var vault = await TestVault.CreateAsync();
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        string[] resourceTypes =
        [
            "Project", "Location", "Character", "Organization", "Object", "WorldEvent",
            "Continuity", "VariantGroup", "Source", "Tag", "RelationshipType", "OwnershipPrincipal",
            "EntityNote", "EntityEvent", "ProjectAssignment", "CharacterAlias", "CharacterResidence",
            "OrganizationAlias", "OrganizationMembership", "OrganizationLocation", "ObjectOwnershipPeriod",
            "ObjectCustodyPeriod", "ObjectLocationPeriod", "WorldEventParticipant", "WorldEventLocation",
            "CharacterRelationship", "Claim", "NoteSource", "ClaimEvidence", "SourceSnapshot"
        ];

        foreach (var resourceType in resourceTypes)
        {
            var reference = await references.ReferenceAsync(resourceType, int.MaxValue);
            Assert.Matches("^[a-z][a-z0-9-]+:[a-z][a-z0-9-]*~[A-Z2-9]{10}$", reference);
        }
    }

    [Fact]
    public async Task SemanticReferencesHideStorageKeysRemainStableAndResolveWithinSessionContinuity()
    {
        await using var vault = await TestVault.CreateAsync();
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var mapper = new VaultMcpResultMapper(references);
        var session = new VaultSessionContext { ClientLabel = "phase7-test" };
        var writes = new SemanticVaultWriteTools(vault.Service, session, references, mapper);
        var reads = new SemanticVaultReadTools(vault.Service, vault.Schema, vault.Integrity, vault.Coordinator,
            new WritingVaultStorageOptions(vault.StorageRoot), session, references, mapper);

        var continuity = await writes.CreateContinuity(new("create-lostville", "Lostville", "UTC"), default);
        Assert.True(continuity.Success, continuity.Message);
        Assert.Equal("Lostville", continuity.ResourceReference);
        await reads.SetContinuity("Lostville");

        var created = await writes.CreateEntity(new(
            "create-ada", CanonEntityType.Character, "Ada",
            Birth: StoryDate.ExactDate(new DateOnly(2000, 6, 15))), default);
        Assert.True(created.Success, created.Message);
        Assert.Matches("^character:ada~[A-Z2-9]{10}$", created.ResourceReference!);
        Assert.DoesNotContain("operationId", JsonSerializer.Serialize(created), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase), JsonSerializer.Serialize(created));

        var page = await reads.SearchEntities(CanonEntityType.Character);
        var reference = Assert.Single(page.Items).Reference;
        Assert.Equal(created.ResourceReference, reference);
        Assert.Null(page.NextCursor);

        var patched = await writes.PatchEntity(new("rename-ada", reference, 1,
            Name: new(true, "Ada North")), default);
        Assert.True(patched.Success, patched.Message);
        var reread = await reads.GetEntity(reference);
        Assert.Equal("Ada North", reread!.Summary.Name);
        Assert.StartsWith("character:ada-north~", reread.Summary.Reference);
        Assert.Equal(reference.Split('~')[1], reread.Summary.Reference.Split('~')[1]);

        var replay = await writes.PatchEntity(new("rename-ada", reference, 1,
            Name: new(true, "Ada North")), default);
        Assert.True(replay.Success);
        Assert.True(replay.Replayed);

        var history = await reads.GetHistory(reference);
        var historyJson = JsonSerializer.Serialize(history);
        Assert.DoesNotContain("operationId", historyJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("continuityId", historyJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continuityName", historyJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientTimeAndContinuityStateAreIndependentAndDriveAge()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var characterResult = await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Taylor", Birth: StoryDate.ExactDate(new DateOnly(2000, 6, 15))));
        var character = int.Parse(characterResult.ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var characterReference = await references.ReferenceAsync("Character", character);

        async Task<(VaultSessionContext Session, SemanticVaultReadTools Reads)> Client(string label)
        {
            var session = new VaultSessionContext { ClientLabel = label };
            var mapper = new VaultMcpResultMapper(references);
            var reads = new SemanticVaultReadTools(vault.Service, vault.Schema, vault.Integrity, vault.Coordinator,
                new WritingVaultStorageOptions(vault.StorageRoot), session, references, mapper);
            await reads.SetContinuity("Canon");
            return (session, reads);
        }

        var first = await Client("first");
        var second = await Client("second");
        first.Reads.SetSessionTime(new DateTimeOffset(2025, 6, 14, 12, 0, 0, TimeSpan.Zero), "UTC");
        second.Reads.SetSessionTime(new DateTimeOffset(2030, 6, 15, 12, 0, 0, TimeSpan.Zero), "UTC");

        Assert.Equal(24, (await first.Reads.GetCharacterAge(characterReference))!.ExactYears);
        Assert.Equal(30, (await second.Reads.GetCharacterAge(characterReference))!.ExactYears);
        Assert.NotEqual(first.Session.CurrentTimeOverride, second.Session.CurrentTimeOverride);
    }

    [Fact]
    public async Task SemanticMutationErrorsDoNotEchoInternalKeys()
    {
        await using var vault = await TestVault.CreateAsync();
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var mapper = new VaultMcpResultMapper(references);
        var session = new VaultSessionContext();
        var writes = new SemanticVaultWriteTools(vault.Service, session, references, mapper);
        var reads = new SemanticVaultReadTools(vault.Service, vault.Schema, vault.Integrity, vault.Coordinator,
            new WritingVaultStorageOptions(vault.StorageRoot), session, references, mapper);
        Assert.True((await writes.CreateContinuity(new("error-canon", "Canon", "UTC"), default)).Success);
        await reads.SetContinuity("Canon");
        var organization = await writes.CreateEntity(new("error-org", CanonEntityType.Organization, "Council"), default);
        var failure = await writes.CreateOwnershipPrincipal(new(
            "error-principal", PrincipalKind.Character, organization.ResourceReference), default);

        Assert.False(failure.Success);
        Assert.Equal("entity.type_mismatch", failure.Code);
        Assert.Equal("A referenced resource has the wrong type.", failure.Message);
        Assert.DoesNotContain("Entity 1", JsonSerializer.Serialize(failure), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrganizationTemporalLocationsUseOrganizationLocationReferences()
    {
        await using var vault = await TestVault.CreateAsync();
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var mapper = new VaultMcpResultMapper(references);
        var session = new VaultSessionContext();
        var writes = new SemanticVaultWriteTools(vault.Service, session, references, mapper);
        var reads = new SemanticVaultReadTools(vault.Service, vault.Schema, vault.Integrity, vault.Coordinator,
            new WritingVaultStorageOptions(vault.StorageRoot), session, references, mapper);

        Assert.True((await writes.CreateContinuity(new("org-time-canon", "Org Time Canon", "UTC"), default)).Success);
        await reads.SetContinuity("Org Time Canon");
        var organization = await writes.CreateEntity(new("org-time-org", CanonEntityType.Organization, "Council"), default);
        var location = await writes.CreateEntity(new("org-time-location", CanonEntityType.Location, "Hall"), default);
        Assert.True(organization.Success, $"{organization.Code}: {organization.Message}");
        Assert.True(location.Success, $"{location.Code}: {location.Message}");
        var linked = await writes.AddOrganizationLocation(new("org-time-link", organization.ResourceReference!,
            location.ResourceReference!, StoryDate.Year(2020), true, "headquarters"), default);
        reads.SetSessionTime(new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero), "UTC");

        Assert.True(linked.Success, $"{linked.Code}: {linked.Message}");

        var state = await reads.GetTemporalState(organization.ResourceReference!);
        var active = Assert.Single(state!.ActiveRecords["locations"]);
        var reference = Assert.IsType<string>(active["Reference"]);
        Assert.StartsWith("organization-location:", reference, StringComparison.Ordinal);
        AssertNoStorageIdentity(JsonSerializer.Serialize(state));
    }

    private static void AssertNoStorageIdentity(string text)
    {
        Assert.DoesNotMatch(new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase), text);
        Assert.DoesNotMatch(new Regex("\\\"(?:id|operationId|continuityId|entityId|characterId|sourceId|tagId|recordId)\\\"\\s*:", RegexOptions.IgnoreCase), text);
    }
}
