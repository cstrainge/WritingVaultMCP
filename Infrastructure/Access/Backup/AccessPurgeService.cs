using System.Data.OleDb;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Infrastructure.Access.Backup;

public sealed record PurgePreviewResult(
    string Token, int EntityId, int ExpectedVersion, DateTime ExpiresAtUtc,
    IReadOnlyList<BlockingReference> Blockers, bool CanPurge);

internal sealed record StoredPurgePreview(
    string Token, string DatabaseFileName, string DatabasePathSha256, int EntityId, int ExpectedVersion,
    string EntityType, DateTime ExpiresAtUtc, IReadOnlyList<BlockingReference> Blockers);

public sealed class AccessPurgeService(string provider = "Microsoft.ACE.OLEDB.12.0")
{
    public async Task<PurgePreviewResult> PreviewAsync(
        string databasePath, string backupRoot, int entityId, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var database = Path.GetFullPath(databasePath);
        AccessDatabaseUseGuard.ThrowIfLockFilePresent(database);
        using var ownerLease = AccessOwnerLease.Acquire(database);
        var factory = new AccessConnectionFactory(database, provider);
        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var entity = new AccessCommand(connection, "SELECT [EntityType],[Version],[IsDeleted] FROM [CanonEntities] WHERE [Id]=?")
            .Add(OleDbType.Integer, entityId);
        var rows = await entity.QueryAsync(reader => (Type: reader.GetString(0), Version: reader.GetInt32(1), Deleted: reader.GetBoolean(2)), cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) throw new InvalidOperationException("Entity was not found.");
        if (!rows[0].Deleted) throw new InvalidOperationException("Entity must be soft-deleted before purge preview.");
        if (rows[0].Version != expectedVersion) throw new InvalidOperationException("Entity version does not match.");
        var blockers = await FindBlockersAsync(connection, entityId, rows[0].Type, cancellationToken).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("D");
        var expires = DateTime.UtcNow.AddMinutes(30);
        var stored = new StoredPurgePreview(token, Path.GetFileName(database), AccessBackupService.HashPath(database), entityId, expectedVersion, rows[0].Type, expires, blockers);
        var directory = Path.Combine(Path.GetFullPath(backupRoot), "purge-previews");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, token + ".json"), JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        return new PurgePreviewResult(token, entityId, expectedVersion, expires, blockers, blockers.Count == 0);
    }

    public async Task ExecuteAsync(
        string databasePath, string backupRoot, string token, string recentBackupManifest,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(token, "D", out _)) throw new ArgumentException("Purge token is invalid.");
        var database = Path.GetFullPath(databasePath);
        AccessDatabaseUseGuard.ThrowIfLockFilePresent(database);
        using var ownerLease = AccessOwnerLease.Acquire(database);
        var tokenPath = Path.Combine(Path.GetFullPath(backupRoot), "purge-previews", token + ".json");
        if (!File.Exists(tokenPath))
        {
            if (await WasCompletedAsync(database, token, cancellationToken).ConfigureAwait(false)) return;
            throw new FileNotFoundException("Purge preview token was not found.");
        }
        var preview = JsonSerializer.Deserialize<StoredPurgePreview>(File.ReadAllText(tokenPath))
            ?? throw new InvalidDataException("Purge preview is invalid.");
        if (!string.Equals(preview.Token, token, StringComparison.Ordinal) || preview.ExpiresAtUtc < DateTime.UtcNow)
            throw new InvalidOperationException("Purge preview is expired or does not match the token.");
        if (!string.Equals(preview.DatabaseFileName, Path.GetFileName(database), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Purge preview belongs to another database.");
        if (!string.Equals(preview.DatabasePathSha256, AccessBackupService.HashPath(database), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Purge preview belongs to another database path.");
        if (preview.Blockers.Count > 0) throw new InvalidOperationException("Purge preview contains blocking relationships.");
        var backup = await new AccessBackupService(provider).VerifyAsync(recentBackupManifest, cancellationToken).ConfigureAwait(false);
        if (backup.CreatedAtUtc < DateTime.UtcNow.AddHours(-24) || backup.CreatedAtUtc > DateTime.UtcNow.AddMinutes(5))
            throw new InvalidOperationException("A verified backup from the last 24 hours is required.");
        if (!string.Equals(backup.Purpose, VaultBackupPurpose.Regular.ToString(), StringComparison.Ordinal) ||
            !backup.SchemaValid || !backup.IntegrityValid)
            throw new InvalidOperationException("Purge requires a verified regular backup with valid schema and integrity.");
        if (!string.Equals(backup.SourceFileName, Path.GetFileName(database), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(backup.SourcePathSha256) ||
            !string.Equals(backup.SourcePathSha256, AccessBackupService.HashPath(database), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verified backup was not created from this database path.");
        if (!string.Equals(backup.SourceSha256, AccessBackupService.HashFile(database), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The database changed after the verified pre-purge backup was created.");

        var factory = new AccessConnectionFactory(database, provider);
        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var replay = new AccessCommand(connection, "SELECT [Status] FROM [ProcessedOperations] WHERE [OperationId]=?", transaction).Add(OleDbType.VarWChar, token, 36))
            {
                var status = await replay.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (status is not null and not DBNull)
                {
                    if (string.Equals(Convert.ToString(status), "Completed", StringComparison.Ordinal))
                    {
                        transaction.Rollback();
                        TryDelete(tokenPath);
                        return;
                    }
                    throw new InvalidOperationException("The purge operation is already in progress or incomplete.");
                }
            }
            var blockers = await FindBlockersAsync(connection, preview.EntityId, preview.EntityType, cancellationToken, transaction).ConfigureAwait(false);
            if (blockers.Count > 0) throw new InvalidOperationException("Relationships changed after preview; purge was refused.");
            var now = DateTime.UtcNow;
            var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                preview.EntityId,
                preview.ExpectedVersion,
                Backup = backup.BackupSha256
            }))));
            using (var operation = new AccessCommand(connection, "INSERT INTO [ProcessedOperations] ([OperationId],[CommandType],[InputSha256],[Status],[StartedAtUtc]) VALUES (?,?,?,?,?)", transaction)
                       .Add(OleDbType.VarWChar, token, 36).Add(OleDbType.VarWChar, "admin.entity.purge", 100)
                       .Add(OleDbType.VarWChar, inputHash, 64).Add(OleDbType.VarWChar, "InProgress", 20).Add(OleDbType.Date, now))
                await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using (var subtype = new AccessCommand(connection, $"DELETE FROM [{SubtypeTable(preview.EntityType)}] WHERE [EntityId]=?", transaction).Add(OleDbType.Integer, preview.EntityId))
                await subtype.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using (var entity = new AccessCommand(connection, "DELETE FROM [CanonEntities] WHERE [Id]=? AND [Version]=? AND [IsDeleted]=True", transaction)
                       .Add(OleDbType.Integer, preview.EntityId).Add(OleDbType.Integer, preview.ExpectedVersion))
            {
                if (await entity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Entity changed after preview; purge was refused.");
            }
            using (var journal = new AccessCommand(connection, "INSERT INTO [ChangeLog] ([OperationId],[ChangedAtUtc],[ClientLabel],[ToolName],[Action],[RecordType],[RecordKey],[VersionBefore],[VersionAfter],[ChangeJson]) VALUES (?,?,?,?,?,?,?,?,?,?)", transaction)
                       .Add(OleDbType.VarWChar, token, 36).Add(OleDbType.Date, now).Add(OleDbType.VarWChar, "admin", 100)
                       .Add(OleDbType.VarWChar, "admin_purge", 100).Add(OleDbType.VarWChar, "purge", 50)
                       .Add(OleDbType.VarWChar, "CanonEntity", 100).Add(OleDbType.VarWChar, preview.EntityId.ToString(), 100)
                       .Add(OleDbType.Integer, preview.ExpectedVersion).Add(OleDbType.Integer, null)
                       .Add(OleDbType.LongVarWChar, JsonSerializer.Serialize(new { preview.EntityType, BackupSha256 = backup.BackupSha256 })))
                await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using (var complete = new AccessCommand(connection, "UPDATE [ProcessedOperations] SET [Status]='Completed',[ResultReference]=?,[CompletedAtUtc]=? WHERE [OperationId]=?", transaction)
                       .Add(OleDbType.VarWChar, JsonSerializer.Serialize(new { Success = true, EntityId = preview.EntityId }), 255)
                       .Add(OleDbType.Date, now).Add(OleDbType.VarWChar, token, 36))
                await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            TryDelete(tokenPath);
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    private async Task<bool> WasCompletedAsync(string database, string token, CancellationToken cancellationToken)
    {
        var factory = new AccessConnectionFactory(database, provider);
        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new AccessCommand(connection, "SELECT [Status] FROM [ProcessedOperations] WHERE [OperationId]=?")
            .Add(OleDbType.VarWChar, token, 36);
        return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)), "Completed", StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<BlockingReference>> FindBlockersAsync(
        OleDbConnection connection, int entityId, string entityType, CancellationToken token, OleDbTransaction? transaction = null)
    {
        var probes = new List<(string Name, string Table, string Column)>
        {
            ("EntityNotes","EntityNotes","EntityId"), ("EntityEvents","EntityEvents","EntityId"),
            ("EntityImages","EntityImages","EntityId"),
            ("EntityTags","EntityTags","EntityId"), ("EntitySources","EntitySources","EntityId"),
            ("ProjectMembership","ProjectEntities","MemberEntityId"), ("WorldEventParticipation","WorldEventParticipants","ParticipantEntityId"),
            ("ClaimTargets","ClaimEntities","EntityId")
        };
        probes.AddRange(entityType switch
        {
            "Project" => [("ProjectAssignments","ProjectEntities","ProjectId")],
            "Location" => [("ChildLocations","Locations","ParentLocationId"), ("Birthplaces","Characters","BirthLocationId"), ("Residences","CharacterResidences","LocationId"), ("OrganizationLocations","OrganizationLocations","LocationId"), ("ObjectLocations","ObjectLocationPeriods","LocationId"), ("EventLocations","WorldEventLocations","LocationId")],
            "Character" => [("Aliases","CharacterAliases","CharacterId"), ("Residences","CharacterResidences","CharacterId"), ("Memberships","OrganizationMemberships","CharacterId"), ("RelationshipSources","CharacterRelationships","SourceCharacterId"), ("RelationshipTargets","CharacterRelationships","TargetCharacterId"), ("OwnershipPrincipals","OwnershipPrincipals","CharacterId")],
            "Organization" => [("Aliases","OrganizationAliases","OrganizationId"), ("Memberships","OrganizationMemberships","OrganizationId"), ("OrganizationLocations","OrganizationLocations","OrganizationId"), ("OwnershipPrincipals","OwnershipPrincipals","OrganizationId")],
            "Object" => [("OwnershipPeriods","ObjectOwnershipPeriods","ObjectId"), ("CustodyPeriods","ObjectCustodyPeriods","ObjectId"), ("ObjectLocations","ObjectLocationPeriods","ObjectId")],
            "WorldEvent" => [("EntityEventReferences","EntityEvents","WorldEventId"), ("Participants","WorldEventParticipants","WorldEventId"), ("EventLocations","WorldEventLocations","WorldEventId")],
            _ => []
        });
        var blockers = new List<BlockingReference>();
        foreach (var probe in probes)
        {
            using var command = new AccessCommand(connection, $"SELECT COUNT(*) FROM [{probe.Table}] WHERE [{probe.Column}]=?", transaction).Add(OleDbType.Integer, entityId);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            if (count > 0) blockers.Add(new BlockingReference(probe.Name, count));
        }
        return blockers;
    }

    private static string SubtypeTable(string type) => type switch
    {
        "Project" => "Projects", "Location" => "Locations", "Character" => "Characters",
        "Organization" => "Organizations", "Object" => "Objects", "WorldEvent" => "WorldEvents",
        _ => throw new InvalidOperationException("Unsupported entity type.")
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
