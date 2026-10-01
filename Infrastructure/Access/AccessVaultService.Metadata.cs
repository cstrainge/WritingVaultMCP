using System.Data.OleDb;
using WritingVaultMcp.Application;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessVaultService
{
    public Task<VaultMutationResult> PatchContinuityAsync(PatchContinuityRequest request, CancellationToken cancellationToken = default)
    {
        var specified = new[] { request.Name?.Specified == true, request.Description?.Specified == true, request.DefaultTimeZoneId?.Specified == true };
        if (!specified.Any(value => value)) return Task.FromResult(new VaultMutationResult(false, "patch.empty", Message: "At least one field must be specified."));
        string? name = null;
        try
        {
            if (request.Name?.Specified == true) name = TextNormalization.Required(request.Name.Value!, 255, nameof(request.Name));
            if (request.DefaultTimeZoneId?.Specified == true)
            {
                if (string.IsNullOrWhiteSpace(request.DefaultTimeZoneId.Value)) throw new ArgumentException("DefaultTimeZoneId cannot be null or blank.");
                _ = TimeZoneInfo.FindSystemTimeZoneById(request.DefaultTimeZoneId.Value);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: exception.Message)); }

        return writes.ExecuteAsync(request.OperationId, "continuity.patch", request, "continuity_patch", request.ClientLabel,
            async (context, token) =>
            {
                var assignments = new List<string>();
                var parameters = new List<(OleDbType Type, object? Value, int? Size)>();
                if (request.Name?.Specified == true)
                {
                    assignments.Add("[Name]=?"); parameters.Add((OleDbType.VarWChar, name, 255));
                    assignments.Add("[NormalizedName]=?"); parameters.Add((OleDbType.VarWChar, TextNormalization.CanonicalKey(name!), 255));
                }
                if (request.Description?.Specified == true) { assignments.Add("[Description]=?"); parameters.Add((OleDbType.LongVarWChar, request.Description.Value, null)); }
                if (request.DefaultTimeZoneId?.Specified == true) { assignments.Add("[DefaultTimeZoneId]=?"); parameters.Add((OleDbType.VarWChar, request.DefaultTimeZoneId.Value, 100)); }
                assignments.Add("[UpdatedAtUtc]=?"); parameters.Add((OleDbType.Date, DateTime.UtcNow, null));
                assignments.Add("[Version]=[Version]+1");
                using var update = context.Command($"UPDATE [Continuities] SET {string.Join(',', assignments)} WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False");
                foreach (var parameter in parameters) update.Add(parameter.Type, parameter.Value, parameter.Size);
                update.Add(OleDbType.Integer, request.ContinuityId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "Continuity", request.ContinuityId, "Continuities", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome("Continuity", request.ContinuityId.ToString(), request.ExpectedVersion + 1, "patch", request, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> PatchVariantGroupAsync(PatchVariantGroupRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Name?.Specified != true && request.Notes?.Specified != true)
            return Task.FromResult(new VaultMutationResult(false, "patch.empty", Message: "At least one field must be specified."));
        if (request.Name?.Value is { Length: > 255 })
            return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: "Name cannot exceed 255 characters."));
        return writes.ExecuteAsync(request.OperationId, "variant_group.patch", request, "variant_group_patch", request.ClientLabel,
            async (context, token) =>
            {
                var assignments = new List<string>();
                var parameters = new List<(OleDbType Type, object? Value, int? Size)>();
                if (request.Name?.Specified == true) { assignments.Add("[Name]=?"); parameters.Add((OleDbType.VarWChar, string.IsNullOrWhiteSpace(request.Name.Value) ? null : request.Name.Value.Trim(), 255)); }
                if (request.Notes?.Specified == true) { assignments.Add("[Notes]=?"); parameters.Add((OleDbType.LongVarWChar, request.Notes.Value, null)); }
                assignments.Add("[UpdatedAtUtc]=?"); parameters.Add((OleDbType.Date, DateTime.UtcNow, null));
                assignments.Add("[Version]=[Version]+1");
                using var update = context.Command($"UPDATE [VariantGroups] SET {string.Join(',', assignments)} WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False");
                foreach (var parameter in parameters) update.Add(parameter.Type, parameter.Value, parameter.Size);
                update.Add(OleDbType.Integer, request.VariantGroupId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "VariantGroup", request.VariantGroupId, "VariantGroups", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome("VariantGroup", request.VariantGroupId.ToString(), request.ExpectedVersion + 1, "patch", request, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> PatchTagAsync(PatchTagRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Name?.Specified != true && request.Description?.Specified != true)
            return Task.FromResult(new VaultMutationResult(false, "patch.empty", Message: "At least one field must be specified."));
        string? name = null;
        try { if (request.Name?.Specified == true) name = TextNormalization.Required(request.Name.Value!, 100, nameof(request.Name)); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: exception.Message)); }
        return writes.ExecuteAsync(request.OperationId, "tag.patch", request, "tag_patch", request.ClientLabel,
            async (context, token) =>
            {
                var assignments = new List<string>();
                using var update = context.Command(BuildTagSql());
                if (request.Name?.Specified == true)
                {
                    update.Add(OleDbType.VarWChar, name, 100).Add(OleDbType.VarWChar, TextNormalization.CanonicalKey(name!), 100);
                }
                if (request.Description?.Specified == true) update.Add(OleDbType.LongVarWChar, request.Description.Value);
                update.Add(OleDbType.Date, DateTime.UtcNow).Add(OleDbType.Integer, request.TagId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "Tag", request.TagId, "Tags", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome("Tag", request.TagId.ToString(), request.ExpectedVersion + 1, "patch", request, request.ExpectedVersion);

                string BuildTagSql()
                {
                    if (request.Name?.Specified == true) assignments.Add("[Name]=?,[NormalizedName]=?");
                    if (request.Description?.Specified == true) assignments.Add("[Description]=?");
                    assignments.Add("[UpdatedAtUtc]=?,[Version]=[Version]+1");
                    return $"UPDATE [Tags] SET {string.Join(',', assignments)} WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False";
                }
            }, cancellationToken);
    }

    public Task<VaultMutationResult> PatchSourceAsync(PatchSourceRequest request, CancellationToken cancellationToken = default)
    {
        var fields = new[] { request.Title, request.CanonicalUrl, request.ArchiveUrl, request.SourceType, request.AuthorPublisher, request.Citation, request.Notes };
        if (!fields.Any(field => field?.Specified == true)) return Task.FromResult(new VaultMutationResult(false, "patch.empty", Message: "At least one field must be specified."));
        string? title = null;
        try
        {
            if (request.Title?.Specified == true) title = TextNormalization.Required(request.Title.Value!, 255, nameof(request.Title));
            if (request.CanonicalUrl?.Specified == true) ValidateWebUrl(request.CanonicalUrl.Value, nameof(request.CanonicalUrl));
            if (request.ArchiveUrl?.Specified == true) ValidateWebUrl(request.ArchiveUrl.Value, nameof(request.ArchiveUrl));
            if (request.SourceType?.Value is { Length: > 100 }) throw new ArgumentException("SourceType cannot exceed 100 characters.");
            if (request.AuthorPublisher?.Value is { Length: > 255 }) throw new ArgumentException("AuthorPublisher cannot exceed 255 characters.");
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.source", Message: exception.Message)); }

        return writes.ExecuteAsync(request.OperationId, "source.patch", request, "source_patch", request.ClientLabel,
            async (context, token) =>
            {
                var assignments = new List<string>();
                var parameters = new List<(OleDbType Type, object? Value, int? Size)>();
                Add(request.Title, "Title", OleDbType.VarWChar, title, 255);
                Add(request.CanonicalUrl, "CanonicalUrl", OleDbType.LongVarWChar, request.CanonicalUrl?.Value, null);
                Add(request.ArchiveUrl, "ArchiveUrl", OleDbType.LongVarWChar, request.ArchiveUrl?.Value, null);
                Add(request.SourceType, "SourceType", OleDbType.VarWChar, request.SourceType?.Value, 100);
                Add(request.AuthorPublisher, "AuthorPublisher", OleDbType.VarWChar, request.AuthorPublisher?.Value, 255);
                Add(request.Citation, "Citation", OleDbType.LongVarWChar, request.Citation?.Value, null);
                Add(request.Notes, "Notes", OleDbType.LongVarWChar, request.Notes?.Value, null);
                assignments.Add("[UpdatedAtUtc]=?"); parameters.Add((OleDbType.Date, DateTime.UtcNow, null));
                assignments.Add("[Version]=[Version]+1");
                using var update = context.Command($"UPDATE [Sources] SET {string.Join(',', assignments)} WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False");
                foreach (var parameter in parameters) update.Add(parameter.Type, parameter.Value, parameter.Size);
                update.Add(OleDbType.Integer, request.SourceId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "Source", request.SourceId, "Sources", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome("Source", request.SourceId.ToString(), request.ExpectedVersion + 1, "patch", request, request.ExpectedVersion);

                void Add(PatchField<string>? field, string column, OleDbType type, object? value, int? size)
                {
                    if (field?.Specified != true) return;
                    assignments.Add($"[{column}]=?"); parameters.Add((type, value, size));
                }
            }, cancellationToken);
    }

    public Task<VaultMutationResult> SoftDeleteVaultRecordAsync(VersionedVaultRecordRequest request, CancellationToken cancellationToken = default) =>
        Enum.IsDefined(request.RecordType)
            ? SetVaultRecordDeletionAsync(request, true, cancellationToken)
            : Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: "RecordType is not supported."));

    public Task<VaultMutationResult> RestoreVaultRecordAsync(VersionedVaultRecordRequest request, CancellationToken cancellationToken = default) =>
        Enum.IsDefined(request.RecordType)
            ? SetVaultRecordDeletionAsync(request, false, cancellationToken)
            : Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: "RecordType is not supported."));

    private Task<VaultMutationResult> SetVaultRecordDeletionAsync(VersionedVaultRecordRequest request, bool deleted, CancellationToken cancellationToken)
    {
        var table = request.RecordType switch
        {
            VaultRecordType.Continuity => "Continuities", VaultRecordType.VariantGroup => "VariantGroups",
            VaultRecordType.Source => "Sources", VaultRecordType.Tag => "Tags",
            VaultRecordType.RelationshipType => "RelationshipTypes", VaultRecordType.OwnershipPrincipal => "OwnershipPrincipals",
            _ => throw new ArgumentOutOfRangeException()
        };
        return writes.ExecuteAsync(request.OperationId, deleted ? "vault_record.soft_delete" : "vault_record.restore", request,
            deleted ? "vault_record_soft_delete" : "vault_record_restore", request.ClientLabel,
            async (context, token) =>
            {
                if (deleted && request.RecordType == VaultRecordType.Continuity)
                {
                    using var entities = context.Command("SELECT COUNT(*) FROM [CanonEntities] WHERE [ContinuityId]=?").Add(OleDbType.Integer, request.RecordId);
                    using var claims = context.Command("SELECT COUNT(*) FROM [Claims] WHERE [ContinuityId]=?").Add(OleDbType.Integer, request.RecordId);
                    var entityCount = Convert.ToInt32(await entities.ExecuteScalarAsync(token).ConfigureAwait(false));
                    var claimCount = Convert.ToInt32(await claims.ExecuteScalarAsync(token).ConfigureAwait(false));
                    if (entityCount > 0 || claimCount > 0)
                        throw new VaultCommandException("delete.blocked", $"Continuity deletion is blocked by {entityCount} canon entities and {claimCount} claims.");
                }
                if (deleted && request.RecordType == VaultRecordType.VariantGroup)
                {
                    using var members = context.Command("SELECT COUNT(*) FROM [CanonEntities] WHERE [VariantGroupId]=?").Add(OleDbType.Integer, request.RecordId);
                    var memberCount = Convert.ToInt32(await members.ExecuteScalarAsync(token).ConfigureAwait(false));
                    if (memberCount > 0)
                        throw new VaultCommandException("delete.blocked", $"Variant-group deletion is blocked by {memberCount} assigned canon entities.");
                }
                if (deleted && request.RecordType == VaultRecordType.RelationshipType)
                {
                    using var relationships = context.Command("SELECT COUNT(*) FROM [CharacterRelationships] WHERE [RelationshipTypeId]=?").Add(OleDbType.Integer, request.RecordId);
                    var relationshipCount = Convert.ToInt32(await relationships.ExecuteScalarAsync(token).ConfigureAwait(false));
                    if (relationshipCount > 0)
                        throw new VaultCommandException("delete.blocked", $"Relationship-type deletion is blocked by {relationshipCount} character relationships.");
                }
                if (deleted && request.RecordType == VaultRecordType.OwnershipPrincipal)
                {
                    using var owners = context.Command("SELECT COUNT(*) FROM [ObjectOwnershipOwners] WHERE [PrincipalId]=?").Add(OleDbType.Integer, request.RecordId);
                    using var custody = context.Command("SELECT COUNT(*) FROM [ObjectCustodyPeriods] WHERE [PrincipalId]=?").Add(OleDbType.Integer, request.RecordId);
                    var ownerCount = Convert.ToInt32(await owners.ExecuteScalarAsync(token).ConfigureAwait(false));
                    var custodyCount = Convert.ToInt32(await custody.ExecuteScalarAsync(token).ConfigureAwait(false));
                    if (ownerCount > 0 || custodyCount > 0)
                        throw new VaultCommandException("delete.blocked", $"Ownership-principal deletion is blocked by {ownerCount} ownership links and {custodyCount} custody periods.");
                }
                using var update = context.Command($"UPDATE [{table}] SET [IsDeleted]=?,[DeletedAtUtc]=?,[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=? AND [IsDeleted]=?")
                    .Add(OleDbType.Boolean, deleted).Add(OleDbType.Date, deleted ? DateTime.UtcNow : null)
                    .Add(OleDbType.VarWChar, deleted ? context.OperationId : null, 36).Add(OleDbType.Date, DateTime.UtcNow)
                    .Add(OleDbType.Integer, request.RecordId).Add(OleDbType.Integer, request.ExpectedVersion).Add(OleDbType.Boolean, !deleted);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, request.RecordType.ToString(), request.RecordId, table, "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome(request.RecordType.ToString(), request.RecordId.ToString(), request.ExpectedVersion + 1,
                    deleted ? "soft-delete" : "restore", new { deleted }, request.ExpectedVersion);
            }, cancellationToken);
    }
}
