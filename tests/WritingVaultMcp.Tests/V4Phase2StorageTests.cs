using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase2StorageTests
{
    private static readonly string[] V4ForeignKeys =
    [
        "FK_ContinuityNotes_Continuity", "FK_ContinuityNoteSources_Note", "FK_ContinuityNoteSources_Source",
        "FK_EntityEventProjects_Event", "FK_EntityEventProjects_Project", "FK_TemporalProfiles_Character",
        "FK_TemporalEffects_Continuity", "FK_TemporalEffects_Character", "FK_TemporalEffects_WorldEvent",
        "FK_EntityImages_Entity", "FK_EntityImages_Source", "FK_ImageRenditions_Image"
    ];

    private static readonly string[] V4Tables =
    [
        "ImageRenditions", "EntityImages", "CharacterTemporalEffects", "CharacterTemporalProfiles",
        "EntityEventProjects", "ContinuityNoteSources", "ContinuityNotes"
    ];

    private static readonly string[] RelationshipTables =
    ["RelationshipEventProjects", "RelationshipEvents", "RelationshipMembershipPeriods", "RelationshipParticipants"];

    [Fact]
    public async Task LegacyRangeReviewIdentifiesRowsWithoutReclassifyingThem()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Legacy range review", "UTC"))).ResourceKey!);
        var organization = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Organization, "The Society"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Sam"))).ResourceKey!);
        var created = await vault.Service.AddMembershipAsync(new(Guid.NewGuid().ToString(),
            organization, character, new StoryDate(StoryDateKind.Range,
                new DateTime(2022, 1, 1), new DateTime(2022, 2, 1),
                OriginalText: "sometime in January")));
        Assert.True(created.Success);

        var review = new AccessLegacyRangeReview(vault.Factory);
        var row = Assert.Single(await review.ListAsync());
        Assert.Equal("OrganizationMemberships", row.Table);
        Assert.Equal("Period", row.Field);
        Assert.Equal(int.Parse(created.ResourceKey!), row.StorageKey);
        Assert.Equal("sometime in January", row.OriginalText);
        Assert.Equal(row, Assert.Single(await review.ListAsync()));
    }

    [Fact]
    public async Task OrganizationTransitionMigrationBackfillsOnlyProvenExactDates()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Organization legacy", "UTC"))).ResourceKey!);
        var organization = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Organization, "The Society"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Sam"))).ResourceKey!);
        var exact = await vault.Service.AddMembershipAsync(new(Guid.NewGuid().ToString(),
            organization, character, StoryDate.ExactDate(new DateOnly(2021, 9, 8))));
        var fuzzy = await vault.Service.AddMembershipAsync(new(Guid.NewGuid().ToString(),
            organization, character, new StoryDate(StoryDateKind.Range,
                new DateTime(2022, 1, 1), new DateTime(2022, 2, 1))));
        Assert.True(exact.Success);
        Assert.True(fuzzy.Success);
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            await RemoveMembershipTransitionDescriptionSchema(connection);
        }
        var migrated = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migrated.Verification.IsValid);
        await using var check = vault.Factory.Create();
        await check.OpenAsync();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT [MembershipId],COUNT(*) FROM [OrganizationMembershipTransitions] " +
            "GROUP BY [MembershipId] ORDER BY [MembershipId]";
        var groups = new List<(int Membership, int Count)>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) groups.Add((reader.GetInt32(0), reader.GetInt32(1)));
        Assert.Equal((int.Parse(exact.ResourceKey!), 2), Assert.Single(groups));
        Assert.DoesNotContain(groups, item => item.Membership == int.Parse(fuzzy.ResourceKey!));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task PopulatedV3ShapeMigratesForwardWithoutLosingExistingRows()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            await Execute(connection, "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Preserved v3','PRESERVED V3','UTC',Now(),Now())");
            await RemoveV4Schema(connection);
        }

        var before = await vault.Schema.VerifyAsync();
        Assert.Contains(before.Issues, issue => issue.Code == "schema.table_missing" && issue.ObjectName == "ContinuityNotes");

        var migrated = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migrated.Changed);
        Assert.True(migrated.Verification.IsValid, string.Join(Environment.NewLine, migrated.Verification.Issues));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);

        await using var verify = vault.Factory.Create();
        await verify.OpenAsync();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM [Continuities] WHERE [NormalizedName]='PRESERVED V3'";
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task InterruptedV4ForwardMigrationResumesIdempotently()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            await RemoveV4Schema(connection);
            var continuityNotes = AccessSchemaDefinition.Tables.Single(table => table.Name == "ContinuityNotes");
            await Execute(connection, continuityNotes.CreateSql());
        }

        var migrated = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migrated.Changed);
        Assert.True(migrated.Verification.IsValid, string.Join(Environment.NewLine, migrated.Verification.Issues));
        Assert.False((await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false)).Changed);
    }

    [Fact]
    public async Task RelationshipMigrationKeepsLegacyIdentityAndBackfillsBothMemberships()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Legacy relationship migration", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "First"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Second"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friend of", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8)), "A preserved note."))).ResourceKey!);
        var uncertainRelationship = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            new StoryDate(StoryDateKind.Range, new DateTime(2022, 1, 1),
                new DateTime(2022, 2, 1)), "Uncertain join and leave."))).ResourceKey!);

        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            await RemoveMembershipTransitionDescriptionSchema(connection);
            await Execute(connection, "DROP TABLE [RelationshipMembershipTransitions]");
            await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260930_007_relationship_transitions'");
            await Execute(connection, "DROP TABLE [RelationshipMergeRedirects]");
            await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260930_006_relationship_merge'");
            foreach (var table in RelationshipTables) await Execute(connection, $"DROP TABLE [{table}]");
            await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260929_005_relationship_membership'");
        }

        var migrated = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migrated.Changed);
        Assert.True(migrated.Verification.IsValid, string.Join(Environment.NewLine, migrated.Verification.Issues));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
        await using var verify = vault.Factory.Create();
        await verify.OpenAsync();
        using var rows = verify.CreateCommand();
        rows.CommandText = $"SELECT p.[CharacterId],m.[PeriodKind] FROM [RelationshipParticipants] AS p " +
            $"INNER JOIN [RelationshipMembershipPeriods] AS m ON p.[Id]=m.[ParticipantId] " +
            $"WHERE p.[RelationshipId]={relationship} ORDER BY p.[CharacterId]";
        var members = new List<(int Character, string Kind)>();
        await using (var reader = await rows.ExecuteReaderAsync())
            while (await reader.ReadAsync()) members.Add((reader.GetInt32(0), reader.GetString(1)));
        Assert.Equal(new[] { first, second }.Order(), members.Select(item => item.Character));
        Assert.All(members, item => Assert.Equal("ExactDate", item.Kind));
        using var identity = verify.CreateCommand();
        identity.CommandText = $"SELECT [Notes] FROM [CharacterRelationships] WHERE [Id]={relationship}";
        Assert.Equal("A preserved note.", await identity.ExecuteScalarAsync());
        using var exactTransitions = verify.CreateCommand();
        exactTransitions.CommandText =
            "SELECT t.[TransitionKind],t.[OccurredKind] FROM " +
            "([RelationshipMembershipTransitions] AS t INNER JOIN " +
            "[RelationshipMembershipPeriods] AS m ON t.[MembershipPeriodId]=m.[Id]) " +
            "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] " +
            $"WHERE p.[RelationshipId]={relationship} ORDER BY t.[Id]";
        var backfilled = new List<(string Transition, string DateKind)>();
        await using (var reader = await exactTransitions.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                backfilled.Add((reader.GetString(0), reader.GetString(1)));
        Assert.Equal(2, backfilled.Count(item => item == ("Join", "ExactDate")));
        Assert.Equal(2, backfilled.Count(item => item == ("Leave", "ExactDate")));
        using var uncertainTransitions = verify.CreateCommand();
        uncertainTransitions.CommandText =
            "SELECT COUNT(*) FROM " +
            "([RelationshipMembershipTransitions] AS t INNER JOIN " +
            "[RelationshipMembershipPeriods] AS m ON t.[MembershipPeriodId]=m.[Id]) " +
            "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] " +
            $"WHERE p.[RelationshipId]={uncertainRelationship}";
        Assert.Equal(0, Convert.ToInt32(await uncertainTransitions.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task InterruptedRelationshipTransitionMigrationCompletesWithoutDuplicateBoundaries()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Partial transition migration", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "First"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Second"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8))))).ResourceKey!);

        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            await RemoveMembershipTransitionDescriptionSchema(connection);
            await Execute(connection, "DROP TABLE [RelationshipMembershipTransitions]");
            await Execute(connection,
                "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260930_007_relationship_transitions'");
            var table = AccessSchemaDefinition.Tables.Single(item =>
                item.Name == "RelationshipMembershipTransitions");
            await Execute(connection, table.CreateSql());
            await Execute(connection,
                "INSERT INTO [RelationshipMembershipTransitions] ([MembershipPeriodId],[TransitionKind]," +
                "[OccurredKind],[OccurredLowerBound],[OccurredUpperBound],[OccurredLowerInclusive]," +
                "[OccurredUpperInclusive],[OccurredOriginalText],[OccurredCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
                "SELECT TOP 1 m.[Id],'Join',m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound]," +
                "m.[PeriodLowerInclusive],m.[PeriodUpperInclusive],m.[PeriodOriginalText]," +
                "m.[PeriodCalendarId],m.[CreatedAtUtc],m.[UpdatedAtUtc] " +
                "FROM [RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p " +
                $"ON m.[ParticipantId]=p.[Id] WHERE p.[RelationshipId]={relationship}");
        }

        var migrated = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migrated.Changed);
        Assert.True(migrated.Verification.IsValid);
        await using (var verify = vault.Factory.Create())
        {
            await verify.OpenAsync();
            using var query = verify.CreateCommand();
            query.CommandText =
                "SELECT t.[MembershipPeriodId],t.[TransitionKind] " +
                "FROM ([RelationshipMembershipTransitions] AS t INNER JOIN " +
                "[RelationshipMembershipPeriods] AS m ON t.[MembershipPeriodId]=m.[Id]) " +
                "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] " +
                $"WHERE p.[RelationshipId]={relationship}";
            var transitions = new List<(int Period, string Kind)>();
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                transitions.Add((reader.GetInt32(0), reader.GetString(1)));
            Assert.Equal(2, transitions.Select(item => item.Period).Distinct().Count());
            Assert.All(transitions.GroupBy(item => item.Period), group =>
                Assert.Equal(new[] { "Join", "Leave" }, group.Select(item => item.Kind).Order()));
        }
        Assert.False((await new AccessSchemaMigrator(vault.Factory, TimeProvider.System)
            .MigrateAsync(false)).Changed);
    }

    [Fact]
    public async Task NewDatabaseConstraintsRejectInvalidTemporalAndImageRows()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Constraint world", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Constraint owner"))).ResourceKey!);
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();

        await Assert.ThrowsAsync<OleDbException>(() => Execute(connection,
            "INSERT INTO [CharacterTemporalProfiles] ([CharacterId],[Enabled],[LegalAgePolicy],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (999999,True,'UnknownPolicy',Now(),Now())"));
        await Assert.ThrowsAsync<OleDbException>(() => Execute(connection,
            "INSERT INTO [ImageRenditions] ([ImageId],[RenditionKind],[MediaType],[Width],[Height],[ByteSize],[Content]) VALUES (999999,'Original','image/jpeg',1,1,1,0x00)"));
        await Assert.ThrowsAsync<OleDbException>(() => Execute(connection,
            "INSERT INTO [EntityEventProjects] ([EntityEventId],[ProjectId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (999999,999998,Now(),Now())"));
        using var image = connection.CreateCommand();
        image.CommandText = $"INSERT INTO [EntityImages] ([EntityId],[IsPrimary],[OriginalRelativePath],[OriginalSha256],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ({character},False,'originals\\constraint.png','{new string('A', 64)}','image/png',1,1,1,Now(),Now())";
        await image.ExecuteNonQueryAsync();
        image.CommandText = "SELECT @@IDENTITY";
        var imageId = Convert.ToInt32(await image.ExecuteScalarAsync());
        await Assert.ThrowsAsync<OleDbException>(() => Execute(connection,
            $"INSERT INTO [ImageRenditions] ([ImageId],[RenditionKind],[MediaType],[Width],[Height],[ByteSize],[Content]) VALUES ({imageId},'Thumbnail','image/jpeg',513,1,1,0x00)"));
        await Assert.ThrowsAsync<OleDbException>(() => Execute(connection,
            $"INSERT INTO [EntityImages] ([EntityId],[IsPrimary],[OriginalRelativePath],[OriginalSha256],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ({character},False,'originals\\too-large.png','{new string('B', 64)}','image/png',1,1,20971521,Now(),Now())"));
    }

    [Fact]
    public void EveryV4ForeignKeyHasALeadingIndex()
    {
        foreach (var foreignKey in AccessSchemaDefinition.ForeignKeys.Where(key => V4ForeignKeys.Contains(key.Name)))
        {
            var table = AccessSchemaDefinition.Tables.Single(candidate => candidate.Name == foreignKey.DependentTable);
            Assert.True(
                table.PrimaryKey.FirstOrDefault()?.Equals(foreignKey.DependentColumn, StringComparison.OrdinalIgnoreCase) == true ||
                AccessSchemaDefinition.Indexes.Any(index => index.Table == foreignKey.DependentTable &&
                    index.Columns.FirstOrDefault()?.Equals(foreignKey.DependentColumn, StringComparison.OrdinalIgnoreCase) == true),
                $"{foreignKey.DependentTable}.{foreignKey.DependentColumn} has no leading index.");
        }
    }

    [Fact]
    public async Task IntegrityVerifierFindsMalformedV4ShapesThatForeignKeysCannotExpress()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Malformed", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Owner"))).ResourceKey!);

        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            for (var index = 0; index < 2; index++)
            {
                using var image = connection.CreateCommand();
                image.CommandText = "INSERT INTO [EntityImages] ([EntityId],[IsPrimary],[OriginalRelativePath],[OriginalSha256],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?)";
                image.Parameters.Add("?", OleDbType.Integer).Value = character;
                image.Parameters.Add("?", OleDbType.Boolean).Value = true;
                image.Parameters.Add("?", OleDbType.VarWChar, 255).Value = $"originals\\{index}.png";
                image.Parameters.Add("?", OleDbType.VarWChar, 64).Value = new string((char)('A' + index), 64);
                image.Parameters.Add("?", OleDbType.VarWChar, 100).Value = "image/png";
                image.Parameters.Add("?", OleDbType.Integer).Value = 1;
                image.Parameters.Add("?", OleDbType.Integer).Value = 1;
                image.Parameters.Add("?", OleDbType.Integer).Value = 1;
                image.Parameters.Add("?", OleDbType.Date).Value = DateTime.UtcNow;
                image.Parameters.Add("?", OleDbType.Date).Value = DateTime.UtcNow;
                await image.ExecuteNonQueryAsync();
                image.CommandText = "SELECT @@IDENTITY";
                image.Parameters.Clear();
                var imageId = Convert.ToInt32(await image.ExecuteScalarAsync());
                foreach (var kind in new[] { "Thumbnail", "Display" })
                {
                    using var rendition = connection.CreateCommand();
                    rendition.CommandText = "INSERT INTO [ImageRenditions] ([ImageId],[RenditionKind],[MediaType],[Width],[Height],[ByteSize],[Content]) VALUES (?,?,?,?,?,?,?)";
                    rendition.Parameters.Add("?", OleDbType.Integer).Value = imageId;
                    rendition.Parameters.Add("?", OleDbType.VarWChar, 20).Value = kind;
                    rendition.Parameters.Add("?", OleDbType.VarWChar, 100).Value = "image/jpeg";
                    rendition.Parameters.Add("?", OleDbType.Integer).Value = 1;
                    rendition.Parameters.Add("?", OleDbType.Integer).Value = 1;
                    rendition.Parameters.Add("?", OleDbType.Integer).Value = 1;
                    rendition.Parameters.Add("?", OleDbType.LongVarBinary).Value = new byte[] { 1 };
                    await rendition.ExecuteNonQueryAsync();
                }
            }
            await Execute(connection, "UPDATE [EntityImages] SET [OriginalRelativePath]='..\\escape.png'");
            await Execute(connection, "UPDATE [ImageRenditions] SET [ByteSize]=2");
        }

        var verification = await vault.Integrity.VerifyAsync();
        Assert.Contains(verification.Issues, issue => issue.ObjectName == "EntityImages.IsPrimary");
        Assert.Contains(verification.Issues, issue => issue.Code == "integrity.image_path");
        Assert.Contains(verification.Issues, issue => issue.Code == "integrity.image_rendition_size");
    }

    [Fact]
    public async Task TransactionGuardRollsBackAnImageWithoutBothRenditions()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Atomic image", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Owner"))).ResourceKey!);

        var result = await vault.Coordinator.ExecuteAsync(
            Guid.NewGuid().ToString(), "test.partial-image", new { character }, "test_partial_image", "tests",
            async (context, token) =>
            {
                using var insert = context.Command("INSERT INTO [EntityImages] ([EntityId],[IsPrimary],[OriginalRelativePath],[OriginalSha256],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, character).Add(OleDbType.Boolean, false)
                    .Add(OleDbType.VarWChar, "originals\\partial.png", 255)
                    .Add(OleDbType.VarWChar, new string('C', 64), 64)
                    .Add(OleDbType.VarWChar, "image/png", 100)
                    .Add(OleDbType.Integer, 1).Add(OleDbType.Integer, 1).Add(OleDbType.Integer, 1)
                    .Add(OleDbType.Date, DateTime.UtcNow).Add(OleDbType.Date, DateTime.UtcNow);
                await insert.ExecuteNonQueryAsync(token);
                return new VaultMutationOutcome("EntityImage", "uncommitted", 1, "create", null);
            });

        Assert.False(result.Success);
        Assert.Equal("integrity.invalid_shape", result.Code);
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM [EntityImages]";
        Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task PreMigrationBackupRestoresByteForByteThenMigratesForward()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            await Execute(connection, "INSERT INTO [Continuities] ([Name],[NormalizedName],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES ('Recovery proof','RECOVERY PROOF','UTC',Now(),Now())");
            await RemoveV4Schema(connection);
        }

        var backup = await new AccessBackupService().CreateAsync(
            vault.DatabasePath, vault.StorageRoot, purpose: VaultBackupPurpose.PreMigration);
        Assert.Equal("20260927_003_longtext_checks", backup.Manifest.SchemaMigrationId);
        var restored = Path.Combine(vault.Directory, "restored-v3.accdb");
        await new AccessBackupService().RestoreToNewPathAsync(
            backup.ManifestPath, restored, requireCurrentSchema: false);
        Assert.Equal(backup.Sha256, AccessBackupService.HashFile(restored));

        var restoredFactory = new AccessConnectionFactory(restored);
        var migrated = await new AccessSchemaMigrator(restoredFactory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migrated.Verification.IsValid);
        await using var verify = restoredFactory.Create();
        await verify.OpenAsync();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM [Continuities] WHERE [NormalizedName]='RECOVERY PROOF'";
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task V3ApplicationOperationsContinueOnTheTransitionalV4Schema()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Compatibility", "UTC"))).ResourceKey!);
        var entity = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Still works"));
        Assert.True(entity.Success);
        var page = await vault.Service.SearchEntitiesAsync(CanonEntityType.Character, continuity, "Still works");
        Assert.Single(page.Items);
    }

    private static async Task RemoveV4Schema(OleDbConnection connection)
    {
        await RemoveMembershipTransitionDescriptionSchema(connection);
        await Execute(connection, "DROP TABLE [RelationshipMembershipTransitions]");
        await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260930_007_relationship_transitions'");
        await Execute(connection, "DROP TABLE [RelationshipMergeRedirects]");
        await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260930_006_relationship_merge'");
        foreach (var table in RelationshipTables) await Execute(connection, $"DROP TABLE [{table}]");
        await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260929_005_relationship_membership'");
        foreach (var foreignKey in V4ForeignKeys)
        {
            var definition = AccessSchemaDefinition.ForeignKeys.Single(key => key.Name == foreignKey);
            await Execute(connection, $"ALTER TABLE [{definition.DependentTable}] DROP CONSTRAINT [{definition.Name}]");
        }
        foreach (var table in V4Tables) await Execute(connection, $"DROP TABLE [{table}]");
        await Execute(connection, "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260928_004_v4_foundations'");
    }

    private static async Task RemoveMembershipTransitionDescriptionSchema(OleDbConnection connection)
    {
        await Execute(connection,
            "ALTER TABLE [RecordPageDependencies] DROP CONSTRAINT [FK_RecordPageDependencies_Context]");
        await Execute(connection,
            "ALTER TABLE [RecordPageSnapshots] DROP CONSTRAINT [FK_RecordPageSnapshots_Context]");
        await Execute(connection, "DROP TABLE [RecordPageDependencies]");
        await Execute(connection, "DROP TABLE [RecordPageSnapshots]");
        await Execute(connection,
            "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20261001_012_record_page_snapshots'");
        await Execute(connection, "DROP TABLE [ImageHistoricalContent]");
        await Execute(connection, "DROP TABLE [ImageContentVersions]");
        await Execute(connection,
            "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20261001_011_image_content_revisions'");
        await Execute(connection, "DROP TABLE [StoryImageRenditions]");
        await Execute(connection, "DROP TABLE [StoryImages]");
        await Execute(connection,
            "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20261001_010_story_image_owners'");
        await Execute(connection,
            "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20261001_009_explicit_range_meaning'");
        await Execute(connection, "DROP TABLE [RelationshipTransitionDescriptions]");
        await Execute(connection, "DROP TABLE [OrganizationMembershipTransitions]");
        await Execute(connection,
            "DELETE FROM [SchemaMigrations] WHERE [MigrationId]='20260930_008_membership_transition_descriptions'");
    }

    private static async Task Execute(OleDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
