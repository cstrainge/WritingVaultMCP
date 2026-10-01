using System.Data;
using System.Data.OleDb;

namespace WritingVaultMcp.Infrastructure.Access.Schema;

public sealed record SchemaIssue(string Code, string ObjectName, string Detail);

public sealed record SchemaVerificationResult(IReadOnlyList<SchemaIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class AccessSchemaVerifier(IAccessConnectionFactory connectionFactory)
{
    public async Task<SchemaVerificationResult> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await VerifyOpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SchemaVerificationResult> VerifyOpenConnectionAsync(
        OleDbConnection connection,
        CancellationToken cancellationToken)
    {
        var issues = new List<SchemaIssue>();
        var expectedTables = AccessSchemaDefinition.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var actualTables = AccessSchemaInspector.GetUserTableNames(connection);

        foreach (var missing in expectedTables.Keys.Except(actualTables, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new SchemaIssue("schema.table_missing", missing, "Required table is missing."));
        }

        foreach (var unexpected in actualTables.Except(expectedTables.Keys, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new SchemaIssue("schema.table_unexpected", unexpected, "Unexpected user table is present."));
        }

        var columnRows = connection.GetSchema("Columns").Rows.Cast<DataRow>()
            .Where(row => actualTables.Contains(Convert.ToString(row["TABLE_NAME"])!, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        foreach (var expectedTable in expectedTables.Values.Where(table => actualTables.Contains(table.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var actualColumns = columnRows
                .Where(row => string.Equals(Convert.ToString(row["TABLE_NAME"]), expectedTable.Name, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => Convert.ToString(row["COLUMN_NAME"])!, StringComparer.OrdinalIgnoreCase);

            foreach (var expectedColumn in expectedTable.Columns)
            {
                var objectName = $"{expectedTable.Name}.{expectedColumn.Name}";
                if (!actualColumns.TryGetValue(expectedColumn.Name, out var actualColumn))
                {
                    issues.Add(new SchemaIssue("schema.column_missing", objectName, "Required column is missing."));
                    continue;
                }

                var actualType = Convert.ToInt32(actualColumn["DATA_TYPE"]);
                var expectedType = ExpectedOleDbType(expectedColumn.SqlType);
                if (actualType != expectedType)
                {
                    issues.Add(new SchemaIssue(
                        "schema.column_type",
                        objectName,
                        $"Expected provider type {expectedType}, found {actualType}."));
                }

                var expectedLength = ExpectedTextLength(expectedColumn.SqlType);
                if (expectedLength is not null)
                {
                    var actualLength = actualColumn["CHARACTER_MAXIMUM_LENGTH"] is DBNull
                        ? (int?)null
                        : Convert.ToInt32(actualColumn["CHARACTER_MAXIMUM_LENGTH"]);
                    if (actualLength != expectedLength)
                    {
                        issues.Add(new SchemaIssue(
                            "schema.column_length",
                            objectName,
                            $"Expected text length {expectedLength}, found {actualLength?.ToString() ?? "null"}."));
                    }
                }

                var nullable = IsNullable(actualColumn["IS_NULLABLE"]);
                if (expectedColumn.Required == nullable)
                {
                    issues.Add(new SchemaIssue(
                        "schema.column_nullability",
                        objectName,
                        expectedColumn.Required ? "Column must be required." : "Column must be nullable."));
                }


                var hasDefault = Convert.ToBoolean(actualColumn["COLUMN_HASDEFAULT"]);
                if (expectedColumn.DefaultSql is null && hasDefault)
                {
                    issues.Add(new SchemaIssue("schema.column_default", objectName, "Column has an unexpected default."));
                }
                else if (expectedColumn.DefaultSql is not null)
                {
                    var actualDefault = hasDefault && actualColumn["COLUMN_DEFAULT"] is not DBNull
                        ? Convert.ToString(actualColumn["COLUMN_DEFAULT"])?.Trim()
                        : null;
                    if (!DefaultsEquivalent(expectedColumn.DefaultSql, actualDefault))
                    {
                        issues.Add(new SchemaIssue(
                            "schema.column_default",
                            objectName,
                            $"Expected default {expectedColumn.DefaultSql}, found {actualDefault ?? "none"}."));
                    }
                }
            }

            foreach (var unexpectedColumn in actualColumns.Keys.Except(
                         expectedTable.Columns.Select(column => column.Name),
                         StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new SchemaIssue(
                    "schema.column_unexpected",
                    $"{expectedTable.Name}.{unexpectedColumn}",
                    "Unexpected column is present."));
            }
        }

        VerifyIndexes(connection, issues);
        VerifyForeignKeys(connection, issues);
        VerifyCheckConstraints(connection, issues);
        await VerifyMigrationLedgerAsync(connection, actualTables, issues, cancellationToken).ConfigureAwait(false);

        return new SchemaVerificationResult(issues
            .OrderBy(issue => issue.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ToArray());
    }

    private static void VerifyIndexes(OleDbConnection connection, ICollection<SchemaIssue> issues)
    {
        var rows = connection.GetSchema("Indexes").Rows.Cast<DataRow>();
        var actual = rows
            .Where(row => Convert.ToString(row["TABLE_NAME"]) is { } table && !table.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                row => (Table: Convert.ToString(row["TABLE_NAME"])!, Name: Convert.ToString(row["INDEX_NAME"])!),
                new TableIndexKeyComparer())
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Columns = group.OrderBy(row => Convert.ToInt32(row["ORDINAL_POSITION"]))
                        .Select(row => Convert.ToString(row["COLUMN_NAME"])!)
                        .ToArray(),
                    Unique = Convert.ToBoolean(group.First()["UNIQUE"])
                },
                new TableIndexKeyComparer());

        var expected = AccessSchemaDefinition.Indexes
            .Select(index => (Table: index.Table, IndexName: index.Name, Columns: index.Columns, Unique: index.Unique))
            .Concat(AccessSchemaDefinition.Tables.Select(table =>
                (Table: table.Name, IndexName: table.PrimaryKeyName, Columns: table.PrimaryKey, Unique: true)))
            .ToArray();

        foreach (var index in expected)
        {
            var key = (index.Table, index.IndexName);
            if (!actual.TryGetValue(key, out var found))
            {
                issues.Add(new SchemaIssue("schema.index_missing", $"{index.Table}.{index.IndexName}", "Required index is missing."));
                continue;
            }

            if (found.Unique != index.Unique || !found.Columns.SequenceEqual(index.Columns, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new SchemaIssue(
                    "schema.index_definition",
                    $"{index.Table}.{index.IndexName}",
                "Index uniqueness or ordered columns do not match."));
            }
        }

        var allowed = new HashSet<(string Table, string Name)>(
            expected.Select(index => (index.Table, index.IndexName))
                .Concat(AccessSchemaDefinition.ForeignKeys.Select(key => (key.DependentTable, key.Name))),
            new TableIndexKeyComparer());
        foreach (var unexpected in actual.Keys.Where(key => !allowed.Contains(key)))
        {
            issues.Add(new SchemaIssue(
                "schema.index_unexpected",
                $"{unexpected.Table}.{unexpected.Name}",
                "Unexpected user index is present."));
        }
    }

    private static void VerifyForeignKeys(OleDbConnection connection, ICollection<SchemaIssue> issues)
    {
        var table = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null)
            ?? throw new InvalidOperationException("ACE did not return foreign-key metadata.");
        var actual = table.Rows.Cast<DataRow>()
            .Where(row =>
                Convert.ToString(row["FK_TABLE_NAME"]) is { } dependent &&
                Convert.ToString(row["PK_TABLE_NAME"]) is { } principal &&
                !dependent.StartsWith("MSys", StringComparison.OrdinalIgnoreCase) &&
                !principal.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => Convert.ToString(row["FK_NAME"])!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => Convert.ToInt32(row["ORDINAL"]))
                    .Select(row => new
                    {
                        DependentTable = Convert.ToString(row["FK_TABLE_NAME"])!,
                        DependentColumn = Convert.ToString(row["FK_COLUMN_NAME"])!,
                        PrincipalTable = Convert.ToString(row["PK_TABLE_NAME"])!,
                        PrincipalColumn = Convert.ToString(row["PK_COLUMN_NAME"])!,
                        UpdateRule = Convert.ToString(row["UPDATE_RULE"])!,
                        DeleteRule = Convert.ToString(row["DELETE_RULE"])!
                    }).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var expected in AccessSchemaDefinition.ForeignKeys)
        {
            if (!actual.TryGetValue(expected.Name, out var found))
            {
                issues.Add(new SchemaIssue("schema.foreign_key_missing", expected.Name, "Required foreign key is missing."));
                continue;
            }

            if (found.Length != 1 ||
                !string.Equals(found[0].DependentTable, expected.DependentTable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(found[0].DependentColumn, expected.DependentColumn, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(found[0].PrincipalTable, expected.PrincipalTable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(found[0].PrincipalColumn, expected.PrincipalColumn, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(found[0].UpdateRule, "NO ACTION", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(found[0].DeleteRule, "NO ACTION", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new SchemaIssue(
                    "schema.foreign_key_definition",
                    expected.Name,
                    "Foreign-key columns or non-cascading update/delete rules do not match."));
            }
        }

        var expectedNames = AccessSchemaDefinition.ForeignKeys
            .Select(key => key.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unexpected in actual.Keys.Where(name => !expectedNames.Contains(name)))
        {
            issues.Add(new SchemaIssue("schema.foreign_key_unexpected", unexpected, "Unexpected foreign key is present."));
        }
    }

    private static async Task VerifyMigrationLedgerAsync(
        OleDbConnection connection,
        IReadOnlySet<string> actualTables,
        ICollection<SchemaIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!actualTables.Contains("SchemaMigrations"))
        {
            return;
        }

        using var command = new AccessCommand(
            connection,
            "SELECT [MigrationId], [Checksum], [ApplicationVersion], [Status] FROM [SchemaMigrations]");
        var rows = await command.QueryAsync(
            reader => (
                MigrationId: reader.String("MigrationId"),
                Checksum: reader.String("Checksum"),
                ApplicationVersion: reader.String("ApplicationVersion"),
                Status: reader.String("Status")),
            cancellationToken).ConfigureAwait(false);
        var expectedIds = AccessSchemaMigrations.All
            .Select(migration => migration.MigrationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unexpected in rows.Where(row => !expectedIds.Contains(row.MigrationId)))
        {
            issues.Add(new SchemaIssue(
                "schema.migration_unexpected",
                unexpected.MigrationId,
                "Unexpected migration row is present; the database may be newer than this application."));
        }

        foreach (var expected in AccessSchemaMigrations.All)
        {
            var matches = rows
                .Where(row => string.Equals(row.MigrationId, expected.MigrationId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new SchemaIssue("schema.migration_missing", expected.MigrationId, "Applied migration row is missing."));
                continue;
            }

            var applied = matches[0];
            if (!string.Equals(applied.Checksum, expected.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new SchemaIssue("schema.migration_checksum", expected.MigrationId, "Migration checksum does not match the application."));
            }

            if (!string.Equals(applied.ApplicationVersion, expected.ApplicationVersion, StringComparison.Ordinal))
            {
                issues.Add(new SchemaIssue("schema.migration_application_version", expected.MigrationId, "Migration application version does not match."));
            }

            if (!string.Equals(applied.Status, "Applied", StringComparison.Ordinal))
            {
                issues.Add(new SchemaIssue("schema.migration_status", expected.MigrationId, $"Migration status is '{applied.Status}'."));
            }
        }
    }

    private static void VerifyCheckConstraints(OleDbConnection connection, ICollection<SchemaIssue> issues)
    {
        var table = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Check_Constraints, null)
            ?? throw new InvalidOperationException("ACE did not return check-constraint metadata.");
        var actual = table.Rows.Cast<DataRow>()
            .Where(row => Convert.ToString(row["CONSTRAINT_NAME"]) is { } name &&
                          !name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                row => Convert.ToString(row["CONSTRAINT_NAME"])!,
                row => Convert.ToString(row["CHECK_CLAUSE"])!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var expected in AccessSchemaDefinition.CheckConstraints)
        {
            if (!actual.TryGetValue(expected.Name, out var clause))
            {
                issues.Add(new SchemaIssue("schema.check_missing", expected.Name, "Required check constraint is missing."));
                continue;
            }

            static string Normalize(string value) =>
                string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
            if (!string.Equals(Normalize(clause), Normalize(expected.Expression), StringComparison.Ordinal))
            {
                issues.Add(new SchemaIssue("schema.check_definition", expected.Name, "Check constraint expression does not match."));
            }
        }

        var expectedNames = AccessSchemaDefinition.CheckConstraints
            .Select(check => check.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unexpected in actual.Keys.Where(name => !expectedNames.Contains(name)))
        {
            issues.Add(new SchemaIssue("schema.check_unexpected", unexpected, "Unexpected check constraint is present."));
        }
    }

    private static int ExpectedOleDbType(string sqlType)
    {
        if (sqlType.StartsWith("TEXT", StringComparison.OrdinalIgnoreCase) ||
            sqlType.Equals("LONGTEXT", StringComparison.OrdinalIgnoreCase))
        {
            // GetSchema("Columns") reports the ADO data-type code (130) for
            // both Access Short Text and Long Text, rather than OleDbType.VarWChar (202).
            return 130;
        }

        return sqlType.ToUpperInvariant() switch
        {
            "AUTOINCREMENT" or "LONG" => (int)OleDbType.Integer,
            "DATETIME" => (int)OleDbType.Date,
            "YESNO" => (int)OleDbType.Boolean,
            "DOUBLE" => (int)OleDbType.Double,
            // ACE reports Access OLE Object / LONGBINARY columns using ADO
            // binary type 128 even though commands stream them as LongVarBinary.
            "LONGBINARY" => 128,
            _ => throw new InvalidOperationException($"Unsupported schema type '{sqlType}'.")
        };
    }

    private static int? ExpectedTextLength(string sqlType)
    {
        if (sqlType.Equals("LONGTEXT", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (!sqlType.StartsWith("TEXT(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.Parse(sqlType[5..^1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsNullable(object value) => value switch
    {
        bool boolean => boolean,
        string text => text.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
                       text.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToBoolean(value)
    };

    private static bool DefaultsEquivalent(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        static string Normalize(string value) =>
            value.Trim().Trim('(', ')').Replace("\"", "'", StringComparison.Ordinal);
        return string.Equals(Normalize(expected), Normalize(actual), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TableIndexKeyComparer : IEqualityComparer<(string Table, string Name)>
    {
        public bool Equals((string Table, string Name) x, (string Table, string Name) y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Table, string Name) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}

internal static class AccessSchemaInspector
{
    public static HashSet<string> GetUserTableNames(OleDbConnection connection) =>
        connection.GetSchema("Tables").Rows.Cast<DataRow>()
            .Where(row => string.Equals(Convert.ToString(row["TABLE_TYPE"]), "TABLE", StringComparison.OrdinalIgnoreCase))
            .Select(row => Convert.ToString(row["TABLE_NAME"])!)
            .Where(name => !name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
