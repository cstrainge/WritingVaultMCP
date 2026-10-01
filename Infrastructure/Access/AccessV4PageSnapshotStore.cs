using System.Data.OleDb;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>
/// Immutable, bounded storage for a materialized record page. The caller must
/// assemble the complete page from the active write transaction before calling
/// CaptureAsync; this store never reconstructs a page from audit deltas.
/// </summary>
internal sealed class AccessV4PageSnapshotStore(IAccessConnectionFactory factory)
{
    private const int MaximumPageBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal sealed record Page(V4RecordOverview Overview,
        IReadOnlyDictionary<string, V4RecordOverview> Notes);

    internal sealed record SavedPage(int SnapshotVersion, DateTime SavedAtUtc,
        bool IsBaseline, Page Content);

    internal async Task<int> CaptureAsync(VaultWriteContext context, string recordType,
        int recordKey, int contextContinuityId, Page page, bool baseline,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentNullException.ThrowIfNull(page);
        if (recordType.Length > 50 || recordKey < 1 || contextContinuityId < 1)
            throw new ArgumentOutOfRangeException(nameof(recordKey), "The snapshot record identity is invalid.");

        // Revision cursors are live-change metadata, not page content. Their
        // movement alone must not create a new historical page version.
        var normalized = page with
        {
            Overview = page.Overview with { ObservedRevision = string.Empty },
            Notes = page.Notes.ToDictionary(pair => pair.Key,
                pair => pair.Value with { ObservedRevision = string.Empty }, StringComparer.Ordinal)
        };
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length is < 1 or > MaximumPageBytes)
            throw new VaultCommandException("snapshot.page_too_large",
                "The change would make a historical page exceed its one-megabyte snapshot limit.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var previous = context.Command(
            "SELECT TOP 1 [SnapshotVersion],[PageSha256] FROM [RecordPageSnapshots] " +
            "WHERE [RecordType]=? AND [RecordKey]=? AND [ContextContinuityId]=? " +
            "ORDER BY [SnapshotVersion] DESC")
            .Add(OleDbType.VarWChar, recordType, 50).Add(OleDbType.Integer, recordKey)
            .Add(OleDbType.Integer, contextContinuityId);
        var latest = await previous.QueryAsync(reader =>
            (Version: reader.GetInt32(0), Hash: reader.GetString(1)), token).ConfigureAwait(false);
        if (latest.Count == 1 && string.Equals(latest[0].Hash, hash, StringComparison.Ordinal))
            return latest[0].Version;
        var next = latest.Count == 0 ? 1 : checked(latest[0].Version + 1);
        using var insert = context.Command(
            "INSERT INTO [RecordPageSnapshots] " +
            "([RecordType],[RecordKey],[ContextContinuityId],[SnapshotVersion],[SavedAtUtc]," +
            "[OperationId],[IsBaseline],[PageBytes],[PageSha256],[PageJson]) VALUES (?,?,?,?,?,?,?,?,?,?)")
            .Add(OleDbType.VarWChar, recordType, 50)
            .Add(OleDbType.Integer, recordKey)
            .Add(OleDbType.Integer, contextContinuityId)
            .Add(OleDbType.Integer, next)
            .Add(OleDbType.Date, DateTime.UtcNow)
            .Add(OleDbType.VarWChar, baseline ? null : context.OperationId, 36)
            .Add(OleDbType.Boolean, baseline)
            .Add(OleDbType.Integer, bytes.Length)
            .Add(OleDbType.VarWChar, hash, 64)
            .Add(OleDbType.LongVarWChar, json);
        if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The historical page snapshot was not saved.");
        using (var clear = context.Command(
                   "DELETE FROM [RecordPageDependencies] WHERE [PageType]=? AND [PageKey]=? " +
                   "AND [ContextContinuityId]=?")
                   .Add(OleDbType.VarWChar, recordType, 50).Add(OleDbType.Integer, recordKey)
                   .Add(OleDbType.Integer, contextContinuityId))
            await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        foreach (var dependency in PageReferences(normalized))
        {
            using var add = context.Command(
                "INSERT INTO [RecordPageDependencies] " +
                "([PageType],[PageKey],[ContextContinuityId],[TargetReference]) VALUES (?,?,?,?)")
                .Add(OleDbType.VarWChar, recordType, 50).Add(OleDbType.Integer, recordKey)
                .Add(OleDbType.Integer, contextContinuityId)
                .Add(OleDbType.VarWChar, dependency, 80);
            await add.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        return next;
    }

    internal async Task<IReadOnlyList<(string Type, int Key, int ContinuityId)>>
        FindDependentPagesAsync(VaultWriteContext context, string targetRef,
            CancellationToken token = default)
    {
        using var command = context.Command(
            "SELECT [PageType],[PageKey],[ContextContinuityId] FROM [RecordPageDependencies] " +
            "WHERE [TargetReference]=?")
            .Add(OleDbType.VarWChar, StableReferenceKey(targetRef), 80);
        return await command.QueryAsync(reader =>
            (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)), token)
            .ConfigureAwait(false);
    }

    internal async Task<bool> HasSnapshotAsync(VaultWriteContext context, string recordType,
        int recordKey, int contextContinuityId, CancellationToken token = default)
    {
        using var command = context.Command(
            "SELECT TOP 1 [Id] FROM [RecordPageSnapshots] WHERE [RecordType]=? " +
            "AND [RecordKey]=? AND [ContextContinuityId]=?")
            .Add(OleDbType.VarWChar, recordType, 50).Add(OleDbType.Integer, recordKey)
            .Add(OleDbType.Integer, contextContinuityId);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    private static IReadOnlyList<string> PageReferences(Page page)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Add(V4RecordOverview overview)
        {
            // Continuities are addressed by exact name, not a semantic ref.
            // They have no stable token to enter in this reverse-link index.
            if (TryStableReferenceKey(overview.Summary.Ref, out var self)) result.Add(self);
            foreach (var section in overview.Sections.Values)
                foreach (var item in section.Items)
                    if (TryStableReferenceKey(item.Ref, out var dependency)) result.Add(dependency);
        }
        Add(page.Overview);
        foreach (var note in page.Notes.Values) Add(note);
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string StableReferenceKey(string reference)
    {
        if (!TryStableReferenceKey(reference, out var key))
            throw new InvalidDataException("A page contains an invalid semantic record reference.");
        return key;
    }

    private static bool TryStableReferenceKey(string reference, out string key)
    {
        key = string.Empty;
        var separator = reference.IndexOf(':');
        var token = reference.LastIndexOf('~');
        if (separator < 1 || token <= separator + 1 || token == reference.Length - 1)
            return false;
        key = reference[..(separator + 1)] + reference[token..];
        return key.Length <= 80;
    }

    internal async Task<SavedPage?> ReadAsync(string recordType, int recordKey,
        int contextContinuityId, int snapshotVersion, CancellationToken token = default)
    {
        if (snapshotVersion < 1 || recordKey < 1 || contextContinuityId < 1 ||
            string.IsNullOrWhiteSpace(recordType)) return null;
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
            "SELECT [SavedAtUtc],[IsBaseline],[PageBytes],[PageSha256],[PageJson] " +
            "FROM [RecordPageSnapshots] WHERE [RecordType]=? AND [RecordKey]=? " +
            "AND [ContextContinuityId]=? AND [SnapshotVersion]=?")
            .Add(OleDbType.VarWChar, recordType, 50).Add(OleDbType.Integer, recordKey)
            .Add(OleDbType.Integer, contextContinuityId)
            .Add(OleDbType.Integer, snapshotVersion);
        var rows = await command.QueryAsync(reader => (
            SavedAtUtc: reader.GetDateTime(0), IsBaseline: reader.GetBoolean(1),
            ByteSize: reader.GetInt32(2), Hash: reader.GetString(3), Json: reader.GetString(4)), token)
            .ConfigureAwait(false);
        if (rows.Count == 0) return null;
        var row = rows.Single();
        var bytes = Encoding.UTF8.GetBytes(row.Json);
        if (bytes.Length != row.ByteSize || bytes.Length is < 1 or > MaximumPageBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), row.Hash, StringComparison.Ordinal))
            throw new InvalidDataException("A historical page snapshot failed its integrity check.");
        var page = JsonSerializer.Deserialize<Page>(row.Json, JsonOptions)
            ?? throw new InvalidDataException("The historical page snapshot is malformed.");
        return new(snapshotVersion, DateTime.SpecifyKind(row.SavedAtUtc, DateTimeKind.Utc),
            row.IsBaseline, page);
    }

    internal async Task<V4RecordSnapshotListResult> ListAsync(string recordType,
        int recordKey, int contextContinuityId, int? beforeVersion, int limit,
        CancellationToken token = default)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                $"SELECT TOP {limit + 1} [SnapshotVersion],[SavedAtUtc],[IsBaseline] " +
                "FROM [RecordPageSnapshots] WHERE [RecordType]=? AND [RecordKey]=? " +
                "AND [ContextContinuityId]=? AND [SnapshotVersion]<? " +
                "ORDER BY [SnapshotVersion] DESC")
            .Add(OleDbType.VarWChar, recordType, 50)
            .Add(OleDbType.Integer, recordKey)
            .Add(OleDbType.Integer, contextContinuityId)
            .Add(OleDbType.Integer, beforeVersion ?? int.MaxValue);
        var rows = await command.QueryAsync(reader => new V4RecordSnapshotSummary(
            reader.GetInt32(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            reader.GetBoolean(2)), token).ConfigureAwait(false);
        var items = rows.Take(limit).ToArray();
        return new(items, rows.Count > limit ? items[^1].SnapshotVersion : null,
            rows.Count > limit);
    }
}
