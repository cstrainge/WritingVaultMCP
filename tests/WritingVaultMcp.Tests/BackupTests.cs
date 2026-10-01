using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task ActiveServerLeaseBlocksCompetingBackupAndSecondOwner()
    {
        await using var vault = await TestVault.CreateAsync();
        using (AccessOwnerLease.Acquire(vault.DatabasePath))
        {
            Assert.Throws<IOException>(() => AccessOwnerLease.Acquire(vault.DatabasePath));
            await Assert.ThrowsAsync<IOException>(() => new AccessBackupService().CreateAsync(vault.DatabasePath, vault.StorageRoot));
        }
        Assert.False(File.Exists(Path.Combine(vault.Directory, "test.writingvault.owner.lock")));
    }

    [Fact]
    public async Task BackupIsHashedOpenedAndRestorable()
    {
        await using var vault = await TestVault.CreateAsync();
        var backupRoot = Path.Combine(vault.Directory, "backup-root");
        var service = new AccessBackupService();
        var backup = await service.CreateAsync(vault.DatabasePath, backupRoot);
        Assert.True(File.Exists(backup.BackupPath));
        Assert.True(File.Exists(backup.ManifestPath));
        var manifest = await service.VerifyAsync(backup.ManifestPath);
        Assert.Equal(backup.Sha256, manifest.BackupSha256);
        var restored = Path.Combine(vault.Directory, "restored.accdb");
        await service.RestoreToNewPathAsync(backup.ManifestPath, restored);
        Assert.True(File.Exists(restored));
        Assert.False(File.Exists(Path.ChangeExtension(restored, ".laccdb")));
    }

    [Fact]
    public async Task RetentionIsConfigurableAndAlwaysPreservesAtLeastTwoBackups()
    {
        await using var vault = await TestVault.CreateAsync();
        var backupRoot = Path.Combine(vault.Directory, "retained-backups");
        var service = new AccessBackupService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateAsync(vault.DatabasePath, backupRoot, retentionCount: 1));
        for (var index = 0; index < 3; index++)
            await service.CreateAsync(vault.DatabasePath, backupRoot, retentionCount: 2);

        var directory = Path.Combine(backupRoot, "backups");
        Assert.Equal(2, Directory.GetFiles(directory, "*.manifest.json").Length);
        Assert.Equal(2, Directory.GetFiles(directory, "*.accdb").Length);
    }
}
