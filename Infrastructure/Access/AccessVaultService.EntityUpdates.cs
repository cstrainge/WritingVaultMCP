using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Versioned canon-entity patching.</summary>
public sealed partial class AccessVaultService
{
    public Task<VaultMutationResult> PatchEntityAsync(PatchEntityRequest request, CancellationToken cancellationToken = default)
    {
        var fields = new[]
        {
            request.Name?.Specified == true, request.Description?.Specified == true,
            request.SecondaryType?.Specified == true, request.TimeZoneId?.Specified == true,
            request.Birth?.Specified == true, request.Death?.Specified == true,
            request.Occurred?.Specified == true, request.BirthLocationId?.Specified == true,
            request.BirthLocationDetail?.Specified == true, request.NarrativeOrder?.Specified == true,
            request.MiddleNames?.Specified == true, request.FamilyName?.Specified == true,
            request.PreferredName?.Specified == true, request.Gender?.Specified == true,
            request.Pronouns?.Specified == true, request.Species?.Specified == true,
            request.Occupation?.Specified == true, request.Nationality?.Specified == true,
            request.PhysicalDescription?.Specified == true, request.PersonalitySummary?.Specified == true, request.BirthdayRecurring?.Specified == true
        };
        if (!fields.Any(value => value)) return Task.FromResult(new VaultMutationResult(false, "patch.empty", Message: "At least one field must be specified."));
        if (request.Name?.Specified == true && string.IsNullOrWhiteSpace(request.Name.Value)) return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: "Name cannot be null or blank."));
        if (request.Species?.Specified == true && request.SecondaryType?.Specified == true)
            return Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: "Specify either Species or SecondaryType for a character patch, not both."));
        if (request.PersonalitySummary?.Specified == true && request.Description?.Specified == true)
            return Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: "Specify either PersonalitySummary or Description for a character patch, not both."));
        if (request.TimeZoneId?.Specified == true && request.TimeZoneId.Value is not null)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId.Value); }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            { return Task.FromResult(new VaultMutationResult(false, "validation.timezone", Message: exception.Message)); }
        }
        try
        {
            if (request.Birth?.Specified == true) ValidateStoryDate(request.Birth.Value ?? StoryDate.Unknown());
            if (request.Death?.Specified == true) ValidateStoryDate(request.Death.Value ?? StoryDate.Unknown());
            if (request.Occurred?.Specified == true) ValidateStoryDate(request.Occurred.Value ?? StoryDate.Unknown());
            if (request.NarrativeOrder?.Specified == true && request.NarrativeOrder.Value is { } order && !double.IsFinite(order))
                throw new ArgumentException("NarrativeOrder must be a finite number.");
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.story_date", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "entity.patch", request, "entity_patch", request.ClientLabel,
            async (context, token) =>
            {
                var entity = await RequireEntityAsync(context, request.EntityId, null, null, token).ConfigureAwait(false);
                var (table, nameColumn, descriptionColumn, typeColumn) = entity.Type switch
                {
                    CanonEntityType.Project => ("Projects", "Name", "Description", (string?)null),
                    CanonEntityType.Location => ("Locations", "Name", "Description", "LocationType"),
                    CanonEntityType.Character => ("Characters", "GivenName", "PersonalitySummary", "Species"),
                    CanonEntityType.Organization => ("Organizations", "Name", "Description", "OrganizationType"),
                    CanonEntityType.Object => ("Objects", "Name", "Description", "ObjectType"),
                    CanonEntityType.WorldEvent => ("WorldEvents", "Title", "Description", (string?)null),
                    _ => throw new ArgumentOutOfRangeException()
                };
                var assignments = new List<string>();
                var parameters = new List<(OleDbType Type, object? Value, int? Size)>();
                if (request.Name?.Specified == true) { assignments.Add($"[{nameColumn}]=?"); parameters.Add((OleDbType.VarWChar, request.Name.Value!.Trim(), entity.Type == CanonEntityType.Character ? 100 : 255)); }
                if (request.Description?.Specified == true) { assignments.Add($"[{descriptionColumn}]=?"); parameters.Add((OleDbType.LongVarWChar, request.Description.Value, null)); }
                if (request.SecondaryType?.Specified == true)
                {
                    if (typeColumn is null) throw new VaultCommandException("patch.unsupported_field", $"SecondaryType is not supported for {entity.Type}.");
                    assignments.Add($"[{typeColumn}]=?"); parameters.Add((OleDbType.VarWChar, request.SecondaryType.Value, 100));
                }
                if (request.TimeZoneId?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.Location) throw new VaultCommandException("patch.unsupported_field", "TimeZoneId is supported only for locations.");
                    assignments.Add("[TimeZoneId]=?"); parameters.Add((OleDbType.VarWChar, request.TimeZoneId.Value, 100));
                }
                void AddDatePatch(string prefix, StoryDate value)
                {
                    foreach (var column in new[] { "Kind", "LowerBound", "UpperBound", "LowerInclusive", "UpperInclusive", "OriginalText", "CalendarId" })
                        assignments.Add($"[{prefix}{column}]=?");
                    parameters.Add((OleDbType.VarWChar, value.Kind.ToString(), 30));
                    parameters.Add((OleDbType.Date, value.LowerBound, null));
                    parameters.Add((OleDbType.Date, value.UpperBound, null));
                    parameters.Add((OleDbType.Boolean, value.LowerInclusive, null));
                    parameters.Add((OleDbType.Boolean, value.UpperInclusive, null));
                    parameters.Add((OleDbType.LongVarWChar, value.OriginalText, null));
                    parameters.Add((OleDbType.VarWChar, value.CalendarId, 50));
                }
                if (request.Birth?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.Character) throw new VaultCommandException("patch.unsupported_field", "Birth is supported only for characters.");
                    AddDatePatch("Birth", request.Birth.Value ?? StoryDate.Unknown());
                }
                if (request.Death?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.Character) throw new VaultCommandException("patch.unsupported_field", "Death is supported only for characters.");
                    AddDatePatch("Death", request.Death.Value ?? StoryDate.Unknown());
                }
                if (request.Occurred?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.WorldEvent) throw new VaultCommandException("patch.unsupported_field", "Occurred is supported only for world events.");
                    AddDatePatch("Event", request.Occurred.Value ?? StoryDate.Unknown());
                }
                if (request.BirthLocationId?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.Character) throw new VaultCommandException("patch.unsupported_field", "BirthLocationId is supported only for characters.");
                    if (request.BirthLocationId.Value is { } locationId)
                        await RequireEntityAsync(context, locationId, CanonEntityType.Location, entity.ContinuityId, token).ConfigureAwait(false);
                    assignments.Add("[BirthLocationId]=?"); parameters.Add((OleDbType.Integer, request.BirthLocationId.Value, null));
                }
                if (request.BirthLocationDetail?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.Character) throw new VaultCommandException("patch.unsupported_field", "BirthLocationDetail is supported only for characters.");
                    assignments.Add("[BirthLocationDetail]=?"); parameters.Add((OleDbType.VarWChar, request.BirthLocationDetail.Value, 255));
                }
                if (request.NarrativeOrder?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.WorldEvent) throw new VaultCommandException("patch.unsupported_field", "NarrativeOrder is supported only for world events.");
                    assignments.Add("[NarrativeOrder]=?"); parameters.Add((OleDbType.Double, request.NarrativeOrder.Value, null));
                }
                if (request.BirthdayRecurring?.Specified == true)
                {
                    if (entity.Type != CanonEntityType.Character) throw new VaultCommandException("patch.unsupported_field", "BirthdayRecurring is supported only for characters.");
                    assignments.Add("[BirthdayRecurring]=?"); parameters.Add((OleDbType.Boolean, request.BirthdayRecurring.Value, null));
                }
                void AddCharacterField(PatchField<string>? field, string column, OleDbType type, int? size)
                {
                    if (field?.Specified != true) return;
                    if (entity.Type != CanonEntityType.Character)
                        throw new VaultCommandException("patch.unsupported_field", $"{column} is supported only for characters.");
                    assignments.Add($"[{column}]=?"); parameters.Add((type, field.Value, size));
                }
                AddCharacterField(request.MiddleNames, "MiddleNames", OleDbType.VarWChar, 255);
                AddCharacterField(request.FamilyName, "FamilyName", OleDbType.VarWChar, 100);
                AddCharacterField(request.PreferredName, "PreferredName", OleDbType.VarWChar, 100);
                AddCharacterField(request.Gender, "Gender", OleDbType.VarWChar, 100);
                AddCharacterField(request.Pronouns, "Pronouns", OleDbType.VarWChar, 100);
                AddCharacterField(request.Species, "Species", OleDbType.VarWChar, 100);
                AddCharacterField(request.Occupation, "Occupation", OleDbType.VarWChar, 255);
                AddCharacterField(request.Nationality, "Nationality", OleDbType.VarWChar, 100);
                AddCharacterField(request.PhysicalDescription, "PhysicalDescription", OleDbType.LongVarWChar, null);
                AddCharacterField(request.PersonalitySummary, "PersonalitySummary", OleDbType.LongVarWChar, null);
                using (var update = context.Command($"UPDATE [{table}] SET {string.Join(',', assignments)} WHERE [EntityId]=?"))
                {
                    foreach (var parameter in parameters) update.Add(parameter.Type, parameter.Value, parameter.Size);
                    update.Add(OleDbType.Integer, request.EntityId);
                    await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                using var bump = context.Command("UPDATE [CanonEntities] SET [UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                    .Add(OleDbType.Date, DateTime.UtcNow).Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await bump.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, entity.Type.ToString(), request.EntityId, "CanonEntities", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome(entity.Type.ToString(), request.EntityId.ToString(), request.ExpectedVersion + 1, "patch", request, request.ExpectedVersion);
            }, cancellationToken);
    }

}
