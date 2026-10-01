using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Soft deletion and restoration of canon and relationship records.</summary>
public sealed partial class AccessVaultService
{
    public Task<VaultMutationResult> SoftDeleteEntityAsync(VersionedEntityRequest request, CancellationToken cancellationToken = default) =>
        SetDeletionAsync(request, true, cancellationToken);

    public Task<VaultMutationResult> RestoreEntityAsync(VersionedEntityRequest request, CancellationToken cancellationToken = default) =>
        SetDeletionAsync(request, false, cancellationToken);

    public Task<VaultMutationResult> SoftDeleteRelationshipAsync(VersionedRelationshipRequest request, CancellationToken cancellationToken = default) =>
        Enum.IsDefined(request.RecordType)
            ? SetRelationshipDeletionAsync(request, true, cancellationToken)
            : Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: "RecordType is not supported."));

    public Task<VaultMutationResult> RestoreRelationshipAsync(VersionedRelationshipRequest request, CancellationToken cancellationToken = default) =>
        Enum.IsDefined(request.RecordType)
            ? SetRelationshipDeletionAsync(request, false, cancellationToken)
            : Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: "RecordType is not supported."));

    private Task<VaultMutationResult> SetRelationshipDeletionAsync(VersionedRelationshipRequest request, bool deleted, CancellationToken cancellationToken)
    {
        var (table, recordType) = request.RecordType switch
        {
            RelationshipRecordType.EntityNote => ("EntityNotes", "EntityNote"),
            RelationshipRecordType.EntityEvent => ("EntityEvents", "EntityEvent"),
            RelationshipRecordType.NoteSource => ("NoteSources", "NoteSource"),
            RelationshipRecordType.ProjectAssignment => ("ProjectEntities", "ProjectAssignment"),
            RelationshipRecordType.CharacterAlias => ("CharacterAliases", "CharacterAlias"),
            RelationshipRecordType.CharacterResidence => ("CharacterResidences", "CharacterResidence"),
            RelationshipRecordType.OrganizationAlias => ("OrganizationAliases", "OrganizationAlias"),
            RelationshipRecordType.OrganizationMembership => ("OrganizationMemberships", "OrganizationMembership"),
            RelationshipRecordType.OrganizationLocation => ("OrganizationLocations", "OrganizationLocation"),
            RelationshipRecordType.CharacterRelationship => ("CharacterRelationships", "CharacterRelationship"),
            RelationshipRecordType.ObjectOwnershipPeriod => ("ObjectOwnershipPeriods", "ObjectOwnershipPeriod"),
            RelationshipRecordType.ObjectCustodyPeriod => ("ObjectCustodyPeriods", "ObjectCustodyPeriod"),
            RelationshipRecordType.ObjectLocationPeriod => ("ObjectLocationPeriods", "ObjectLocationPeriod"),
            RelationshipRecordType.WorldEventParticipant => ("WorldEventParticipants", "WorldEventParticipant"),
            RelationshipRecordType.WorldEventLocation => ("WorldEventLocations", "WorldEventLocation"),
            RelationshipRecordType.Claim => ("Claims", "Claim"),
            RelationshipRecordType.ClaimEvidence => ("ClaimSources", "ClaimEvidence"),
            RelationshipRecordType.SourceSnapshot => ("SourceSnapshots", "SourceSnapshot"),
            _ => throw new ArgumentOutOfRangeException(nameof(request.RecordType))
        };
        return writes.ExecuteAsync(
            request.OperationId, deleted ? "relationship.soft_delete" : "relationship.restore", request,
            deleted ? "relationship_soft_delete" : "relationship_restore", request.ClientLabel,
            async (context, token) =>
            {
                if (!deleted && request.RecordType == RelationshipRecordType.CharacterRelationship)
                {
                    using var merged = context.Command(
                        "SELECT COUNT(*) FROM [RelationshipMergeRedirects] WHERE [SourceRelationshipId]=?")
                        .Add(OleDbType.Integer, request.RecordId);
                    if (Convert.ToInt32(await merged.ExecuteScalarAsync(token)) > 0)
                        throw new VaultCommandException("relationship.merged",
                            "This relationship was merged. Follow its redirect instead of restoring the archived source.");
                }
                using var update = context.Command($"UPDATE [{table}] SET [IsDeleted]=?,[DeletedAtUtc]=?,[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=? AND [IsDeleted]=?")
                    .Add(OleDbType.Boolean, deleted).Add(OleDbType.Date, deleted ? DateTime.UtcNow : null)
                    .Add(OleDbType.VarWChar, deleted ? context.OperationId : null, 36).Add(OleDbType.Date, DateTime.UtcNow)
                    .Add(OleDbType.Integer, request.RecordId).Add(OleDbType.Integer, request.ExpectedVersion).Add(OleDbType.Boolean, !deleted);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, recordType, request.RecordId, table, "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome(recordType, request.RecordId.ToString(), request.ExpectedVersion + 1,
                    deleted ? "soft-delete" : "restore", new { deleted }, request.ExpectedVersion);
            }, cancellationToken);
    }

    private Task<VaultMutationResult> SetDeletionAsync(VersionedEntityRequest request, bool deleted, CancellationToken cancellationToken) =>
        writes.ExecuteAsync(
            request.OperationId, deleted ? "entity.soft_delete" : "entity.restore", request,
            deleted ? "entity_soft_delete" : "entity_restore", request.ClientLabel,
            async (context, token) =>
            {
                using var update = context.Command("UPDATE [CanonEntities] SET [IsDeleted]=?,[DeletedAtUtc]=?,[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=? AND [IsDeleted]=?")
                    .Add(OleDbType.Boolean, deleted).Add(OleDbType.Date, deleted ? DateTime.UtcNow : null)
                    .Add(OleDbType.VarWChar, deleted ? context.OperationId : null, 36).Add(OleDbType.Date, DateTime.UtcNow)
                    .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.ExpectedVersion)
                    .Add(OleDbType.Boolean, !deleted);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "CanonEntity", request.EntityId, "CanonEntities", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome("CanonEntity", request.EntityId.ToString(), request.ExpectedVersion + 1, deleted ? "soft-delete" : "restore", new { deleted }, request.ExpectedVersion);
            }, cancellationToken);

}
