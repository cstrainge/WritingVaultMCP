using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Application;

public sealed record CreateContinuityRequest(
    string OperationId, string Name, string DefaultTimeZoneId, string? Description = null, string? ClientLabel = null);

public sealed record CreateVariantGroupRequest(
    string OperationId, int ContinuityId, CanonEntityType EntityType,
    string? Name = null, string? Notes = null, string? ClientLabel = null);

public sealed record SetVariantGroupRequest(
    string OperationId, int EntityId, int? VariantGroupId, int ExpectedVersion, string? ClientLabel = null);

public sealed record PatchVariantGroupRequest(
    string OperationId, int VariantGroupId, int ExpectedVersion,
    PatchField<string>? Name = null, PatchField<string>? Notes = null, string? ClientLabel = null);

public sealed record PatchContinuityRequest(
    string OperationId, int ContinuityId, int ExpectedVersion,
    PatchField<string>? Name = null, PatchField<string>? Description = null,
    PatchField<string>? DefaultTimeZoneId = null, string? ClientLabel = null);

public sealed record CreateCanonEntityRequest(
    string OperationId,
    int ContinuityId,
    CanonEntityType EntityType,
    string Name,
    string? Description = null,
    string? SecondaryType = null,
    string? TimeZoneId = null,
    StoryDate? Birth = null,
    StoryDate? Death = null,
    StoryDate? Occurred = null,
    int? BirthLocationId = null,
    string? BirthLocationDetail = null,
    int? VariantGroupId = null,
    string? ClientLabel = null,
    double? NarrativeOrder = null,
    string? MiddleNames = null,
    string? FamilyName = null,
    string? PreferredName = null,
    string? Gender = null,
    string? Pronouns = null,
    string? Species = null,
    string? Occupation = null,
    string? Nationality = null,
    string? PhysicalDescription = null,
    string? PersonalitySummary = null);

public sealed record DuplicateEntityRequest(
    string OperationId, int SourceEntityId, int TargetContinuityId,
    string? Name = null, string? ClientLabel = null);

public sealed record SetContinuityClockRequest(
    string OperationId,
    int ContinuityId,
    DateTimeOffset? CurrentInstant,
    string? ReferenceTimeZoneId,
    int ExpectedVersion,
    string? ClientLabel = null);

public sealed record LinkEntityRequest(
    string OperationId, int EntityId, int RelatedId, string? Role = null, string? Notes = null, string? ClientLabel = null);

public sealed record UnlinkEntityRequest(
    string OperationId, int EntityId, int RelatedId, string? ClientLabel = null);

public enum RelationshipRecordType
{
    EntityNote, EntityEvent, NoteSource, ProjectAssignment, CharacterAlias, CharacterResidence,
    OrganizationAlias, OrganizationMembership, OrganizationLocation, CharacterRelationship,
    ObjectOwnershipPeriod, ObjectCustodyPeriod, ObjectLocationPeriod,
    WorldEventParticipant, WorldEventLocation, Claim, ClaimEvidence, SourceSnapshot
}

public sealed record VersionedRelationshipRequest(
    string OperationId, RelationshipRecordType RecordType, int RecordId, int ExpectedVersion, string? ClientLabel = null);

public sealed record AddNoteRequest(
    string OperationId, int EntityId, string Body, string? Title = null, string? ClientLabel = null);

public sealed record AddEntityEventRequest(
    string OperationId, int EntityId, string Title, StoryDate Occurred,
    int? WorldEventId = null, string? Description = null, string? ClientLabel = null,
    double? NarrativeOrder = null);

public sealed record AddAliasRequest(
    string OperationId, int EntityId, string Alias, string? Notes = null, string? ClientLabel = null);

public sealed record LinkNoteSourceRequest(
    string OperationId, int NoteId, int SourceId, string? Locator = null, string? Notes = null, string? ClientLabel = null);

public sealed record CreateTagRequest(string OperationId, string Name, string? Description = null, string? ClientLabel = null);

public sealed record PatchTagRequest(
    string OperationId, int TagId, int ExpectedVersion,
    PatchField<string>? Name = null, PatchField<string>? Description = null, string? ClientLabel = null);

public sealed record CreateSourceRequest(
    string OperationId, string Title, string? CanonicalUrl = null, string? SourceType = null,
    string? Citation = null, string? Notes = null, string? ClientLabel = null,
    string? ArchiveUrl = null, string? AuthorPublisher = null);

public sealed record PatchSourceRequest(
    string OperationId, int SourceId, int ExpectedVersion,
    PatchField<string>? Title = null, PatchField<string>? CanonicalUrl = null,
    PatchField<string>? ArchiveUrl = null, PatchField<string>? SourceType = null,
    PatchField<string>? AuthorPublisher = null, PatchField<string>? Citation = null,
    PatchField<string>? Notes = null, string? ClientLabel = null);

public enum VaultRecordType { Continuity, VariantGroup, Source, Tag, RelationshipType, OwnershipPrincipal }

public sealed record VersionedVaultRecordRequest(
    string OperationId, VaultRecordType RecordType, int RecordId, int ExpectedVersion, string? ClientLabel = null);

public sealed record AddSourceSnapshotRequest(
    string OperationId, int SourceId, string Content, string? MediaType = null,
    DateTime? RetrievedAtUtc = null, string? ClientLabel = null);

public sealed record ClaimEvidenceInput(
    int SourceId, int? SourceSnapshotId = null, string EvidenceRelation = "Supports",
    string? Locator = null, string? EvidenceExcerpt = null, string? Summary = null);

public sealed record CreateClaimRequest(
    string OperationId, int ContinuityId, string ClaimText, IReadOnlyList<int> EntityIds,
    IReadOnlyList<ClaimEvidenceInput> Evidence, string ClaimStatus = "Active", double? Confidence = null,
    string? TargetField = null, string? Commentary = null, string? ClientLabel = null);

public sealed record AddResidenceRequest(
    string OperationId, int CharacterId, int LocationId, StoryDate Period, bool IsPrimary = true, string? Notes = null, string? ClientLabel = null);

public sealed record TransitionResidenceRequest(
    string OperationId, int ResidenceId, int ExpectedVersion, int NewLocationId,
    DateTime EffectiveAt, bool IsPrimary = true, string? Notes = null, string? ClientLabel = null);

public sealed record MoveLocationRequest(
    string OperationId, int LocationId, int? ParentLocationId, int ExpectedVersion, string? ClientLabel = null);

public sealed record AddMembershipRequest(
    string OperationId, int OrganizationId, int CharacterId, StoryDate Period, string? Role = null, string? Notes = null, string? ClientLabel = null);

public sealed record TransitionMembershipRequest(
    string OperationId, int MembershipId, int ExpectedVersion, DateTime EffectiveAt,
    bool CreateReplacement, string? NewRole = null, string? Notes = null, string? ClientLabel = null);

public sealed record CreateRelationshipRequest(
    string OperationId, int ContinuityId, int SourceCharacterId, int TargetCharacterId,
    int RelationshipTypeId, StoryDate Period, string? Notes = null, string? ClientLabel = null);

public sealed record CreateRelationshipGroupRequest(
    string OperationId, int ContinuityId, IReadOnlyList<int> CharacterIds,
    int RelationshipTypeId, StoryDate? InitialPeriod, string? Notes = null, string? ClientLabel = null);

public sealed record CreateRelationshipTypeRequest(
    string OperationId, string Name, bool IsDirected, string? InverseName = null,
    bool AllowsOverlappingPeriods = false, string? Description = null, string? ClientLabel = null);

public sealed record AddWorldEventParticipantRequest(
    string OperationId, int WorldEventId, int ParticipantEntityId, string? Role = null,
    string? Impact = null, string? Outcome = null, string? Notes = null, string? ClientLabel = null);

public sealed record AddWorldEventLocationRequest(
    string OperationId, int WorldEventId, int LocationId, bool IsPrimary = false,
    string? Role = null, string? Notes = null, string? ClientLabel = null);

public sealed record OwnershipOwnerInput(int PrincipalId, int? SharePartsPerMillion = null, string? Notes = null);

public sealed record AddOwnershipPeriodRequest(
    string OperationId, int ObjectId, OwnershipState State, StoryDate Period,
    IReadOnlyList<OwnershipOwnerInput> Owners, string? Notes = null, string? ClientLabel = null);

public sealed record ReplaceOwnershipOwnersRequest(
    string OperationId, int OwnershipPeriodId, int ExpectedVersion,
    IReadOnlyList<OwnershipOwnerInput> Owners, string? ClientLabel = null);

public sealed record TransferOwnershipRequest(
    string OperationId, int OwnershipPeriodId, int ExpectedVersion, DateTime EffectiveAt,
    OwnershipState NewState, IReadOnlyList<OwnershipOwnerInput> NewOwners,
    string? Notes = null, string? ClientLabel = null);

public sealed record AddOrganizationLocationRequest(
    string OperationId, int OrganizationId, int LocationId, StoryDate Period,
    bool IsPrimary = false, string? LocationRole = null, string? Notes = null, string? ClientLabel = null);

public sealed record AddObjectLocationRequest(
    string OperationId, int ObjectId, int LocationId, StoryDate Period, string? Notes = null, string? ClientLabel = null);

public sealed record AddObjectCustodyRequest(
    string OperationId, int ObjectId, StoryDate Period, CustodyState CustodianState,
    int? PrincipalId = null, string? Notes = null, string? ClientLabel = null);

public sealed record CreateOwnershipPrincipalRequest(
    string OperationId, int ContinuityId, PrincipalKind Kind, int? CharacterId = null,
    int? OrganizationId = null, string? Label = null, string? ClientLabel = null);

public sealed record VersionedEntityRequest(
    string OperationId, int EntityId, int ExpectedVersion, string? ClientLabel = null);

public sealed record PatchField<T>(bool Specified, T? Value);

public sealed record PatchEntityRequest(
    string OperationId, int EntityId, int ExpectedVersion,
    PatchField<string>? Name = null,
    PatchField<string>? Description = null,
    PatchField<string>? SecondaryType = null,
    PatchField<string>? TimeZoneId = null,
    string? ClientLabel = null,
    PatchField<StoryDate>? Birth = null,
    PatchField<StoryDate>? Death = null,
    PatchField<StoryDate>? Occurred = null,
    PatchField<int?>? BirthLocationId = null,
    PatchField<string>? BirthLocationDetail = null,
    PatchField<double?>? NarrativeOrder = null,
    PatchField<string>? MiddleNames = null,
    PatchField<string>? FamilyName = null,
    PatchField<string>? PreferredName = null,
    PatchField<string>? Gender = null,
    PatchField<string>? Pronouns = null,
    PatchField<string>? Species = null,
    PatchField<string>? Occupation = null,
    PatchField<string>? Nationality = null,
    PatchField<string>? PhysicalDescription = null,
    PatchField<string>? PersonalitySummary = null);

public sealed record EntitySummary(
    int Id, int ContinuityId, CanonEntityType EntityType, string Name, int Version, bool IsDeleted, DateTime UpdatedAtUtc,
    int? VariantGroupId = null);

public sealed record VariantGroupSummary(
    int Id, int ContinuityId, CanonEntityType EntityType, string? Name, string? Notes, int Version, bool IsDeleted,
    bool NotesTruncated = false);

public sealed record CharacterRelationshipView(
    int RelationshipId, int CharacterId, int RelatedCharacterId, int RelationshipTypeId,
    string Label, string Perspective, StoryDate Period, string? Notes, int Version, bool NotesTruncated = false);

public sealed record EntityDetails(EntitySummary Summary, IReadOnlyDictionary<string, object?> Fields);

public sealed record PageResult<T>(IReadOnlyList<T> Items, int? NextAfterId);

public sealed record HistoryEntry(
    int Id, string OperationId, DateTime ChangedAtUtc, string? ClientLabel, string ToolName, string Action,
    string RecordType, string RecordKey, int? VersionBefore, int? VersionAfter, string? ChangeJson);

public sealed record DeletePreview(int EntityId, IReadOnlyList<BlockingReference> Blockers, bool CanSoftDelete);

public sealed record ContinuitySummary(int Id, string Name, string DefaultTimeZoneId, int Version, bool IsDeleted, DateTime? ArtificialInstantUtc, string? ReferenceTimeZoneId, int ClockVersion);
public sealed record SourceSummary(int Id, string Title, string? CanonicalUrl, int Version, bool IsDeleted);
public sealed record TagSummary(int Id, string Name, int Version, bool IsDeleted);
public sealed record EntityGraph(
    int EntityId,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Notes,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Events,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Tags,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Sources,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Projects,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> TypeSpecificRelations,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Claims,
    bool Truncated);

public sealed record SourceGraph(
    SourceSummary Source,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Entities,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Notes,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Claims,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Tags,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Snapshots,
    bool Truncated);

public sealed record EntityTemporalState(
    int EntityId, int ContinuityId, DateTime StoryReferenceTime,
    DateTime ArtificialInstantUtc, string ReferenceTimeZoneId,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> ActiveRecords);
