using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

public sealed class ProvenanceTests
{
    [Fact]
    public async Task InvalidSnapshotRequestCreatesNoCacheArtifact()
    {
        await using var vault = await TestVault.CreateAsync();
        var result = await vault.Service.AddSourceSnapshotAsync(new(Guid.NewGuid().ToString(), 999, "must not be cached"));
        Assert.Equal("entity.not_found", result.Code);
        Assert.False(Directory.Exists(vault.StorageRoot));
    }

    [Fact]
    public async Task SourceSnapshotAndClaimRemainLocallyQueryable()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var source = int.Parse((await vault.Service.CreateSourceAsync(new(Guid.NewGuid().ToString(), "Reference", "https://example.com/fact"))).ResourceKey!);
        var snapshot = await vault.Service.AddSourceSnapshotAsync(new(Guid.NewGuid().ToString(), source, "cached important fact", "text/plain"));
        Assert.True(snapshot.Success, snapshot.Message);
        var snapshotId = int.Parse(snapshot.ResourceKey!);
        Assert.Single(Directory.GetFiles(vault.StorageRoot, "*.bin", SearchOption.AllDirectories));
        var claim = await vault.Service.CreateClaimAsync(new(
            Guid.NewGuid().ToString(), continuity, "Mara was present.", [character],
            [new ClaimEvidenceInput(source, snapshotId, Locator: "paragraph 1", Summary: "Presence is stated.")]));
        Assert.True(claim.Success, claim.Message);

        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [ClaimSources]";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));

        var graph = await vault.Service.GetSourceGraphAsync(source);
        Assert.NotNull(graph);
        Assert.Single(graph!.Claims);
        Assert.Single(graph.Snapshots);
        Assert.Contains(graph.Entities, row => Convert.ToInt32(row["EntityId"]) == character);
    }

    [Fact]
    public async Task ClaimEvidenceRelationsAreClosedAndCanonical()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityId = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Evidence canon", "UTC"))).ResourceKey!);
        var characterId = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuityId, CanonEntityType.Character, "Subject"))).ResourceKey!);
        var sourceId = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Source"))).ResourceKey!);

        var invalid = await vault.Service.CreateClaimAsync(new(
            Guid.NewGuid().ToString(), continuityId, "Invalid relation", [characterId],
            [new ClaimEvidenceInput(sourceId, EvidenceRelation: "Maybe")]));
        Assert.Equal("validation.evidence", invalid.Code);
        var valid = await vault.Service.CreateClaimAsync(new(
            Guid.NewGuid().ToString(), continuityId, "Canonical relation", [characterId],
            [new ClaimEvidenceInput(sourceId, EvidenceRelation: "  contradicts ")]));
        Assert.True(valid.Success, valid.Message);

        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT [EvidenceRelation] FROM [ClaimSources]";
        Assert.Equal("Contradicts", Convert.ToString(await command.ExecuteScalarAsync()));
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }
}
