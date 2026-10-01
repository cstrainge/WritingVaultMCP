using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Tests;

public sealed class Phase10AcceptanceTests
{
    [Fact]
    public async Task DamagedDisposableCopyIsRejectedAndVerifiedBackupRestoresService()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Recovery Canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Recoverable"))).ResourceKey!);

        var backups = new AccessBackupService();
        var backup = await backups.CreateAsync(vault.DatabasePath, vault.StorageRoot);
        _ = await backups.VerifyAsync(backup.ManifestPath);

        var damaged = Path.Combine(vault.Directory, "damaged.accdb");
        File.Copy(vault.DatabasePath, damaged);
        await using (var stream = new FileStream(damaged, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(4096);
        var damageRejected = false;
        try
        {
            damageRejected = !(await new AccessSchemaVerifier(new AccessConnectionFactory(damaged)).VerifyAsync()).IsValid;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.OleDb.OleDbException)
        {
            damageRejected = true;
        }
        Assert.True(damageRejected);

        var restored = Path.Combine(vault.Directory, "restored.accdb");
        await backups.RestoreToNewPathAsync(backup.ManifestPath, restored);
        var restoredFactory = new AccessConnectionFactory(restored);
        Assert.True((await new AccessSchemaVerifier(restoredFactory).VerifyAsync()).IsValid);
        Assert.True((await new AccessIntegrityVerifier(restoredFactory).VerifyAsync()).IsValid);
        var restoredCoordinator = new VaultWriteCoordinator(restoredFactory,
            new SchemaWriteGate(new AccessSchemaVerifier(restoredFactory)));
        var restoredService = new AccessVaultService(restoredFactory, restoredCoordinator,
            new WritingVaultStorageOptions(Path.Combine(vault.Directory, "restored-storage")));
        Assert.Equal("Recoverable", (await restoredService.GetEntityAsync(entity))!.Summary.Name);
    }
}
