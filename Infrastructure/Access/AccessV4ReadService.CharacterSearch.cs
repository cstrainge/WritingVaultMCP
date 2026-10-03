using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ReadService
{
    private sealed record CharacterListRow(int Id, string First, string? Middle, string? Family,
        string? Preferred, int Version, bool Deleted, string SortName);

    private static string CharacterListLabel(CharacterListRow row)
    {
        var full = string.Join(" ", new[] { row.First, row.Middle, row.Family }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));
        var preferred = row.Preferred?.Trim();
        return !string.IsNullOrWhiteSpace(preferred) &&
            !string.Equals(preferred, row.First.Trim(), StringComparison.OrdinalIgnoreCase)
            ? $"“{preferred}” {full}" : full;
    }

    private async Task<V4Page<V4ReferenceSummary>> SearchCharactersByNameAsync(
        V4SearchRequest request, int continuity, string revision, CancellationToken token)
    {
        var scope = SearchScope(continuity, request, [V4RecordKind.Character]) + "|order:preferred-first-v1";
        var after = request.Cursor is null ? 0 : checked((int)cursors.Decode(request.Cursor, "search", scope).Position);
        var eligible = await LoadSearchEligibleEntityIdsAsync(continuity, request, token).ConfigureAwait(false);
        var aliasIds = (await AliasMatchesAsync(CanonEntityType.Character, continuity, request.Text,
            request.DeletionState, token).ConfigureAwait(false)).Select(row => row.Id).Distinct().ToArray();
        const string firstName = "Trim(ch.[GivenName])";
        const string sortName = "IIf(Len(Trim(IIf(ch.[PreferredName] Is Null,'',ch.[PreferredName])))=0,Trim(ch.[GivenName]),Trim(ch.[PreferredName]))";
        const string from = " FROM [Characters] AS ch INNER JOIN [CanonEntities] AS c ON ch.[EntityId]=c.[Id]";
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        string? afterSort = null, afterFirst = null;
        if (after != 0)
        {
            using var anchor = new AccessCommand(connection, $"SELECT {sortName},{firstName}{from} WHERE c.[ContinuityId]=? AND c.[Id]=?")
                .Add(OleDbType.Integer, continuity).Add(OleDbType.Integer, after);
            var keys = await anchor.QueryAsync(r => (Sort: r.GetString(0), First: r.GetString(1)), token).ConfigureAwait(false);
            if (keys.Count == 0) throw new VaultValidationException([new("cursor.stale", "cursor", "The character list changed. Reload its first page.")]);
            (afterSort, afterFirst) = keys[0];
        }
        var found = new List<CharacterListRow>();
        while (found.Count <= request.Limit)
        {
            var search = !string.IsNullOrWhiteSpace(request.Text);
            var predicate = search ?
                " AND (ch.[GivenName] LIKE ? OR ch.[MiddleNames] LIKE ? OR ch.[FamilyName] LIKE ? OR ch.[PreferredName] LIKE ?" +
                " OR (ch.[GivenName] & ' ' & IIf(ch.[MiddleNames] Is Null,'',ch.[MiddleNames] & ' ') & IIf(ch.[FamilyName] Is Null,'',ch.[FamilyName])) LIKE ?" +
                (aliasIds.Length > 0 ? $" OR c.[Id] IN ({string.Join(',', aliasIds)})" : "") +
                (request.IncludeContent ? " OR ch.[PhysicalDescription] LIKE ? OR ch.[PersonalitySummary] LIKE ?" : "") + ")" : "";
            var seek = after == 0 ? "" : $" AND ({sortName}>? OR ({sortName}=? AND {firstName}>?) OR ({sortName}=? AND {firstName}=? AND c.[Id]>?))";
            using var command = new AccessCommand(connection,
                $"SELECT TOP 100 c.[Id],ch.[GivenName],ch.[MiddleNames],ch.[FamilyName],ch.[PreferredName],c.[Version],c.[IsDeleted],{sortName}{from}" +
                " WHERE c.[ContinuityId]=?" + DeletionSql("c", request.DeletionState) + predicate + seek +
                $" ORDER BY {sortName},{firstName},c.[Id]").Add(OleDbType.Integer, continuity);
            if (search)
            {
                var pattern = $"%{EscapeLike(request.Text!.Trim())}%";
                for (var index = 0; index < 5; index++) command.Add(OleDbType.VarWChar, pattern, 255);
                if (request.IncludeContent) command.Add(OleDbType.LongVarWChar, pattern).Add(OleDbType.LongVarWChar, pattern);
            }
            if (after != 0) command.Add(OleDbType.VarWChar, afterSort, 255)
                .Add(OleDbType.VarWChar, afterSort, 255).Add(OleDbType.VarWChar, afterFirst, 255)
                .Add(OleDbType.VarWChar, afterSort, 255).Add(OleDbType.VarWChar, afterFirst, 255).Add(OleDbType.Integer, after);
            var rows = await command.QueryAsync(r => new CharacterListRow(r.GetInt32(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetBoolean(6), r.GetString(7)), token).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (eligible is null || eligible.Contains(row.Id)) found.Add(row);
                if (found.Count > request.Limit) break;
            }
            if (found.Count > request.Limit || rows.Count < 100) break;
            var last = rows[^1]; after = last.Id; afterSort = last.SortName; afterFirst = last.First.Trim();
        }
        var hasMore = found.Count > request.Limit;
        return new(found.Take(request.Limit).Select(row => new V4ReferenceSummary(
            references.ReferenceFromKnownRecord("Character", row.Id, row.First), V4RecordKind.Character,
            CharacterListLabel(row), ContinuityName: session.ContinuityName, Version: row.Version, IsDeleted: row.Deleted)).ToArray(),
            hasMore ? cursors.Encode("search", found[request.Limit - 1].Id, scope) : null, hasMore, revision);
    }
}
