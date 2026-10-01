using System.Security.Cryptography;
using System.Text;

namespace WritingVaultMcp.Infrastructure.Access.Schema;

internal sealed record AccessColumnDefinition(
    string Name,
    string SqlType,
    bool Required = false,
    string? DefaultSql = null);

internal sealed record AccessTableDefinition(
    string Name,
    IReadOnlyList<AccessColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKey)
{
    public string PrimaryKeyName => $"PK_{Name}";

    public string CreateSql()
    {
        var columnSql = Columns.Select(column =>
        {
            var required = column.Required && !column.SqlType.Equals("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase)
                ? " NOT NULL"
                : string.Empty;
            var defaultSql = column.DefaultSql is null ? string.Empty : $" DEFAULT {column.DefaultSql}";
            return $"[{column.Name}] {column.SqlType}{required}{defaultSql}";
        });
        var primaryColumns = string.Join(", ", PrimaryKey.Select(Quote));
        return $"CREATE TABLE [{Name}] ({string.Join(", ", columnSql)}, " +
               $"CONSTRAINT [{PrimaryKeyName}] PRIMARY KEY ({primaryColumns}))";
    }

    private static string Quote(string value) => $"[{value}]";
}

internal sealed record AccessIndexDefinition(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    bool Unique = false)
{
    public string CreateSql()
    {
        var unique = Unique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", Columns.Select(column => $"[{column}]"));
        return $"CREATE {unique}INDEX [{Name}] ON [{Table}] ({columns})";
    }
}

internal sealed record AccessForeignKeyDefinition(
    string Name,
    string DependentTable,
    string DependentColumn,
    string PrincipalTable,
    string PrincipalColumn = "Id")
{
    public string CreateSql() =>
        $"ALTER TABLE [{DependentTable}] ADD CONSTRAINT [{Name}] " +
        $"FOREIGN KEY ([{DependentColumn}]) REFERENCES [{PrincipalTable}] ([{PrincipalColumn}])";
}

internal sealed record AccessCheckConstraintDefinition(string Name, string Table, string Expression)
{
    public string CreateSql() =>
        $"ALTER TABLE [{Table}] ADD CONSTRAINT [{Name}] CHECK ({Expression})";
}

internal static class AccessSchemaDefinition
{
    public const string FoundationMigrationId = "20260928_004_v4_foundations";
    public const string FoundationChecksum = "ADED4A2CC2A56018D8AD6FB5D4CFCEDD2266E0CA6C37D878B9A92BE88B55B0FE";
    public const string RelationshipMembershipMigrationId = "20260929_005_relationship_membership";
    public const string RelationshipMembershipChecksum = "3F3CF1E183F8C673B3E706809DDFB9356D03413D68853E61D7D01DDD8F4CA2E1";
    public const string RelationshipMergeMigrationId = "20260930_006_relationship_merge";
    public const string RelationshipMergeChecksum = "E7C02DEB9FC77AB93CC6B0C1AF3CE12A86BCC5A34869E17F95019A720135A5C9";
    public const string RelationshipTransitionMigrationId = "20260930_007_relationship_transitions";
    public const string RelationshipTransitionChecksum = "DEC0C05DE1CA4B3FBEC60E3E4DE0C003C29D168465F11F7365691371AE011ED5";
    public const string MembershipTransitionDescriptionMigrationId = "20260930_008_membership_transition_descriptions";
    public const string MembershipTransitionDescriptionChecksum = "87389273795DE9090BEEAE0917F30B76F3DAAE253F64F863203FA0C552815144";
    public const string ExplicitRangeMeaningMigrationId = "20261001_009_explicit_range_meaning";
    public const string ExplicitRangeMeaningChecksum = "AF56DFA54302F8C2ED746197A32DB6610EFE71A296BBCF7170E94873F6598EA6";
    public const string StoryImageOwnerMigrationId = "20261001_010_story_image_owners";
    public const string StoryImageOwnerChecksum = "9E0E8B23111F0534D008898D7B9523034C9B5A3915C03CFB18F3A807CBC5D3F0";
    public const string ImageContentRevisionMigrationId = "20261001_011_image_content_revisions";
    // Migration 011 was already exercised on disposable databases. Keep its
    // fingerprint fixed when subsequent schema definitions are appended.
    public const string ImageContentRevisionChecksum = "86E54828D2907B3F29CC83E01935AB3E106C8BCE8E512166C56D4CD802A5A16D";
    public const string MigrationId = "20261001_012_record_page_snapshots";
    public const string ApplicationVersion = "4.0.0";

    private static AccessColumnDefinition C(
        string name,
        string sqlType,
        bool required = false,
        string? defaultSql = null) => new(name, sqlType, required, defaultSql);

    private static AccessColumnDefinition[] EntityColumns(params AccessColumnDefinition[] specific) =>
    [
        C("Id", "AUTOINCREMENT", true),
        .. specific,
        C("CreatedAtUtc", "DATETIME", true),
        C("UpdatedAtUtc", "DATETIME", true),
        C("Version", "LONG", true, "1"),
        C("IsDeleted", "YESNO", true, "0"),
        C("DeletedAtUtc", "DATETIME"),
        C("DeletedOperationId", "TEXT(36)")
    ];

    private static AccessColumnDefinition[] StoryDate(string prefix) =>
    [
        C($"{prefix}Kind", "TEXT(30)", true),
        C($"{prefix}LowerBound", "DATETIME"),
        C($"{prefix}UpperBound", "DATETIME"),
        C($"{prefix}LowerInclusive", "YESNO", true, "1"),
        C($"{prefix}UpperInclusive", "YESNO", true, "0"),
        C($"{prefix}OriginalText", "LONGTEXT"),
        C($"{prefix}CalendarId", "TEXT(50)", true, "'Gregorian'")
    ];

    private static AccessColumnDefinition[] PeriodColumns() => StoryDate("Period");

    public static IReadOnlyList<AccessTableDefinition> Tables { get; } =
    [
        new("SchemaMigrations",
        [
            C("MigrationId", "TEXT(100)", true),
            C("Checksum", "TEXT(64)", true),
            C("AppliedAtUtc", "DATETIME", true),
            C("ApplicationVersion", "TEXT(50)", true),
            C("Status", "TEXT(20)", true)
        ], ["MigrationId"]),

        new("Continuities", EntityColumns(
            C("Name", "TEXT(255)", true),
            C("NormalizedName", "TEXT(255)", true),
            C("Description", "LONGTEXT"),
            C("DefaultTimeZoneId", "TEXT(100)", true)), ["Id"]),

        new("ContinuityClocks",
        [
            C("ContinuityId", "LONG", true),
            C("CurrentInstantUtc", "DATETIME"),
            C("ReferenceTimeZoneId", "TEXT(100)"),
            C("UpdatedAtUtc", "DATETIME", true),
            C("Version", "LONG", true, "1")
        ], ["ContinuityId"]),

        new("VariantGroups", EntityColumns(
            C("ContinuityId", "LONG", true),
            C("EntityType", "TEXT(30)", true),
            C("Name", "TEXT(255)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("CanonEntities", EntityColumns(
            C("ContinuityId", "LONG", true),
            C("EntityType", "TEXT(30)", true),
            C("VariantGroupId", "LONG")), ["Id"]),

        new("Projects",
        [
            C("EntityId", "LONG", true),
            C("Name", "TEXT(255)", true),
            C("Description", "LONGTEXT")
        ], ["EntityId"]),

        new("Locations",
        [
            C("EntityId", "LONG", true),
            C("Name", "TEXT(255)", true),
            C("LocationType", "TEXT(100)"),
            C("ParentLocationId", "LONG"),
            C("TimeZoneId", "TEXT(100)"),
            C("Description", "LONGTEXT")
        ], ["EntityId"]),

        new("Characters",
        [
            C("EntityId", "LONG", true),
            C("GivenName", "TEXT(100)", true),
            C("MiddleNames", "TEXT(255)"),
            C("FamilyName", "TEXT(100)"),
            C("PreferredName", "TEXT(100)"),
            .. StoryDate("Birth"),
            .. StoryDate("Death"),
            C("BirthLocationId", "LONG"),
            C("BirthLocationDetail", "TEXT(255)"),
            C("Gender", "TEXT(100)"),
            C("Pronouns", "TEXT(100)"),
            C("Species", "TEXT(100)"),
            C("Occupation", "TEXT(255)"),
            C("Nationality", "TEXT(100)"),
            C("PhysicalDescription", "LONGTEXT"),
            C("PersonalitySummary", "LONGTEXT")
        ], ["EntityId"]),

        new("Organizations",
        [
            C("EntityId", "LONG", true),
            C("Name", "TEXT(255)", true),
            C("OrganizationType", "TEXT(100)"),
            C("Description", "LONGTEXT")
        ], ["EntityId"]),

        new("Objects",
        [
            C("EntityId", "LONG", true),
            C("Name", "TEXT(255)", true),
            C("ObjectType", "TEXT(100)"),
            C("Description", "LONGTEXT")
        ], ["EntityId"]),

        new("WorldEvents",
        [
            C("EntityId", "LONG", true),
            C("Title", "TEXT(255)", true),
            C("Description", "LONGTEXT"),
            C("NarrativeOrder", "DOUBLE"),
            .. StoryDate("Event")
        ], ["EntityId"]),

        new("Sources", EntityColumns(
            C("Title", "TEXT(255)", true),
            C("SourceType", "TEXT(100)"),
            C("AuthorPublisher", "TEXT(255)"),
            C("CanonicalUrl", "LONGTEXT"),
            C("ArchiveUrl", "LONGTEXT"),
            C("Citation", "LONGTEXT"),
            C("Notes", "LONGTEXT"),
            C("LastRetrievedAtUtc", "DATETIME"),
            C("RetrievalStatus", "TEXT(50)")), ["Id"]),

        new("SourceSnapshots", EntityColumns(
            C("SourceId", "LONG", true),
            C("RetrievedAtUtc", "DATETIME", true),
            C("MediaType", "TEXT(100)"),
            C("ByteSize", "LONG"),
            C("ContentSha256", "TEXT(64)", true),
            C("RelativeCachePath", "TEXT(255)"),
            C("ExtractionStatus", "TEXT(50)")), ["Id"]),

        new("Tags", EntityColumns(
            C("Name", "TEXT(100)", true),
            C("NormalizedName", "TEXT(100)", true),
            C("Description", "LONGTEXT")), ["Id"]),

        new("EntityNotes", EntityColumns(
            C("EntityId", "LONG", true),
            C("Title", "TEXT(255)"),
            C("Body", "LONGTEXT", true)), ["Id"]),

        new("ContinuityNotes", EntityColumns(
            C("ContinuityId", "LONG", true),
            C("Title", "TEXT(255)"),
            C("Body", "LONGTEXT", true)), ["Id"]),

        new("ContinuityNoteSources", EntityColumns(
            C("ContinuityNoteId", "LONG", true),
            C("SourceId", "LONG", true),
            C("Locator", "TEXT(255)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("EntityEvents", EntityColumns([
            C("EntityId", "LONG", true),
            C("WorldEventId", "LONG"),
            C("Title", "TEXT(255)", true),
            C("Description", "LONGTEXT"),
            C("NarrativeOrder", "DOUBLE"),
            .. StoryDate("Event")]), ["Id"]),

        new("EntityTags",
        [C("EntityId", "LONG", true), C("TagId", "LONG", true)],
        ["EntityId", "TagId"]),

        new("EntitySources",
        [C("EntityId", "LONG", true), C("SourceId", "LONG", true)],
        ["EntityId", "SourceId"]),

        new("NoteSources", EntityColumns(
            C("NoteId", "LONG", true),
            C("SourceId", "LONG", true),
            C("Locator", "TEXT(255)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("ProjectEntities", EntityColumns(
            C("ProjectId", "LONG", true),
            C("MemberEntityId", "LONG", true),
            C("Role", "TEXT(100)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("EntityEventProjects", EntityColumns(
            C("EntityEventId", "LONG", true),
            C("ProjectId", "LONG", true),
            C("Role", "TEXT(100)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("CharacterTemporalProfiles",
        [
            C("CharacterId", "LONG", true),
            C("Enabled", "YESNO", true, "1"),
            C("LegalAgePolicy", "TEXT(30)", true, "'CalendarAge'"),
            C("Notes", "LONGTEXT"),
            C("CreatedAtUtc", "DATETIME", true),
            C("UpdatedAtUtc", "DATETIME", true),
            C("Version", "LONG", true, "1"),
            C("IsDeleted", "YESNO", true, "0"),
            C("DeletedAtUtc", "DATETIME"),
            C("DeletedOperationId", "TEXT(36)")
        ], ["CharacterId"]),

        new("CharacterTemporalEffects", EntityColumns([
            C("ContinuityId", "LONG", true),
            C("CharacterId", "LONG", true),
            C("Name", "TEXT(255)", true),
            .. PeriodColumns(),
            C("BiologicalRate", "DOUBLE", true),
            C("ExperiencedRate", "DOUBLE", true),
            C("WorldEventId", "LONG"),
            C("Notes", "LONGTEXT")]), ["Id"]),

        new("EntityImages", EntityColumns(
            C("EntityId", "LONG", true),
            C("SourceId", "LONG"),
            C("Title", "TEXT(255)"),
            C("Caption", "LONGTEXT"),
            C("AltText", "LONGTEXT"),
            C("Role", "TEXT(100)"),
            C("CanonStatus", "TEXT(50)"),
            C("IsPrimary", "YESNO", true, "0"),
            C("OriginalRelativePath", "TEXT(255)", true),
            C("OriginalSha256", "TEXT(64)", true),
            C("OriginalMediaType", "TEXT(100)", true),
            C("OriginalWidth", "LONG", true),
            C("OriginalHeight", "LONG", true),
            C("OriginalBytes", "LONG", true)), ["Id"]),

        new("ImageRenditions",
        [
            C("ImageId", "LONG", true),
            C("RenditionKind", "TEXT(20)", true),
            C("MediaType", "TEXT(100)", true),
            C("Width", "LONG", true),
            C("Height", "LONG", true),
            C("ByteSize", "LONG", true),
            C("Content", "LONGBINARY", true)
        ], ["ImageId", "RenditionKind"]),

        new("StoryImages", EntityColumns(
            C("ContinuityId", "LONG", true),
            C("OwnerKind", "TEXT(30)", true),
            C("RelationshipId", "LONG"),
            C("EntityEventId", "LONG"),
            C("RelationshipEventId", "LONG"),
            C("SourceId", "LONG"),
            C("Title", "TEXT(255)"),
            C("Caption", "LONGTEXT"),
            C("AltText", "LONGTEXT"),
            C("Role", "TEXT(100)"),
            C("CanonStatus", "TEXT(50)"),
            C("IsPrimary", "YESNO", true, "0"),
            C("OriginalRelativePath", "TEXT(255)", true),
            C("OriginalSha256", "TEXT(64)", true),
            C("OriginalMediaType", "TEXT(100)", true),
            C("OriginalWidth", "LONG", true),
            C("OriginalHeight", "LONG", true),
            C("OriginalBytes", "LONG", true)), ["Id"]),

        new("StoryImageRenditions",
        [
            C("ImageId", "LONG", true),
            C("RenditionKind", "TEXT(20)", true),
            C("MediaType", "TEXT(100)", true),
            C("Width", "LONG", true),
            C("Height", "LONG", true),
            C("ByteSize", "LONG", true),
            C("Content", "LONGBINARY", true)
        ], ["ImageId", "RenditionKind"]),

        new("ImageContentVersions",
        [
            C("ImageKind", "TEXT(20)", true),
            C("ImageId", "LONG", true),
            C("CurrentRevision", "LONG", true)
        ], ["ImageKind", "ImageId"]),

        new("ImageHistoricalContent",
        [
            C("ImageKind", "TEXT(20)", true),
            C("ImageId", "LONG", true),
            C("ContentRevision", "LONG", true),
            C("OriginalRelativePath", "TEXT(255)", true),
            C("OriginalSha256", "TEXT(64)", true),
            C("OriginalMediaType", "TEXT(100)", true),
            C("OriginalWidth", "LONG", true),
            C("OriginalHeight", "LONG", true),
            C("OriginalBytes", "LONG", true),
            C("DisplayMediaType", "TEXT(100)", true),
            C("DisplayWidth", "LONG", true),
            C("DisplayHeight", "LONG", true),
            C("DisplayBytes", "LONG", true),
            C("DisplaySha256", "TEXT(64)", true),
            C("DisplayContent", "LONGBINARY", true),
            C("ThumbnailMediaType", "TEXT(100)", true),
            C("ThumbnailWidth", "LONG", true),
            C("ThumbnailHeight", "LONG", true),
            C("ThumbnailBytes", "LONG", true),
            C("ThumbnailSha256", "TEXT(64)", true),
            C("ThumbnailContent", "LONGBINARY", true),
            C("ArchivedAtUtc", "DATETIME", true)
        ], ["ImageKind", "ImageId", "ContentRevision"]),

        new("RecordPageSnapshots",
        [
            C("Id", "AUTOINCREMENT", true),
            C("RecordType", "TEXT(50)", true),
            C("RecordKey", "LONG", true),
            C("ContextContinuityId", "LONG", true),
            C("SnapshotVersion", "LONG", true),
            C("SavedAtUtc", "DATETIME", true),
            C("OperationId", "TEXT(36)"),
            C("IsBaseline", "YESNO", true, "0"),
            C("PageBytes", "LONG", true),
            C("PageSha256", "TEXT(64)", true),
            C("PageJson", "LONGTEXT", true)
        ], ["Id"]),

        new("RecordPageDependencies",
        [
            C("PageType", "TEXT(50)", true),
            C("PageKey", "LONG", true),
            C("ContextContinuityId", "LONG", true),
            C("TargetReference", "TEXT(80)", true)
        ], ["PageType", "PageKey", "ContextContinuityId", "TargetReference"]),

        new("CharacterAliases", EntityColumns(
            C("CharacterId", "LONG", true),
            C("Alias", "TEXT(255)", true),
            C("NormalizedAlias", "TEXT(255)", true),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("CharacterResidences", EntityColumns([
            C("CharacterId", "LONG", true),
            C("LocationId", "LONG", true),
            C("IsPrimary", "YESNO", true, "1"),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        new("OrganizationAliases", EntityColumns(
            C("OrganizationId", "LONG", true),
            C("Alias", "TEXT(255)", true),
            C("NormalizedAlias", "TEXT(255)", true),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("OrganizationMemberships", EntityColumns([
            C("OrganizationId", "LONG", true),
            C("CharacterId", "LONG", true),
            C("Role", "TEXT(255)"),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        new("OrganizationLocations", EntityColumns([
            C("OrganizationId", "LONG", true),
            C("LocationId", "LONG", true),
            C("LocationRole", "TEXT(100)"),
            C("IsPrimary", "YESNO", true, "0"),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        new("RelationshipTypes", EntityColumns(
            C("Name", "TEXT(100)", true),
            C("NormalizedName", "TEXT(100)", true),
            C("IsDirected", "YESNO", true),
            C("InverseName", "TEXT(100)"),
            C("AllowsOverlappingPeriods", "YESNO", true, "0"),
            C("Description", "LONGTEXT")), ["Id"]),

        new("CharacterRelationships", EntityColumns([
            C("ContinuityId", "LONG", true),
            C("SourceCharacterId", "LONG", true),
            C("TargetCharacterId", "LONG", true),
            C("RelationshipTypeId", "LONG", true),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        // The legacy pair and period columns remain on CharacterRelationships so
        // existing references and v3 reads continue to resolve after migration.
        new("RelationshipParticipants", EntityColumns(
            C("RelationshipId", "LONG", true),
            C("CharacterId", "LONG", true)), ["Id"]),

        new("RelationshipMembershipPeriods", EntityColumns([
            C("ParticipantId", "LONG", true),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        // A join and a leave each retain their own precision. The period row
        // remains the conservative possible-occupancy envelope and stable ref.
        new("RelationshipMembershipTransitions", EntityColumns([
            C("MembershipPeriodId", "LONG", true),
            C("TransitionKind", "TEXT(10)", true),
            .. StoryDate("Occurred")]), ["Id"]),

        // Optional prose belongs to the named transition, not to the whole
        // membership period. Keeping this additive preserves migration 007.
        new("RelationshipTransitionDescriptions", [
            C("TransitionId", "LONG", true),
            C("Description", "LONGTEXT", true)
        ], ["TransitionId"]),

        new("OrganizationMembershipTransitions", EntityColumns([
            C("MembershipId", "LONG", true),
            C("TransitionKind", "TEXT(10)", true),
            C("Description", "LONGTEXT"),
            .. StoryDate("Occurred")]), ["Id"]),

        new("RelationshipEvents", EntityColumns([
            C("RelationshipId", "LONG", true),
            C("WorldEventId", "LONG"),
            C("Title", "TEXT(255)", true),
            C("Description", "LONGTEXT"),
            C("NarrativeOrder", "DOUBLE"),
            .. StoryDate("Event")]), ["Id"]),

        new("RelationshipEventProjects", EntityColumns(
            C("RelationshipEventId", "LONG", true),
            C("ProjectId", "LONG", true),
            C("Role", "TEXT(100)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        // An immutable pointer from an archived legacy identity to the
        // surviving identity. The source row and its notes/history stay intact.
        new("RelationshipMergeRedirects", EntityColumns(
            C("SourceRelationshipId", "LONG", true),
            C("TargetRelationshipId", "LONG", true),
            C("SourceVersionBefore", "LONG", true),
            C("TargetVersionBefore", "LONG", true)), ["Id"]),

        new("OwnershipPrincipals", EntityColumns(
            C("ContinuityId", "LONG", true),
            C("PrincipalKind", "TEXT(30)", true),
            C("CharacterId", "LONG"),
            C("OrganizationId", "LONG"),
            C("Label", "TEXT(255)")), ["Id"]),

        new("ObjectOwnershipPeriods", EntityColumns([
            C("ObjectId", "LONG", true),
            C("OwnerState", "TEXT(30)", true),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        new("ObjectOwnershipOwners",
        [
            C("OwnershipPeriodId", "LONG", true),
            C("PrincipalId", "LONG", true),
            C("SharePartsPerMillion", "LONG"),
            C("Notes", "LONGTEXT")
        ], ["OwnershipPeriodId", "PrincipalId"]),

        new("ObjectCustodyPeriods", EntityColumns([
            C("ObjectId", "LONG", true),
            C("CustodianState", "TEXT(30)", true),
            C("PrincipalId", "LONG"),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        new("ObjectLocationPeriods", EntityColumns([
            C("ObjectId", "LONG", true),
            C("LocationId", "LONG", true),
            C("Notes", "LONGTEXT"),
            .. PeriodColumns()]), ["Id"]),

        new("WorldEventParticipants", EntityColumns(
            C("WorldEventId", "LONG", true),
            C("ParticipantEntityId", "LONG", true),
            C("Role", "TEXT(100)"),
            C("Impact", "LONGTEXT"),
            C("Outcome", "LONGTEXT"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("WorldEventLocations", EntityColumns(
            C("WorldEventId", "LONG", true),
            C("LocationId", "LONG", true),
            C("IsPrimary", "YESNO", true, "0"),
            C("Role", "TEXT(100)"),
            C("Notes", "LONGTEXT")), ["Id"]),

        new("Claims", EntityColumns(
            C("ContinuityId", "LONG", true),
            C("ClaimText", "LONGTEXT", true),
            C("ClaimStatus", "TEXT(30)", true),
            C("Confidence", "DOUBLE"),
            C("TargetField", "TEXT(100)"),
            C("Commentary", "LONGTEXT")), ["Id"]),

        new("ClaimSources", EntityColumns(
            C("ClaimId", "LONG", true),
            C("SourceId", "LONG", true),
            C("SourceSnapshotId", "LONG"),
            C("EvidenceRelation", "TEXT(30)", true),
            C("Locator", "TEXT(255)"),
            C("EvidenceExcerpt", "LONGTEXT"),
            C("Summary", "LONGTEXT")), ["Id"]),

        new("ClaimEntities",
        [C("ClaimId", "LONG", true), C("EntityId", "LONG", true)],
        ["ClaimId", "EntityId"]),

        new("ClaimRelationships",
        [C("ClaimId", "LONG", true), C("RelationshipId", "LONG", true)],
        ["ClaimId", "RelationshipId"]),

        new("ClaimNotes",
        [C("ClaimId", "LONG", true), C("NoteId", "LONG", true)],
        ["ClaimId", "NoteId"]),

        new("SourceTags",
        [C("SourceId", "LONG", true), C("TagId", "LONG", true)],
        ["SourceId", "TagId"]),

        new("ProcessedOperations",
        [
            C("OperationId", "TEXT(36)", true),
            C("CommandType", "TEXT(100)", true),
            C("InputSha256", "TEXT(64)", true),
            C("Status", "TEXT(20)", true),
            C("ResultReference", "TEXT(255)"),
            C("StartedAtUtc", "DATETIME", true),
            C("CompletedAtUtc", "DATETIME")
        ], ["OperationId"]),

        new("ChangeLog",
        [
            C("Id", "AUTOINCREMENT", true),
            C("OperationId", "TEXT(36)", true),
            C("ChangedAtUtc", "DATETIME", true),
            C("ClientLabel", "TEXT(100)"),
            C("ToolName", "TEXT(100)", true),
            C("Action", "TEXT(50)", true),
            C("RecordType", "TEXT(100)", true),
            C("RecordKey", "TEXT(100)", true),
            C("VersionBefore", "LONG"),
            C("VersionAfter", "LONG"),
            C("ChangeJson", "LONGTEXT")
        ], ["Id"])
    ];

    public static IReadOnlyList<AccessIndexDefinition> Indexes { get; } =
    [
        new("UX_Continuities_NormalizedName", "Continuities", ["NormalizedName"], true),
        new("IX_VariantGroups_ContinuityType", "VariantGroups", ["ContinuityId", "EntityType"]),
        new("IX_CanonEntities_ContinuityType", "CanonEntities", ["ContinuityId", "EntityType"]),
        new("IX_CanonEntities_VariantGroup", "CanonEntities", ["VariantGroupId"]),
        new("IX_Locations_Parent", "Locations", ["ParentLocationId"]),
        new("IX_Characters_BirthLocation", "Characters", ["BirthLocationId"]),
        new("IX_SourceSnapshots_Source", "SourceSnapshots", ["SourceId"]),
        new("UX_SourceSnapshots_SourceHash", "SourceSnapshots", ["SourceId", "ContentSha256"], true),
        new("UX_Tags_NormalizedName", "Tags", ["NormalizedName"], true),
        new("IX_EntityNotes_Entity", "EntityNotes", ["EntityId"]),
        new("IX_ContinuityNotes_Continuity", "ContinuityNotes", ["ContinuityId"]),
        new("UX_ContinuityNoteSources_NoteSource", "ContinuityNoteSources", ["ContinuityNoteId", "SourceId"], true),
        new("IX_ContinuityNoteSources_Source", "ContinuityNoteSources", ["SourceId"]),
        new("IX_EntityEvents_Entity", "EntityEvents", ["EntityId"]),
        new("IX_EntityEvents_WorldEvent", "EntityEvents", ["WorldEventId"]),
        new("IX_EntityTags_Tag", "EntityTags", ["TagId"]),
        new("IX_EntitySources_Source", "EntitySources", ["SourceId"]),
        new("UX_NoteSources_NoteSource", "NoteSources", ["NoteId", "SourceId"], true),
        new("IX_NoteSources_Source", "NoteSources", ["SourceId"]),
        new("UX_ProjectEntities_ProjectMember", "ProjectEntities", ["ProjectId", "MemberEntityId"], true),
        new("IX_ProjectEntities_Member", "ProjectEntities", ["MemberEntityId"]),
        new("UX_EntityEventProjects_EventProject", "EntityEventProjects", ["EntityEventId", "ProjectId"], true),
        new("IX_EntityEventProjects_Project", "EntityEventProjects", ["ProjectId"]),
        new("IX_TemporalEffects_Continuity", "CharacterTemporalEffects", ["ContinuityId"]),
        new("IX_TemporalEffects_Character", "CharacterTemporalEffects", ["CharacterId"]),
        new("IX_TemporalEffects_WorldEvent", "CharacterTemporalEffects", ["WorldEventId"]),
        new("IX_TemporalEffects_Period", "CharacterTemporalEffects", ["PeriodLowerBound", "PeriodUpperBound"]),
        new("IX_EntityImages_Entity", "EntityImages", ["EntityId"]),
        new("IX_EntityImages_Source", "EntityImages", ["SourceId"]),
        new("UX_EntityImages_OriginalHash", "EntityImages", ["EntityId", "OriginalSha256"], true),
        new("IX_StoryImages_Continuity", "StoryImages", ["ContinuityId"]),
        new("IX_StoryImages_Relationship", "StoryImages", ["RelationshipId"]),
        new("IX_StoryImages_EntityEvent", "StoryImages", ["EntityEventId"]),
        new("IX_StoryImages_RelationshipEvent", "StoryImages", ["RelationshipEventId"]),
        new("IX_StoryImages_Source", "StoryImages", ["SourceId"]),
        new("UX_CharacterAliases_Normalized", "CharacterAliases", ["CharacterId", "NormalizedAlias"], true),
        new("IX_CharacterResidences_Character", "CharacterResidences", ["CharacterId"]),
        new("IX_CharacterResidences_Location", "CharacterResidences", ["LocationId"]),
        new("UX_OrganizationAliases_Normalized", "OrganizationAliases", ["OrganizationId", "NormalizedAlias"], true),
        new("IX_OrgMemberships_Organization", "OrganizationMemberships", ["OrganizationId"]),
        new("IX_OrgMemberships_Character", "OrganizationMemberships", ["CharacterId"]),
        new("IX_OrgLocations_Organization", "OrganizationLocations", ["OrganizationId"]),
        new("IX_OrgLocations_Location", "OrganizationLocations", ["LocationId"]),
        new("UX_RelationshipTypes_Normalized", "RelationshipTypes", ["NormalizedName"], true),
        new("IX_CharacterRelationships_Source", "CharacterRelationships", ["SourceCharacterId"]),
        new("IX_CharacterRelationships_Target", "CharacterRelationships", ["TargetCharacterId"]),
        new("IX_CharacterRelationships_Type", "CharacterRelationships", ["RelationshipTypeId"]),
        new("IX_CharacterRelationships_Continuity", "CharacterRelationships", ["ContinuityId"]),
        new("UX_RelationshipParticipants_Pair", "RelationshipParticipants", ["RelationshipId", "CharacterId"], true),
        new("IX_RelationshipParticipants_Character", "RelationshipParticipants", ["CharacterId"]),
        new("IX_RelationshipMembershipPeriods_Participant", "RelationshipMembershipPeriods", ["ParticipantId"]),
        new("IX_RelationshipMembershipPeriods_Dates", "RelationshipMembershipPeriods", ["PeriodLowerBound", "PeriodUpperBound"]),
        new("UX_RelMembershipTransitions_Kind", "RelationshipMembershipTransitions", ["MembershipPeriodId", "TransitionKind"], true),
        new("IX_RelMembershipTransitions_Dates", "RelationshipMembershipTransitions", ["OccurredLowerBound", "OccurredUpperBound"]),
        new("UX_OrgMembershipTransitions_Kind", "OrganizationMembershipTransitions", ["MembershipId", "TransitionKind"], true),
        new("IX_OrgMembershipTransitions_Dates", "OrganizationMembershipTransitions", ["OccurredLowerBound", "OccurredUpperBound"]),
        new("IX_RelationshipEvents_Relationship", "RelationshipEvents", ["RelationshipId"]),
        new("IX_RelationshipEvents_WorldEvent", "RelationshipEvents", ["WorldEventId"]),
        new("IX_RelationshipEvents_Dates", "RelationshipEvents", ["EventLowerBound", "EventUpperBound"]),
        new("UX_RelationshipEventProjects_Pair", "RelationshipEventProjects", ["RelationshipEventId", "ProjectId"], true),
        new("IX_RelationshipEventProjects_Project", "RelationshipEventProjects", ["ProjectId"]),
        new("UX_RelationshipMergeRedirects_Source", "RelationshipMergeRedirects", ["SourceRelationshipId"], true),
        new("IX_RelationshipMergeRedirects_Target", "RelationshipMergeRedirects", ["TargetRelationshipId"]),
        new("UX_OwnershipPrincipals_Character", "OwnershipPrincipals", ["CharacterId"], true),
        new("UX_OwnershipPrincipals_Organization", "OwnershipPrincipals", ["OrganizationId"], true),
        new("IX_OwnershipPrincipals_Continuity", "OwnershipPrincipals", ["ContinuityId"]),
        new("IX_OwnershipPeriods_Object", "ObjectOwnershipPeriods", ["ObjectId"]),
        new("IX_OwnershipOwners_Principal", "ObjectOwnershipOwners", ["PrincipalId"]),
        new("IX_CustodyPeriods_Object", "ObjectCustodyPeriods", ["ObjectId"]),
        new("IX_CustodyPeriods_Principal", "ObjectCustodyPeriods", ["PrincipalId"]),
        new("IX_ObjectLocationPeriods_Object", "ObjectLocationPeriods", ["ObjectId"]),
        new("IX_ObjectLocationPeriods_Location", "ObjectLocationPeriods", ["LocationId"]),
        new("UX_WorldEventParticipants", "WorldEventParticipants", ["WorldEventId", "ParticipantEntityId"], true),
        new("IX_WorldEventParticipants_Entity", "WorldEventParticipants", ["ParticipantEntityId"]),
        new("UX_WorldEventLocations", "WorldEventLocations", ["WorldEventId", "LocationId"], true),
        new("IX_WorldEventLocations_Location", "WorldEventLocations", ["LocationId"]),
        new("IX_Claims_Continuity", "Claims", ["ContinuityId"]),
        new("UX_ClaimSources_ClaimSource", "ClaimSources", ["ClaimId", "SourceId"], true),
        new("IX_ClaimSources_Source", "ClaimSources", ["SourceId"]),
        new("IX_ClaimSources_Snapshot", "ClaimSources", ["SourceSnapshotId"]),
        new("IX_ClaimEntities_Entity", "ClaimEntities", ["EntityId"]),
        new("IX_ClaimRelationships_Relationship", "ClaimRelationships", ["RelationshipId"]),
        new("IX_ClaimNotes_Note", "ClaimNotes", ["NoteId"]),
        new("IX_SourceTags_Tag", "SourceTags", ["TagId"]),
        new("IX_ChangeLog_Operation", "ChangeLog", ["OperationId"]),
        new("IX_ChangeLog_Record", "ChangeLog", ["RecordType", "RecordKey"]),
        new("IX_ChangeLog_ChangedAt", "ChangeLog", ["ChangedAtUtc"]),
        new("UX_RecordPageSnapshots_Version", "RecordPageSnapshots",
            ["RecordType", "RecordKey", "ContextContinuityId", "SnapshotVersion"], true),
        new("IX_RecordPageSnapshots_Context", "RecordPageSnapshots", ["ContextContinuityId"]),
        new("IX_RecordPageDependencies_Context", "RecordPageDependencies", ["ContextContinuityId"]),
        new("IX_RecordPageDependencies_Target", "RecordPageDependencies", ["TargetReference"])
    ];

    public static IReadOnlyList<AccessForeignKeyDefinition> ForeignKeys { get; } =
    [
        new("FK_RecordPageSnapshots_Context", "RecordPageSnapshots", "ContextContinuityId", "Continuities"),
        new("FK_RecordPageDependencies_Context", "RecordPageDependencies", "ContextContinuityId", "Continuities"),
        new("FK_Clock_Continuity", "ContinuityClocks", "ContinuityId", "Continuities"),
        new("FK_VariantGroups_Continuity", "VariantGroups", "ContinuityId", "Continuities"),
        new("FK_Canon_Continuity", "CanonEntities", "ContinuityId", "Continuities"),
        new("FK_Canon_VariantGroup", "CanonEntities", "VariantGroupId", "VariantGroups"),
        new("FK_Projects_Canon", "Projects", "EntityId", "CanonEntities"),
        new("FK_Locations_Canon", "Locations", "EntityId", "CanonEntities"),
        new("FK_Locations_Parent", "Locations", "ParentLocationId", "Locations", "EntityId"),
        new("FK_Characters_Canon", "Characters", "EntityId", "CanonEntities"),
        new("FK_Characters_BirthLocation", "Characters", "BirthLocationId", "Locations", "EntityId"),
        new("FK_Organizations_Canon", "Organizations", "EntityId", "CanonEntities"),
        new("FK_Objects_Canon", "Objects", "EntityId", "CanonEntities"),
        new("FK_WorldEvents_Canon", "WorldEvents", "EntityId", "CanonEntities"),
        new("FK_SourceSnapshots_Source", "SourceSnapshots", "SourceId", "Sources"),
        new("FK_EntityNotes_Entity", "EntityNotes", "EntityId", "CanonEntities"),
        new("FK_ContinuityNotes_Continuity", "ContinuityNotes", "ContinuityId", "Continuities"),
        new("FK_ContinuityNoteSources_Note", "ContinuityNoteSources", "ContinuityNoteId", "ContinuityNotes"),
        new("FK_ContinuityNoteSources_Source", "ContinuityNoteSources", "SourceId", "Sources"),
        new("FK_EntityEvents_Entity", "EntityEvents", "EntityId", "CanonEntities"),
        new("FK_EntityEvents_WorldEvent", "EntityEvents", "WorldEventId", "WorldEvents", "EntityId"),
        new("FK_EntityTags_Entity", "EntityTags", "EntityId", "CanonEntities"),
        new("FK_EntityTags_Tag", "EntityTags", "TagId", "Tags"),
        new("FK_EntitySources_Entity", "EntitySources", "EntityId", "CanonEntities"),
        new("FK_EntitySources_Source", "EntitySources", "SourceId", "Sources"),
        new("FK_NoteSources_Note", "NoteSources", "NoteId", "EntityNotes"),
        new("FK_NoteSources_Source", "NoteSources", "SourceId", "Sources"),
        new("FK_ProjectEntities_Project", "ProjectEntities", "ProjectId", "Projects", "EntityId"),
        new("FK_ProjectEntities_Member", "ProjectEntities", "MemberEntityId", "CanonEntities"),
        new("FK_EntityEventProjects_Event", "EntityEventProjects", "EntityEventId", "EntityEvents"),
        new("FK_EntityEventProjects_Project", "EntityEventProjects", "ProjectId", "Projects", "EntityId"),
        new("FK_TemporalProfiles_Character", "CharacterTemporalProfiles", "CharacterId", "Characters", "EntityId"),
        new("FK_TemporalEffects_Continuity", "CharacterTemporalEffects", "ContinuityId", "Continuities"),
        new("FK_TemporalEffects_Character", "CharacterTemporalEffects", "CharacterId", "Characters", "EntityId"),
        new("FK_TemporalEffects_WorldEvent", "CharacterTemporalEffects", "WorldEventId", "WorldEvents", "EntityId"),
        new("FK_EntityImages_Entity", "EntityImages", "EntityId", "CanonEntities"),
        new("FK_EntityImages_Source", "EntityImages", "SourceId", "Sources"),
        new("FK_ImageRenditions_Image", "ImageRenditions", "ImageId", "EntityImages"),
        new("FK_StoryImages_Continuity", "StoryImages", "ContinuityId", "Continuities"),
        new("FK_StoryImages_Relationship", "StoryImages", "RelationshipId", "CharacterRelationships"),
        new("FK_StoryImages_EntityEvent", "StoryImages", "EntityEventId", "EntityEvents"),
        new("FK_StoryImages_RelationshipEvent", "StoryImages", "RelationshipEventId", "RelationshipEvents"),
        new("FK_StoryImages_Source", "StoryImages", "SourceId", "Sources"),
        new("FK_StoryImageRenditions_Image", "StoryImageRenditions", "ImageId", "StoryImages"),
        new("FK_CharacterAliases_Character", "CharacterAliases", "CharacterId", "Characters", "EntityId"),
        new("FK_Residences_Character", "CharacterResidences", "CharacterId", "Characters", "EntityId"),
        new("FK_Residences_Location", "CharacterResidences", "LocationId", "Locations", "EntityId"),
        new("FK_OrgAliases_Organization", "OrganizationAliases", "OrganizationId", "Organizations", "EntityId"),
        new("FK_Memberships_Organization", "OrganizationMemberships", "OrganizationId", "Organizations", "EntityId"),
        new("FK_Memberships_Character", "OrganizationMemberships", "CharacterId", "Characters", "EntityId"),
        new("FK_OrgLocations_Organization", "OrganizationLocations", "OrganizationId", "Organizations", "EntityId"),
        new("FK_OrgLocations_Location", "OrganizationLocations", "LocationId", "Locations", "EntityId"),
        new("FK_Relationships_Continuity", "CharacterRelationships", "ContinuityId", "Continuities"),
        new("FK_Relationships_Source", "CharacterRelationships", "SourceCharacterId", "Characters", "EntityId"),
        new("FK_Relationships_Target", "CharacterRelationships", "TargetCharacterId", "Characters", "EntityId"),
        new("FK_Relationships_Type", "CharacterRelationships", "RelationshipTypeId", "RelationshipTypes"),
        new("FK_RelParticipants_Relationship", "RelationshipParticipants", "RelationshipId", "CharacterRelationships"),
        new("FK_RelParticipants_Character", "RelationshipParticipants", "CharacterId", "Characters", "EntityId"),
        new("FK_RelPeriods_Participant", "RelationshipMembershipPeriods", "ParticipantId", "RelationshipParticipants"),
        new("FK_RelTransitions_Period", "RelationshipMembershipTransitions", "MembershipPeriodId", "RelationshipMembershipPeriods"),
        new("FK_RelTransitionDescriptions_Transition", "RelationshipTransitionDescriptions", "TransitionId", "RelationshipMembershipTransitions"),
        new("FK_OrgTransitions_Membership", "OrganizationMembershipTransitions", "MembershipId", "OrganizationMemberships"),
        new("FK_RelEvents_Relationship", "RelationshipEvents", "RelationshipId", "CharacterRelationships"),
        new("FK_RelEvents_WorldEvent", "RelationshipEvents", "WorldEventId", "WorldEvents", "EntityId"),
        new("FK_RelEventProjects_Event", "RelationshipEventProjects", "RelationshipEventId", "RelationshipEvents"),
        new("FK_RelEventProjects_Project", "RelationshipEventProjects", "ProjectId", "Projects", "EntityId"),
        new("FK_RelMerge_Source", "RelationshipMergeRedirects", "SourceRelationshipId", "CharacterRelationships"),
        new("FK_RelMerge_Target", "RelationshipMergeRedirects", "TargetRelationshipId", "CharacterRelationships"),
        new("FK_Principals_Continuity", "OwnershipPrincipals", "ContinuityId", "Continuities"),
        new("FK_Principals_Character", "OwnershipPrincipals", "CharacterId", "Characters", "EntityId"),
        new("FK_Principals_Organization", "OwnershipPrincipals", "OrganizationId", "Organizations", "EntityId"),
        new("FK_OwnershipPeriods_Object", "ObjectOwnershipPeriods", "ObjectId", "Objects", "EntityId"),
        new("FK_OwnershipOwners_Period", "ObjectOwnershipOwners", "OwnershipPeriodId", "ObjectOwnershipPeriods"),
        new("FK_OwnershipOwners_Principal", "ObjectOwnershipOwners", "PrincipalId", "OwnershipPrincipals"),
        new("FK_CustodyPeriods_Object", "ObjectCustodyPeriods", "ObjectId", "Objects", "EntityId"),
        new("FK_CustodyPeriods_Principal", "ObjectCustodyPeriods", "PrincipalId", "OwnershipPrincipals"),
        new("FK_ObjectLocations_Object", "ObjectLocationPeriods", "ObjectId", "Objects", "EntityId"),
        new("FK_ObjectLocations_Location", "ObjectLocationPeriods", "LocationId", "Locations", "EntityId"),
        new("FK_EventParticipants_Event", "WorldEventParticipants", "WorldEventId", "WorldEvents", "EntityId"),
        new("FK_EventParticipants_Entity", "WorldEventParticipants", "ParticipantEntityId", "CanonEntities"),
        new("FK_EventLocations_Event", "WorldEventLocations", "WorldEventId", "WorldEvents", "EntityId"),
        new("FK_EventLocations_Location", "WorldEventLocations", "LocationId", "Locations", "EntityId"),
        new("FK_Claims_Continuity", "Claims", "ContinuityId", "Continuities"),
        new("FK_ClaimSources_Claim", "ClaimSources", "ClaimId", "Claims"),
        new("FK_ClaimSources_Source", "ClaimSources", "SourceId", "Sources"),
        new("FK_ClaimSources_Snapshot", "ClaimSources", "SourceSnapshotId", "SourceSnapshots"),
        new("FK_ClaimEntities_Claim", "ClaimEntities", "ClaimId", "Claims"),
        new("FK_ClaimEntities_Entity", "ClaimEntities", "EntityId", "CanonEntities"),
        new("FK_ClaimRelationships_Claim", "ClaimRelationships", "ClaimId", "Claims"),
        new("FK_ClaimRelationships_Rel", "ClaimRelationships", "RelationshipId", "CharacterRelationships"),
        new("FK_ClaimNotes_Claim", "ClaimNotes", "ClaimId", "Claims"),
        new("FK_ClaimNotes_Note", "ClaimNotes", "NoteId", "EntityNotes"),
        new("FK_SourceTags_Source", "SourceTags", "SourceId", "Sources"),
        new("FK_SourceTags_Tag", "SourceTags", "TagId", "Tags"),
        new("FK_ChangeLog_Operation", "ChangeLog", "OperationId", "ProcessedOperations", "OperationId")
    ];

    private static readonly (string Table, string Prefix)[] StoryDateColumns =
    [
        ("Characters", "Birth"), ("Characters", "Death"),
        ("WorldEvents", "Event"), ("EntityEvents", "Event"),
        ("CharacterResidences", "Period"), ("OrganizationMemberships", "Period"),
        ("OrganizationLocations", "Period"), ("CharacterRelationships", "Period"),
        ("RelationshipMembershipPeriods", "Period"), ("RelationshipEvents", "Event"),
        ("RelationshipMembershipTransitions", "Occurred"),
        ("OrganizationMembershipTransitions", "Occurred"),
        ("ObjectOwnershipPeriods", "Period"), ("ObjectCustodyPeriods", "Period"),
        ("ObjectLocationPeriods", "Period"), ("CharacterTemporalEffects", "Period")
    ];

    internal static IReadOnlyList<(string Table, string Prefix)> StoryDateFields => StoryDateColumns;

    private static IEnumerable<AccessCheckConstraintDefinition> StoryDateShapeChecks((string Table, string Prefix) item)
    {
        var (table, prefix) = item;
        yield return new($"CK_{table}_{prefix}_Kind", table,
            $"[{prefix}Kind] IN ('Unknown','ExactInstant','ExactDate','Month','Year','Circa','Before','After','Range','KnownRange','UncertainRange')");
        yield return new($"CK_{table}_{prefix}_Unknown", table,
            $"[{prefix}Kind]<>'Unknown' OR ([{prefix}LowerBound] IS NULL AND [{prefix}UpperBound] IS NULL)");
        yield return new($"CK_{table}_{prefix}_Instant", table,
            $"[{prefix}Kind]<>'ExactInstant' OR ([{prefix}LowerBound] IS NOT NULL AND [{prefix}UpperBound]=[{prefix}LowerBound] AND [{prefix}LowerInclusive]=True AND [{prefix}UpperInclusive]=True)");
        yield return new($"CK_{table}_{prefix}_Bounded", table,
            $"[{prefix}Kind] NOT IN ('ExactDate','Month','Year','Circa','Range','KnownRange','UncertainRange') OR ([{prefix}LowerBound] IS NOT NULL AND [{prefix}UpperBound] IS NOT NULL AND [{prefix}LowerBound]<[{prefix}UpperBound])");
        yield return new($"CK_{table}_{prefix}_Before", table,
            $"[{prefix}Kind]<>'Before' OR ([{prefix}LowerBound] IS NULL AND [{prefix}UpperBound] IS NOT NULL)");
        yield return new($"CK_{table}_{prefix}_After", table,
            $"[{prefix}Kind]<>'After' OR ([{prefix}LowerBound] IS NOT NULL AND [{prefix}UpperBound] IS NULL)");
    }

    public static IReadOnlyList<AccessCheckConstraintDefinition> CheckConstraints { get; } =
        Tables.SelectMany(table => table.Columns
                .Where(column => column.Required &&
                                 column.SqlType.StartsWith("TEXT", StringComparison.OrdinalIgnoreCase))
                .Select(column => new AccessCheckConstraintDefinition(
                    $"CK_{table.Name}_{column.Name}_NotBlank",
                    table.Name,
                    $"Len(Trim([{column.Name}])) > 0")))
            .Concat(StoryDateColumns.Select(item => new AccessCheckConstraintDefinition(
                $"CK_{item.Table}_{item.Prefix}_Bounds",
                item.Table,
                $"[{item.Prefix}LowerBound] IS NULL OR [{item.Prefix}UpperBound] IS NULL OR " +
                $"[{item.Prefix}LowerBound] <= [{item.Prefix}UpperBound]")))
            .Concat(StoryDateColumns.SelectMany(StoryDateShapeChecks))
            .Concat(Tables.Where(table => table.Columns.Any(column => column.Name == "IsDeleted"))
                .Select(table => new AccessCheckConstraintDefinition(
                    $"CK_{table.Name}_DeletionState",
                    table.Name,
                    "([IsDeleted]=False AND [DeletedAtUtc] IS NULL AND [DeletedOperationId] IS NULL) OR " +
                    "([IsDeleted]=True AND [DeletedAtUtc] IS NOT NULL AND [DeletedOperationId] IS NOT NULL)")))
            .Concat(Tables.Where(table => table.Columns.Any(column => column.Name == "Version"))
                .Select(table => new AccessCheckConstraintDefinition(
                    $"CK_{table.Name}_VersionPositive",
                    table.Name,
                    "[Version] >= 1")))
            .Concat(
            [
                new("CK_Locations_NoSelfParent", "Locations", "[ParentLocationId] IS NULL OR [ParentLocationId] <> [EntityId]"),
                new("CK_VariantGroups_EntityType", "VariantGroups", "[EntityType] IN ('Project','Location','Character','Organization','Object','WorldEvent')"),
                new("CK_CanonEntities_EntityType", "CanonEntities", "[EntityType] IN ('Project','Location','Character','Organization','Object','WorldEvent')"),
                new("CK_CharacterRelationships_NoSelf", "CharacterRelationships", "[SourceCharacterId] <> [TargetCharacterId]"),
                new("CK_RelMerge_NoSelf", "RelationshipMergeRedirects", "[SourceRelationshipId] <> [TargetRelationshipId]"),
                new("CK_RelTransitions_Kind", "RelationshipMembershipTransitions", "[TransitionKind] IN ('Join','Leave')"),
                new("CK_OrgTransitions_Kind", "OrganizationMembershipTransitions", "[TransitionKind] IN ('Join','Leave')"),
                new("CK_RelationshipTypes_Direction", "RelationshipTypes", "([IsDirected]=True AND [InverseName] IS NOT NULL AND Len(Trim([InverseName]))>0) OR ([IsDirected]=False AND [InverseName] IS NULL)"),
                new("CK_OwnershipPrincipals_Kind", "OwnershipPrincipals", "[PrincipalKind] IN ('Character','Organization','External')"),
                new("CK_OwnershipPrincipals_Character", "OwnershipPrincipals", "[PrincipalKind]<>'Character' OR ([CharacterId] IS NOT NULL AND [OrganizationId] IS NULL AND [Label] IS NULL)"),
                new("CK_OwnershipPrincipals_Organization", "OwnershipPrincipals", "[PrincipalKind]<>'Organization' OR ([CharacterId] IS NULL AND [OrganizationId] IS NOT NULL AND [Label] IS NULL)"),
                new("CK_OwnershipPrincipals_External", "OwnershipPrincipals", "[PrincipalKind]<>'External' OR ([CharacterId] IS NULL AND [OrganizationId] IS NULL AND [Label] IS NOT NULL AND Len(Trim([Label]))>0)"),
                new("CK_OwnershipPeriods_State", "ObjectOwnershipPeriods", "[OwnerState] IN ('Owned','Unknown','Unowned')"),
                new("CK_OwnershipOwner_ShareRange", "ObjectOwnershipOwners", "[SharePartsPerMillion] IS NULL OR ([SharePartsPerMillion] >= 0 AND [SharePartsPerMillion] <= 1000000)"),
                new("CK_CustodyPeriods_State", "ObjectCustodyPeriods",
                    "([CustodianState]='Known' AND [PrincipalId] IS NOT NULL) OR " +
                    "([CustodianState] IN ('Unknown','Unowned') AND [PrincipalId] IS NULL)"),
                new("CK_Claims_ConfidenceRange", "Claims", "[Confidence] IS NULL OR ([Confidence] >= 0 AND [Confidence] <= 1)"),
                new("CK_TemporalProfiles_LegalAgePolicy", "CharacterTemporalProfiles", "[LegalAgePolicy] IN ('CalendarAge')"),
                new("CK_TemporalEffects_Rates", "CharacterTemporalEffects", "[BiologicalRate] >= 0 AND [ExperiencedRate] >= 0"),
                new("CK_EntityImages_Dimensions", "EntityImages", "[OriginalWidth] > 0 AND [OriginalHeight] > 0 AND [OriginalBytes] > 0 AND [OriginalBytes] <= 20971520"),
                new("CK_StoryImages_OwnerKind", "StoryImages",
                    "[OwnerKind] IN ('Continuity','Relationship','EntityEvent','RelationshipEvent')"),
                new("CK_StoryImages_ContinuityOwner", "StoryImages",
                    "[OwnerKind]<>'Continuity' OR ([RelationshipId] IS NULL AND [EntityEventId] IS NULL AND [RelationshipEventId] IS NULL)"),
                new("CK_StoryImages_RelationshipOwner", "StoryImages",
                    "[OwnerKind]<>'Relationship' OR ([RelationshipId] IS NOT NULL AND [EntityEventId] IS NULL AND [RelationshipEventId] IS NULL)"),
                new("CK_StoryImages_EntityEventOwner", "StoryImages",
                    "[OwnerKind]<>'EntityEvent' OR ([RelationshipId] IS NULL AND [EntityEventId] IS NOT NULL AND [RelationshipEventId] IS NULL)"),
                new("CK_StoryImages_RelationshipEventOwner", "StoryImages",
                    "[OwnerKind]<>'RelationshipEvent' OR ([RelationshipId] IS NULL AND [EntityEventId] IS NULL AND [RelationshipEventId] IS NOT NULL)"),
                new("CK_StoryImages_Dimensions", "StoryImages", "[OriginalWidth] > 0 AND [OriginalHeight] > 0 AND [OriginalBytes] > 0 AND [OriginalBytes] <= 20971520"),
                new("CK_ImageRenditions_Kind", "ImageRenditions", "[RenditionKind] IN ('Thumbnail','Display')"),
                new("CK_ImageRenditions_Dimensions", "ImageRenditions", "[Width] > 0 AND [Height] > 0 AND [ByteSize] > 0"),
                new("CK_ImageRenditions_Ceilings", "ImageRenditions",
                    "([RenditionKind]='Thumbnail' AND [Width] <= 512 AND [Height] <= 512 AND [ByteSize] <= 524288) OR " +
                    "([RenditionKind]='Display' AND [Width] <= 2048 AND [Height] <= 2048 AND [ByteSize] <= 5242880)"),
                new("CK_StoryImageRenditions_Kind", "StoryImageRenditions", "[RenditionKind] IN ('Thumbnail','Display')"),
                new("CK_StoryImageRenditions_Dimensions", "StoryImageRenditions", "[Width] > 0 AND [Height] > 0 AND [ByteSize] > 0"),
                new("CK_StoryImageRenditions_Ceilings", "StoryImageRenditions",
                    "([RenditionKind]='Thumbnail' AND [Width] <= 512 AND [Height] <= 512 AND [ByteSize] <= 524288) OR " +
                    "([RenditionKind]='Display' AND [Width] <= 2048 AND [Height] <= 2048 AND [ByteSize] <= 5242880)"),
                new("CK_ImageContentVersions_Kind", "ImageContentVersions",
                    "[ImageKind] IN ('EntityImage','StoryImage')"),
                new("CK_ImageContentVersions_Revision", "ImageContentVersions",
                    "[CurrentRevision] >= 1"),
                new("CK_ImageHistoricalContent_Kind", "ImageHistoricalContent",
                    "[ImageKind] IN ('EntityImage','StoryImage')"),
                new("CK_ImageHistoricalContent_Revision", "ImageHistoricalContent",
                    "[ContentRevision] >= 1"),
                new("CK_ImageHistoricalContent_OriginalSize", "ImageHistoricalContent",
                    "[OriginalWidth] > 0 AND [OriginalHeight] > 0 AND [OriginalBytes] > 0 AND [OriginalBytes] <= 20971520"),
                new("CK_ImageHistoricalContent_DisplaySize", "ImageHistoricalContent",
                    "[DisplayWidth] > 0 AND [DisplayHeight] > 0 AND [DisplayBytes] > 0 AND [DisplayBytes] <= 5242880"),
                new("CK_ImageHistoricalContent_ThumbnailSize", "ImageHistoricalContent",
                    "[ThumbnailWidth] > 0 AND [ThumbnailHeight] > 0 AND [ThumbnailBytes] > 0 AND [ThumbnailBytes] <= 524288"),
                new("CK_RecordPageSnapshots_Version", "RecordPageSnapshots", "[SnapshotVersion] >= 1"),
                new("CK_RecordPageSnapshots_Bytes", "RecordPageSnapshots",
                    "[PageBytes] > 0 AND [PageBytes] <= 1048576")
            ])
            .ToArray();

    public static string Checksum { get; } = ComputeChecksum();

    private static string ComputeChecksum()
    {
        var definition = string.Join("\n", Tables.Select(table => table.CreateSql())
            .Concat(Indexes.Select(index => index.CreateSql()))
            .Concat(ForeignKeys.Select(key => key.CreateSql()))
            .Concat(CheckConstraints.Select(check => check.CreateSql())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
    }
}
