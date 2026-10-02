using System.Data.OleDb;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WritingVaultMcp.Infrastructure.Access.Schema;

public sealed record SchemaMigrationResult(
    bool Changed,
    string MigrationId,
    string Checksum,
    SchemaVerificationResult Verification);

public sealed class AccessSchemaMigrator(IAccessConnectionFactory connectionFactory, TimeProvider auditClock)
{
    public async Task<SchemaMigrationResult> MigrateAsync(
        bool allowDestructiveEmptyRebuild,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(auditClock);

        if (connectionFactory is AccessConnectionFactory accessFactory)
        {
            AccessDatabaseUseGuard.ThrowIfLockFilePresent(accessFactory.DatabasePath);
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var initialVerification = await AccessSchemaVerifier
            .VerifyOpenConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (initialVerification.IsValid)
        {
            return new SchemaMigrationResult(
                false,
                AccessSchemaDefinition.MigrationId,
                AccessSchemaDefinition.Checksum,
                initialVerification);
        }

        if (await TryApplyCurrentForwardMigrationAsync(connection, initialVerification, cancellationToken)
                .ConfigureAwait(false))
        {
            var upgradedVerification = await AccessSchemaVerifier
                .VerifyOpenConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (!upgradedVerification.IsValid)
            {
                throw VerificationFailure(upgradedVerification);
            }

            return new SchemaMigrationResult(
                true,
                AccessSchemaDefinition.MigrationId,
                AccessSchemaDefinition.Checksum,
                upgradedVerification);
        }

        var existingTables = AccessSchemaInspector.GetUserTableNames(connection);
        if (existingTables.Count > 0)
        {
            if (!allowDestructiveEmptyRebuild)
            {
                throw new InvalidOperationException(
                    "The database is not at the expected schema. An empty destructive rebuild must be explicitly enabled.");
            }

            var nonEmptyTables = await FindNonEmptyTablesAsync(connection, existingTables, cancellationToken)
                .ConfigureAwait(false);
            if (nonEmptyTables.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Refusing destructive rebuild because these tables contain rows: {string.Join(", ", nonEmptyTables)}.");
            }

            await DropAllUserObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in AccessSchemaDefinition.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteDdlAsync(connection, table.CreateSql(), cancellationToken).ConfigureAwait(false);
        }

        foreach (var index in AccessSchemaDefinition.Indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteDdlAsync(connection, index.CreateSql(), cancellationToken).ConfigureAwait(false);
        }

        foreach (var foreignKey in AccessSchemaDefinition.ForeignKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteDdlAsync(connection, foreignKey.CreateSql(), cancellationToken).ConfigureAwait(false);
        }

        foreach (var check in AccessSchemaDefinition.CheckConstraints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteDdlAsync(connection, check.CreateSql(), cancellationToken).ConfigureAwait(false);
        }

        foreach (var migration in AccessSchemaMigrations.All)
        {
            await InsertMigrationAsync(connection, migration, cancellationToken).ConfigureAwait(false);
        }

        var verification = await AccessSchemaVerifier
            .VerifyOpenConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (!verification.IsValid)
        {
            throw VerificationFailure(verification);
        }

        return new SchemaMigrationResult(
            true,
            AccessSchemaDefinition.MigrationId,
            AccessSchemaDefinition.Checksum,
            verification);
    }

    private async Task<bool> TryApplyCurrentForwardMigrationAsync(
        OleDbConnection connection,
        SchemaVerificationResult verification,
        CancellationToken cancellationToken)
    {
        var missingMigrationIds = verification.Issues
            .Where(issue => issue.Code == "schema.migration_missing")
            .Select(issue => issue.ObjectName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = AccessSchemaMigrations.All
            .Where(migration => missingMigrationIds.Contains(migration.MigrationId))
            .ToArray();
        if (pending.Length == 0) return false;

        var addressedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressedIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressedChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressedForeignKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in pending.SelectMany(migration => migration.Commands))
        {
            if (TryParseAddColumn(command) is { } column) addressedColumns.Add($"{column.Table}.{column.Name}");
            if (TryParseIndex(command) is { } index) addressedIndexes.Add($"{index.Table}.{index.Name}");
            if (TryParseConstraint(command) is { } constraint) addressedChecks.Add(constraint);
            if (TryParseCreateTable(command) is { } table)
            {
                addressedTables.Add(table);
                addressedIndexes.Add($"{table}.PK_{table}");
            }
            if (TryParseForeignKey(command) is { } foreignKey) addressedForeignKeys.Add(foreignKey);
        }
        var canUpgrade = verification.Issues.All(issue =>
            (issue.Code == "schema.migration_missing" && missingMigrationIds.Contains(issue.ObjectName)) ||
            (issue.Code == "schema.column_missing" && addressedColumns.Contains(issue.ObjectName)) ||
            (issue.Code == "schema.table_missing" && addressedTables.Contains(issue.ObjectName)) ||
            (issue.Code == "schema.index_missing" && addressedIndexes.Contains(issue.ObjectName)) ||
            (issue.Code == "schema.foreign_key_missing" && addressedForeignKeys.Contains(issue.ObjectName)) ||
            (issue.Code is "schema.check_missing" or "schema.check_definition" or "schema.check_unexpected" &&
             addressedChecks.Contains(issue.ObjectName)));
        if (!canUpgrade) return false;

        foreach (var migration in pending)
        {
            foreach (var command in migration.Commands)
                await ExecuteForwardCommandAsync(connection, command, cancellationToken).ConfigureAwait(false);
            await InsertMigrationAsync(connection, migration, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task ExecuteForwardCommandAsync(
        OleDbConnection connection,
        string command,
        CancellationToken cancellationToken)
    {
        if (TryParseAddColumn(command) is { } column && connection.GetSchema("Columns").Rows.Cast<System.Data.DataRow>()
            .Any(row => string.Equals(row["TABLE_NAME"]?.ToString(), column.Table, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(row["COLUMN_NAME"]?.ToString(), column.Name, StringComparison.OrdinalIgnoreCase))) return;
        if (TryParseCreateTable(command) is { } table &&
            AccessSchemaInspector.GetUserTableNames(connection).Contains(table)) return;
        if (TryParseIndex(command) is { } index)
        {
            var exists = connection.GetSchema("Indexes").Rows.Cast<System.Data.DataRow>().Any(row =>
                string.Equals(Convert.ToString(row["TABLE_NAME"]), index.Table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Convert.ToString(row["INDEX_NAME"]), index.Name, StringComparison.OrdinalIgnoreCase));
            if (exists) return;
        }
        if (TryParseForeignKey(command) is { } foreignKey)
        {
            var exists = (connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null)?.Rows.Cast<System.Data.DataRow>() ?? [])
                .Any(row => string.Equals(Convert.ToString(row["FK_NAME"]), foreignKey, StringComparison.OrdinalIgnoreCase));
            if (exists) return;
        }
        if (TryParseConstraint(command) is { } constraint)
        {
            var exists = (connection.GetOleDbSchemaTable(OleDbSchemaGuid.Check_Constraints, null)?.Rows.Cast<System.Data.DataRow>() ?? [])
                .Any(row => string.Equals(Convert.ToString(row["CONSTRAINT_NAME"]), constraint, StringComparison.OrdinalIgnoreCase));
            if (command.Contains(" DROP CONSTRAINT ", StringComparison.OrdinalIgnoreCase) && !exists) return;
            if (command.Contains(" ADD CONSTRAINT ", StringComparison.OrdinalIgnoreCase) && exists) return;
        }
        await ExecuteDdlAsync(connection, command, cancellationToken).ConfigureAwait(false);
    }

    private static (string Table, string Name)? TryParseAddColumn(string command)
    {
        var match = Regex.Match(command, @"^ALTER TABLE \[(?<table>[^\]]+)\] ADD COLUMN \[(?<column>[^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups["table"].Value, match.Groups["column"].Value) : null;
    }

    private static (string Name, string Table)? TryParseIndex(string command)
    {
        var match = Regex.Match(command, @"CREATE(?:\s+UNIQUE)?\s+INDEX\s+\[(?<name>[^\]]+)\]\s+ON\s+\[(?<table>[^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups["name"].Value, match.Groups["table"].Value) : null;
    }

    private static string? TryParseConstraint(string command)
    {
        var match = Regex.Match(command, @"(?:DROP|ADD)\s+CONSTRAINT\s+\[(?<name>[^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string? TryParseCreateTable(string command)
    {
        var match = Regex.Match(command, @"^\s*CREATE\s+TABLE\s+\[(?<name>[^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string? TryParseForeignKey(string command)
    {
        if (!command.Contains(" FOREIGN KEY ", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(command, @"ADD\s+CONSTRAINT\s+\[(?<name>[^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private async Task InsertMigrationAsync(
        OleDbConnection connection,
        AccessSchemaMigration migration,
        CancellationToken cancellationToken)
    {
        using var insert = new AccessCommand(
                connection,
                "INSERT INTO [SchemaMigrations] " +
                "([MigrationId], [Checksum], [AppliedAtUtc], [ApplicationVersion], [Status]) " +
                "VALUES (?, ?, ?, ?, ?)")
            .Add(OleDbType.VarWChar, migration.MigrationId, 100)
            .Add(OleDbType.VarWChar, migration.Checksum, 64)
            .Add(OleDbType.Date, auditClock.GetUtcNow().UtcDateTime)
            .Add(OleDbType.VarWChar, migration.ApplicationVersion, 50)
            .Add(OleDbType.VarWChar, "Applied", 20);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static InvalidOperationException VerificationFailure(SchemaVerificationResult verification)
    {
        var detail = string.Join(
            Environment.NewLine,
            verification.Issues.Select(issue => $"{issue.Code}: {issue.ObjectName}: {issue.Detail}"));
        return new InvalidOperationException($"The migrated schema failed verification:{Environment.NewLine}{detail}");
    }

    private static async Task<IReadOnlyList<string>> FindNonEmptyTablesAsync(
        OleDbConnection connection,
        IEnumerable<string> tables,
        CancellationToken cancellationToken)
    {
        var nonEmpty = new List<string>();
        foreach (var table in tables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var command = new AccessCommand(connection, $"SELECT COUNT(*) FROM [{EscapeIdentifier(table)}]");
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt64(value, CultureInfo.InvariantCulture) > 0 &&
                !table.Equals("SchemaMigrations", StringComparison.OrdinalIgnoreCase))
            {
                nonEmpty.Add(table);
            }
        }

        return nonEmpty;
    }

    private static async Task DropAllUserObjectsAsync(
        OleDbConnection connection,
        CancellationToken cancellationToken)
    {
        var foreignKeyTable = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null)
            ?? throw new InvalidOperationException("ACE did not return foreign-key metadata.");
        var foreignKeys = foreignKeyTable.Rows.Cast<System.Data.DataRow>()
            .Where(row => Convert.ToString(row["FK_TABLE_NAME"]) is { } table &&
                          !table.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
            .Select(row => (
                Table: Convert.ToString(row["FK_TABLE_NAME"])!,
                Name: Convert.ToString(row["FK_NAME"])!))
            .Distinct()
            .ToArray();

        foreach (var foreignKey in foreignKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteDdlAsync(
                connection,
                $"ALTER TABLE [{EscapeIdentifier(foreignKey.Table)}] DROP CONSTRAINT [{EscapeIdentifier(foreignKey.Name)}]",
                cancellationToken).ConfigureAwait(false);
        }

        var tables = AccessSchemaInspector.GetUserTableNames(connection);
        foreach (var table in tables.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteDdlAsync(connection, $"DROP TABLE [{EscapeIdentifier(table)}]", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteDdlAsync(
        OleDbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeIdentifier(string value) => value.Replace("]", "]]", StringComparison.Ordinal);
}
