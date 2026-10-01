using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4RecordService
{
    /// <summary>
    /// Stores two independently uncertain transition dates while keeping the
    /// existing period row as a conservative possible-occupancy envelope.
    /// This preserves the period's public reference and existing overlap rules.
    /// </summary>
    public async Task<VaultMutationResult> SetRelationshipMembershipTransitionsAsync(
        V4RelationshipMembershipTransitionsSetRequest request, StoryDate joined, StoryDate left,
        CancellationToken token = default)
    {
        if (joined.Validate().Count > 0 || left.Validate().Count > 0)
            return new(false, "validation.transition", Message: "A join or leave date is invalid.");
        if (!string.Equals(joined.CalendarId, left.CalendarId, StringComparison.OrdinalIgnoreCase))
            return new(false, "validation.calendar", Message: "Join and leave dates must use the same calendar.");
        if (request.Notes?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.ClearNotes && request.Notes is not null)
            return new(false, "validation.notes", Message: "Provide valid notes or clearNotes, not both.");
        if (request.JoinDescription?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.LeaveDescription?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.ClearJoinDescription && request.JoinDescription is not null ||
            request.ClearLeaveDescription && request.LeaveDescription is not null ||
            request.JoinDescription is not null && string.IsNullOrWhiteSpace(request.JoinDescription) ||
            request.LeaveDescription is not null && string.IsNullOrWhiteSpace(request.LeaveDescription))
            return new(false, "validation.description", Message: "Provide nonblank descriptions or clear flags, not both.");
        var possible = PossibleOccupancy(joined, left);
        if (possible is null)
            return new(false, "validation.transition_order",
                Message: "The character cannot leave before every possible join date.");

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
            return new(false, "record.deleted", Message: "Restore the membership period before editing it.");
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception)
        {
            return new(false, "validation.mutation_token", Message: exception.Message);
        }
        return await coordinator.ExecuteAsync(operation, "v4.relationship.transitions.set", request,
            "relationship_membership_transitions_set", session.ClientLabel, async (context, ct) =>
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
            }, ct).ConfigureAwait(false);
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
                CalendarId: reader.GetString(5)), ct).ConfigureAwait(false);
            if (others.Any(other => other.Overlaps(possible)))
                throw new VaultCommandException("interval.overlap",
                    "The possible membership interval overlaps another active period.");

            var now = DateTime.UtcNow;
            var joinId = await SaveTransitionAsync(context, record.Id, "Join", joined, now, ct).ConfigureAwait(false);
            var leaveId = await SaveTransitionAsync(context, record.Id, "Leave", left, now, ct).ConfigureAwait(false);
            await SaveTransitionDescriptionAsync(context, joinId, request.JoinDescription,
                request.ClearJoinDescription, ct).ConfigureAwait(false);
            await SaveTransitionDescriptionAsync(context, leaveId, request.LeaveDescription,
                request.ClearLeaveDescription, ct).ConfigureAwait(false);
            var notes = request.ClearNotes ? null : request.Notes ?? rows[0].Notes;
            using var update = context.Command(
                "UPDATE [RelationshipMembershipPeriods] SET [Notes]=?,[PeriodKind]=?," +
                "[PeriodLowerBound]=?,[PeriodUpperBound]=?,[PeriodLowerInclusive]=?," +
                "[PeriodUpperInclusive]=?,[PeriodOriginalText]=?,[PeriodCalendarId]=?," +
                "[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.LongVarWChar, notes);
            AddDate(update, possible);
            update.Add(OleDbType.Date, now).Add(OleDbType.Integer, record.Id)
                .Add(OleDbType.Integer, rows[0].Version);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new VaultCommandException("concurrency.conflict", "The membership period changed.");
            using var parent = context.Command(
                "UPDATE [CharacterRelationships] SET [UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [Id]=? AND [IsDeleted]=False")
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, rows[0].Relationship);
            if (await parent.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new VaultCommandException("record.not_found", "The relationship is unavailable.");
            return new VaultMutationOutcome("RelationshipMembershipPeriod", record.Id.ToString(),
                rows[0].Version + 1, "transitions-set",
                new { joined, left, possibleOccupancy = possible, notesPresent = notes is not null,
                    request.JoinDescription, request.LeaveDescription,
                    request.ClearJoinDescription, request.ClearLeaveDescription },
                rows[0].Version);
        }, token).ConfigureAwait(false);
    }

    internal static StoryDate? PossibleOccupancy(StoryDate joined, StoryDate left)
    {
        if (joined.Kind == StoryDateKind.ExactDate && left.Kind == StoryDateKind.ExactDate &&
            joined.LowerBound == left.LowerBound) return joined;
        var lower = joined.LowerBound;
        var upper = left.UpperBound;
        if (lower is { } start && upper is { } end &&
            (start > end || start == end && !(joined.LowerInclusive && left.UpperInclusive)))
            return null;
        if (lower is null && upper is null)
            return new(StoryDateKind.Unknown, null, null, CalendarId: joined.CalendarId);
        if (lower is null) return new(StoryDateKind.Before, null, upper,
            UpperInclusive: left.Kind == StoryDateKind.ExactInstant ? false : left.UpperInclusive,
            CalendarId: joined.CalendarId);
        if (upper is null) return new(StoryDateKind.After, lower, null,
            LowerInclusive: joined.LowerInclusive, CalendarId: joined.CalendarId);
        if (lower == upper) return new(StoryDateKind.ExactInstant, lower, upper,
            true, true, CalendarId: joined.CalendarId);
        var meaning = (joined.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant) &&
                      (left.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant)
            ? StoryDateKind.KnownRange : StoryDateKind.UncertainRange;
        return new(meaning, lower, upper,
            joined.LowerInclusive,
            left.Kind == StoryDateKind.ExactInstant ? false : left.UpperInclusive,
            CalendarId: joined.CalendarId);
    }

    internal static async Task<int> SaveTransitionAsync(VaultWriteContext context, int periodId,
        string kind, StoryDate date, DateTime now, CancellationToken token)
    {
        using var find = context.Command(
            "SELECT [Id],[Version] FROM [RelationshipMembershipTransitions] " +
            "WHERE [MembershipPeriodId]=? AND [TransitionKind]=?")
            .Add(OleDbType.Integer, periodId).Add(OleDbType.VarWChar, kind, 10);
        var rows = await find.QueryAsync(reader =>
            (Id: reader.GetInt32(0), Version: reader.GetInt32(1)), token).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            using var insert = context.Command(
                "INSERT INTO [RelationshipMembershipTransitions] ([MembershipPeriodId],[TransitionKind]," +
                "[OccurredKind],[OccurredLowerBound],[OccurredUpperBound],[OccurredLowerInclusive]," +
                "[OccurredUpperInclusive],[OccurredOriginalText],[OccurredCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
                "VALUES (?,?,?,?,?,?,?,?,?,?,?)")
                .Add(OleDbType.Integer, periodId).Add(OleDbType.VarWChar, kind, 10);
            AddDate(insert, date);
            insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            using var identity = context.Command("SELECT @@IDENTITY");
            return Convert.ToInt32(await identity.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        using var update = context.Command(
            "UPDATE [RelationshipMembershipTransitions] SET [OccurredKind]=?,[OccurredLowerBound]=?," +
            "[OccurredUpperBound]=?,[OccurredLowerInclusive]=?,[OccurredUpperInclusive]=?," +
            "[OccurredOriginalText]=?,[OccurredCalendarId]=?,[IsDeleted]=False," +
            "[DeletedAtUtc]=NULL,[DeletedOperationId]=NULL,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
            "WHERE [Id]=? AND [Version]=?");
        AddDate(update, date);
        update.Add(OleDbType.Date, now).Add(OleDbType.Integer, rows[0].Id)
            .Add(OleDbType.Integer, rows[0].Version);
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new VaultCommandException("concurrency.conflict", "A transition changed during the update.");
        return rows[0].Id;
    }

    internal static async Task SaveTransitionDescriptionAsync(VaultWriteContext context,
        int transitionId, string? description, bool clear, CancellationToken token)
    {
        if (description is null && !clear) return;
        if (clear)
        {
            using var delete = context.Command(
                "DELETE FROM [RelationshipTransitionDescriptions] WHERE [TransitionId]=?")
                .Add(OleDbType.Integer, transitionId);
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return;
        }
        var value = description!.Trim();
        if (value.Length == 0) throw new VaultCommandException("validation.description", "A description cannot be blank.");
        using var update = context.Command(
            "UPDATE [RelationshipTransitionDescriptions] SET [Description]=? WHERE [TransitionId]=?")
            .Add(OleDbType.LongVarWChar, value).Add(OleDbType.Integer, transitionId);
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 0) return;
        using var insert = context.Command(
            "INSERT INTO [RelationshipTransitionDescriptions] ([TransitionId],[Description]) VALUES (?,?)")
            .Add(OleDbType.Integer, transitionId).Add(OleDbType.LongVarWChar, value);
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void AddDate(AccessCommand command, StoryDate date) => command
        .Add(OleDbType.VarWChar, date.Kind.ToString(), 30)
        .Add(OleDbType.Date, date.LowerBound)
        .Add(OleDbType.Date, date.UpperBound)
        .Add(OleDbType.Boolean, date.LowerInclusive)
        .Add(OleDbType.Boolean, date.UpperInclusive)
        .Add(OleDbType.LongVarWChar, date.OriginalText)
        .Add(OleDbType.VarWChar, date.CalendarId, 50);
}
