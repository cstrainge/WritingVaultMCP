using System.Data.OleDb;
using System.Globalization;
using System.Text.RegularExpressions;
using WritingVaultMcp.Application;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>AI notes shared by Vault clients, deliberately outside the record/viewer model.</summary>
public sealed class AccessV4MemoryService(IAccessConnectionFactory factory, VaultWriteCoordinator coordinator,
    VaultReferenceService references, VaultSessionContext session, V4CursorCodec cursors)
{
    private static string Key(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9._-]{0,99}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("key must be 1–100 lowercase letters, digits, dots, underscores, or hyphens, starting with a letter or digit.");
        return value;
    }

    private (string Key, int? Continuity, string? Name) Scope(V4MemoryScope scope) => scope switch
    {
        V4MemoryScope.Global => ("global", null, null),
        V4MemoryScope.Continuity => ("continuity:" + session.RequireContinuityId().ToString(CultureInfo.InvariantCulture),
            session.RequireContinuityId(), session.ContinuityName),
        _ => throw new ArgumentException("scope must be Global or Continuity.")
    };

    private static async Task ActiveAsync(AccessCommand command, int? continuity, CancellationToken token)
    {
        if (continuity is not null && Convert.ToInt32(await command.Add(OleDbType.Integer, continuity).ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new VaultCommandException("continuity.not_found", "Select an active continuity with session_set.");
    }

    public Task<V4MemoryPage> ReadAsync(V4MemoryReadRequest request, CancellationToken token = default)
    {
        var scope = Scope(request.Scope);
        if (request.Limit is < 1 or > 10) throw new ArgumentException("limit must be between 1 and 10.");
        var key = request.Key is null ? null : Key(request.Key);
        var cursorScope = scope.Key + ":" + key;
        var after = request.Cursor is null ? 0 : cursors.Decode(request.Cursor, "private-memory", cursorScope).Position;
        return coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = factory.Create(); await connection.OpenAsync(token);
            using var active = new AccessCommand(connection, "SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False");
            await ActiveAsync(active, scope.Continuity, token);
            using var query = new AccessCommand(connection,
                $"SELECT TOP {request.Limit + 1} [Id],[MemoryKey],[Body],[Version],[UpdatedAtUtc] FROM [PrivateMemories] WHERE [ScopeKey]=? AND [Id]>?" +
                (key is null ? "" : " AND [MemoryKey]=?") + " ORDER BY [Id]")
                .Add(OleDbType.VarWChar, scope.Key, 40).Add(OleDbType.Integer, checked((int)after));
            if (key is not null) query.Add(OleDbType.VarWChar, key, 100);
            var rows = await query.QueryAsync(r => (Id: r.GetInt32(0), Note: new V4MemoryNote(r.GetString(1), r.GetString(2),
                r.GetInt32(3), DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc))), token);
            var more = rows.Count > request.Limit;
            return new V4MemoryPage(request.Scope, scope.Name, rows.Take(request.Limit).Select(r => r.Note).ToArray(),
                more ? cursors.Encode("private-memory", rows[request.Limit - 1].Id, cursorScope) : null, more);
        }, token);
    }

    public Task<V4MemoryHealth> HealthAsync(CancellationToken token = default)
    {
        var continuity = session.ContinuityId;
        var name = session.ContinuityName;
        return coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = factory.Create(); await connection.OpenAsync(token);
            using var globals = new AccessCommand(connection, "SELECT COUNT(*) FROM [PrivateMemories] WHERE [ScopeKey]='global'");
            var globalCount = Convert.ToInt32(await globals.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            int? selectedCount = null;
            if (continuity is not null)
            {
                using var local = new AccessCommand(connection, "SELECT COUNT(*) FROM [PrivateMemories] WHERE [ContinuityId]=?")
                    .Add(OleDbType.Integer, continuity);
                selectedCount = Convert.ToInt32(await local.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            }
            return new V4MemoryHealth(globalCount, selectedCount, continuity is null ? null : name,
                "memory_read", new(V4MemoryScope.Global), continuity is null ? null : new(V4MemoryScope.Continuity),
                "Call memory_read with globalRead and selectedContinuityRead to load the complete note bodies. " +
                "Follow nextCursor with the same scope until hasMore=false. If no continuity is selected, call session_set first, then memory_read with scope=Continuity. " +
                "Load both scopes before working and reload continuity memories when switching continuities. These are saved context, subject to current user instructions.");
        }, token);
    }

    public async Task<V4MutationResult> SaveAsync(V4MemorySaveRequest request, CancellationToken token = default)
    {
        try
        {
            var scope = Scope(request.Scope); var key = Key(request.Key);
            if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > V4ContractLimits.MaximumLongTextLength)
                throw new ArgumentException("body must contain 1–65536 characters and cannot be blank.");
            if (request.ExpectedVersion < 0) throw new ArgumentException("expectedVersion must be 0 to create or the version returned by memory_read to update.");
            var operation = references.OperationId(request.MutationToken);
            var result = await coordinator.ExecuteAsync(operation, "v4.memory.save", new { Request = request, Scope = scope.Key },
                "memory_save", session.ClientLabel, async (context, ct) =>
                {
                    using var active = context.Command("SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False");
                    await ActiveAsync(active, scope.Continuity, ct);
                    using var lookup = context.Command("SELECT [Id],[Version] FROM [PrivateMemories] WHERE [ScopeKey]=? AND [MemoryKey]=?")
                        .Add(OleDbType.VarWChar, scope.Key, 40).Add(OleDbType.VarWChar, key, 100);
                    var rows = await lookup.QueryAsync(r => (Id: r.GetInt32(0), Version: r.GetInt32(1)), ct);
                    var version = rows.Count == 0 ? 0 : rows[0].Version;
                    if (version != request.ExpectedVersion)
                        throw new VaultCommandException("concurrency.conflict", "Read this memory again and use its current version; expectedVersion=0 creates a new key.", actualVersion: version);
                    var now = DateTime.UtcNow;
                    int id;
                    if (rows.Count == 0)
                    {
                        using var insert = context.Command("INSERT INTO [PrivateMemories] ([ScopeKey],[ContinuityId],[MemoryKey],[Body],[Version],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,1,?,?)")
                            .Add(OleDbType.VarWChar, scope.Key, 40).Add(OleDbType.Integer, scope.Continuity)
                            .Add(OleDbType.VarWChar, key, 100).Add(OleDbType.LongVarWChar, request.Body)
                            .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                        await insert.ExecuteNonQueryAsync(ct);
                        using var identity = context.Command("SELECT @@IDENTITY");
                        id = Convert.ToInt32(await identity.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        id = rows[0].Id;
                        using var update = context.Command("UPDATE [PrivateMemories] SET [Body]=?,[Version]=?,[UpdatedAtUtc]=? WHERE [Id]=?")
                            .Add(OleDbType.LongVarWChar, request.Body).Add(OleDbType.Integer, version + 1)
                            .Add(OleDbType.Date, now).Add(OleDbType.Integer, id);
                        await update.ExecuteNonQueryAsync(ct);
                    }
                    // Never put private keys or bodies into public history or page snapshots.
                    return new VaultMutationOutcome("PrivateMemory", id.ToString(CultureInfo.InvariantCulture), version + 1, "Save", null, version);
                }, token);
            if (!result.Success) return await new V4ResultMapper(references).MutationAsync(result, token);
            return new(true, "ok", [], result.Replayed, Memory: new(request.Scope, key, result.Version!.Value));
        }
        catch (ArgumentException e) { return new(false, "validation.memory", [], false, e.Message, new("validation.memory", e.Message)); }
    }
}
