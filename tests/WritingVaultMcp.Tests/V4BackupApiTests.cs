using System.Data.OleDb;
using System.Security.Cryptography;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access.Backup;

namespace WritingVaultMcp.Tests;

public sealed class V4BackupApiTests
{
    [Fact]
    public async Task CoordinatedBackupIsVerifiedPathFreeAndIdempotent()
    {
        await using var vault = await TestVault.CreateAsync();
        var service = new CoordinatedVaultBackupService(vault.Factory, vault.Coordinator, vault.StorageRoot, 3);
        var operation = Guid.NewGuid().ToString("D");

        var first = await service.CreateAsync(operation, "Before timeline edits");
        var replay = await service.CreateAsync(operation, "Before timeline edits");

        Assert.True(first.SchemaValid);
        Assert.True(first.IntegrityValid);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.DatabaseSha256, replay.DatabaseSha256);
        Assert.Equal(first.CreatedAtUtc, replay.CreatedAtUtc);
        Assert.Single(Directory.GetFiles(Path.Combine(vault.StorageRoot, "backups"), "*.manifest.json"));
        Assert.DoesNotContain(vault.Directory, System.Text.Json.JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(operation, "Different purpose"));
    }

    [Fact]
    public async Task BackupManifestVerifiesEveryReferencedImageMaster()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Image canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Portrait owner"))).ResourceKey!);

        var bytes = "immutable-original"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var relative = Path.Combine("originals", hash + ".png");
        var full = Path.Combine(vault.StorageRoot, "assets", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes);

        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO [EntityImages] ([EntityId],[IsPrimary],[OriginalRelativePath],[OriginalSha256],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?)";
            insert.Parameters.Add("?", OleDbType.Integer).Value = entity;
            insert.Parameters.Add("?", OleDbType.Boolean).Value = true;
            insert.Parameters.Add("?", OleDbType.VarWChar, 255).Value = relative;
            insert.Parameters.Add("?", OleDbType.VarWChar, 64).Value = hash;
            insert.Parameters.Add("?", OleDbType.VarWChar, 100).Value = "image/png";
            insert.Parameters.Add("?", OleDbType.Integer).Value = 1;
            insert.Parameters.Add("?", OleDbType.Integer).Value = 1;
            insert.Parameters.Add("?", OleDbType.Integer).Value = bytes.Length;
            insert.Parameters.Add("?", OleDbType.Date).Value = DateTime.UtcNow;
            insert.Parameters.Add("?", OleDbType.Date).Value = DateTime.UtcNow;
            await insert.ExecuteNonQueryAsync();
            insert.CommandText = "SELECT @@IDENTITY";
            insert.Parameters.Clear();
            var imageId = Convert.ToInt32(await insert.ExecuteScalarAsync());

            using (var revision = connection.CreateCommand())
            {
                revision.CommandText = "INSERT INTO [ImageContentVersions] " +
                    "([ImageKind],[ImageId],[CurrentRevision]) VALUES ('EntityImage',?,1)";
                revision.Parameters.Add("?", OleDbType.Integer).Value = imageId;
                await revision.ExecuteNonQueryAsync();
            }

            foreach (var kind in new[] { "Thumbnail", "Display" })
            {
                using var rendition = connection.CreateCommand();
                rendition.CommandText = "INSERT INTO [ImageRenditions] ([ImageId],[RenditionKind],[MediaType],[Width],[Height],[ByteSize],[Content]) VALUES (?,?,?,?,?,?,?)";
                rendition.Parameters.Add("?", OleDbType.Integer).Value = imageId;
                rendition.Parameters.Add("?", OleDbType.VarWChar, 20).Value = kind;
                rendition.Parameters.Add("?", OleDbType.VarWChar, 100).Value = "image/jpeg";
                rendition.Parameters.Add("?", OleDbType.Integer).Value = 1;
                rendition.Parameters.Add("?", OleDbType.Integer).Value = 1;
                rendition.Parameters.Add("?", OleDbType.Integer).Value = 1;
                rendition.Parameters.Add("?", OleDbType.LongVarBinary).Value = new byte[] { 1 };
                await rendition.ExecuteNonQueryAsync();
            }
        }

        var backup = await new CoordinatedVaultBackupService(vault.Factory, vault.Coordinator, vault.StorageRoot, 3)
            .CreateAsync(Guid.NewGuid().ToString("D"));
        Assert.Equal(1, backup.AssetCount);
        Assert.Equal(bytes.Length, backup.AssetBytes);

        var manifestPath = Assert.Single(Directory.GetFiles(Path.Combine(vault.StorageRoot, "backups"), "*.manifest.json"));
        var manifest = await new AccessBackupService().VerifyAsync(manifestPath);
        var snapshotRoot = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.AssetSnapshotDirectoryName!);
        var snapshotAsset = Path.Combine(snapshotRoot, relative);
        Assert.True(File.Exists(snapshotAsset));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshotAsset));

        var restoredDatabase = Path.Combine(vault.Directory, "restored-with-assets.accdb");
        var restoredAssets = Path.Combine(vault.Directory, "restored-assets");
        await new AccessBackupService().RestoreToNewPathsAsync(
            manifestPath, restoredDatabase, restoredAssets);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(restoredAssets, relative)));

        await File.WriteAllTextAsync(full, "tampered");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshotAsset));
        Assert.True((await new AccessBackupService().VerifyAsync(manifestPath)).IntegrityValid);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CoordinatedVaultBackupService(vault.Factory, vault.Coordinator, vault.StorageRoot, 3)
                .CreateAsync(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public async Task ExpiredBackupTokenCannotCreateASecondBackupAfterRetention()
    {
        await using var vault = await TestVault.CreateAsync();
        var service = new CoordinatedVaultBackupService(vault.Factory, vault.Coordinator, vault.StorageRoot, 2);
        var expiredToken = Guid.NewGuid().ToString("D");
        await service.CreateAsync(expiredToken, "First");
        await service.CreateAsync(Guid.NewGuid().ToString("D"), "Second");
        await service.CreateAsync(Guid.NewGuid().ToString("D"), "Third");

        var backupDirectory = Path.Combine(vault.StorageRoot, "backups");
        Assert.Equal(2, Directory.GetFiles(backupDirectory, "*.accdb").Length);
        Assert.Equal(2, Directory.GetFiles(backupDirectory, "*.manifest.json").Length);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(expiredToken, "First"));
        Assert.Contains("no longer retained", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Directory.GetFiles(backupDirectory, "*.accdb").Length);
    }
}
