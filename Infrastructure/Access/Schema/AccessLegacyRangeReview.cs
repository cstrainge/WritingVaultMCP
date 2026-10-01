using System.Data.OleDb;

namespace WritingVaultMcp.Infrastructure.Access.Schema;

// Administrative, read-only inventory. Storage keys are deliberately confined
// to this local command; the MCP and viewer contracts expose semantic refs.
public sealed record LegacyRangeReviewRow(
    string Table, string Field, int StorageKey, DateTime? Lower,
    DateTime? Upper, string? OriginalText);

public sealed class AccessLegacyRangeReview(IAccessConnectionFactory factory)
{
    public async Task<IReadOnlyList<LegacyRangeReviewRow>> ListAsync(CancellationToken token = default)
    {
        var rows = new List<LegacyRangeReviewRow>();
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        // This inventory is needed *before* the production v3->v4 migration.
        // Older schemas lack several v4 tables, so requiring the current
        // migration here would defeat the administrative review. A present
        // table must still have the expected date columns; never silently
        // ignore a damaged or partially migrated table.
        var presentTables = AccessSchemaInspector.GetUserTableNames(connection);
        foreach (var (table, prefix) in AccessSchemaDefinition.StoryDateFields)
        {
            token.ThrowIfCancellationRequested();
            if (!presentTables.Contains(table)) continue;
            var key = AccessSchemaDefinition.Tables.Single(item => item.Name == table).PrimaryKey.Single();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = $"SELECT * FROM [{table}] WHERE 1=0";
                await using var reader = await schema.ExecuteReaderAsync(token).ConfigureAwait(false);
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var required in new[] { key, $"{prefix}Kind", $"{prefix}LowerBound",
                             $"{prefix}UpperBound", $"{prefix}OriginalText" })
                    if (!columns.Contains(required))
                        throw new InvalidOperationException($"The {table} date columns are incomplete.");
            }
            using var command = new AccessCommand(connection,
                $"SELECT [{key}],[{prefix}LowerBound],[{prefix}UpperBound],[{prefix}OriginalText] " +
                $"FROM [{table}] WHERE [{prefix}Kind]='Range' ORDER BY [{key}]");
            rows.AddRange(await command.QueryAsync(reader => new LegacyRangeReviewRow(
                table, prefix, reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)), token).ConfigureAwait(false));
        }
        return rows;
    }
}
