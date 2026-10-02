namespace WritingVaultMcp.Infrastructure.Access.Schema;

internal sealed record AccessSchemaMigration(
    string MigrationId,
    string Checksum,
    string ApplicationVersion,
    IReadOnlyList<string> Commands);

internal static class AccessSchemaMigrations
{
    // This checksum is the immutable fingerprint written by the released v2
    // baseline. Never recalculate or edit an applied migration fingerprint.
    private const string BaselineChecksum =
        "9BF754C1F43CA15CE5C32D5DBFFF31AA11DE6FFA4EFE04D2822366933D9FD653";
    private const string ForeignKeyIndexChecksum =
        "9AAA3645F14DDFE77AF547BF130E4AF2F6C341BFB06C53BAEDF6F0EA0F7F1599";
    private const string LongTextChecksChecksum =
        "73EEB64F808A189EFD88B979CD4883E529896F9EA451430D86CB03AD81AA3A2A";

    private static readonly string[] V4Tables =
    [
        "ContinuityNotes", "ContinuityNoteSources", "EntityEventProjects",
        "CharacterTemporalProfiles", "CharacterTemporalEffects", "EntityImages", "ImageRenditions"
    ];

    private static readonly string[] RelationshipTables =
    [
        "RelationshipParticipants", "RelationshipMembershipPeriods",
        "RelationshipEvents", "RelationshipEventProjects"
    ];

    public static IReadOnlyList<AccessSchemaMigration> All { get; } =
    [
        new(
            "20260927_001_v2_baseline",
            BaselineChecksum,
            "2.0.0",
            []),
        new(
            "20260927_002_fk_indexes",
            ForeignKeyIndexChecksum,
            "2.0.1",
            [
                "CREATE INDEX [IX_CharacterRelationships_Continuity] ON [CharacterRelationships] ([ContinuityId])",
                "CREATE INDEX [IX_OwnershipPrincipals_Continuity] ON [OwnershipPrincipals] ([ContinuityId])"
            ]),
        new(
            "20260927_003_longtext_checks",
            LongTextChecksChecksum,
            "2.0.2",
            [
                "ALTER TABLE [EntityNotes] DROP CONSTRAINT [CK_EntityNotes_Body_NotBlank]",
                "ALTER TABLE [Claims] DROP CONSTRAINT [CK_Claims_ClaimText_NotBlank]"
            ]),
        new(
            AccessSchemaDefinition.FoundationMigrationId,
            AccessSchemaDefinition.FoundationChecksum,
            "4.0.0",
            V4Commands()),
        new(
            AccessSchemaDefinition.RelationshipMembershipMigrationId,
            AccessSchemaDefinition.RelationshipMembershipChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            RelationshipCommands()),
        new(
            AccessSchemaDefinition.RelationshipMergeMigrationId,
            AccessSchemaDefinition.RelationshipMergeChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            RelationshipMergeCommands()),
        new(
            AccessSchemaDefinition.RelationshipTransitionMigrationId,
            AccessSchemaDefinition.RelationshipTransitionChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            RelationshipTransitionCommands()),
        new(
            AccessSchemaDefinition.MembershipTransitionDescriptionMigrationId,
            AccessSchemaDefinition.MembershipTransitionDescriptionChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            MembershipTransitionDescriptionCommands()),
        new(
            AccessSchemaDefinition.ExplicitRangeMeaningMigrationId,
            AccessSchemaDefinition.ExplicitRangeMeaningChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            ExplicitRangeMeaningCommands()),
        new(
            AccessSchemaDefinition.StoryImageOwnerMigrationId,
            AccessSchemaDefinition.StoryImageOwnerChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            StoryImageOwnerCommands()),
        new(
            AccessSchemaDefinition.ImageContentRevisionMigrationId,
            AccessSchemaDefinition.ImageContentRevisionChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            ImageContentRevisionCommands()),
        new(
            AccessSchemaDefinition.RecordPageSnapshotMigrationId,
            AccessSchemaDefinition.RecordPageSnapshotChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            RecordPageSnapshotCommands()),
        new(AccessSchemaDefinition.ProjectStoryEventMigrationId, AccessSchemaDefinition.ProjectStoryEventChecksum,
            AccessSchemaDefinition.ApplicationVersion,
            new[] { "ALTER TABLE [EntityEvents] ADD COLUMN [ProjectBoundary] TEXT(20)",
                "ALTER TABLE [Characters] ADD COLUMN [BirthdayRecurring] YESNO NOT NULL DEFAULT 0" }.Concat(
                new[] { "WorldEvents", "EntityEvents", "RelationshipEvents" }.SelectMany(table => new[] {
                    $"ALTER TABLE [{table}] ADD COLUMN [RecurrenceFrequency] TEXT(10)",
                    $"ALTER TABLE [{table}] ADD COLUMN [RecurrenceInterval] LONG",
                    $"ALTER TABLE [{table}] ADD COLUMN [RecurrenceUntil] DATETIME" })).ToArray()),
        new(AccessSchemaDefinition.SpeciesMigrationId, AccessSchemaDefinition.SpeciesChecksum,
            AccessSchemaDefinition.ApplicationVersion, SpeciesCommands()),
        new(AccessSchemaDefinition.PrivateMemoryMigrationId, AccessSchemaDefinition.PrivateMemoryChecksum,
            AccessSchemaDefinition.ApplicationVersion, PrivateMemoryCommands()),
        new(AccessSchemaDefinition.MigrationId, AccessSchemaDefinition.Checksum,
            AccessSchemaDefinition.ApplicationVersion, [
                "ALTER TABLE [PrivateMemories] ADD COLUMN [Pinned] YESNO NOT NULL DEFAULT 0",
                "ALTER TABLE [PrivateMemories] ADD COLUMN [Removed] YESNO NOT NULL DEFAULT 0",
                "ALTER TABLE [WorldEvents] ADD COLUMN [FactStatus] TEXT(20) DEFAULT 'Unspecified'",
                "ALTER TABLE [EntityEvents] ADD COLUMN [FactStatus] TEXT(20) DEFAULT 'Unspecified'",
                "ALTER TABLE [RelationshipEvents] ADD COLUMN [FactStatus] TEXT(20) DEFAULT 'Unspecified'"
            ])
    ];

    public static AccessSchemaMigration Current => All[^1];

    private static IReadOnlyList<string> PrivateMemoryCommands() =>
        AccessSchemaDefinition.Tables.Where(t => t.Name == "PrivateMemories")
            .Select(t => (t with { Columns = t.Columns.Where(c => c.Name is not ("Pinned" or "Removed")).ToArray() }).CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(i => i.Table == "PrivateMemories").Select(i => i.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(k => k.DependentTable == "PrivateMemories").Select(k => k.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(c => c.Table == "PrivateMemories").Select(c => c.CreateSql())).ToArray();

    private static IReadOnlyList<string> SpeciesCommands() =>
        AccessSchemaDefinition.Tables.Where(t => t.Name == "Species").Select(t => t.CreateSql())
            .Concat(new[] { "ALTER TABLE [Characters] ADD COLUMN [SpeciesId] LONG", "ALTER TABLE [Characters] ADD COLUMN [Race] TEXT(100)", "ALTER TABLE [Characters] DROP COLUMN [Species]" })
            .Concat(AccessSchemaDefinition.Indexes.Where(i => i.Table == "Species" || i.Columns.Contains("SpeciesId")).Select(i => i.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(k => k.Name is "FK_Species_Canon" or "FK_Characters_Species").Select(k => k.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(c => c.Table == "Species").Select(c => c.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(c => c.Name is "CK_CanonEntities_EntityType" or "CK_VariantGroups_EntityType")
                .SelectMany(c => new[] { $"ALTER TABLE [{c.Table}] DROP CONSTRAINT [{c.Name}]", c.CreateSql() })).ToArray();

    private static IReadOnlyList<string> V4Commands()
    {
        var tables = V4Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AccessSchemaDefinition.Tables.Where(table => tables.Contains(table.Name)).Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => tables.Contains(index.Table)).Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => tables.Contains(key.DependentTable)).Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => tables.Contains(check.Table)).Select(LegacyCheckSql))
            .ToArray();
    }

    private static IReadOnlyList<string> RelationshipCommands()
    {
        var tables = RelationshipTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schema = AccessSchemaDefinition.Tables.Where(table => tables.Contains(table.Name)).Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => tables.Contains(index.Table)).Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => tables.Contains(key.DependentTable)).Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => tables.Contains(check.Table)).Select(LegacyCheckSql));
        // Both old endpoints receive their own participation row and identical
        // initial period. This preserves each old relationship identity and its
        // notes rather than guessing which separately authored rows to merge.
        string ParticipantInsert(string column) =>
            "INSERT INTO [RelationshipParticipants] ([RelationshipId],[CharacterId],[CreatedAtUtc],[UpdatedAtUtc]) " +
            $"SELECT r.[Id],r.[{column}],r.[CreatedAtUtc],r.[UpdatedAtUtc] FROM [CharacterRelationships] AS r " +
            $"WHERE NOT EXISTS (SELECT * FROM [RelationshipParticipants] AS p WHERE p.[RelationshipId]=r.[Id] AND p.[CharacterId]=r.[{column}])";
        const string periodInsert =
            "INSERT INTO [RelationshipMembershipPeriods] ([ParticipantId],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound]," +
            "[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
            "SELECT p.[Id],r.[PeriodKind],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[PeriodLowerInclusive]," +
            "r.[PeriodUpperInclusive],r.[PeriodOriginalText],r.[PeriodCalendarId],r.[CreatedAtUtc],r.[UpdatedAtUtc] " +
            "FROM [RelationshipParticipants] AS p INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id] " +
            "WHERE NOT EXISTS (SELECT * FROM [RelationshipMembershipPeriods] AS m WHERE m.[ParticipantId]=p.[Id])";
        return schema.Concat([ParticipantInsert("SourceCharacterId"), ParticipantInsert("TargetCharacterId"), periodInsert]).ToArray();
    }

    private static IReadOnlyList<string> RelationshipMergeCommands()
    {
        const string tableName = "RelationshipMergeRedirects";
        return AccessSchemaDefinition.Tables.Where(table => table.Name == tableName).Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => index.Table == tableName).Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => key.DependentTable == tableName).Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => check.Table == tableName).Select(LegacyCheckSql))
            .ToArray();
    }

    private static IReadOnlyList<string> RelationshipTransitionCommands()
    {
        const string tableName = "RelationshipMembershipTransitions";
        var schema = AccessSchemaDefinition.Tables.Where(table => table.Name == tableName).Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => index.Table == tableName).Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => key.DependentTable == tableName).Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => check.Table == tableName).Select(LegacyCheckSql));
        // Only a one-day or one-instant legacy period establishes both named
        // transitions exactly. A range or open/fuzzy period does not prove
        // that either bound is the actual join or leave date.
        string Backfill(string kind) =>
            "INSERT INTO [RelationshipMembershipTransitions] ([MembershipPeriodId],[TransitionKind]," +
            "[OccurredKind],[OccurredLowerBound],[OccurredUpperBound],[OccurredLowerInclusive]," +
            "[OccurredUpperInclusive],[OccurredOriginalText],[OccurredCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
            $"SELECT m.[Id],'{kind}',m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound]," +
            "m.[PeriodLowerInclusive],m.[PeriodUpperInclusive],m.[PeriodOriginalText],m.[PeriodCalendarId]," +
            "m.[CreatedAtUtc],m.[UpdatedAtUtc] FROM [RelationshipMembershipPeriods] AS m " +
            "WHERE m.[PeriodKind] IN ('ExactDate','ExactInstant') AND NOT EXISTS " +
            $"(SELECT * FROM [RelationshipMembershipTransitions] AS t WHERE t.[MembershipPeriodId]=m.[Id] AND t.[TransitionKind]='{kind}')";
        return schema.Concat([Backfill("Join"), Backfill("Leave")]).ToArray();
    }

    private static IReadOnlyList<string> MembershipTransitionDescriptionCommands()
    {
        var tables = new[] { "RelationshipTransitionDescriptions", "OrganizationMembershipTransitions" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schema = AccessSchemaDefinition.Tables.Where(table => tables.Contains(table.Name))
            .Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => tables.Contains(index.Table))
                .Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => tables.Contains(key.DependentTable))
                .Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => tables.Contains(check.Table))
                .Select(LegacyCheckSql));
        // Only exact legacy membership dates establish an actual transition.
        // A Range may be a known duration or fuzzy window in older data.
        string Backfill(string kind) =>
            "INSERT INTO [OrganizationMembershipTransitions] ([MembershipId],[TransitionKind]," +
            "[OccurredKind],[OccurredLowerBound],[OccurredUpperBound],[OccurredLowerInclusive]," +
            "[OccurredUpperInclusive],[OccurredOriginalText],[OccurredCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
            $"SELECT m.[Id],'{kind}',m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound]," +
            "m.[PeriodLowerInclusive],m.[PeriodUpperInclusive],m.[PeriodOriginalText],m.[PeriodCalendarId]," +
            "m.[CreatedAtUtc],m.[UpdatedAtUtc] FROM [OrganizationMemberships] AS m " +
            "WHERE m.[PeriodKind] IN ('ExactDate','ExactInstant') AND NOT EXISTS " +
            $"(SELECT * FROM [OrganizationMembershipTransitions] AS t WHERE t.[MembershipId]=m.[Id] AND t.[TransitionKind]='{kind}')";
        return schema.Concat([Backfill("Join"), Backfill("Leave")]).ToArray();
    }

    private static IReadOnlyList<string> ExplicitRangeMeaningCommands() =>
        AccessSchemaDefinition.CheckConstraints
            .Where(check => check.Expression.Contains("'KnownRange'", StringComparison.Ordinal))
            .SelectMany(check => new[]
            {
                $"ALTER TABLE [{check.Table}] DROP CONSTRAINT [{check.Name}]",
                check.CreateSql()
            })
            .ToArray();

    private static IReadOnlyList<string> StoryImageOwnerCommands()
    {
        var tables = new[] { "StoryImages", "StoryImageRenditions" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AccessSchemaDefinition.Tables.Where(table => tables.Contains(table.Name))
            .Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => tables.Contains(index.Table))
                .Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => tables.Contains(key.DependentTable))
                .Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => tables.Contains(check.Table))
                .Select(check => check.CreateSql()))
            .ToArray();
    }

    private static IReadOnlyList<string> ImageContentRevisionCommands()
    {
        var tables = new[] { "ImageContentVersions", "ImageHistoricalContent" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AccessSchemaDefinition.Tables.Where(table => tables.Contains(table.Name))
            .Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => tables.Contains(index.Table))
                .Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => tables.Contains(key.DependentTable))
                .Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => tables.Contains(check.Table))
                .Select(check => check.CreateSql()))
            .Concat(new[]
            {
                "INSERT INTO [ImageContentVersions] ([ImageKind],[ImageId],[CurrentRevision]) " +
                "SELECT 'EntityImage',i.[Id],1 FROM [EntityImages] AS i WHERE NOT EXISTS " +
                "(SELECT * FROM [ImageContentVersions] AS v WHERE v.[ImageKind]='EntityImage' AND v.[ImageId]=i.[Id])",
                "INSERT INTO [ImageContentVersions] ([ImageKind],[ImageId],[CurrentRevision]) " +
                "SELECT 'StoryImage',i.[Id],1 FROM [StoryImages] AS i WHERE NOT EXISTS " +
                "(SELECT * FROM [ImageContentVersions] AS v WHERE v.[ImageKind]='StoryImage' AND v.[ImageId]=i.[Id])"
            })
            .ToArray();
    }

    private static IReadOnlyList<string> RecordPageSnapshotCommands()
    {
        var tables = new[] { "RecordPageSnapshots", "RecordPageDependencies" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AccessSchemaDefinition.Tables.Where(table => tables.Contains(table.Name))
            .Select(table => table.CreateSql())
            .Concat(AccessSchemaDefinition.Indexes.Where(index => tables.Contains(index.Table))
                .Select(index => index.CreateSql()))
            .Concat(AccessSchemaDefinition.ForeignKeys.Where(key => tables.Contains(key.DependentTable))
                .Select(key => key.CreateSql()))
            .Concat(AccessSchemaDefinition.CheckConstraints.Where(check => tables.Contains(check.Table))
                .Select(check => check.CreateSql()))
            .ToArray();
    }

    private static string LegacyCheckSql(AccessCheckConstraintDefinition check)
    {
        // Applied migrations retain their original DDL; 009 alone expands
        // stored date kinds. The old fingerprints above never change.
        var expression = check.Expression.Replace(",'KnownRange','UncertainRange'", string.Empty, StringComparison.Ordinal);
        return new AccessCheckConstraintDefinition(check.Name, check.Table, expression).CreateSql();
    }
}
