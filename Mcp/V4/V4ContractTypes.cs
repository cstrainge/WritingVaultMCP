using System.Text.Json;

namespace WritingVaultMcp.Mcp.V4;

public static class V4ContractLimits
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;
    public const int DefaultRelationSectionSize = 20;
    public const int MaximumRelationSectionSize = 100;
    public const int DefaultTimelinePageSize = 200;
    public const int MaximumTimelinePageSize = 500;
    public const int MaximumIncludeSections = 20;
    public const int MaximumAmbiguityCandidates = 10;
    public const int MaximumMutationTokenLength = 100;
    public const int MaximumCursorLength = 512;
    public const int MaximumSearchTextLength = 500;
    public const int MaximumNameLength = 255;
    public const int MaximumShortTextLength = 4_000;
    public const int MaximumLongTextLength = 65_536;
    public const int MaximumSnapshotUtf8Bytes = 1_000_000;
    public const int DefaultSnapshotTextPageCharacters = 8_192;
    public const int MaximumSnapshotTextPageCharacters = 16_384;
    public const int MaximumImageInputBytes = 20 * 1024 * 1024;
    public const int OriginalImageMaximumBytes = MaximumImageInputBytes;
    public const int MaximumChangeWaitSeconds = 30;
    public const int DisplayImageMaximumPixels = 2_048;
    public const int DisplayImageMaximumBytes = 5 * 1024 * 1024;
    public const int ThumbnailMaximumPixels = 512;
    public const int ThumbnailMaximumBytes = 512 * 1024;
}

public enum V4DeletionState { Active, Deleted, All }
public enum V4SessionTimeAction { Keep, Set, Clear }
public enum V4TimelineMode { Calendar, Narrative }
public enum V4TimelineResolution { Auto, Aggregate, Detail }
public enum V4ImageSize { Thumbnail, Display, Original }
public enum V4TagAction { Add, Remove }
public enum V4CanonEntityKind { Project, Location, Character, Organization, Object, WorldEvent }
public enum V4RecordKind
{
    Continuity, Project, Location, Character, Organization, Object, WorldEvent,
    Source, SourceSnapshot, Claim, Tag, Note, Image, VariantGroup,
    Relationship, RelationshipType, Residence, Membership, OrganizationLocation,
    Ownership, OwnershipPrincipal, Custody, ObjectLocation, EntityEvent, TemporalEffect,
    RelationshipEvent, RelationshipMembershipPeriod, RelationshipParticipant
}

public enum V4TimelineLane
{
    WorldEvents, EntityEvents, Characters, Relationships, Residences,
    Memberships, OrganizationLocations, Ownership, Custody, ObjectLocations, TemporalEffects,
    RelationshipEvents
}

public enum V4StoryDateKind
{
    Unknown, ExactInstant, ExactDate, Month, Year, Circa, Range, KnownRange, UncertainRange, Before, After
}

public sealed record V4EmptyRequest;

public sealed record V4ReferenceSummary(
    string Ref, V4RecordKind Kind, string Label, string? Context = null,
    string? ContinuityName = null, int? Version = null, bool IsDeleted = false,
    string? AssociationRole = null, string? AssociationNotes = null);

public sealed record V4Page<T>(
    IReadOnlyList<T> Items, string? NextCursor, bool HasMore, string ObservedRevision);

public sealed record V4Section<T>(
    IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

public sealed record V4ErrorDetail(string? Field = null, string? Reason = null, JsonElement? RejectedValue = null);

public sealed record V4Error(
    string Code, string Message, bool Retryable = false,
    IReadOnlyList<V4ErrorDetail>? Details = null,
    IReadOnlyList<V4ReferenceSummary>? Candidates = null,
    string? Recovery = null);

public sealed record V4ClockView(
    string Status, DateTimeOffset? CurrentTime, string? ReferenceTimeZoneId,
    string Source, string? LocalDisplay = null, DateOnly? CurrentDate = null);

public sealed record V4SessionView(
    string? ContinuityName, V4ClockView Clock, string SurfaceVersion = "4.0");

[System.Text.Json.Serialization.JsonConverter(typeof(V4StoryDateInputJsonConverter))]
public sealed record V4StoryDateInput(
    V4StoryDateKind? Kind = null, string? Value = null, string? Lower = null, string? Upper = null,
    bool? LowerInclusive = null, bool? UpperInclusive = null,
    string? OriginalText = null, string? CalendarId = "Gregorian");

public sealed record V4StoryDateView(
    V4StoryDateKind Kind, string Display, string? Lower, string? Upper,
    bool LowerInclusive, bool UpperInclusive, string CalendarId,
    string? OriginalText = null);

public sealed record V4ContinuitySummary(
    string Name, string? Description, string DefaultTimeZoneId, V4ClockView Clock,
    int Version, bool IsDeleted, string ObservedRevision);

public sealed record V4RecordOverview(
    V4ReferenceSummary Summary,
    IReadOnlyDictionary<string, JsonElement> Fields,
    IReadOnlyDictionary<string, V4Section<V4ReferenceSummary>> Sections,
    string ObservedRevision);

public sealed record V4RecordSnapshotGetRequest(string Ref, int SnapshotVersion);

public sealed record V4RecordSnapshotListRequest(string Ref, int? BeforeVersion = null,
    int Limit = 50);

public sealed record V4RecordSnapshotSummary(int SnapshotVersion, DateTime SavedAtUtc,
    bool IsBaseline);

public sealed record V4RecordSnapshotListResult(
    IReadOnlyList<V4RecordSnapshotSummary> Items, int? NextBeforeVersion,
    bool HasMore);

public sealed record V4RecordSnapshotResult(
    V4RecordOverview Overview,
    IReadOnlyDictionary<string, V4RecordOverview> Notes,
    int SnapshotVersion,
    DateTime SavedAtUtc,
    bool IsBaseline,
    string LatestRef);

public sealed record V4SourceSnapshotViewResult(
    string SnapshotRef, string Text, string? NextCursor, bool HasMore,
    string? MediaType, string? RetrievedAtUtc, int ByteSize, string ObservedRevision);

public sealed record V4HistoryEntry(
    string ChangedAtUtc, string ClientLabel, string Action, string RecordKind,
    string? RecordRef, int? VersionBefore, int? VersionAfter,
    IReadOnlyDictionary<string, JsonElement>? Changes = null);

public sealed record V4TimelineItem(
    string Ref, string Kind, string Title, string? Summary, V4TimelineLane Lane,
    V4StoryDateView Occurred, IReadOnlyList<V4ReferenceSummary> Related,
    double? NarrativeOrder = null, bool IsDeleted = false,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<V4ReferenceSummary>? Projects = null,
    bool ContainsHighlight = false,
    string? Boundary = null, V4StoryDateView? BoundaryDate = null,
    bool HasCustomDescription = false, bool IsMembershipTransition = false);

public sealed record V4TimelineResult(
    IReadOnlyList<V4TimelineItem> Items, string? NextCursor, bool HasMore,
    IReadOnlyList<V4ReferenceSummary> Undated, V4ClockView Clock,
    string ObservedRevision, bool Aggregated = false);

public sealed record V4MutationResult(
    bool Success, string Code, IReadOnlyList<V4ReferenceSummary> Affected,
    bool Replayed, string? Message = null, V4Error? Error = null,
    V4BackupSummary? Backup = null);

public sealed record V4BackupSummary(
    string CreatedAtUtc, long DatabaseBytes, string DatabaseSha256,
    string SchemaMigration, bool SchemaValid, bool IntegrityValid,
    int RetentionCount, int AssetCount, long AssetBytes);

public sealed record V4DeletePreview(
    V4ReferenceSummary Target, bool CanDelete,
    IReadOnlyList<V4ReferenceSummary> Blockers, string ObservedRevision);

public sealed record V4AgeMeasure(
    string Status, double? ExactYears, double? MinimumYears, double? MaximumYears,
    string? Display = null);

public sealed record V4CharacterAgeResult(
    V4ReferenceSummary Character, string Status, string? AsOf,
    string? ReferenceTimeZoneId, V4AgeMeasure Calendar, V4AgeMeasure Legal,
    V4AgeMeasure Biological, V4AgeMeasure Experienced,
    IReadOnlyList<V4ReferenceSummary> AppliedEffects,
    IReadOnlyList<string> Warnings, string ObservedRevision, DateOnly? AsOfDate = null);

public sealed record V4TemporalPreviewResult(
    bool Valid, IReadOnlyList<V4Error> Conflicts,
    V4CharacterAgeResult? ProjectedAge, string ObservedRevision);

public sealed record V4LocalTimeResult(
    V4ReferenceSummary Target, string Status, string? LocalDateTime,
    string? TimeZoneId, string? TimeZoneSource, string ObservedRevision);

public sealed record V4ImageMetadata(
    string Ref, V4ReferenceSummary Owner, string? Title, string? Caption,
    string? AltText, string? Role, string? CanonStatus, bool IsPrimary,
    string MediaType, int Width, int Height, long ByteCount,
    int Version, bool IsDeleted, string ObservedRevision,
    V4ReferenceSummary? Source = null, DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record V4ImageViewResult(
    V4ImageMetadata Image, V4ImageSize Size, string MediaType,
    int Width, int Height, long ByteCount, string ObservedRevision,
    int ContentRevision = 1, bool IsCurrentContent = true);

public sealed record V4ImageContentRevision(
    int Revision, bool IsCurrent, string MediaType,
    int Width, int Height, long ByteCount, DateTimeOffset? ArchivedAtUtc);

public sealed record V4ImageRevisionHistoryResult(
    string ImageRef, int CurrentRevision,
    IReadOnlyList<V4ImageContentRevision> Revisions, bool HasMore,
    int? NextBeforeRevision,
    string ObservedRevision);

public sealed record V4HealthResult(
    bool Ready, string ToolSurfaceVersion, string SchemaMigration,
    int PendingWrites, string DatabaseCapacityBand, long ImageRenditionBytes,
    string? LastSuccessfulBackupUtc, IReadOnlyList<V4Error> Issues,
    string ObservedRevision);

public sealed record V4Change(
    string Scope, string Kind, string? Ref, string Action,
    bool TimelineAffected, bool FullVisibleRefreshRequired = false);

public sealed record V4ChangesResult(
    string Cursor, IReadOnlyList<V4Change> Changes, bool Heartbeat,
    bool CursorExpired, string ObservedRevision);

public sealed record V4ContinuityListRequest(
    V4DeletionState DeletionState = V4DeletionState.Active,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize);

public sealed record V4SessionSetRequest(
    string? ContinuityName = null,
    V4SessionTimeAction TimeAction = V4SessionTimeAction.Keep,
    DateTimeOffset? CurrentTime = null,
    string? ReferenceTimeZoneId = null,
    DateOnly? CurrentDate = null);

public sealed record V4SearchRequest(
    string? Text = null, IReadOnlyList<V4RecordKind>? Kinds = null,
    IReadOnlyList<string>? Tags = null, string? Project = null,
    bool IncludeContent = false, V4DeletionState DeletionState = V4DeletionState.Active,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize);

public sealed record V4GetRequest(
    string? Ref = null, IReadOnlyList<string>? Include = null,
    IReadOnlyDictionary<string, int>? Limits = null,
    bool IncludeDeleted = false);

public sealed record V4SourceSnapshotViewRequest(
    string SnapshotRef, string? Cursor = null,
    int Limit = V4ContractLimits.DefaultSnapshotTextPageCharacters,
    bool IncludeDeleted = false);

public sealed record V4ListRelatedRequest(
    string? Ref, string Relation, V4DeletionState DeletionState = V4DeletionState.Active,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize);

public sealed record V4TimelineRequest(
    V4StoryDateInput? From = null, V4StoryDateInput? To = null,
    IReadOnlyList<V4TimelineLane>? Lanes = null,
    IReadOnlyList<string>? FocusRefs = null, IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Projects = null, IReadOnlyList<string>? Locations = null,
    string? Text = null, bool IncludeUndated = true,
    V4TimelineMode Mode = V4TimelineMode.Calendar,
    V4TimelineResolution Resolution = V4TimelineResolution.Auto,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultTimelinePageSize,
    IReadOnlyList<V4RecordKind>? Kinds = null,
    IReadOnlyList<V4RecordKind>? EntityEventKinds = null,
    bool UndatedOnly = false,
    string? HighlightRef = null,
    bool ExpandRanges = false);

public sealed record V4TagTargetsRequest(
    string Tag, IReadOnlyList<V4RecordKind>? Kinds = null,
    V4DeletionState DeletionState = V4DeletionState.Active,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize);

public sealed record V4HistoryRequest(
    string? Ref = null, string? MutationToken = null,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize);

public sealed record V4ChangesSinceRequest(string? Cursor = null, int WaitSeconds = 0);

public sealed record V4AsOfInput(DateTimeOffset CurrentTime, string ReferenceTimeZoneId);
public sealed record V4CharacterAgeRequest(string Character, V4AsOfInput? At = null);

public sealed record V4TemporalEffectPreviewRequest(
    string Character, V4StoryDateInput Period, double BiologicalRate,
    double ExperiencedRate, string? Name = null, string? CauseWorldEvent = null,
    string? ExcludeEffectRef = null, V4AsOfInput? At = null);

public sealed record V4EntityLocalTimeRequest(string Ref, V4AsOfInput? At = null);
public sealed record V4RecordRefRequest(string Ref, bool IncludeDeleted = false);

public sealed record V4ImageListRequest(
    string Target, string? Role = null, string? CanonStatus = null,
    V4DeletionState DeletionState = V4DeletionState.Active,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize);

public sealed record V4ImageSearchRequest(
    string? Text = null, IReadOnlyList<string>? Entities = null,
    IReadOnlyList<V4CanonEntityKind>? EntityKinds = null,
    IReadOnlyList<string>? Roles = null, IReadOnlyList<string>? CanonStatuses = null,
    V4DeletionState DeletionState = V4DeletionState.Active,
    string? Cursor = null, int Limit = V4ContractLimits.DefaultPageSize,
    bool AcrossContinuities = false);

public sealed record V4ImageViewRequest(string ImageRef, V4ImageSize Size = V4ImageSize.Display,
    int? Revision = null);
public sealed record V4ImageRevisionHistoryRequest(string ImageRef,
    int? BeforeRevision = null, int Limit = 50);

public sealed record V4EntityFields(
    string? Description = null, string? SecondaryType = null, string? TimeZoneId = null,
    V4StoryDateInput? Birth = null, V4StoryDateInput? Death = null,
    V4StoryDateInput? Occurred = null, string? BirthLocation = null,
    string? BirthLocationDetail = null, double? NarrativeOrder = null,
    string? MiddleNames = null, string? FamilyName = null, string? PreferredName = null,
    string? Gender = null, string? Pronouns = null, string? Species = null,
    string? Occupation = null, string? Nationality = null,
    string? PhysicalDescription = null, string? PersonalitySummary = null);

public sealed record V4ContinuityCreateRequest(
    string MutationToken, string Name, string DefaultTimeZoneId, string? Description = null);
public sealed record V4BackupCreateRequest(
    string MutationToken, string? Purpose = null);
public sealed record V4ContinuityUpdateRequest(
    string MutationToken, int ExpectedVersion, IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4ContinuityClockSetRequest(
    string MutationToken, int ExpectedVersion, DateTimeOffset? CurrentTime,
    string? ReferenceTimeZoneId);
public sealed record V4VariantGroupCreateRequest(
    string MutationToken, V4CanonEntityKind EntityKind, string? Name = null, string? Notes = null);
public sealed record V4VariantGroupUpdateRequest(
    string MutationToken, string VariantGroupRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4EntityVariantGroupSetRequest(
    string MutationToken, string EntityRef, int ExpectedVersion, string? VariantGroupRef);
public sealed record V4EntityCreateRequest(
    string MutationToken, V4CanonEntityKind EntityKind, string Name,
    V4EntityFields? Fields = null, string? VariantGroupRef = null);
public sealed record V4EntityUpdateRequest(
    string MutationToken, string Ref, int ExpectedVersion, IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4EntityDuplicateRequest(
    string MutationToken, string SourceRef, string TargetContinuityName, string? Name = null);
public sealed record V4TagCreateRequest(string MutationToken, string Name, string? Description = null);
public sealed record V4TagUpdateRequest(
    string MutationToken, string TagRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4TagApplyRequest(
    string MutationToken, string Tag, IReadOnlyList<string> Targets,
    V4TagAction Action, bool CreateIfMissing = false);
public sealed record V4SourceCreateRequest(
    string MutationToken, string Title, string? CanonicalUrl = null,
    string? ArchiveUrl = null, string? SourceType = null,
    string? AuthorPublisher = null, string? Citation = null, string? Notes = null);
public sealed record V4SourceUpdateRequest(
    string MutationToken, string SourceRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4SourceSnapshotAddRequest(
    string MutationToken, string SourceRef, string Content,
    string MediaType = "text/plain", DateTime? RetrievedAtUtc = null);
public sealed record V4ClaimEvidenceInput(
    string Source, string? SnapshotRef = null, string Relation = "Supports",
    string? Locator = null, string? EvidenceExcerpt = null, string? Summary = null);
public sealed record V4ClaimCreateRequest(
    string MutationToken, string ClaimText, IReadOnlyList<string> Targets,
    IReadOnlyList<V4ClaimEvidenceInput> Evidence, string Status = "Active",
    double? Confidence = null, string? TargetField = null, string? Commentary = null);
public sealed record V4ClaimUpdateRequest(
    string MutationToken, string ClaimRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4NoteAddRequest(
    string MutationToken, string Target, string Body, string? Title = null,
    string Format = "markdown");
public sealed record V4NoteUpdateRequest(
    string MutationToken, string NoteRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4EventParticipantInput(
    string Entity, string? Role = null, string? Impact = null,
    string? Outcome = null, string? Notes = null);
public sealed record V4EventLocationInput(
    string Location, bool IsPrimary = false, string? Role = null, string? Notes = null);
public sealed record V4EventRecordRequest(
    string MutationToken, string Title, V4StoryDateInput Occurred,
    string? Description = null, double? NarrativeOrder = null,
    IReadOnlyList<V4EventParticipantInput>? Participants = null,
    IReadOnlyList<V4EventLocationInput>? Locations = null,
    IReadOnlyList<string>? Projects = null);
public sealed record V4EntityEventAddRequest(
    string MutationToken, string Entity, string Title, V4StoryDateInput Occurred,
    string? WorldEvent = null, string? Description = null, double? NarrativeOrder = null,
    IReadOnlyList<string>? Projects = null);
public sealed record V4RelationshipEventAddRequest(
    string MutationToken, string RelationshipRef, string Title, V4StoryDateInput Occurred,
    string? WorldEvent = null, string? Description = null, double? NarrativeOrder = null,
    IReadOnlyList<string>? Projects = null);
public sealed record V4EventProjectApplyRequest(
    string MutationToken, string Event, IReadOnlyList<string> Projects,
    V4TagAction Action, string? Role = null, string? Notes = null);
public sealed record V4AliasAddRequest(
    string MutationToken, string Entity, string Alias, string? Notes = null);
public sealed record V4NoteSourceLinkRequest(
    string MutationToken, string Note, string Source,
    string? Locator = null, string? Notes = null);
public sealed record V4EntitySourceLinkRequest(string MutationToken, string Entity, string Source);
public sealed record V4ProjectEntityLinkRequest(
    string MutationToken, string Project, string Entity,
    string? Role = null, string? Notes = null);
public sealed record V4LocationMoveRequest(
    string MutationToken, string Location, int ExpectedVersion, string? ParentLocation);
public sealed record V4ResidenceAddRequest(
    string MutationToken, string Character, string Location,
    V4StoryDateInput Period, bool IsPrimary = true, string? Notes = null);
public sealed record V4ResidenceTransitionRequest(
    string MutationToken, string ResidenceRef, int ExpectedVersion,
    string NewLocation, string EffectiveAt, bool IsPrimary = true, string? Notes = null);
public sealed record V4MembershipAddRequest(
    string MutationToken, string Organization, string Character,
    V4StoryDateInput? Period = null, string? Role = null, string? Notes = null,
    V4StoryDateInput? Joined = null, V4StoryDateInput? Left = null,
    string? JoinDescription = null, string? LeaveDescription = null);
public sealed record V4MembershipTransitionRequest(
    string MutationToken, string MembershipRef, int ExpectedVersion,
    string EffectiveAt, bool CreateReplacement, string? NewRole = null, string? Notes = null,
    string? LeaveDescription = null, string? ReplacementJoinDescription = null);
public sealed record V4OrganizationMembershipTransitionsSetRequest(
    string MutationToken, string MembershipRef, int ExpectedVersion,
    V4StoryDateInput Joined, V4StoryDateInput Left,
    string? JoinDescription = null, string? LeaveDescription = null,
    bool ClearJoinDescription = false, bool ClearLeaveDescription = false);
public sealed record V4OrganizationLocationAddRequest(
    string MutationToken, string Organization, string Location,
    V4StoryDateInput Period, bool IsPrimary = false,
    string? LocationRole = null, string? Notes = null);
public sealed record V4RelationshipTypeCreateRequest(
    string MutationToken, string Name, bool IsDirected, string? InverseName = null,
    bool AllowsOverlappingPeriods = false, string? Description = null);
public sealed record V4CharacterRelationshipCreateRequest(
    string MutationToken, string SourceCharacter, string TargetCharacter,
    string RelationshipType, V4StoryDateInput Period, string? Notes = null);
public sealed record V4RelationshipCreateRequest(
    string MutationToken, IReadOnlyList<string> Characters,
    string RelationshipType, V4StoryDateInput? InitialPeriod = null, string? Notes = null);
public sealed record V4RelationshipParticipantAddRequest(
    string MutationToken, string RelationshipRef, string Character,
    int ExpectedVersion, V4StoryDateInput? InitialPeriod = null);
public sealed record V4RelationshipMembershipPeriodAddRequest(
    string MutationToken, string RelationshipRef, string Character,
    int ExpectedVersion, V4StoryDateInput? Period = null, string? Notes = null,
    V4StoryDateInput? Joined = null, V4StoryDateInput? Left = null,
    string? JoinDescription = null, string? LeaveDescription = null);
public sealed record V4RelationshipMembershipPeriodUpdateRequest(
    string MutationToken, string PeriodRef, int ExpectedVersion,
    V4StoryDateInput Period, string? Notes = null, bool ClearNotes = false);
public sealed record V4RelationshipMembershipTransitionsSetRequest(
    string MutationToken, string PeriodRef, int ExpectedVersion,
    V4StoryDateInput Joined, V4StoryDateInput Left,
    string? Notes = null, bool ClearNotes = false,
    string? JoinDescription = null, string? LeaveDescription = null,
    bool ClearJoinDescription = false, bool ClearLeaveDescription = false);
public sealed record V4RelationshipNotesSetRequest(
    string MutationToken, string RelationshipRef, int ExpectedVersion, string? Notes);
public sealed record V4RelationshipMergePreviewRequest(string SourceRef, string TargetRef);
public sealed record V4RelationshipMergeApplyRequest(
    string MutationToken, string SourceRef, string TargetRef, string ReviewToken);
public sealed record V4RelationshipMergePeriod(
    string Ref, string CharacterRef, V4StoryDateView Period, string? Notes, bool IsDeleted,
    V4StoryDateView? Joined = null, V4StoryDateView? Left = null);
public sealed record V4RelationshipMergeEvent(
    string Ref, string Title, string? Description, V4StoryDateView Occurred, bool IsDeleted,
    string? WorldEventRef, IReadOnlyList<string> Projects);
public sealed record V4RelationshipMergeCandidate(
    string Ref, string Type, IReadOnlyList<string> Characters, string? Notes,
    IReadOnlyList<V4RelationshipMergePeriod> Periods,
    IReadOnlyList<V4RelationshipMergeEvent> Events,
    IReadOnlyList<string> Claims, int HistoryEntries,
    IReadOnlyList<string> Images);
public sealed record V4RelationshipMergePreview(
    bool CanMerge, string? ReviewToken, IReadOnlyList<string> Conflicts,
    V4RelationshipMergeCandidate Source, V4RelationshipMergeCandidate Target,
    string Preservation);
public sealed record V4OwnershipPrincipalCreateRequest(
    string MutationToken, string Kind, string? Entity = null, string? Label = null);
public sealed record V4OwnershipOwnerInput(
    string Principal, int? SharePartsPerMillion = null, string? Notes = null);
public sealed record V4ObjectOwnershipAddRequest(
    string MutationToken, string Object, string State, V4StoryDateInput Period,
    IReadOnlyList<V4OwnershipOwnerInput> Owners, string? Notes = null);
public sealed record V4ObjectOwnershipReplaceOwnersRequest(
    string MutationToken, string OwnershipRef, int ExpectedVersion,
    IReadOnlyList<V4OwnershipOwnerInput> Owners);
public sealed record V4ObjectOwnershipTransferRequest(
    string MutationToken, string OwnershipRef, int ExpectedVersion,
    string EffectiveAt, string NewState,
    IReadOnlyList<V4OwnershipOwnerInput> NewOwners, string? Notes = null);
public sealed record V4ObjectLocationAddRequest(
    string MutationToken, string Object, string Location,
    V4StoryDateInput Period, string? Notes = null);
public sealed record V4ObjectCustodyAddRequest(
    string MutationToken, string Object, V4StoryDateInput Period,
    string CustodianState, string? Principal = null, string? Notes = null);
public sealed record V4WorldEventParticipantAddRequest(
    string MutationToken, string WorldEvent, string Participant,
    string? Role = null, string? Impact = null, string? Outcome = null, string? Notes = null);
public sealed record V4WorldEventLocationAddRequest(
    string MutationToken, string WorldEvent, string Location,
    bool IsPrimary = false, string? Role = null, string? Notes = null);
public sealed record V4TemporalProfileSetRequest(
    string MutationToken, string Character, int? ExpectedVersion,
    bool Enabled, string LegalAgePolicy = "CalendarAge", string? Notes = null);
public sealed record V4TemporalEffectCreateRequest(
    string MutationToken, string Character, string Name, V4StoryDateInput Period,
    double BiologicalRate, double ExperiencedRate,
    string? CauseWorldEvent = null, string? Notes = null);
public sealed record V4TemporalEffectUpdateRequest(
    string MutationToken, string EffectRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4VersionedRecordRequest(string MutationToken, string Ref, int ExpectedVersion);
public sealed record V4InlineImageInput(
    string MediaType, string? DataBase64 = null, string? DataUrl = null);
public sealed record V4ImageAttachRequest(
    string MutationToken, string Entity, V4InlineImageInput Image,
    string? Title = null, string? Caption = null, string? AltText = null,
    string? Role = null, string? CanonStatus = null, string? Source = null,
    string BackgroundColor = "#FFFFFF", bool IsPrimary = false);
public sealed record V4StoryImageAttachRequest(
    string MutationToken, string Owner, V4InlineImageInput Image,
    string? Title = null, string? Caption = null, string? AltText = null,
    string? Role = null, string? CanonStatus = null, string? Source = null,
    string BackgroundColor = "#FFFFFF", bool IsPrimary = false);
public sealed record V4ImageUpdateRequest(
    string MutationToken, string ImageRef, int ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> Changes);
public sealed record V4ImageReplaceRequest(
    string MutationToken, string ImageRef, int ExpectedVersion,
    V4InlineImageInput Image, string BackgroundColor = "#FFFFFF");
