using System.Data.OleDb;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Tests;

public sealed class SchemaAndConstraintTests
{
    [Fact]
    public async Task FreshMigrationIsExactAndIdempotent()
    {
        await using var vault = await TestVault.CreateAsync();
        Assert.True((await vault.Schema.VerifyAsync()).IsValid);
        var second = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.False(second.Changed);
        Assert.True(second.Verification.IsValid);
    }

    [Fact]
    public async Task PopulatedBaselineUpgradesForwardWithoutRebuildOrDataLoss()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                "DROP INDEX [IX_CharacterRelationships_Continuity] ON [CharacterRelationships]",
                "DROP INDEX [IX_OwnershipPrincipals_Continuity] ON [OwnershipPrincipals]",
                "ALTER TABLE [EntityNotes] ADD CONSTRAINT [CK_EntityNotes_Body_NotBlank] CHECK (Len(Trim([Body])) > 0)",
                "ALTER TABLE [Claims] ADD CONSTRAINT [CK_Claims_ClaimText_NotBlank] CHECK (Len(Trim([ClaimText])) > 0)",
                "DELETE FROM [SchemaMigrations] WHERE [MigrationId] IN ('20260927_002_fk_indexes','20260927_003_longtext_checks')",
                "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Preserved','PRESERVED','UTC',Now(),Now())"
            })
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }

        var result = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);

        Assert.True(result.Changed);
        Assert.True(result.Verification.IsValid);
        await using var verify = vault.Factory.Create();
        await verify.OpenAsync();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM [Continuities] WHERE [NormalizedName]='PRESERVED'";
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public void EveryForeignKeyColumnHasALeadingIndex()
    {
        foreach (var foreignKey in AccessSchemaDefinition.ForeignKeys)
        {
            var table = AccessSchemaDefinition.Tables.Single(candidate =>
                candidate.Name.Equals(foreignKey.DependentTable, StringComparison.OrdinalIgnoreCase));
            var coveredByPrimaryKey = table.PrimaryKey.Count > 0 &&
                                      table.PrimaryKey[0].Equals(foreignKey.DependentColumn, StringComparison.OrdinalIgnoreCase);
            var coveredBySecondaryIndex = AccessSchemaDefinition.Indexes.Any(index =>
                index.Table.Equals(foreignKey.DependentTable, StringComparison.OrdinalIgnoreCase) &&
                index.Columns.Count > 0 &&
                index.Columns[0].Equals(foreignKey.DependentColumn, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                coveredByPrimaryKey || coveredBySecondaryIndex,
                $"{foreignKey.DependentTable}.{foreignKey.DependentColumn} is not the leading column of an index.");
        }
    }

    [Fact]
    public async Task DriftedIndexIsRejectedStructurally()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX [UX_Tags_NormalizedName] ON [Tags]";
            await command.ExecuteNonQueryAsync();
        }
        var result = await vault.Schema.VerifyAsync();
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "schema.index_missing" && issue.ObjectName.Contains("UX_Tags_NormalizedName"));
    }

    [Fact]
    public async Task AccessRejectsHostileRows()
    {
        await using var vault = await TestVault.CreateAsync();
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Canon','CANON','UTC',Now(),Now())");
            var continuityId = await IdentityAsync(connection, transaction);
            await Assert.ThrowsAsync<OleDbException>(() => ExecuteAsync(connection, transaction, "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (' ','BLANK','UTC',Now(),Now())"));
            await Assert.ThrowsAsync<OleDbException>(() => ExecuteAsync(connection, transaction, "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Null','NULL','UTC',Null,Now())"));
            await Assert.ThrowsAsync<OleDbException>(() => ExecuteAsync(connection, transaction, "INSERT INTO [RelationshipTypes] ([Name],[NormalizedName],[IsDirected],[InverseName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('parent of','PARENT OF',True,Null,Now(),Now())"));
            await Assert.ThrowsAsync<OleDbException>(() => ExecuteAsync(connection, transaction, "INSERT INTO [EntityTags] ([EntityId],[TagId]) VALUES (999999,999998)"));
            await ExecuteAsync(connection, transaction, "INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Mystery','MYSTERY',Now(),Now())");
            await Assert.ThrowsAsync<OleDbException>(() => ExecuteAsync(connection, transaction, "INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('mystery','MYSTERY',Now(),Now())"));
            await ExecuteAsync(connection, transaction, $"INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ({continuityId},'Location',Now(),Now())");
            var locationId = await IdentityAsync(connection, transaction);
            await Assert.ThrowsAsync<OleDbException>(() => ExecuteAsync(connection, transaction, $"INSERT INTO [Locations] ([EntityId],[Name],[ParentLocationId]) VALUES ({locationId},'Loop',{locationId})"));
        }
        finally { transaction.Rollback(); }
    }

    [Fact]
    public async Task VerifierDetectsMissingTableColumnAndRelationship()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                "ALTER TABLE [ContinuityClocks] DROP CONSTRAINT [FK_Clock_Continuity]",
                "ALTER TABLE [Sources] DROP COLUMN [Citation]",
                "ALTER TABLE [ChangeLog] DROP CONSTRAINT [FK_ChangeLog_Operation]",
                "DROP TABLE [ChangeLog]"
            })
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
        }
        var result = await vault.Schema.VerifyAsync();
        Assert.Contains(result.Issues, issue => issue.Code == "schema.table_missing" && issue.ObjectName == "ChangeLog");
        Assert.Contains(result.Issues, issue => issue.Code == "schema.column_missing" && issue.ObjectName == "Sources.Citation");
        Assert.Contains(result.Issues, issue => issue.Code == "schema.foreign_key_missing" && issue.ObjectName == "FK_Clock_Continuity");
    }

    [Fact]
    public async Task InterruptedEmptyMigrationCanBeBackedUpRebuiltAndRestored()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WritingVaultMcpMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "legacy.accdb");
        try
        {
            AccessDatabaseFileInitializer.Create(database);
            var factory = new WritingVaultMcp.Infrastructure.Access.AccessConnectionFactory(database);
            await using (var connection = factory.Create())
            {
                await connection.OpenAsync();
                using var partial = connection.CreateCommand();
                partial.CommandText = "CREATE TABLE [InterruptedStep] ([Id] AUTOINCREMENT CONSTRAINT [PK_InterruptedStep] PRIMARY KEY)";
                await partial.ExecuteNonQueryAsync();
            }

            var backupRoot = Path.Combine(directory, "backup");
            var backups = new AccessBackupService();
            var backup = await backups.CreateAsync(database, backupRoot, purpose: VaultBackupPurpose.PreMigration);
            var manifest = await backups.VerifyAsync(backup.ManifestPath, requireCurrentSchema: false);
            Assert.Equal("PreMigration", manifest.Purpose);
            Assert.False(manifest.SchemaValid);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new AccessSchemaMigrator(factory, TimeProvider.System).MigrateAsync(false));
            var migrated = await new AccessSchemaMigrator(factory, TimeProvider.System).MigrateAsync(true);
            Assert.True(migrated.Verification.IsValid);

            var restored = Path.Combine(directory, "restored-legacy.accdb");
            await backups.RestoreToNewPathAsync(backup.ManifestPath, restored, requireCurrentSchema: false);
            await using var restoredConnection = new WritingVaultMcp.Infrastructure.Access.AccessConnectionFactory(restored).Create();
            await restoredConnection.OpenAsync();
            var tables = restoredConnection.GetSchema("Tables").Rows.Cast<System.Data.DataRow>()
                .Select(row => Convert.ToString(row["TABLE_NAME"]))
                .ToArray();
            Assert.Contains("InterruptedStep", tables);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DestructiveRecoveryRefusesAnyUserData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WritingVaultMcpMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "legacy.accdb");
        try
        {
            AccessDatabaseFileInitializer.Create(database);
            var factory = new WritingVaultMcp.Infrastructure.Access.AccessConnectionFactory(database);
            await using (var connection = factory.Create())
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE [LegacyData] ([Id] LONG);";
                await command.ExecuteNonQueryAsync();
                command.CommandText = "INSERT INTO [LegacyData] ([Id]) VALUES (1)";
                await command.ExecuteNonQueryAsync();
            }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AccessSchemaMigrator(factory, TimeProvider.System).MigrateAsync(true));
            Assert.Contains("LegacyData", error.Message);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task ExecuteAsync(OleDbConnection connection, OleDbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> IdentityAsync(OleDbConnection connection, OleDbTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT @@IDENTITY";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
