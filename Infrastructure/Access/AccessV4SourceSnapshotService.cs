using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Reads verified, bounded text from the immutable cache named by a source snapshot.</summary>
public sealed class AccessV4SourceSnapshotService(
    IAccessConnectionFactory factory,
    VaultReferenceService references,
    VaultSessionContext session,
    V4CursorCodec cursors,
    WritingVaultStorageOptions storage,
    AccessV4ReadService reads)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<V4SourceSnapshotViewResult> ViewAsync(
        V4SourceSnapshotViewRequest request, CancellationToken token = default)
    {
        if (request.Limit is < 2 or > V4ContractLimits.MaximumSnapshotTextPageCharacters)
            throw new ArgumentException($"limit must be between 2 and {V4ContractLimits.MaximumSnapshotTextPageCharacters}.");

        // Sources and their snapshots are vault-global, but a viewer must still select a continuity.
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference target;
        try { target = await references.ResolveAsync(request.SnapshotRef, continuity, token, "SourceSnapshot"); }
        catch (ArgumentException exception) { throw new V4ResolutionException("reference.invalid", exception.Message); }
        catch (KeyNotFoundException exception) { throw new V4ResolutionException("record.not_found", exception.Message); }
        if (target.IsDeleted && !request.IncludeDeleted)
            throw new V4ResolutionException("record.deleted", "The source snapshot is deleted.");

        await using var connection = factory.Create();
        await connection.OpenAsync(token);
        using var command = new AccessCommand(connection,
            "SELECT x.[RelativeCachePath],x.[ContentSha256],x.[ByteSize],x.[MediaType],x.[RetrievedAtUtc],x.[Version] " +
            "FROM [SourceSnapshots] AS x INNER JOIN [Sources] AS s ON x.[SourceId]=s.[Id] " +
            "WHERE x.[Id]=?" + (request.IncludeDeleted ? "" : " AND x.[IsDeleted]=False AND s.[IsDeleted]=False"))
            .Add(OleDbType.Integer, target.Id);
        var rows = await command.QueryAsync(reader => new
        {
            RelativePath = reader.GetString(0), Hash = reader.GetString(1),
            ByteSize = reader.GetInt32(2), MediaType = reader.IsDBNull(3) ? null : reader.GetString(3),
            RetrievedAtUtc = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
            Version = reader.GetInt32(5)
        }, token);
        if (rows.Count != 1) throw new V4ResolutionException("record.not_found", "The source snapshot was not found.");
        var row = rows[0];
        if (row.ByteSize is < 1 or > V4ContractLimits.MaximumSnapshotUtf8Bytes ||
            row.Hash.Length != 64 || !row.Hash.All(Uri.IsHexDigit))
            throw new V4ResolutionException("storage.snapshot_invalid", "The cached source metadata is invalid.");

        var hash = row.Hash.ToUpperInvariant();
        var expectedRelative = Path.Combine("source-cache", hash[..2], hash + ".bin");
        if (!string.Equals(NormalizeRelative(row.RelativePath), NormalizeRelative(expectedRelative), StringComparison.OrdinalIgnoreCase))
            throw new V4ResolutionException("storage.snapshot_path", "The cached source path is invalid.");
        var root = Path.GetFullPath(storage.BackupRoot);
        var full = Path.GetFullPath(Path.Combine(root, expectedRelative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new V4ResolutionException("storage.snapshot_path", "The cached source path escaped its root.");
        byte[] bytes;
        try
        {
            if (!File.Exists(full)) throw new V4ResolutionException("storage.snapshot_missing", "The cached source file is missing.");
            if (new FileInfo(full).Length != row.ByteSize)
                throw new V4ResolutionException("storage.snapshot_size", "The cached source size does not match its record.");
            bytes = await File.ReadAllBytesAsync(full, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new V4ResolutionException("storage.snapshot_unavailable", "The cached source file could not be read.");
        }
        if (bytes.Length != row.ByteSize ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash)))
            throw new V4ResolutionException("storage.snapshot_hash", "The cached source hash does not match its record.");
        string content;
        try { content = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new V4ResolutionException("storage.snapshot_encoding", "The cached source is not valid UTF-8."); }

        var scope = $"snapshot:{target.Id}:{row.Version}:{hash}";
        var position = request.Cursor is null ? 0 : checked((int)cursors.Decode(request.Cursor, "snapshot-text", scope).Position);
        if (position < 0 || position > content.Length || (position > 0 && position < content.Length && char.IsLowSurrogate(content[position])))
            throw new V4CursorException("cursor.invalid", "The source-text cursor is invalid for this snapshot.");
        var end = Math.Min(content.Length, position + request.Limit);
        if (end < content.Length && end > position && char.IsHighSurrogate(content[end - 1])) end--;
        if (end == position && end < content.Length) end = Math.Min(content.Length, position + 2);
        var hasMore = end < content.Length;
        return new(target.Reference, content[position..end],
            hasMore ? cursors.Encode("snapshot-text", end, scope) : null, hasMore,
            row.MediaType, row.RetrievedAtUtc is { } retrieved
                ? DateTime.SpecifyKind(retrieved, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                : null,
            row.ByteSize, await reads.RevisionAsync(token));
    }

    private static string NormalizeRelative(string path) => path.Replace('/', '\\');
}
