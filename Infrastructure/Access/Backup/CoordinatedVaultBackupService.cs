using System.Data.OleDb;

namespace WritingVaultMcp.Infrastructure.Access.Backup;

public sealed record CoordinatedBackupResult(
    DateTime CreatedAtUtc,
    long DatabaseBytes,
    string DatabaseSha256,
    string SchemaMigration,
    bool SchemaValid,
    bool IntegrityValid,
    int RetentionCount,
    int AssetCount,
    long AssetBytes,
    bool Replayed);

/// <summary>
/// Creates a regular verified backup from inside the elected backend. The
/// coordinator waits for an active write to finish and prevents another write
/// from starting while the stable database stream is hashed and copied.
/// </summary>
public sealed class CoordinatedVaultBackupService(
    AccessConnectionFactory connectionFactory,
    VaultWriteCoordinator coordinator,
    string backupRoot,
    int retentionCount,
    string provider = "Microsoft.ACE.OLEDB.12.0")
{
    public async Task<DateTime?> FindLatestVerifiedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.Combine(Path.GetFullPath(backupRoot), "backups");
            if (!Directory.Exists(directory)) return null;
            var verifier = new AccessBackupService(provider);
            foreach (var path in Directory.EnumerateFiles(directory, "WritingVault.*.manifest.json")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var manifest = await verifier.VerifyAsync(path, cancellationToken).ConfigureAwait(false);
                    if (manifest is { SchemaValid: true, IntegrityValid: true } &&
                        string.Equals(manifest.Purpose, VaultBackupPurpose.Regular.ToString(), StringComparison.Ordinal))
                        return manifest.CreatedAtUtc;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // A damaged or partial entry is not a successful backup.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public async Task<CoordinatedBackupResult> CreateAsync(
        string operationId,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(operationId, out var operationGuid) || operationGuid == Guid.Empty)
            throw new ArgumentException("operationId must be a non-empty GUID.", nameof(operationId));
        if (purpose is not null && (string.IsNullOrWhiteSpace(purpose) || purpose.Trim().Length > 255))
            throw new ArgumentException("purpose must be omitted or contain 1 through 255 characters.", nameof(purpose));
        if (retentionCount < 2) throw new InvalidOperationException("Configured backup retention must preserve at least two backups.");

        return await coordinator.ExecuteConsistentReadAsync(async () =>
        {
            // All application writes are now quiesced. Releasing idle provider
            // objects makes the stable file-copy boundary explicit; active
            // read-only queries do not change database pages.
            OleDbConnection.ReleaseObjectPool();
            var result = await new AccessBackupService(provider).CreateWhileOwnedAsync(
                connectionFactory.DatabasePath,
                backupRoot,
                retentionCount,
                operationGuid.ToString("D"),
                purpose,
                cancellationToken).ConfigureAwait(false);
            var manifest = result.Manifest;
            return new CoordinatedBackupResult(
                manifest.CreatedAtUtc,
                result.Bytes,
                result.Sha256,
                manifest.SchemaMigrationId,
                manifest.SchemaValid,
                manifest.IntegrityValid,
                manifest.RetentionCount ?? retentionCount,
                manifest.Assets?.Count ?? 0,
                manifest.AssetBytes,
                result.Replayed);
        }, cancellationToken).ConfigureAwait(false);
    }
}
