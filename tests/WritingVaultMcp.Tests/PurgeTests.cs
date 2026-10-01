using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access.Backup;

namespace WritingVaultMcp.Tests;

public sealed class PurgeTests
{
    [Fact]
    public async Task PurgeRequiresSoftDeletePreviewTokenAndRecentVerifiedBackup()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Disposable"))).ResourceKey!);
        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), entity, 1))).Success);
        var purge = new AccessPurgeService();
        var preview = await purge.PreviewAsync(vault.DatabasePath, vault.StorageRoot, entity, 2);
        Assert.True(preview.CanPurge);
        var backup = await new AccessBackupService().CreateAsync(vault.DatabasePath, vault.StorageRoot);
        await purge.ExecuteAsync(vault.DatabasePath, vault.StorageRoot, preview.Token, backup.ManifestPath);
        Assert.Null(await vault.Service.GetEntityAsync(entity, true));
        await purge.ExecuteAsync(vault.DatabasePath, vault.StorageRoot, preview.Token, backup.ManifestPath);
        var history = await vault.Service.GetHistoryAsync("CanonEntity", entity.ToString());
        Assert.Single(history, entry => entry.Action == "purge" && entry.OperationId == preview.Token);
        Assert.Single(await vault.Service.GetOperationHistoryAsync(preview.Token));
    }

    [Fact]
    public async Task PurgeRejectsBackupFromAnotherDatabaseEvenWithSameFileName()
    {
        await using var target = await TestVault.CreateAsync();
        await using var other = await TestVault.CreateAsync();
        var continuity = int.Parse((await target.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await target.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Disposable"))).ResourceKey!);
        Assert.True((await target.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), entity, 1))).Success);
        var foreignBackup = await new AccessBackupService().CreateAsync(other.DatabasePath, other.StorageRoot);
        var purge = new AccessPurgeService();
        var preview = await purge.PreviewAsync(target.DatabasePath, target.StorageRoot, entity, 2);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            purge.ExecuteAsync(target.DatabasePath, target.StorageRoot, preview.Token, foreignBackup.ManifestPath));
        Assert.Contains("database path", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await target.Service.GetEntityAsync(entity, true));
    }

    [Fact]
    public async Task PurgeRejectsAValidBackupWhenDatabaseChangedAfterBackup()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Disposable"))).ResourceKey!);
        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(), entity, 1))).Success);
        var purge = new AccessPurgeService();
        var preview = await purge.PreviewAsync(vault.DatabasePath, vault.StorageRoot, entity, 2);
        var backup = await new AccessBackupService().CreateAsync(vault.DatabasePath, vault.StorageRoot);
        Assert.True((await vault.Service.CreateTagAsync(new(Guid.NewGuid().ToString(), "After backup"))).Success);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            purge.ExecuteAsync(vault.DatabasePath, vault.StorageRoot, preview.Token, backup.ManifestPath));
        Assert.Contains("changed after", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await vault.Service.GetEntityAsync(entity, true));
        Assert.DoesNotContain(await vault.Service.GetHistoryAsync("CanonEntity", entity.ToString()), entry => entry.Action == "purge");
    }
}
