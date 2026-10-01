using System.Data.OleDb;
using System.Security.Cryptography;
using System.Text;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed record WritingVaultStorageOptions(string BackupRoot);

public sealed partial class AccessVaultService
{
    public async Task<VaultMutationResult> AddSourceSnapshotAsync(AddSourceSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.OperationId, out _)) return new(false, "validation.operation_id", Message: "operationId must be a GUID.");
        if (request.SourceId <= 0) return new(false, "validation.failed", Message: "SourceId must be positive.");
        if (string.IsNullOrEmpty(request.Content)) return new(false, "validation.content", Message: "Snapshot content is required.");
        if (Encoding.UTF8.GetByteCount(request.Content) > 1_000_000) return new(false, "validation.content_size", Message: "Snapshot content cannot exceed 1,000,000 UTF-8 bytes.");
        if (request.MediaType is { Length: > 100 }) return new(false, "validation.media_type", Message: "MediaType cannot exceed 100 characters.");
        if (request.RetrievedAtUtc is { } retrieved && (retrieved.Kind != DateTimeKind.Utc || retrieved.Year < StoryDate.AccessMinimum.Year))
            return new(false, "validation.retrieved_at", Message: "RetrievedAtUtc must be a UTC value in Access's supported date range.");
        var bytes = Encoding.UTF8.GetBytes(request.Content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var relative = Path.Combine("source-cache", hash[..2], hash + ".bin");
        var full = Path.Combine(_storage.BackupRoot, relative);
        await SnapshotCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var created = false;
        try
        {
            var result = await writes.ExecuteAsync(
                request.OperationId, "source.snapshot.add", request, "source_snapshot_add", request.ClientLabel,
                async (context, token) =>
                {
                    using var source = context.Command("SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, request.SourceId);
                    if (Convert.ToInt32(await source.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("entity.not_found", "Source was not found.");
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (!File.Exists(full))
                    {
                        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                            try { File.Move(temporary, full, false); created = true; }
                            catch (IOException) when (File.Exists(full)) { }
                        }
                        finally
                        {
                            try { if (File.Exists(temporary)) File.Delete(temporary); }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    if (!HashFile(full).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new VaultCommandException("storage.snapshot_hash", "Cached snapshot hash verification failed.");
                    var now = DateTime.UtcNow;
                    using var insert = context.Command("INSERT INTO [SourceSnapshots] ([SourceId],[RetrievedAtUtc],[MediaType],[ByteSize],[ContentSha256],[RelativeCachePath],[ExtractionStatus],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?)")
                        .Add(OleDbType.Integer, request.SourceId).Add(OleDbType.Date, request.RetrievedAtUtc ?? now)
                        .Add(OleDbType.VarWChar, request.MediaType, 100).Add(OleDbType.Integer, bytes.Length)
                        .Add(OleDbType.VarWChar, hash, 64).Add(OleDbType.VarWChar, relative, 255)
                        .Add(OleDbType.VarWChar, "Cached", 50).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    var id = await IdentityAsync(context, token).ConfigureAwait(false);
                    var retrievedAt = request.RetrievedAtUtc ?? now;
                    using var updateSource = context.Command(
                            "UPDATE [Sources] SET [LastRetrievedAtUtc]=IIF([LastRetrievedAtUtc] IS NULL OR [LastRetrievedAtUtc]<?,?,[LastRetrievedAtUtc]),[RetrievalStatus]='Cached',[UpdatedAtUtc]=? WHERE [Id]=?")
                        .Add(OleDbType.Date, retrievedAt).Add(OleDbType.Date, retrievedAt)
                        .Add(OleDbType.Date, now).Add(OleDbType.Integer, request.SourceId);
                    await updateSource.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return new VaultMutationOutcome("SourceSnapshot", id.ToString(), 1, "cache", new { request.SourceId, hash, bytes = bytes.Length });
                }, cancellationToken).ConfigureAwait(false);
            if (!result.Success && created) await DeleteUnreferencedCacheFileAsync(full, hash, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (created) await DeleteUnreferencedCacheFileAsync(full, hash, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally { SnapshotCacheGate.Release(); }
    }

    private async Task DeleteUnreferencedCacheFileAsync(string path, string hash, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = new AccessCommand(connection, "SELECT COUNT(*) FROM [SourceSnapshots] WHERE [ContentSha256]=?")
                .Add(OleDbType.VarWChar, hash, 64);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0 && File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OleDbException) { }
    }

    public Task<VaultMutationResult> CreateClaimAsync(CreateClaimRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimText)) return Task.FromResult(new VaultMutationResult(false, "validation.claim_text", Message: "ClaimText is required."));
        if (request.ClaimText.Length > 1_000_000) return Task.FromResult(new VaultMutationResult(false, "validation.claim_text", Message: "ClaimText cannot exceed 1,000,000 characters."));
        if (request.Confidence is < 0 or > 1) return Task.FromResult(new VaultMutationResult(false, "validation.confidence", Message: "Confidence must be between 0 and 1."));
        if (request.EntityIds is null || request.Evidence is null) return Task.FromResult(new VaultMutationResult(false, "validation.claim_collections", Message: "EntityIds and Evidence are required arrays."));
        if (request.EntityIds.Count == 0) return Task.FromResult(new VaultMutationResult(false, "validation.claim_target", Message: "At least one entity target is required."));
        if (request.EntityIds.Count > 100 || request.Evidence.Count > 100) return Task.FromResult(new VaultMutationResult(false, "validation.list_size", Message: "Claims support at most 100 entity targets and 100 evidence links."));
        if (request.Evidence.Select(item => item.SourceId).Distinct().Count() != request.Evidence.Count)
            return Task.FromResult(new VaultMutationResult(false, "validation.duplicate_evidence", Message: "A source may appear only once in a claim's evidence list."));
        if (string.IsNullOrWhiteSpace(request.ClaimStatus) || request.ClaimStatus.Length > 30)
            return Task.FromResult(new VaultMutationResult(false, "validation.claim_status", Message: "ClaimStatus is required and limited to 30 characters."));
        if (request.TargetField is { Length: > 100 }) return Task.FromResult(new VaultMutationResult(false, "validation.target_field", Message: "TargetField cannot exceed 100 characters."));
        if (request.Evidence.Any(item => NormalizeEvidenceRelation(item.EvidenceRelation) is null || item.Locator is { Length: > 255 }))
            return Task.FromResult(new VaultMutationResult(false, "validation.evidence", Message: "EvidenceRelation must be Supports, Contradicts, or Context; Locator is limited to 255 characters."));
        return writes.ExecuteAsync(
            request.OperationId, "claim.create", request, "claim_create", request.ClientLabel,
            async (context, token) =>
            {
                await RequireContinuityAsync(context, request.ContinuityId, token).ConfigureAwait(false);
                foreach (var entityId in request.EntityIds.Distinct())
                    await RequireEntityAsync(context, entityId, null, request.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [Claims] ([ContinuityId],[ClaimText],[ClaimStatus],[Confidence],[TargetField],[Commentary],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ContinuityId).Add(OleDbType.LongVarWChar, request.ClaimText.Trim())
                    .Add(OleDbType.VarWChar, request.ClaimStatus, 30).Add(OleDbType.Double, request.Confidence)
                    .Add(OleDbType.VarWChar, request.TargetField, 100).Add(OleDbType.LongVarWChar, request.Commentary)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var claimId = await IdentityAsync(context, token).ConfigureAwait(false);
                foreach (var entityId in request.EntityIds.Distinct())
                {
                    using var target = context.Command("INSERT INTO [ClaimEntities] ([ClaimId],[EntityId]) VALUES (?,?)")
                        .Add(OleDbType.Integer, claimId).Add(OleDbType.Integer, entityId);
                    await target.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                foreach (var evidence in request.Evidence)
                {
                    using var check = context.Command("SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, evidence.SourceId);
                    if (Convert.ToInt32(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("entity.not_found", $"Source {evidence.SourceId} was not found.");
                    if (evidence.SourceSnapshotId is { } snapshotId)
                    {
                        using var snapshot = context.Command("SELECT COUNT(*) FROM [SourceSnapshots] WHERE [Id]=? AND [SourceId]=? AND [IsDeleted]=False")
                            .Add(OleDbType.Integer, snapshotId).Add(OleDbType.Integer, evidence.SourceId);
                        if (Convert.ToInt32(await snapshot.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                            throw new VaultCommandException("source.snapshot_mismatch", "Snapshot does not belong to the selected source.");
                    }
                    using var link = context.Command("INSERT INTO [ClaimSources] ([ClaimId],[SourceId],[SourceSnapshotId],[EvidenceRelation],[Locator],[EvidenceExcerpt],[Summary],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?)")
                        .Add(OleDbType.Integer, claimId).Add(OleDbType.Integer, evidence.SourceId).Add(OleDbType.Integer, evidence.SourceSnapshotId)
                        .Add(OleDbType.VarWChar, NormalizeEvidenceRelation(evidence.EvidenceRelation), 30).Add(OleDbType.VarWChar, evidence.Locator, 255)
                        .Add(OleDbType.LongVarWChar, evidence.EvidenceExcerpt).Add(OleDbType.LongVarWChar, evidence.Summary)
                        .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                    await link.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                return new VaultMutationOutcome("Claim", claimId.ToString(), 1, "create", new { request.ContinuityId, targets = request.EntityIds.Count, evidence = request.Evidence.Count });
            }, cancellationToken);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? NormalizeEvidenceRelation(string? value)
    {
        if (value is null) return null;
        return value.Trim().ToUpperInvariant() switch
        {
            "SUPPORTS" => "Supports",
            "CONTRADICTS" => "Contradicts",
            "CONTEXT" => "Context",
            _ => null
        };
    }
}
