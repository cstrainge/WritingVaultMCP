using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Real-UTC history and continuity-aware committed-change feed.</summary>
public sealed partial class AccessV4ReadService
{
    public Task<V4Page<V4HistoryEntry>> HistoryAsync(V4HistoryRequest request, CancellationToken token = default) =>
        MeasureAsync<V4Page<V4HistoryEntry>>("history", async () =>
        {
            ValidateLimit(request.Limit, V4ContractLimits.MaximumPageSize);
            var continuity = session.RequireContinuityId();
            string? recordType = null;
            string? recordKey = null;
            string? operationId = null;
            string scope;
            if (!string.IsNullOrWhiteSpace(request.MutationToken))
            {
                operationId = references.OperationId(request.MutationToken);
                scope = $"operation:{operationId}";
            }
            else if (!string.IsNullOrWhiteSpace(request.Ref))
            {
                var resolved = await references.ResolveAsync(request.Ref, continuity, token).ConfigureAwait(false);
                recordType = resolved.ResourceType;
                recordKey = resolved.Id.ToString(CultureInfo.InvariantCulture);
                scope = $"record:{request.Ref}";
            }
            else
            {
                recordType = "Continuity";
                recordKey = continuity.ToString(CultureInfo.InvariantCulture);
                scope = $"continuity:{continuity}";
            }
            var before = request.Cursor is null ? long.MaxValue : cursors.Decode(request.Cursor, "history", scope).Position;
            var revision = await RevisionAsync(token).ConfigureAwait(false);
            await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
            var predicate = (operationId is not null ? "[OperationId]=?" : "[RecordType]=? AND [RecordKey]=?") + " AND [RecordType]<>'PrivateMemory'";
            using var command = new AccessCommand(connection,
                $"SELECT TOP {request.Limit + 1} [Id],[ChangedAtUtc],[ClientLabel],[Action],[RecordType],[RecordKey],[VersionBefore],[VersionAfter],[ChangeJson] FROM [ChangeLog] WHERE {predicate} AND [Id]<? ORDER BY [Id] DESC");
            if (operationId is not null) command.Add(OleDbType.VarWChar, operationId, 36);
            else command.Add(OleDbType.VarWChar, recordType, 100).Add(OleDbType.VarWChar, recordKey, 100);
            command.Add(OleDbType.Integer, before > int.MaxValue ? int.MaxValue : checked((int)before));
            var rows = await command.QueryAsync(r => new HistoryRow(
                r.GetInt32(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8)), token).ConfigureAwait(false);
            var items = new List<V4HistoryEntry>();
            foreach (var row in rows.Take(request.Limit))
            {
                string? recordRef = null;
                if (int.TryParse(row.RecordKey, out var key))
                {
                    try
                    {
                        recordRef = row.RecordType.Equals("Continuity", StringComparison.OrdinalIgnoreCase)
                            ? await references.ContinuityNameAsync(key, token).ConfigureAwait(false)
                            : await references.ReferenceAsync(row.RecordType, key, token).ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException) { }
                }
                IReadOnlyDictionary<string, JsonElement>? change = null;
                if (!string.IsNullOrWhiteSpace(row.ChangeJson))
                {
                    using var document = JsonDocument.Parse(row.ChangeJson);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var raw = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                        var safe = await v3Mapper.DictionaryAsync(raw, null, token).ConfigureAwait(false);
                        change = ToJsonFields(safe, "operationId", "deletedOperationId");
                    }
                }
                items.Add(new(row.ChangedAtUtc.ToString("O", CultureInfo.InvariantCulture), row.ClientLabel ?? "unspecified",
                    row.Action, row.RecordType, recordRef, row.VersionBefore, row.VersionAfter, change));
            }
            return new(items, rows.Count > request.Limit ? cursors.Encode("history", rows[request.Limit - 1].Id, scope) : null,
                rows.Count > request.Limit, revision);
        });

    public Task<V4ChangesResult> ChangesSinceAsync(V4ChangesSinceRequest request, CancellationToken token = default) =>
        MeasureAsync<V4ChangesResult>("changes", async () =>
        {
            var continuity = session.RequireContinuityId();
            if (request.WaitSeconds is < 0 or > V4ContractLimits.MaximumChangeWaitSeconds)
                throw new VaultValidationException([new("changes.wait", "waitSeconds", "waitSeconds must be between 0 and 30.")]);
            var scope = $"c:{continuity}";
            if (request.Cursor is null)
            {
                var initial = await CurrentChangeIdAsync(token).ConfigureAwait(false);
                var cursor = cursors.Encode("changes", initial, scope, TimeSpan.FromHours(24));
                return new(cursor, [], true, false, cursor);
            }
            V4CursorState state;
            try { state = cursors.Decode(request.Cursor, "changes", scope); }
            catch (V4CursorException exception) when (exception.Code == "cursor.expired")
            {
                var current = await CurrentChangeIdAsync(token).ConfigureAwait(false);
                var cursor = cursors.Encode("changes", current, scope, TimeSpan.FromHours(24));
                return new(cursor, [], false, true, cursor);
            }
            var deadline = DateTime.UtcNow.AddSeconds(request.WaitSeconds);
            while (true)
            {
                var observedSignal = changes.Version;
                var batch = await ReadChangesAsync(state.Position, continuity, token).ConfigureAwait(false);
                if (batch.ScannedTo > state.Position)
                {
                    var cursor = cursors.Encode("changes", batch.ScannedTo, scope, TimeSpan.FromHours(24));
                    return new(cursor, batch.Items, false, false, cursor);
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || request.WaitSeconds == 0)
                {
                    var cursor = cursors.Encode("changes", state.Position, scope, TimeSpan.FromHours(24));
                    return new(cursor, [], true, false, cursor);
                }
                await changes.WaitForChangeAfterAsync(observedSignal, remaining, token).ConfigureAwait(false);
            }
        });

    private async Task<(long ScannedTo, IReadOnlyList<V4Change> Items)> ReadChangesAsync(long after, int continuity, CancellationToken token)
    {
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                $"SELECT TOP {MaximumChangePage} [Id],[RecordType],[RecordKey],[Action] FROM [ChangeLog] WHERE [Id]>? ORDER BY [Id]")
            .Add(OleDbType.Integer, checked((int)after));
        var rows = await command.QueryAsync(r => new
        {
            Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Type = r.GetString(1),
            Key = r.GetString(2), Action = r.GetString(3)
        }, token).ConfigureAwait(false);
        var output = new List<V4Change>();
        foreach (var row in rows)
        {
            string? reference = null;
            var visible = false;
            var global = row.Type.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("Tag", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("SourceSnapshot", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("SourceTag", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("NoteSource", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("ContinuityNoteSource", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("ClaimEvidence", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("EntitySource", StringComparison.OrdinalIgnoreCase) ||
                         // A note may embed an image from any continuity. Every image
                         // mutation must therefore invalidate all open viewer sessions.
                         row.Type.Equals("EntityImage", StringComparison.OrdinalIgnoreCase) ||
                         row.Type.Equals("StoryImage", StringComparison.OrdinalIgnoreCase);
            var opaqueLink=row.Type.Equals("EntityLink",StringComparison.OrdinalIgnoreCase)||
                           row.Type.Equals("EntityTag",StringComparison.OrdinalIgnoreCase)||
                           row.Type.Equals("ProjectAssignment",StringComparison.OrdinalIgnoreCase)||
                           row.Type.Equals("EntityEventProject",StringComparison.OrdinalIgnoreCase);
            if (int.TryParse(row.Key, out var key))
            {
                try
                {
                    if (row.Type.Equals("Continuity", StringComparison.OrdinalIgnoreCase) || row.Type.Equals("ContinuityClock", StringComparison.OrdinalIgnoreCase))
                    {
                        visible = key == continuity;
                        if (visible) reference = session.ContinuityName;
                    }
                    else
                    {
                        reference = await references.ReferenceAsync(row.Type, key, token).ConfigureAwait(false);
                        var resolved = await references.ResolveAsync(reference, null, token).ConfigureAwait(false);
                        visible = global || resolved.ContinuityId == continuity;
                    }
                }
                catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException) { visible = global; }
            }
            if(!visible&&(global||opaqueLink)){visible=true;reference=null;}
            if (!visible) continue;
            output.Add(new(global ? "vault-global" : session.ContinuityName ?? "selected-continuity", row.Type,
                reference, row.Action, TimelineType(row.Type), reference is null||opaqueLink));
        }
        return (rows.Count == 0 ? after : rows[^1].Id, output);
    }

    private async Task<long> CurrentChangeIdAsync(CancellationToken token)
    {
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, "SELECT MAX([Id]) FROM [ChangeLog]");
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

}
