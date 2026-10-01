using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

public sealed class EndToEndAcceptanceTests
{
    [Fact]
    public async Task CompleteWritingWorkflowPreservesCanonTimeProvenanceAndRecovery()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityRequest = new CreateContinuityRequest(Guid.NewGuid().ToString(), "Acceptance Canon", "UTC");
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(continuityRequest)).ResourceKey!);
        Assert.True((await vault.Service.CreateContinuityAsync(continuityRequest)).Replayed);

        async Task<int> Entity(CanonEntityType type, string name, StoryDate? birth = null, StoryDate? occurred = null, string? zone = null) =>
            int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, type, name,
                TimeZoneId: zone, Birth: birth, Occurred: occurred))).ResourceKey!);

        var firstProject = await Entity(CanonEntityType.Project, "Novel One");
        var secondProject = await Entity(CanonEntityType.Project, "Novel Two");
        var region = await Entity(CanonEntityType.Location, "Region", zone: "UTC");
        var city = await Entity(CanonEntityType.Location, "City");
        Assert.True((await vault.Service.MoveLocationAsync(new(Guid.NewGuid().ToString(), city, region, 1))).Success);
        Assert.Equal("location.cycle", (await vault.Service.MoveLocationAsync(new(Guid.NewGuid().ToString(), region, city, 1))).Code);

        var first = await Entity(CanonEntityType.Character, "First", StoryDate.Year(1980));
        var second = await Entity(CanonEntityType.Character, "Second", new StoryDate(
            StoryDateKind.Month, new DateTime(1982, 6, 1), new DateTime(1982, 7, 1)));
        var organization = await Entity(CanonEntityType.Organization, "Archive Guild");
        var artifact = await Entity(CanonEntityType.Object, "Shared Artifact");
        var worldEvent = await Entity(CanonEntityType.WorldEvent, "Convergence", occurred: StoryDate.Year(2003));

        foreach (var member in new[] { first, second, organization, artifact, worldEvent })
            Assert.True((await vault.Service.LinkEntityAsync("project", new(Guid.NewGuid().ToString(), member, firstProject))).Success);
        Assert.True((await vault.Service.LinkEntityAsync("project", new(Guid.NewGuid().ToString(), first, secondProject))).Success);
        Assert.True((await vault.Service.AddAliasAsync(new(Guid.NewGuid().ToString(), first, "The First"))).Success);
        Assert.True((await vault.Service.AddResidenceAsync(new(Guid.NewGuid().ToString(), first, city, StoryDate.Unknown()))).Success);
        Assert.True((await vault.Service.AddMembershipAsync(new(Guid.NewGuid().ToString(), organization, first, StoryDate.Year(2003), "Keeper"))).Success);

        var relationshipType = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "mentor of", true, "student of"))).ResourceKey!);
        Assert.True((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, relationshipType, StoryDate.Unknown()))).Success);
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(first));
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(second));

        Assert.True((await vault.Service.AddWorldEventLocationAsync(new(Guid.NewGuid().ToString(), worldEvent, city, true))).Success);
        Assert.True((await vault.Service.AddWorldEventLocationAsync(new(Guid.NewGuid().ToString(), worldEvent, region))).Success);
        foreach (var participant in new[] { first, organization, artifact })
            Assert.True((await vault.Service.AddWorldEventParticipantAsync(new(Guid.NewGuid().ToString(), worldEvent, participant))).Success);

        var firstPrincipal = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuity, PrincipalKind.Character, CharacterId: first))).ResourceKey!);
        var secondPrincipal = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuity, PrincipalKind.Character, CharacterId: second))).ResourceKey!);
        var organizationPrincipal = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuity, PrincipalKind.Organization, OrganizationId: organization))).ResourceKey!);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), artifact, OwnershipState.Unknown, StoryDate.Year(1999), []))).Success);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), artifact, OwnershipState.Unowned, StoryDate.Year(2000), []))).Success);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), artifact, OwnershipState.Owned, StoryDate.Year(2001), [new(organizationPrincipal)]))).Success);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), artifact, OwnershipState.Unowned, StoryDate.Year(2002), []))).Success);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), artifact, OwnershipState.Owned, StoryDate.Year(2003), [new(firstPrincipal, 500_000), new(secondPrincipal, 500_000)]))).Success);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(Guid.NewGuid().ToString(), artifact, OwnershipState.Owned, StoryDate.Year(2004), [new(firstPrincipal)]))).Success);

        var source = int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(), "Archive entry", "https://example.com/archive"))).ResourceKey!);
        var snapshot = int.Parse((await vault.Service.AddSourceSnapshotAsync(new(Guid.NewGuid().ToString(), source, "locally cached evidence"))).ResourceKey!);
        Assert.True((await vault.Service.CreateClaimAsync(new(Guid.NewGuid().ToString(), continuity, "The artifact changed hands.", [artifact], [new(source, snapshot)]))).Success);
        Assert.Contains((await vault.Service.GetSourceGraphAsync(source))!.Entities, row => Convert.ToInt32(row["EntityId"]) == artifact);

        Assert.True((await vault.Service.SetClockAsync(new(Guid.NewGuid().ToString(), continuity,
            DateTimeOffset.Parse("2003-06-01T12:00:00+00:00"), "UTC", 1))).Success);
        Assert.NotNull(await vault.Service.GetCharacterAgeAsync(first));
        var objectState = await vault.Service.GetEntityTemporalStateAsync(artifact);
        Assert.Single(objectState!.ActiveRecords["ownership"]);
        Assert.Equal(2, objectState.ActiveRecords["owners"].Count);

        var updates = await Task.WhenAll(
            vault.Service.PatchEntityAsync(new(Guid.NewGuid().ToString(), first, 1, Description: new(true, "one"))),
            vault.Service.PatchEntityAsync(new(Guid.NewGuid().ToString(), first, 1, Description: new(true, "two"))));
        Assert.Single(updates, result => result.Success);
        Assert.Single(updates, result => result.Code == "concurrency.conflict");

        var preview = await vault.Service.PreviewDeleteAsync(first);
        Assert.NotEmpty(preview!.Blockers);
        var deleteOperation = Guid.NewGuid().ToString();
        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(deleteOperation, first, 2))).Success);
        Assert.Null(await vault.Service.GetEntityAsync(first));
        Assert.NotNull(await vault.Service.GetEntityAsync(first, includeDeleted: true));
        Assert.Single(await vault.Service.GetOperationHistoryAsync(deleteOperation));
        Assert.True((await vault.Service.RestoreEntityAsync(new(Guid.NewGuid().ToString(), first, 3))).Success);

        Assert.True((await vault.Schema.VerifyAsync()).IsValid);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }
}
