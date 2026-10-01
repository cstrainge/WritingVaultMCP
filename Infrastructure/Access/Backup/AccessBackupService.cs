using System.Security.Cryptography;
using System.Text.Json;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Infrastructure.Access.Backup;

public sealed record VaultBackupManifest(
    DateTime CreatedAtUtc,
    string? SourcePathSha256,
    string SourceFileName,
    long SourceBytes,
    string SourceSha256,
    string BackupFileName,
    long BackupBytes,
    string BackupSha256,
    string SchemaMigrationId,
    string SchemaChecksum,
    string Purpose = "Regular",
    bool SchemaValid = true,
    bool IntegrityValid = true,
    string? Provider = null,
    int? RetentionCount = null,
    string? OperationId = null,
    string? UserPurpose = null,
    IReadOnlyList<VaultBackupAsset>? Assets = null,
    long AssetBytes = 0,
    string? AssetSnapshotDirectoryName = null);

public sealed record VaultBackupAsset(string RelativePath, string Sha256, long Bytes);

internal sealed record VaultBackupReceipt(
    string OperationId,
    string SourcePathSha256,
    string? UserPurpose,
    string ManifestFileName);

public sealed record BackupResult(
    string BackupPath, string ManifestPath, string Sha256, long Bytes,
    VaultBackupManifest Manifest, bool Replayed = false);

public enum VaultBackupPurpose { Regular, PreMigration }

public sealed class AccessBackupService(string provider = "Microsoft.ACE.OLEDB.12.0")
{
    public async Task<BackupResult> CreateAsync(
        string databasePath,
        string backupRoot,
        int retentionCount = 20,
        CancellationToken cancellationToken = default,
        VaultBackupPurpose purpose = VaultBackupPurpose.Regular)
    {
        var source = Path.GetFullPath(databasePath);
        AccessDatabaseUseGuard.ThrowIfLockFilePresent(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Database was not found.");
        using var ownerLease = AccessOwnerLease.Acquire(source);
        return await CreateCoreAsync(source, backupRoot, retentionCount, cancellationToken, purpose, null, null)
            .ConfigureAwait(false);
    }

    internal Task<BackupResult> CreateWhileOwnedAsync(
        string databasePath,
        string backupRoot,
        int retentionCount,
        string operationId,
        string? userPurpose,
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(Path.GetFullPath(databasePath), backupRoot, retentionCount, cancellationToken,
            VaultBackupPurpose.Regular, operationId, userPurpose);

    private async Task<BackupResult> CreateCoreAsync(
        string source,
        string backupRoot,
        int retentionCount,
        CancellationToken cancellationToken,
        VaultBackupPurpose purpose,
        string? operationId,
        string? userPurpose)
    {
        if (retentionCount < 2) throw new ArgumentOutOfRangeException(nameof(retentionCount), "Retention must preserve at least two backups.");
        if (!File.Exists(source)) throw new FileNotFoundException("Database was not found.");
        var directory = Path.Combine(Path.GetFullPath(backupRoot), "backups");
        Directory.CreateDirectory(directory);
        string? receiptPath = null;
        if (operationId is not null)
        {
            receiptPath = ReceiptPath(backupRoot, operationId);
            if (await FindReplayAsync(directory, source, operationId, userPurpose, receiptPath, cancellationToken).ConfigureAwait(false) is { } replay)
                return replay;
        }
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        var backup = Path.Combine(directory, $"WritingVault.{timestamp}.accdb");
        var manifestPath = Path.ChangeExtension(backup, ".manifest.json");
        var assetSnapshotName = $"WritingVault.{timestamp}.assets";
        var assetSnapshot = Path.Combine(directory, assetSnapshotName);
        if (File.Exists(backup) || File.Exists(manifestPath)) throw new IOException("A timestamped backup target already exists.");

        try
        {
            string sourceHash;
            long sourceBytes;
            await using (var sourceStream = new FileStream(
                             source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sourceBytes = sourceStream.Length;
                sourceHash = Hash(sourceStream);
                sourceStream.Position = 0;
                await using var backupStream = new FileStream(
                    backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await sourceStream.CopyToAsync(backupStream, cancellationToken).ConfigureAwait(false);
                await backupStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                backupStream.Flush(true);
            }

            var backupHash = HashFile(backup);
            if (!string.Equals(sourceHash, backupHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup hash did not match the closed source database.");

            var factory = new AccessConnectionFactory(backup, provider);
            var schema = await new AccessSchemaVerifier(factory).VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (!schema.IsValid && purpose == VaultBackupPurpose.Regular)
                throw new InvalidDataException("Backup failed schema verification.");
            var integrity = schema.IsValid
                ? await new AccessIntegrityVerifier(factory).VerifyAsync(cancellationToken).ConfigureAwait(false)
                : new IntegrityVerificationResult([]);
            if (!integrity.IsValid && purpose == VaultBackupPurpose.Regular)
                throw new InvalidDataException("Backup failed integrity verification.");

            var sourceAssetRoot = Path.Combine(Path.GetFullPath(backupRoot), "assets");
            var assets = schema.IsValid
                ? await ReadAndVerifyAssetsAsync(backup, sourceAssetRoot, cancellationToken).ConfigureAwait(false)
                : [];
            if (assets.Count > 0)
                await CopyAssetsAsync(assets, sourceAssetRoot, assetSnapshot, cancellationToken).ConfigureAwait(false);
            var recordedSchema = await ReadRecordedSchemaAsync(backup, cancellationToken).ConfigureAwait(false);

            var backupInfo = new FileInfo(backup);
            var manifest = new VaultBackupManifest(
                DateTime.UtcNow, HashPath(source), Path.GetFileName(source), sourceBytes, sourceHash,
                Path.GetFileName(backup), backupInfo.Length, backupHash,
                recordedSchema.MigrationId, recordedSchema.Checksum,
                purpose.ToString(), schema.IsValid, schema.IsValid && integrity.IsValid, provider, retentionCount,
                operationId, string.IsNullOrWhiteSpace(userPurpose) ? null : userPurpose.Trim(),
                assets, assets.Sum(asset => asset.Bytes), assets.Count == 0 ? null : assetSnapshotName);
            await using (var manifestStream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            if (receiptPath is not null)
                await WriteReceiptAsync(receiptPath, new(
                    operationId!, HashPath(source), manifest.UserPurpose, Path.GetFileName(manifestPath)), cancellationToken).ConfigureAwait(false);
            ApplyRetention(directory, retentionCount, backup);
            return new BackupResult(backup, manifestPath, backupHash, backupInfo.Length, manifest);
        }
        catch
        {
            TryDelete(manifestPath);
            TryDelete(backup);
            TryDeleteDirectory(assetSnapshot);
            throw;
        }
    }

    private async Task<BackupResult?> FindReplayAsync(
        string directory, string source, string operationId, string? userPurpose, string receiptPath, CancellationToken token)
    {
        var normalizedPurpose = string.IsNullOrWhiteSpace(userPurpose) ? null : userPurpose.Trim();
        if (File.Exists(receiptPath))
        {
            VaultBackupReceipt receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<VaultBackupReceipt>(await File.ReadAllTextAsync(receiptPath, token).ConfigureAwait(false))
                    ?? throw new InvalidDataException("The backup operation receipt is invalid.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The backup operation receipt is invalid.", exception);
            }
            if (!string.Equals(receipt.OperationId, operationId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.SourcePathSha256, HashPath(source), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.UserPurpose, normalizedPurpose, StringComparison.Ordinal))
                throw new InvalidOperationException("The backup mutation token was already used with different input.");
            if (!string.Equals(Path.GetFileName(receipt.ManifestFileName), receipt.ManifestFileName, StringComparison.Ordinal))
                throw new InvalidDataException("The backup operation receipt contains an invalid manifest name.");
            var retainedManifest = Path.Combine(directory, receipt.ManifestFileName);
            if (!File.Exists(retainedManifest))
                throw new InvalidOperationException("The backup created by this mutation token is no longer retained; use a new mutation token.");
            var retained = await VerifyAsync(retainedManifest, token).ConfigureAwait(false);
            var retainedBackup = Path.Combine(directory, retained.BackupFileName);
            return new BackupResult(retainedBackup, retainedManifest, retained.BackupSha256, retained.BackupBytes, retained, true);
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.manifest.json").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            VaultBackupManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<VaultBackupManifest>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is JsonException or IOException) { continue; }
            if (manifest is null || !string.Equals(manifest.OperationId, operationId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(manifest.SourcePathSha256, HashPath(source), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.UserPurpose, normalizedPurpose, StringComparison.Ordinal))
                throw new InvalidOperationException("The backup mutation token was already used with different input.");
            var verified = await VerifyAsync(path, token).ConfigureAwait(false);
            await WriteReceiptAsync(receiptPath, new(
                operationId, HashPath(source), normalizedPurpose, Path.GetFileName(path)), token).ConfigureAwait(false);
            var backupPath = Path.Combine(directory, verified.BackupFileName);
            return new BackupResult(backupPath, path, verified.BackupSha256, verified.BackupBytes, verified, true);
        }
        return null;
    }

    private static string ReceiptPath(string backupRoot, string operationId)
    {
        if (!Guid.TryParse(operationId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("operationId must be a non-empty GUID.", nameof(operationId));
        var directory = Path.Combine(Path.GetFullPath(backupRoot), "backup-receipts");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, parsed.ToString("D") + ".json");
    }

    private static async Task WriteReceiptAsync(string path, VaultBackupReceipt receipt, CancellationToken token)
    {
        if (File.Exists(path)) return;
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, receipt, cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { TryDelete(temporary); }
    }

    private async Task<IReadOnlyList<VaultBackupAsset>> ReadAndVerifyAssetsAsync(
        string backupDatabase, string assetRoot, CancellationToken token)
    {
        var factory = new AccessConnectionFactory(backupDatabase, provider);
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        var tables = AccessSchemaInspector.GetUserTableNames(connection);
        var rows = new List<VaultBackupAsset>();
        foreach (var table in new[] { "EntityImages", "StoryImages" }.Where(tables.Contains))
        {
            using var command = new AccessCommand(connection,
                $"SELECT [OriginalRelativePath],[OriginalSha256],[OriginalBytes] FROM [{table}]");
            rows.AddRange(await command.QueryAsync(reader => new VaultBackupAsset(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2)), token).ConfigureAwait(false));
        }
        if (tables.Contains("ImageHistoricalContent"))
        {
            using var historical = new AccessCommand(connection,
                "SELECT [OriginalRelativePath],[OriginalSha256],[OriginalBytes] " +
                "FROM [ImageHistoricalContent]");
            rows.AddRange(await historical.QueryAsync(reader => new VaultBackupAsset(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2)), token)
                .ConfigureAwait(false));
        }
        var distinct = new List<VaultBackupAsset>();
        foreach (var group in rows.GroupBy(asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var versions = group.Distinct().ToArray();
            if (versions.Length != 1)
                throw new InvalidDataException("Image records disagree about one immutable asset path.");
            distinct.Add(versions[0]);
        }
        assetRoot = Path.GetFullPath(assetRoot);
        foreach (var asset in distinct)
        {
            var path = SafeAssetPath(assetRoot, asset.RelativePath);
            EnsureNoReparsePoints(assetRoot, path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != asset.Bytes ||
                !HashFile(path).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A referenced image master is missing or does not match its stored hash.");
        }
        return distinct.OrderBy(asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task CopyAssetsAsync(
        IReadOnlyList<VaultBackupAsset> assets, string sourceRoot, string snapshotRoot, CancellationToken token)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        snapshotRoot = Path.GetFullPath(snapshotRoot);
        Directory.CreateDirectory(snapshotRoot);
        foreach (var asset in assets)
        {
            token.ThrowIfCancellationRequested();
            var source = SafeAssetPath(sourceRoot, asset.RelativePath);
            EnsureNoReparsePoints(sourceRoot, source);
            var target = SafeAssetPath(snapshotRoot, asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            var info = new FileInfo(target);
            if (info.Length != asset.Bytes || !HashFile(target).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A copied image asset does not match its backup manifest entry.");
        }
    }

    private static string SafeAssetPath(string root, string relativePath)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("An image asset path is rooted.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An image asset path escapes its configured root.");
        return path;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("An image asset path crosses a reparse point.");
            }
        }
    }

    public async Task<VaultBackupManifest> VerifyAsync(
        string manifestPath,
        CancellationToken cancellationToken = default,
        bool requireCurrentSchema = true)
    {
        var fullManifest = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<VaultBackupManifest>(File.ReadAllText(fullManifest))
            ?? throw new InvalidDataException("Backup manifest is invalid.");
        if (!string.Equals(Path.GetFileName(manifest.BackupFileName), manifest.BackupFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Backup manifest contains an invalid file name.");
        var backup = Path.Combine(Path.GetDirectoryName(fullManifest)!, manifest.BackupFileName);
        if (!File.Exists(backup)) throw new FileNotFoundException("Backup file is missing.");
        var info = new FileInfo(backup);
        if (info.Length != manifest.BackupBytes || !HashFile(backup).Equals(manifest.BackupSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup size or SHA-256 does not match its manifest.");
        var factory = new AccessConnectionFactory(backup, provider);
        var recordedSchema = await ReadRecordedSchemaAsync(backup, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(recordedSchema.MigrationId, manifest.SchemaMigrationId, StringComparison.Ordinal) ||
            !string.Equals(recordedSchema.Checksum, manifest.SchemaChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup manifest schema metadata does not match the copied database.");
        if (requireCurrentSchema)
        {
            if (!(await new AccessSchemaVerifier(factory).VerifyAsync(cancellationToken).ConfigureAwait(false)).IsValid)
                throw new InvalidDataException("Backup schema is incompatible.");
            if (!(await new AccessIntegrityVerifier(factory).VerifyAsync(cancellationToken).ConfigureAwait(false)).IsValid)
                throw new InvalidDataException("Backup integrity verification failed.");
            var recordedAssets = manifest.Assets ?? [];
            string snapshotRoot;
            if (recordedAssets.Count == 0)
            {
                if (manifest.AssetSnapshotDirectoryName is not null)
                    throw new InvalidDataException("An empty asset manifest names an unexpected snapshot directory.");
                snapshotRoot = Path.GetDirectoryName(fullManifest)!;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(manifest.AssetSnapshotDirectoryName) ||
                    !string.Equals(Path.GetFileName(manifest.AssetSnapshotDirectoryName), manifest.AssetSnapshotDirectoryName, StringComparison.Ordinal))
                    throw new InvalidDataException("Backup manifest contains an invalid asset snapshot directory name.");
                snapshotRoot = Path.Combine(Path.GetDirectoryName(fullManifest)!, manifest.AssetSnapshotDirectoryName);
                if (!Directory.Exists(snapshotRoot)) throw new DirectoryNotFoundException("Backup asset snapshot is missing.");
            }
            var actualAssets = await ReadAndVerifyAssetsAsync(backup, snapshotRoot, cancellationToken).ConfigureAwait(false);
            if (!actualAssets.SequenceEqual(recordedAssets) || actualAssets.Sum(asset => asset.Bytes) != manifest.AssetBytes)
                throw new InvalidDataException("Backup asset manifest does not match the database references.");
        }
        else
        {
            await using var connection = factory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        return manifest;
    }

    public async Task RestoreToNewPathAsync(
        string manifestPath,
        string targetPath,
        CancellationToken cancellationToken = default,
        bool requireCurrentSchema = true)
    {
        var manifest = await VerifyAsync(manifestPath, cancellationToken, requireCurrentSchema).ConfigureAwait(false);
        var target = Path.GetFullPath(targetPath);
        if (File.Exists(target)) throw new IOException("Restore target already exists; restore never overwrites a file.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.BackupFileName);
        File.Copy(backup, target, false);
        if (!HashFile(target).Equals(manifest.BackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidDataException("Restored file hash does not match the backup.");
        }
        var factory = new AccessConnectionFactory(target, provider);
        if (requireCurrentSchema &&
            (!(await new AccessSchemaVerifier(factory).VerifyAsync(cancellationToken).ConfigureAwait(false)).IsValid ||
             !(await new AccessIntegrityVerifier(factory).VerifyAsync(cancellationToken).ConfigureAwait(false)).IsValid))
        {
            File.Delete(target);
            throw new InvalidDataException("Restored database did not pass verification.");
        }
        if (!requireCurrentSchema)
        {
            await using var connection = factory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RestoreToNewPathsAsync(
        string manifestPath,
        string targetDatabasePath,
        string targetAssetRoot,
        CancellationToken cancellationToken = default,
        bool requireCurrentSchema = true)
    {
        var manifest = await VerifyAsync(manifestPath, cancellationToken, requireCurrentSchema).ConfigureAwait(false);
        var targetDatabase = Path.GetFullPath(targetDatabasePath);
        var targetAssets = Path.GetFullPath(targetAssetRoot);
        if (File.Exists(targetDatabase)) throw new IOException("Restore database target already exists; restore never overwrites a file.");
        if (Directory.Exists(targetAssets) || File.Exists(targetAssets)) throw new IOException("Restore asset target already exists; restore never overwrites a path.");

        var temporaryDatabase = targetDatabase + ".restoring-" + Guid.NewGuid().ToString("N");
        var temporaryAssets = targetAssets + ".restoring-" + Guid.NewGuid().ToString("N");
        var assetsInstalled = false;
        try
        {
            var assets = manifest.Assets ?? [];
            if (assets.Count > 0)
            {
                var snapshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.AssetSnapshotDirectoryName!);
                await CopyAssetsAsync(assets, snapshot, temporaryAssets, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(temporaryAssets);
            }
            await RestoreToNewPathAsync(manifestPath, temporaryDatabase, cancellationToken, requireCurrentSchema).ConfigureAwait(false);
            Directory.Move(temporaryAssets, targetAssets);
            assetsInstalled = true;
            File.Move(temporaryDatabase, targetDatabase, false);
        }
        catch
        {
            TryDelete(temporaryDatabase);
            TryDeleteDirectory(temporaryAssets);
            if (assetsInstalled) TryDeleteDirectory(targetAssets);
            throw;
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Hash(stream);
    }

    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

    internal static string HashPath(string path)
    {
        var canonical = LocalPathIdentity.Canonicalize(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<(string MigrationId, string Checksum)> ReadRecordedSchemaAsync(
        string databasePath, CancellationToken token)
    {
        var factory = new AccessConnectionFactory(databasePath, provider);
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        if (!AccessSchemaInspector.GetUserTableNames(connection).Contains("SchemaMigrations"))
            return ("unversioned", new string('0', 64));
        using var command = new AccessCommand(connection,
            "SELECT TOP 1 [MigrationId],[Checksum] FROM [SchemaMigrations] WHERE [Status]='Applied' ORDER BY [AppliedAtUtc] DESC,[MigrationId] DESC");
        var rows = await command.QueryAsync(reader => (
            reader.GetString(0), reader.GetString(1)), token).ConfigureAwait(false);
        return rows.Count == 0 ? ("unversioned", new string('0', 64)) : rows[0];
    }

    private static void ApplyRetention(string directory, int retentionCount, string newest)
    {
        var manifests = Directory.GetFiles(directory, "WritingVault.*.manifest.json")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        foreach (var manifest in manifests.Skip(retentionCount))
        {
            var database = manifest[..^".manifest.json".Length] + ".accdb";
            if (string.Equals(database, newest, StringComparison.OrdinalIgnoreCase)) continue;
            var assetSnapshot = manifest[..^".manifest.json".Length] + ".assets";
            TryDelete(database);
            TryDeleteDirectory(assetSnapshot);
            TryDelete(manifest);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
