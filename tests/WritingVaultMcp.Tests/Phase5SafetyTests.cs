using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Tests;

public sealed class Phase5SafetyTests
{
    [Fact]
    public async Task JournalIsCorrelatedCanonicalAndRedactsLongFormContent()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Subject"))).ResourceKey!);
        var operation = Guid.NewGuid();
        const string secret = "private long-form event detail";
        var created = await vault.Service.AddEntityEventAsync(new(
            operation.ToString("B").ToUpperInvariant(), entity, "Event", StoryDate.Year(2001),
            Description: secret, ClientLabel: "phase5-client"));
        Assert.True(created.Success, created.Message);

        var history = Assert.Single(await vault.Service.GetOperationHistoryAsync(operation.ToString("B").ToUpperInvariant()));
        Assert.Equal(operation.ToString("D"), history.OperationId);
        Assert.Equal("phase5-client", history.ClientLabel);
        Assert.DoesNotContain(secret, history.ChangeJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("phase5-client", history.ChangeJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("operationId", history.ChangeJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", history.ChangeJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sha256", history.ChangeJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalGraphsHideDeletedOwnersAndCounterpartsWhileRestorePreservesLinks()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "First"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Second"))).ResourceKey!);
        var relationshipType = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "knows", false))).ResourceKey!);
        Assert.True((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, relationshipType, StoryDate.Unknown()))).Success);

        var source = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Source"))).ResourceKey!);
        var note = int.Parse((await vault.Service.AddNoteAsync(new(
            Guid.NewGuid().ToString(), first, "Body", "Note"))).ResourceKey!);
        Assert.True((await vault.Service.LinkNoteSourceAsync(new(
            Guid.NewGuid().ToString(), note, source))).Success);

        Assert.True((await vault.Service.SoftDeleteRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.EntityNote, note, 1))).Success);
        Assert.Empty((await vault.Service.GetSourceGraphAsync(source))!.Notes);

        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(
            Guid.NewGuid().ToString(), second, 1))).Success);
        Assert.Empty(await vault.Service.GetCharacterRelationshipsAsync(first));
        Assert.Null(await vault.Service.GetEntityGraphAsync(second));
        Assert.NotNull(await vault.Service.GetEntityGraphAsync(second, includeDeleted: true));

        Assert.True((await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Source, source, 1))).Success);
        Assert.Null(await vault.Service.GetSourceGraphAsync(source));
        Assert.NotNull(await vault.Service.GetSourceGraphAsync(source, includeDeleted: true));

        Assert.True((await vault.Service.RestoreEntityAsync(new(
            Guid.NewGuid().ToString(), second, 2))).Success);
        Assert.Single(await vault.Service.GetCharacterRelationshipsAsync(first));
        Assert.True((await vault.Service.RestoreVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Source, source, 2))).Success);
        Assert.True((await vault.Service.RestoreRelationshipAsync(new(
            Guid.NewGuid().ToString(), RelationshipRecordType.EntityNote, note, 2))).Success);
        Assert.Single((await vault.Service.GetSourceGraphAsync(source))!.Notes);
    }

    [Fact]
    public async Task DeleteBlockedErrorContainsConcreteCounts()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        _ = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "One"));
        _ = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Two"));

        var blocked = await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Continuity, continuity, 1));
        Assert.Equal("delete.blocked", blocked.Code);
        Assert.Contains("2 canon entities", blocked.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("0 claims", blocked.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuxiliaryIdentityRecordsCanBeDeletedRestoredAndAreBlockedWhenReferenced()
    {
        await using var vault = await TestVault.CreateAsync();
        var unusedType = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "unused", false))).ResourceKey!);
        var deletedType = await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.RelationshipType, unusedType, 1));
        Assert.True(deletedType.Success, deletedType.Message);
        Assert.True((await vault.Service.RestoreVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.RelationshipType, unusedType, 2))).Success);

        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Owner"))).ResourceKey!);
        var principal = int.Parse((await vault.Service.CreateOwnershipPrincipalAsync(new(
            Guid.NewGuid().ToString(), continuity, PrincipalKind.Character, CharacterId: character))).ResourceKey!);
        var obj = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Artifact"))).ResourceKey!);
        Assert.True((await vault.Service.AddOwnershipPeriodAsync(new(
            Guid.NewGuid().ToString(), obj, OwnershipState.Owned, StoryDate.Unknown(), [new(principal)]))).Success);
        var blockedPrincipal = await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.OwnershipPrincipal, principal, 1));
        Assert.Equal("delete.blocked", blockedPrincipal.Code);
        Assert.Contains("1 ownership links", blockedPrincipal.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchesCanReturnActiveDeletedOrBothExplicitly()
    {
        await using var vault = await TestVault.CreateAsync();
        var active = int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Active"))).ResourceKey!);
        var deleted = int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Deleted"))).ResourceKey!);
        Assert.True(active > 0);
        Assert.True((await vault.Service.SoftDeleteVaultRecordAsync(new(
            Guid.NewGuid().ToString(), VaultRecordType.Tag, deleted, 1))).Success);

        Assert.Single(await vault.Service.SearchTagsAsync());
        Assert.Equal(2, (await vault.Service.SearchTagsAsync(includeDeleted: true)).Count);
        var deletedOnly = Assert.Single(await vault.Service.SearchTagsAsync(onlyDeleted: true));
        Assert.Equal(deleted, deletedOnly.Id);
        Assert.True(deletedOnly.IsDeleted);
    }

    [Fact]
    public async Task DeletionMetadataUsesTheCanonicalCorrelationId()
    {
        await using var vault = await TestVault.CreateAsync();
        var tag = int.Parse((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Delete me"))).ResourceKey!);
        var operation = Guid.NewGuid();
        var deleted = await vault.Service.SoftDeleteVaultRecordAsync(new(
            operation.ToString("B").ToUpperInvariant(), VaultRecordType.Tag, tag, 1));
        Assert.True(deleted.Success, deleted.Message);

        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT [DeletedOperationId] FROM [Tags] WHERE [Id]=?";
        command.Parameters.Add("@id", System.Data.OleDb.OleDbType.Integer).Value = tag;
        Assert.Equal(operation.ToString("D"), Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RestoredBackupPassesVerificationAndServesApplicationReads()
    {
        await using var vault = await TestVault.CreateAsync();
        _ = await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "Restored tag"));
        var service = new AccessBackupService();
        var backup = await service.CreateAsync(vault.DatabasePath, Path.Combine(vault.Directory, "backups"), retentionCount: 3);
        var manifest = await service.VerifyAsync(backup.ManifestPath);
        Assert.Equal("Microsoft.ACE.OLEDB.12.0", manifest.Provider);
        Assert.Equal(3, manifest.RetentionCount);

        var restored = Path.Combine(vault.Directory, "restore", "served.accdb");
        await service.RestoreToNewPathAsync(backup.ManifestPath, restored);
        var factory = new AccessConnectionFactory(restored);
        var verifier = new AccessSchemaVerifier(factory);
        var coordinator = new VaultWriteCoordinator(factory, new SchemaWriteGate(verifier));
        var restoredVault = new AccessVaultService(factory, coordinator, new WritingVaultStorageOptions(Path.Combine(vault.Directory, "restore-cache")));
        Assert.Single(await restoredVault.SearchTagsAsync("Restored tag"));
    }
}
