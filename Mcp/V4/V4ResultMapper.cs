using WritingVaultMcp.Application;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;

namespace WritingVaultMcp.Mcp.V4;

/// <summary>Maps storage-layer outcomes to the path-, key-, and GUID-free v4 envelope.</summary>
public sealed class V4ResultMapper(VaultReferenceService references)
{
    public async Task<V4MutationResult> MutationAsync(
        VaultMutationResult result, CancellationToken cancellationToken = default)
    {
        if (!result.Success)
        {
            var publicCode = PublicCode(result.Code);
            var candidates = result.Candidates?.Select(candidate => new V4ReferenceSummary(
                candidate.Reference, Kind(candidate.ResourceType), candidate.Label, candidate.Context)).ToArray();
            var details = result.Version is { } actualVersion
                ? new[] { new V4ErrorDetail("expectedVersion", $"The current version is {actualVersion}.") }
                : null;
            var error = new V4Error(
                publicCode,
                string.IsNullOrWhiteSpace(result.Message) ? "The Vault could not complete the change." : result.Message,
                result.Retryable,
                details,
                candidates,
                Recovery(publicCode, candidates));
            return new(false, publicCode, [], result.Replayed, result.Message, error);
        }

        var affected = new List<V4ReferenceSummary>();
        if (result.ResourceType is not null && int.TryParse(result.ResourceKey, out var storageKey))
        {
            var referenceType = result.ResourceType.Equals("ContinuityClock", StringComparison.OrdinalIgnoreCase)
                ? "Continuity"
                : result.ResourceType;
            var reference = await references.ReferenceAsync(referenceType, storageKey, cancellationToken).ConfigureAwait(false);
            var resolved = await references.ResolveAsync(reference, null, cancellationToken).ConfigureAwait(false);
            var continuityName = resolved.ContinuityId is { } continuityKey
                ? await references.ContinuityNameAsync(continuityKey, cancellationToken).ConfigureAwait(false)
                : null;
            affected.Add(new(
                reference,
                Kind(resolved.ResourceType),
                resolved.Label,
                ContinuityName: continuityName,
                Version: result.Version,
                IsDeleted: resolved.IsDeleted));
        }
        return new(true, result.Code, affected, result.Replayed, result.Message);
    }

    public static V4MutationResult Backup(CoordinatedBackupResult result) =>
        new(true, "ok", [], result.Replayed, Backup: new(
            result.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            result.DatabaseBytes,
            result.DatabaseSha256,
            result.SchemaMigration,
            result.SchemaValid,
            result.IntegrityValid,
            result.RetentionCount,
            result.AssetCount,
            result.AssetBytes));

    public static V4MutationResult Validation(VaultValidationException exception)
    {
        var details = exception.Errors.Select(error => new V4ErrorDetail(error.Field, error.Message)).ToArray();
        var code = exception.Errors.Count == 1 ? exception.Errors[0].Code : "validation.failed";
        return new(false, code, [], false, exception.Message,
            new V4Error(code, exception.Message, Details: details));
    }

    private static V4RecordKind Kind(string resourceType) => resourceType.ToUpperInvariant() switch
    {
        "CONTINUITY" => V4RecordKind.Continuity,
        "PROJECT" => V4RecordKind.Project,
        "LOCATION" => V4RecordKind.Location,
        "CHARACTER" => V4RecordKind.Character,
        "ORGANIZATION" => V4RecordKind.Organization,
        "OBJECT" => V4RecordKind.Object,
        "WORLDEVENT" => V4RecordKind.WorldEvent, "SPECIES" => V4RecordKind.Species,
        "SOURCE" => V4RecordKind.Source,
        "SOURCESNAPSHOT" => V4RecordKind.SourceSnapshot,
        "CLAIM" => V4RecordKind.Claim,
        "TAG" => V4RecordKind.Tag,
        "ENTITYNOTE" or "CONTINUITYNOTE" => V4RecordKind.Note,
        "ENTITYIMAGE" => V4RecordKind.Image,
        "VARIANTGROUP" => V4RecordKind.VariantGroup,
        "CHARACTERRELATIONSHIP" or "CHARACTERALIAS" or "ORGANIZATIONALIAS" or
        "PROJECTASSIGNMENT" or "NOTESOURCE" or "CONTINUITYNOTESOURCE" or
        "CLAIMEVIDENCE" or "ENTITYEVENTPROJECT" or "WORLDEVENTPARTICIPANT" or
        "WORLDEVENTLOCATION" => V4RecordKind.Relationship,
        "RELATIONSHIPTYPE" => V4RecordKind.RelationshipType,
        "CHARACTERRESIDENCE" => V4RecordKind.Residence,
        "ORGANIZATIONMEMBERSHIP" => V4RecordKind.Membership,
        "ORGANIZATIONLOCATION" => V4RecordKind.OrganizationLocation,
        "OBJECTOWNERSHIPPERIOD" => V4RecordKind.Ownership,
        "OWNERSHIPPRINCIPAL" => V4RecordKind.OwnershipPrincipal,
        "OBJECTCUSTODYPERIOD" => V4RecordKind.Custody,
        "OBJECTLOCATIONPERIOD" => V4RecordKind.ObjectLocation,
        "ENTITYEVENT" => V4RecordKind.EntityEvent,
        "RELATIONSHIPEVENT" => V4RecordKind.RelationshipEvent,
        "RELATIONSHIPMEMBERSHIPPERIOD" => V4RecordKind.RelationshipMembershipPeriod,
        "RELATIONSHIPPARTICIPANT" => V4RecordKind.RelationshipParticipant,
        "RELATIONSHIPEVENTPROJECT" => V4RecordKind.Relationship,
        "CHARACTERTEMPORALEFFECT" or "CHARACTERTEMPORALPROFILE" => V4RecordKind.TemporalEffect,
        _ => throw new InvalidOperationException("The storage result used an unsupported public record kind.")
    };

    private static string PublicCode(string code) => code switch
    {
        "concurrency.conflict" => "version.conflict",
        "entity.not_found" => "record.not_found",
        "continuity.mismatch" => "scope.mismatch",
        "idempotency.input_mismatch" => "mutation_token.input_mismatch",
        _ => code
    };

    private static string? Recovery(string code,IReadOnlyList<V4ReferenceSummary>? candidates)=>code switch
    {
        "record.ambiguous" when candidates is {Count:>0}=>"Retry with exactly one candidate ref from the candidates list.",
        "version.conflict"=>"Read the record again, reconcile the change, and retry with its current version and a new mutationToken.",
        "record.not_found"=>"Search in the selected continuity and retry with a returned ref.",
        "scope.mismatch"=>"Select the record's continuity or use a record from the current continuity.",
        "mutation_token.input_mismatch"=>"Use a new mutationToken for different mutation input.",
        _=>null
    };
}
