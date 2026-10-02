using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Mcp;

public sealed record McpCreateContinuityRequest(string RequestToken, string Name, string DefaultTimeZoneId, string? Description = null);
public sealed record McpPatchContinuityRequest(string RequestToken, int ExpectedVersion, PatchField<string>? Name = null, PatchField<string>? Description = null, PatchField<string>? DefaultTimeZoneId = null);
public sealed record McpCreateVariantGroupRequest(string RequestToken, CanonEntityType EntityType, string? Name = null, string? Notes = null);
public sealed record McpPatchVariantGroupRequest(string RequestToken, string VariantGroupReference, int ExpectedVersion, PatchField<string>? Name = null, PatchField<string>? Notes = null);
public sealed record McpSetVariantGroupRequest(string RequestToken, string EntityReference, string? VariantGroupReference, int ExpectedVersion);
public sealed record McpCreateEntityRequest(
    string RequestToken, CanonEntityType EntityType, string Name, string? Description = null, string? SecondaryType = null,
    string? TimeZoneId = null, StoryDate? Birth = null, StoryDate? Death = null, StoryDate? Occurred = null,
    string? BirthLocationReference = null, string? BirthLocationDetail = null, string? VariantGroupReference = null,
    double? NarrativeOrder = null, string? MiddleNames = null, string? FamilyName = null, string? PreferredName = null,
    string? Gender = null, string? Pronouns = null, string? Species = null, string? Occupation = null,
    string? Nationality = null, string? PhysicalDescription = null, string? PersonalitySummary = null, bool BirthdayRecurring = false);
public sealed record McpPatchEntityRequest(
    string RequestToken, string EntityReference, int ExpectedVersion,
    PatchField<string>? Name = null, PatchField<string>? Description = null, PatchField<string>? SecondaryType = null,
    PatchField<string>? TimeZoneId = null, PatchField<StoryDate>? Birth = null, PatchField<StoryDate>? Death = null,
    PatchField<StoryDate>? Occurred = null, PatchField<string>? BirthLocationReference = null,
    PatchField<string>? BirthLocationDetail = null, PatchField<double?>? NarrativeOrder = null,
    PatchField<string>? MiddleNames = null, PatchField<string>? FamilyName = null, PatchField<string>? PreferredName = null,
    PatchField<string>? Gender = null, PatchField<string>? Pronouns = null, PatchField<string>? Species = null,
    PatchField<string>? Occupation = null, PatchField<string>? Nationality = null,
    PatchField<string>? PhysicalDescription = null, PatchField<string>? PersonalitySummary = null, PatchField<bool>? BirthdayRecurring = null);
public sealed record McpDuplicateEntityRequest(string RequestToken, string SourceEntityReference, string TargetContinuityName, string? Name = null);
public sealed record McpSetClockRequest(string RequestToken, DateTimeOffset? CurrentInstant, string? ReferenceTimeZoneId, int ExpectedVersion);
public sealed record McpCreateTagRequest(string RequestToken, string Name, string? Description = null);
public sealed record McpPatchTagRequest(string RequestToken, string TagReference, int ExpectedVersion, PatchField<string>? Name = null, PatchField<string>? Description = null);
public sealed record McpCreateSourceRequest(string RequestToken, string Title, string? CanonicalUrl = null, string? SourceType = null, string? Citation = null, string? Notes = null, string? ArchiveUrl = null, string? AuthorPublisher = null);
public sealed record McpPatchSourceRequest(string RequestToken, string SourceReference, int ExpectedVersion, PatchField<string>? Title = null, PatchField<string>? CanonicalUrl = null, PatchField<string>? ArchiveUrl = null, PatchField<string>? SourceType = null, PatchField<string>? AuthorPublisher = null, PatchField<string>? Citation = null, PatchField<string>? Notes = null);
public sealed record McpAddSourceSnapshotRequest(string RequestToken, string SourceReference, string Content, string? MediaType = null, DateTime? RetrievedAtUtc = null);
public sealed record McpClaimEvidenceInput(string SourceReference, string? SourceSnapshotReference = null, string EvidenceRelation = "Supports", string? Locator = null, string? EvidenceExcerpt = null, string? Summary = null);
public sealed record McpCreateClaimRequest(string RequestToken, string ClaimText, IReadOnlyList<string> EntityReferences, IReadOnlyList<McpClaimEvidenceInput> Evidence, string ClaimStatus = "Active", double? Confidence = null, string? TargetField = null, string? Commentary = null);
public sealed record McpAddNoteRequest(string RequestToken, string EntityReference, string Body, string? Title = null);
public sealed record McpAddEntityEventRequest(string RequestToken, string EntityReference, string Title, StoryDate Occurred, string? WorldEventReference = null, string? Description = null, double? NarrativeOrder = null);
public sealed record McpAddAliasRequest(string RequestToken, string EntityReference, string Alias, string? Notes = null);
public sealed record McpLinkNoteSourceRequest(string RequestToken, string NoteReference, string SourceReference, string? Locator = null, string? Notes = null);
public sealed record McpLinkRequest(string RequestToken, string EntityReference, string RelatedReference, string? Role = null, string? Notes = null);
public sealed record McpUnlinkRequest(string RequestToken, string EntityReference, string RelatedReference);
public sealed record McpEntityTagLinkRequest(string RequestToken, string EntityReference, string TagReference);
public sealed record McpEntityTagUnlinkRequest(string RequestToken, string EntityReference, string TagReference);
public sealed record McpEntitySourceLinkRequest(string RequestToken, string EntityReference, string SourceReference);
public sealed record McpEntitySourceUnlinkRequest(string RequestToken, string EntityReference, string SourceReference);
public sealed record McpSourceTagLinkRequest(string RequestToken, string SourceReference, string TagReference);
public sealed record McpSourceTagUnlinkRequest(string RequestToken, string SourceReference, string TagReference);
public sealed record McpProjectEntityLinkRequest(string RequestToken, string ProjectReference, string EntityReference, string? Role = null, string? Notes = null);
public sealed record McpMoveLocationRequest(string RequestToken, string LocationReference, string? ParentLocationReference, int ExpectedVersion);
public sealed record McpAddResidenceRequest(string RequestToken, string CharacterReference, string LocationReference, StoryDate Period, bool IsPrimary = true, string? Notes = null);
public sealed record McpTransitionResidenceRequest(string RequestToken, string ResidenceReference, int ExpectedVersion, string NewLocationReference, DateTime EffectiveAt, bool IsPrimary = true, string? Notes = null);
public sealed record McpAddMembershipRequest(string RequestToken, string OrganizationReference, string CharacterReference, StoryDate Period, string? Role = null, string? Notes = null);
public sealed record McpTransitionMembershipRequest(string RequestToken, string MembershipReference, int ExpectedVersion, DateTime EffectiveAt, bool CreateReplacement, string? NewRole = null, string? Notes = null);
public sealed record McpAddOrganizationLocationRequest(string RequestToken, string OrganizationReference, string LocationReference, StoryDate Period, bool IsPrimary = false, string? LocationRole = null, string? Notes = null);
public sealed record McpCreateRelationshipTypeRequest(string RequestToken, string Name, bool IsDirected, string? InverseName = null, bool AllowsOverlappingPeriods = false, string? Description = null);
public sealed record McpCreateRelationshipRequest(string RequestToken, string SourceCharacterReference, string TargetCharacterReference, string RelationshipTypeReference, StoryDate Period, string? Notes = null);
public sealed record McpCreateOwnershipPrincipalRequest(string RequestToken, PrincipalKind Kind, string? EntityReference = null, string? Label = null);
public sealed record McpOwnershipOwnerInput(string PrincipalReference, int? SharePartsPerMillion = null, string? Notes = null);
public sealed record McpAddOwnershipRequest(string RequestToken, string ObjectReference, OwnershipState State, StoryDate Period, IReadOnlyList<McpOwnershipOwnerInput> Owners, string? Notes = null);
public sealed record McpReplaceOwnershipOwnersRequest(string RequestToken, string OwnershipPeriodReference, int ExpectedVersion, IReadOnlyList<McpOwnershipOwnerInput> Owners);
public sealed record McpTransferOwnershipRequest(string RequestToken, string OwnershipPeriodReference, int ExpectedVersion, DateTime EffectiveAt, OwnershipState NewState, IReadOnlyList<McpOwnershipOwnerInput> NewOwners, string? Notes = null);
public sealed record McpAddObjectLocationRequest(string RequestToken, string ObjectReference, string LocationReference, StoryDate Period, string? Notes = null);
public sealed record McpAddObjectCustodyRequest(string RequestToken, string ObjectReference, StoryDate Period, CustodyState CustodianState, string? PrincipalReference = null, string? Notes = null);
public sealed record McpAddWorldEventParticipantRequest(string RequestToken, string WorldEventReference, string ParticipantEntityReference, string? Role = null, string? Impact = null, string? Outcome = null, string? Notes = null);
public sealed record McpAddWorldEventLocationRequest(string RequestToken, string WorldEventReference, string LocationReference, bool IsPrimary = false, string? Role = null, string? Notes = null);
public sealed record McpVersionedEntityRequest(string RequestToken, string EntityReference, int ExpectedVersion);
public sealed record McpVersionedRelationshipRequest(string RequestToken, RelationshipRecordType RecordType, string RecordReference, int ExpectedVersion);
public sealed record McpVersionedVaultRecordRequest(string RequestToken, VaultRecordType RecordType, string RecordReference, int ExpectedVersion);
