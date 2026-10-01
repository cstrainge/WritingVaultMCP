using System.Data.OleDb;
using System.Globalization;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4RecordService(
    VaultWriteCoordinator coordinator,
    VaultReferenceService references,
    VaultSessionContext session,
    AccessVaultService vault)
{
    private static readonly IReadOnlyDictionary<string, string> RelationshipNoteTables =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CharacterRelationship"] = "CharacterRelationships",
            ["CharacterResidence"] = "CharacterResidences",
            ["OrganizationMembership"] = "OrganizationMemberships",
            ["OrganizationLocation"] = "OrganizationLocations",
            ["ObjectOwnershipPeriod"] = "ObjectOwnershipPeriods",
            ["ObjectCustodyPeriod"] = "ObjectCustodyPeriods",
            ["ObjectLocationPeriod"] = "ObjectLocationPeriods",
            ["WorldEventParticipant"] = "WorldEventParticipants",
            ["WorldEventLocation"] = "WorldEventLocations"
        };

    public async Task<VaultMutationResult> UpdateRelationshipMembershipPeriodAsync(
        V4RelationshipMembershipPeriodUpdateRequest request, StoryDate period,
        CancellationToken token = default)
    {
        if (period is null || period.Validate().Count > 0)
            return new(false, "validation.period", Message: "The membership period is invalid.");
        if (request.Notes?.Length > V4ContractLimits.MaximumLongTextLength)
            return new(false, "validation.notes", Message: "Membership notes are too long.");
        if (request.ClearNotes && request.Notes is not null)
            return new(false, "validation.notes", Message: "Provide notes or clearNotes, not both.");
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference record;
        try
        {
            record = await references.ResolveAsync(request.PeriodRef, continuity, token,
                "RelationshipMembershipPeriod").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return new(false, "record.not_found", Message: exception.Message);
        }
        if (record.IsDeleted)
            return new(false, "record.deleted", Message: "Restore the membership period and its parents before editing.");
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception)
        {
            return new(false, "validation.mutation_token", Message: exception.Message);
        }
        return await coordinator.ExecuteAsync(operation, "v4.relationship.period.update", request,
            "relationship_membership_period_update", session.ClientLabel, async (context, ct) =>
        {
            using var current = context.Command(
                "SELECT m.[ParticipantId],p.[RelationshipId],m.[Version],m.[IsDeleted]," +
                "p.[IsDeleted],r.[IsDeleted],m.[Notes] FROM ([RelationshipMembershipPeriods] AS m " +
                "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) " +
                "INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id] " +
                "WHERE m.[Id]=? AND r.[ContinuityId]=?")
                .Add(OleDbType.Integer, record.Id).Add(OleDbType.Integer, continuity);
            var rows = await current.QueryAsync(reader => new
            {
                Participant = reader.GetInt32(0), Relationship = reader.GetInt32(1),
                Version = reader.GetInt32(2), Deleted = reader.GetBoolean(3) ||
                    reader.GetBoolean(4) || reader.GetBoolean(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6)
            }, ct);
            if (rows.Count != 1 || rows[0].Deleted)
                throw new VaultCommandException("record.not_found", "The membership period is unavailable.");
            if (rows[0].Version != request.ExpectedVersion)
                throw new VaultCommandException("concurrency.conflict",
                    "The membership period changed; read it again before retrying.",
                    actualVersion: rows[0].Version);
            using var siblings = context.Command(
                "SELECT [PeriodKind],[PeriodLowerBound],[PeriodUpperBound]," +
                "[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] " +
                "FROM [RelationshipMembershipPeriods] " +
                "WHERE [ParticipantId]=? AND [Id]<>? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, rows[0].Participant).Add(OleDbType.Integer, record.Id);
            var others = await siblings.QueryAsync(reader => new StoryDate(
                Enum.Parse<StoryDateKind>(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.GetBoolean(3), reader.GetBoolean(4),
                CalendarId: reader.GetString(5)), ct);
            if (others.Any(other => other.Overlaps(period)))
                throw new VaultCommandException("interval.overlap",
                    "The corrected period may overlap another active period.");
            var now = DateTime.UtcNow;
            var notes = request.ClearNotes ? null : request.Notes ?? rows[0].Notes;
            using var update = context.Command(
                "UPDATE [RelationshipMembershipPeriods] SET [Notes]=?,[PeriodKind]=?," +
                "[PeriodLowerBound]=?,[PeriodUpperBound]=?,[PeriodLowerInclusive]=?," +
                "[PeriodUpperInclusive]=?,[PeriodOriginalText]=?,[PeriodCalendarId]=?," +
                "[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.LongVarWChar, notes)
                .Add(OleDbType.VarWChar, period.Kind.ToString(), 30)
                .Add(OleDbType.Date, period.LowerBound)
                .Add(OleDbType.Date, period.UpperBound)
                .Add(OleDbType.Boolean, period.LowerInclusive)
                .Add(OleDbType.Boolean, period.UpperInclusive)
                .Add(OleDbType.VarWChar, period.OriginalText, 255)
                .Add(OleDbType.VarWChar, period.CalendarId, 50)
                .Add(OleDbType.Date, now)
                .Add(OleDbType.Integer, record.Id)
                .Add(OleDbType.Integer, rows[0].Version);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new VaultCommandException("concurrency.conflict", "The membership period changed.");
            // A whole-period correction supersedes any independently authored
            // transitions. Keep those rows archived for audit; a later
            // transitions-set call can restore the same pair of rows.
            using var archiveTransitions = context.Command(
                "UPDATE [RelationshipMembershipTransitions] SET [IsDeleted]=True," +
                "[DeletedAtUtc]=?,[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [MembershipPeriodId]=? AND [IsDeleted]=False")
                .Add(OleDbType.Date, now).Add(OleDbType.VarWChar, context.OperationId, 36)
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, record.Id);
            await archiveTransitions.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            using var parent = context.Command(
                "UPDATE [CharacterRelationships] SET [UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [Id]=? AND [IsDeleted]=False")
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, rows[0].Relationship);
            if (await parent.ExecuteNonQueryAsync(ct) != 1)
                throw new VaultCommandException("record.not_found", "The relationship is unavailable.");
            return new VaultMutationOutcome("RelationshipMembershipPeriod", record.Id.ToString(),
                rows[0].Version + 1, "update", new { period, notesPresent = notes is not null },
                rows[0].Version);
        }, token).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> SetRelationshipNotesAsync(
        V4RelationshipNotesSetRequest request, CancellationToken token = default)
    {
        if (request.Notes?.Length > V4ContractLimits.MaximumLongTextLength)
            return new(false, "validation.notes", Message: $"Relationship notes may contain at most {V4ContractLimits.MaximumLongTextLength:N0} characters.");

        var continuity = session.RequireContinuityId();
        ResolvedVaultReference relationship;
        try
        {
            relationship = await references.ResolveAsync(request.RelationshipRef, continuity, token,
                RelationshipNoteTables.Keys.ToArray()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return new(false, "record.not_found", Message: exception.Message);
        }

        var table = RelationshipNoteTables[relationship.ResourceType];
        return await PatchAsync(request.MutationToken, relationship, request.ExpectedVersion, request,
            "relationship_notes_set", async (context, current, ct) =>
        {
            using var update = context.Command(
                    $"UPDATE [{table}] SET [Notes]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.LongVarWChar, request.Notes)
                .Add(OleDbType.Date, DateTime.UtcNow)
                .Add(OleDbType.Integer, relationship.Id)
                .Add(OleDbType.Integer, current);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new { notesPresent = request.Notes is not null, format = "markdown" };
        }, token).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> UpdateNoteAsync(
        V4NoteUpdateRequest request, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference note;
        try
        {
            note = await references.ResolveAsync(request.NoteRef, continuity, token,
                "EntityNote", "ContinuityNote").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return new(false, "record.not_found", Message: exception.Message);
        }

        var allowed = new HashSet<string>(["title", "body", "format"], StringComparer.OrdinalIgnoreCase);
        try { V4SparseChangeValidator.Validate(request.Changes, allowed); }
        catch (VaultValidationException exception)
        {
            return new(false, exception.Errors[0].Code, Message: exception.Message);
        }
        if (request.Changes.TryGetValue("format", out var format) &&
            !format.GetString()!.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            return new(false, "validation.format", Message: "Only markdown notes are supported.");

        return await PatchAsync(request.MutationToken, note, request.ExpectedVersion, request,
            "note_update", async (context, current, ct) =>
        {
            var table = note.ResourceType == "ContinuityNote" ? "ContinuityNotes" : "EntityNotes";
            var title = await ScalarText(context, $"SELECT [Title] FROM [{table}] WHERE [Id]=?", note.Id, ct);
            var body = await ScalarText(context, $"SELECT [Body] FROM [{table}] WHERE [Id]=?", note.Id, ct) ?? "";
            title = Text(request.Changes, "title", title);
            body = Text(request.Changes, "body", body) ??
                throw new VaultCommandException("validation.body", "Body cannot be null.");
            using var update = context.Command(
                    $"UPDATE [{table}] SET [Title]=?,[Body]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.VarWChar, title, 255)
                .Add(OleDbType.LongVarWChar, body)
                .Add(OleDbType.Date, DateTime.UtcNow)
                .Add(OleDbType.Integer, note.Id)
                .Add(OleDbType.Integer, current);
            await update.ExecuteNonQueryAsync(ct);
            return new { title, format = "markdown" };
        }, token);
    }

    public async Task<VaultMutationResult> UpdateClaimAsync(
        V4ClaimUpdateRequest request, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference claim;
        try
        {
            claim = await references.ResolveAsync(
                request.ClaimRef, continuity, token, "Claim").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return new(false, "record.not_found", Message: exception.Message);
        }

        var allowed = new HashSet<string>(
            ["claimText", "status", "confidence", "targetField", "commentary"],
            StringComparer.OrdinalIgnoreCase);
        try { V4SparseChangeValidator.Validate(request.Changes, allowed); }
        catch (VaultValidationException exception)
        {
            return new(false, exception.Errors[0].Code, Message: exception.Message);
        }

        return await PatchAsync(request.MutationToken, claim, request.ExpectedVersion, request,
            "claim_update", async (context, current, ct) =>
        {
            using var read = context.Command(
                    "SELECT [ClaimText],[ClaimStatus],[Confidence],[TargetField],[Commentary] " +
                    "FROM [Claims] WHERE [Id]=?")
                .Add(OleDbType.Integer, claim.Id);
            var rows = await read.QueryAsync(reader => new
            {
                Text = reader.GetString(0),
                Status = reader.GetString(1),
                Confidence = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                Target = reader.IsDBNull(3) ? null : reader.GetString(3),
                Comment = reader.IsDBNull(4) ? null : reader.GetString(4)
            }, ct);
            var old = rows.Single();

            var claimText = Text(request.Changes, "claimText", old.Text) ??
                throw new VaultCommandException("validation.claim", "Claim text cannot be null.");
            var status = Text(request.Changes, "status", old.Status) ??
                throw new VaultCommandException("validation.status", "Status cannot be null.");
            var confidence = request.Changes.TryGetValue("confidence", out var value)
                ? value.ValueKind == JsonValueKind.Null ? null : value.GetDouble()
                : old.Confidence;
            if (confidence is < 0 or > 1)
                throw new VaultCommandException("validation.confidence", "Confidence must be from 0 through 1.");
            var target = Text(request.Changes, "targetField", old.Target);
            var comment = Text(request.Changes, "commentary", old.Comment);

            using var update = context.Command(
                    "UPDATE [Claims] SET [ClaimText]=?,[ClaimStatus]=?,[Confidence]=?," +
                    "[TargetField]=?,[Commentary]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                    "WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.LongVarWChar, claimText)
                .Add(OleDbType.VarWChar, status, 50)
                .Add(OleDbType.Double, confidence)
                .Add(OleDbType.VarWChar, target, 100)
                .Add(OleDbType.LongVarWChar, comment)
                .Add(OleDbType.Date, DateTime.UtcNow)
                .Add(OleDbType.Integer, claim.Id)
                .Add(OleDbType.Integer, current);
            await update.ExecuteNonQueryAsync(ct);
            return new { claimText, status, confidence, targetField = target };
        }, token);
    }

    public async Task<VaultMutationResult> LifecycleAsync(
        V4VersionedRecordRequest request, bool restore, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference record;
        try
        {
            record = await references.ResolveAsync(request.Ref, continuity, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            return new(false, "record.not_found", Message: exception.Message);
        }

        var operation = references.OperationId(request.MutationToken);
        if (record.ResourceType == "StoryImage")
            return await LifecycleStoryImageAsync(record, request, restore, operation, continuity, token)
                .ConfigureAwait(false);
        if (Enum.TryParse<CanonEntityType>(record.ResourceType, true, out _))
        {
            if (!restore)
            {
                var preview = await vault.PreviewDeleteAsync(record.Id, token).ConfigureAwait(false);
                if (preview is null)
                    return new(false, "record.not_found", Message: "The record was not found.");
                if (preview.Blockers.Count > 0)
                    return new(false, "delete.blocked", Message: "Delete or detach dependent records first: " +
                        string.Join(", ", preview.Blockers.Select(blocker =>
                            $"{blocker.ResourceType} ({blocker.Count})")) + ".");
            }

            var input = new VersionedEntityRequest(
                operation, record.Id, request.ExpectedVersion, session.ClientLabel);
            return restore
                ? await vault.RestoreEntityAsync(input, token)
                : await vault.SoftDeleteEntityAsync(input, token);
        }
        if (Enum.TryParse<VaultRecordType>(record.ResourceType, true, out var metadataType))
        {
            if (metadataType == VaultRecordType.Continuity && record.Id != continuity)
                return new(false, "scope.mismatch", Message:
                    "The continuity must match this connection's selected continuity.");
            var input = new VersionedVaultRecordRequest(
                operation, metadataType, record.Id, request.ExpectedVersion, session.ClientLabel);
            return restore
                ? await vault.RestoreVaultRecordAsync(input, token)
                : await vault.SoftDeleteVaultRecordAsync(input, token);
        }
        if (Enum.TryParse<RelationshipRecordType>(record.ResourceType, true, out var relationshipType) &&
            record.ResourceType is not ("CharacterTemporalEffect" or "EntityImage" or "ContinuityNote"))
        {
            var input = new VersionedRelationshipRequest(
                operation, relationshipType, record.Id, request.ExpectedVersion, session.ClientLabel);
            return restore
                ? await vault.RestoreRelationshipAsync(input, token)
                : await vault.SoftDeleteRelationshipAsync(input, token);
        }

        var tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EntityNote"] = "EntityNotes",
            ["ContinuityNote"] = "ContinuityNotes",
            ["EntityEvent"] = "EntityEvents",
            ["RelationshipEvent"] = "RelationshipEvents",
            ["RelationshipMembershipPeriod"] = "RelationshipMembershipPeriods",
            ["RelationshipParticipant"] = "RelationshipParticipants",
            ["Claim"] = "Claims",
            ["CharacterTemporalEffect"] = "CharacterTemporalEffects",
            ["EntityImage"] = "EntityImages",
            ["CharacterRelationship"] = "CharacterRelationships",
            ["CharacterResidence"] = "CharacterResidences",
            ["OrganizationMembership"] = "OrganizationMemberships",
            ["OrganizationLocation"] = "OrganizationLocations",
            ["ObjectOwnershipPeriod"] = "ObjectOwnershipPeriods",
            ["ObjectCustodyPeriod"] = "ObjectCustodyPeriods",
            ["ObjectLocationPeriod"] = "ObjectLocationPeriods",
            ["WorldEventParticipant"] = "WorldEventParticipants",
            ["WorldEventLocation"] = "WorldEventLocations",
            ["SourceSnapshot"] = "SourceSnapshots"
        };
        if (!tables.TryGetValue(record.ResourceType, out var table))
            return new(false, "record.lifecycle_unsupported", Message:
                "This record kind cannot be deleted or restored through record lifecycle.");

        return await coordinator.ExecuteAsync(operation, "v4.record.lifecycle", request,
            restore ? "record_restore" : "record_soft_delete", session.ClientLabel,
            async (context, ct) =>
        {
            var isImage = record.ResourceType == "EntityImage";
            using var read = context.Command(
                    $"SELECT [Version],[IsDeleted]" +
                    (isImage ? ",[EntityId],[IsPrimary]" : "") +
                    $" FROM [{table}] WHERE [Id]=?")
                .Add(OleDbType.Integer, record.Id);
            var rows = await read.QueryAsync(reader => new
            {
                Version = reader.GetInt32(0),
                Deleted = reader.GetBoolean(1),
                Owner = isImage ? reader.GetInt32(2) : 0,
                Primary = isImage && reader.GetBoolean(3)
            }, ct);
            if (rows.Count != 1)
                throw new VaultCommandException("record.not_found", "The record was not found.");
            var old = rows[0];
            if (old.Version != request.ExpectedVersion)
                throw new VaultCommandException("concurrency.conflict",
                    $"The current version is {old.Version}.", actualVersion: old.Version);
            if (old.Deleted == !restore)
                return new VaultMutationOutcome(record.ResourceType, record.Id.ToString(),
                    old.Version, restore ? "restore" : "delete", new { unchanged = true });

            if (record.ResourceType == "RelationshipParticipant")
            {
                using var participant = context.Command(
                    "SELECT p.[RelationshipId],r.[IsDeleted],c.[IsDeleted]," +
                    "(SELECT COUNT(*) FROM [RelationshipParticipants] AS other " +
                    "WHERE other.[RelationshipId]=p.[RelationshipId] AND other.[IsDeleted]=False) " +
                    "FROM ([RelationshipParticipants] AS p INNER JOIN [CharacterRelationships] AS r " +
                    "ON p.[RelationshipId]=r.[Id]) INNER JOIN [CanonEntities] AS c " +
                    "ON p.[CharacterId]=c.[Id] WHERE p.[Id]=?")
                    .Add(OleDbType.Integer, record.Id);
                var participantRows = await participant.QueryAsync(reader => new
                {
                    Relationship = reader.GetInt32(0), ParentDeleted = reader.GetBoolean(1),
                    CharacterDeleted = reader.GetBoolean(2), ActiveCount = Convert.ToInt32(reader.GetValue(3))
                }, ct);
                if (participantRows.Count != 1)
                    throw new VaultCommandException("record.not_found", "The participant is unavailable.");
                if (restore && (participantRows[0].ParentDeleted || participantRows[0].CharacterDeleted))
                    throw new VaultCommandException("restore.parent_deleted",
                        "Restore the relationship and character before restoring participation.");
                if (restore && participantRows[0].ActiveCount >= 100)
                    throw new VaultCommandException("validation.participants",
                        "A relationship can have at most 100 active characters.");
                if (!restore && participantRows[0].ActiveCount <= 2)
                    throw new VaultCommandException("delete.blocked",
                        "A relationship requires at least two active characters.");
            }

            if (restore && record.ResourceType == "RelationshipEvent")
            {
                using var parent = context.Command(
                    "SELECT COUNT(*) FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r " +
                    "ON e.[RelationshipId]=r.[Id] WHERE e.[Id]=? AND r.[IsDeleted]=False")
                    .Add(OleDbType.Integer, record.Id);
                if (Convert.ToInt32(await parent.ExecuteScalarAsync(ct)) != 1)
                    throw new VaultCommandException("restore.parent_deleted",
                        "Restore the relationship before restoring its event.");
            }
            if (restore && record.ResourceType == "RelationshipMembershipPeriod")
            {
                using var parent = context.Command(
                    "SELECT COUNT(*) FROM ([RelationshipMembershipPeriods] AS m " +
                    "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) " +
                    "INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id] " +
                    "WHERE m.[Id]=? AND p.[IsDeleted]=False AND r.[IsDeleted]=False")
                    .Add(OleDbType.Integer, record.Id);
                if (Convert.ToInt32(await parent.ExecuteScalarAsync(ct)) != 1)
                    throw new VaultCommandException("restore.parent_deleted",
                        "Restore the participant and relationship before restoring this period.");
                using var candidate = context.Command(
                    "SELECT [ParticipantId],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound]," +
                    "[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] " +
                    "FROM [RelationshipMembershipPeriods] WHERE [Id]=?")
                    .Add(OleDbType.Integer, record.Id);
                var candidateRows = await candidate.QueryAsync(reader => new
                {
                    Participant = reader.GetInt32(0),
                    Date = new StoryDate(Enum.Parse<StoryDateKind>(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                        reader.GetBoolean(4), reader.GetBoolean(5),
                        CalendarId: reader.GetString(6))
                }, ct);
                using var siblings = context.Command(
                    "SELECT [PeriodKind],[PeriodLowerBound],[PeriodUpperBound]," +
                    "[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] " +
                    "FROM [RelationshipMembershipPeriods] " +
                    "WHERE [ParticipantId]=? AND [Id]<>? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, candidateRows[0].Participant)
                    .Add(OleDbType.Integer, record.Id);
                var otherDates = await siblings.QueryAsync(reader => new StoryDate(
                    Enum.Parse<StoryDateKind>(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.GetBoolean(3), reader.GetBoolean(4),
                    CalendarId: reader.GetString(5)), ct);
                if (otherDates.Any(date => date.Overlaps(candidateRows[0].Date)))
                    throw new VaultCommandException("interval.overlap",
                        "The restored membership period may overlap another active period.");
            }

            var now = DateTime.UtcNow;
            var sql = restore
                ? $"UPDATE [{table}] SET [IsDeleted]=False,[DeletedAtUtc]=Null," +
                  "[DeletedOperationId]=Null,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                  "WHERE [Id]=? AND [Version]=?"
                : $"UPDATE [{table}] SET [IsDeleted]=True,[DeletedAtUtc]=?," +
                  "[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1" +
                  (isImage ? ",[IsPrimary]=False" : "") + " WHERE [Id]=? AND [Version]=?";
            using var update = context.Command(sql);
            if (restore)
                update.Add(OleDbType.Date, now);
            else
                update.Add(OleDbType.Date, now)
                    .Add(OleDbType.VarWChar, context.OperationId, 36)
                    .Add(OleDbType.Date, now);
            update.Add(OleDbType.Integer, record.Id)
                .Add(OleDbType.Integer, old.Version);
            await update.ExecuteNonQueryAsync(ct);

            if (record.ResourceType is "RelationshipParticipant" or "RelationshipMembershipPeriod")
            {
                var relationshipSql = record.ResourceType == "RelationshipParticipant"
                    ? "SELECT [RelationshipId] FROM [RelationshipParticipants] WHERE [Id]=?"
                    : "SELECT p.[RelationshipId] FROM [RelationshipMembershipPeriods] AS m " +
                      "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] " +
                      "WHERE m.[Id]=?";
                using var relationship = context.Command(relationshipSql).Add(OleDbType.Integer, record.Id);
                var parentId = Convert.ToInt32(await relationship.ExecuteScalarAsync(ct));
                using var bump = context.Command(
                    "UPDATE [CharacterRelationships] SET [UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                    "WHERE [Id]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Date, now).Add(OleDbType.Integer, parentId);
                if (await bump.ExecuteNonQueryAsync(ct) != 1)
                    throw new VaultCommandException("record.not_found", "The relationship is unavailable.");
            }

            if (isImage)
                await EnsureImagePrimaryAsync(context, old.Owner, now, ct);

            return new VaultMutationOutcome(record.ResourceType, record.Id.ToString(),
                old.Version + 1, restore ? "restore" : "delete",
                new { isDeleted = !restore }, old.Version);
        }, token);
    }

    private static async Task EnsureImagePrimaryAsync(
        VaultWriteContext context, int owner, DateTime now, CancellationToken token)
    {
        using var primary = context.Command(
                "SELECT COUNT(*) FROM [EntityImages] " +
                "WHERE [EntityId]=? AND [IsDeleted]=False AND [IsPrimary]=True")
            .Add(OleDbType.Integer, owner);
        var hasPrimary = Convert.ToInt32(await primary.ExecuteScalarAsync(token)) > 0;
        if (hasPrimary) return;

        using var next = context.Command(
                "SELECT TOP 1 [Id] FROM [EntityImages] " +
                "WHERE [EntityId]=? AND [IsDeleted]=False ORDER BY [Id]")
            .Add(OleDbType.Integer, owner);
        var nextId = await next.ExecuteScalarAsync(token);
        if (nextId is null or DBNull) return;

        using var choose = context.Command(
                "UPDATE [EntityImages] SET [IsPrimary]=True,[UpdatedAtUtc]=?," +
                "[Version]=[Version]+1 WHERE [Id]=?")
            .Add(OleDbType.Date, now)
            .Add(OleDbType.Integer, Convert.ToInt32(nextId, CultureInfo.InvariantCulture));
        await choose.ExecuteNonQueryAsync(token);
    }

    private async Task<VaultMutationResult> PatchAsync<T>(
        string mutation, ResolvedVaultReference record, int expected, T input,
        string tool, Func<VaultWriteContext, int, CancellationToken, Task<object>> apply,
        CancellationToken token)
    {
        var operation = references.OperationId(mutation);
        return await coordinator.ExecuteAsync(operation, "v4." + tool, input!, tool,
            session.ClientLabel, async (context, ct) =>
        {
            var table = record.ResourceType switch
            {
                "EntityNote" => "EntityNotes",
                "ContinuityNote" => "ContinuityNotes",
                "Claim" => "Claims",
                _ => RelationshipNoteTables.TryGetValue(record.ResourceType, out var relationshipTable)
                    ? relationshipTable
                    : throw new InvalidOperationException("This record type does not support versioned notes.")
            };
            using var version = context.Command(
                    $"SELECT [Version] FROM [{table}] WHERE [Id]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, record.Id);
            var value = await version.ExecuteScalarAsync(ct);
            if (value is null or DBNull)
                throw new VaultCommandException("record.not_found", "The record is missing or deleted.");
            var current = Convert.ToInt32(value);
            if (current != expected)
                throw new VaultCommandException("concurrency.conflict",
                    $"The current version is {current}.", actualVersion: current);
            var change = await apply(context, current, ct);
            return new VaultMutationOutcome(record.ResourceType,
                record.Id.ToString(CultureInfo.InvariantCulture), current + 1,
                "update", change, current);
        }, token);
    }

    private static async Task<string?> ScalarText(
        VaultWriteContext context, string sql, int id, CancellationToken token)
    {
        using var query = context.Command(sql).Add(OleDbType.Integer, id);
        var value = await query.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? Text(
        IReadOnlyDictionary<string, JsonElement> changes, string key, string? fallback) =>
        !changes.TryGetValue(key, out var value) ? fallback :
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();
}
