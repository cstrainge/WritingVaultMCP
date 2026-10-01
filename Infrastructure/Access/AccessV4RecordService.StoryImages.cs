using System.Data.OleDb;
using System.Globalization;
using WritingVaultMcp.Application;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4RecordService
{
    private Task<VaultMutationResult> LifecycleStoryImageAsync(
        ResolvedVaultReference image, V4VersionedRecordRequest request, bool restore,
        string operation, int continuity, CancellationToken token) =>
        coordinator.ExecuteAsync(operation, "v4.story.image.lifecycle", request,
            restore ? "record_restore" : "record_soft_delete", session.ClientLabel,
            async (context, ct) =>
            {
                using var find = context.Command(
                        "SELECT [Version],[IsDeleted],[ContinuityId],[OwnerKind]," +
                        "[RelationshipId],[EntityEventId],[RelationshipEventId] " +
                        "FROM [StoryImages] WHERE [Id]=?")
                    .Add(OleDbType.Integer, image.Id);
                var rows = await find.QueryAsync(reader => new
                {
                    Version = reader.GetInt32(0), Deleted = reader.GetBoolean(1),
                    Continuity = reader.GetInt32(2), Kind = reader.GetString(3),
                    Relationship = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    EntityEvent = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                    RelationshipEvent = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6)
                }, ct).ConfigureAwait(false);
                if (rows.Count != 1 || rows[0].Continuity != continuity)
                    throw new VaultCommandException("record.not_found", "The image was not found.");
                var old = rows[0];
                if (old.Version != request.ExpectedVersion)
                    throw new VaultCommandException("concurrency.conflict",
                        $"The current version is {old.Version}.", actualVersion: old.Version);
                if (old.Deleted == !restore)
                    return new VaultMutationOutcome("StoryImage", image.Id.ToString(), old.Version,
                        restore ? "restore" : "delete", new { unchanged = true });
                var ownerId = old.Kind switch
                {
                    "Continuity" => continuity,
                    "Relationship" => old.Relationship ?? 0,
                    "EntityEvent" => old.EntityEvent ?? 0,
                    "RelationshipEvent" => old.RelationshipEvent ?? 0,
                    _ => 0
                };
                if (restore)
                {
                    var ownerSql = old.Kind switch
                    {
                        "Continuity" => "SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False",
                        "Relationship" => "SELECT COUNT(*) FROM [CharacterRelationships] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False",
                        "EntityEvent" => "SELECT COUNT(*) FROM [EntityEvents] AS e INNER JOIN [CanonEntities] AS c ON e.[EntityId]=c.[Id] WHERE e.[Id]=? AND c.[ContinuityId]=? AND e.[IsDeleted]=False AND c.[IsDeleted]=False",
                        "RelationshipEvent" => "SELECT COUNT(*) FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] WHERE e.[Id]=? AND r.[ContinuityId]=? AND e.[IsDeleted]=False AND r.[IsDeleted]=False",
                        _ => throw new VaultCommandException("image.owner_invalid", "The image owner is invalid.")
                    };
                    using var check = context.Command(ownerSql).Add(OleDbType.Integer, ownerId);
                    if (old.Kind != "Continuity") check.Add(OleDbType.Integer, continuity);
                    if (Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("record.deleted",
                            "Restore the image owner before restoring this image.");
                }
                var now = DateTime.UtcNow;
                using var update = context.Command(restore
                        ? "UPDATE [StoryImages] SET [IsDeleted]=False,[DeletedAtUtc]=Null," +
                          "[DeletedOperationId]=Null,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                          "WHERE [Id]=? AND [Version]=?"
                        : "UPDATE [StoryImages] SET [IsDeleted]=True,[DeletedAtUtc]=?," +
                          "[DeletedOperationId]=?,[UpdatedAtUtc]=?,[IsPrimary]=False," +
                          "[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?");
                if (restore) update.Add(OleDbType.Date, now);
                else update.Add(OleDbType.Date, now)
                    .Add(OleDbType.VarWChar, context.OperationId, 36)
                    .Add(OleDbType.Date, now);
                update.Add(OleDbType.Integer, image.Id).Add(OleDbType.Integer, old.Version);
                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new VaultCommandException("concurrency.conflict", "The image changed.");
                await EnsureStoryImagePrimaryAsync(context, old.Kind, continuity, ownerId, now, ct)
                    .ConfigureAwait(false);
                return new VaultMutationOutcome("StoryImage",
                    image.Id.ToString(CultureInfo.InvariantCulture), old.Version + 1,
                    restore ? "restore" : "delete", new { isDeleted = !restore }, old.Version);
            }, token);

    private static async Task EnsureStoryImagePrimaryAsync(VaultWriteContext context,
        string kind, int continuity, int ownerId, DateTime now, CancellationToken token)
    {
        var ownerColumn = kind switch
        {
            "Continuity" => null,
            "Relationship" => "RelationshipId",
            "EntityEvent" => "EntityEventId",
            "RelationshipEvent" => "RelationshipEventId",
            _ => throw new VaultCommandException("image.owner_invalid", "The image owner is invalid.")
        };
        var where = "[ContinuityId]=? AND [OwnerKind]=?" +
            (ownerColumn is null ? "" : $" AND [{ownerColumn}]=?") +
            " AND [IsDeleted]=False";
        AccessCommand Scoped(string sql)
        {
            var command = context.Command(sql).Add(OleDbType.Integer, continuity)
                .Add(OleDbType.VarWChar, kind, 30);
            if (ownerColumn is not null) command.Add(OleDbType.Integer, ownerId);
            return command;
        }
        using var current = Scoped(
            $"SELECT COUNT(*) FROM [StoryImages] WHERE {where} AND [IsPrimary]=True");
        if (Convert.ToInt32(await current.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
            return;
        using var next = Scoped(
            $"SELECT TOP 1 [Id] FROM [StoryImages] WHERE {where} ORDER BY [Id]");
        var nextId = await next.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (nextId is null or DBNull) return;
        using var promote = context.Command(
                "UPDATE [StoryImages] SET [IsPrimary]=True,[UpdatedAtUtc]=?," +
                "[Version]=[Version]+1 WHERE [Id]=?")
            .Add(OleDbType.Date, now)
            .Add(OleDbType.Integer, Convert.ToInt32(nextId, CultureInfo.InvariantCulture));
        await promote.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
