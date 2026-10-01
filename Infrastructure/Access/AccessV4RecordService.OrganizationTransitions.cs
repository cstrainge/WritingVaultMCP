using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4RecordService
{
    public async Task<VaultMutationResult> SetOrganizationMembershipTransitionsAsync(
        V4OrganizationMembershipTransitionsSetRequest request, StoryDate joined, StoryDate left,
        CancellationToken token = default)
    {
        if (joined.Validate().Count > 0 || left.Validate().Count > 0 ||
            !string.Equals(joined.CalendarId, left.CalendarId, StringComparison.OrdinalIgnoreCase))
            return new(false, "validation.transition", Message: "Join and leave dates must be valid and use one calendar.");
        var period = PossibleOccupancy(joined, left);
        if (period is null)
            return new(false, "validation.transition_order", Message: "The character cannot leave before every possible join date.");
        if (!ValidDescription(request.JoinDescription, request.ClearJoinDescription) ||
            !ValidDescription(request.LeaveDescription, request.ClearLeaveDescription))
            return new(false, "validation.description", Message: "Provide nonblank descriptions or clear flags, not both.");
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference record;
        try { record = await references.ResolveAsync(request.MembershipRef, continuity, token,
            "OrganizationMembership").ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        { return new(false, "record.not_found", Message: exception.Message); }
        if (record.IsDeleted) return new(false, "record.deleted", Message: "Restore the membership before editing it.");
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception)
        { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await coordinator.ExecuteAsync(operation, "v4.organization.transitions.set", request,
            "organization_membership_transitions_set", session.ClientLabel, async (context, ct) =>
        {
            var row = await MembershipAsync(context, record.Id, continuity, request.ExpectedVersion, ct)
                .ConfigureAwait(false);
            await RejectOrganizationOverlapAsync(context, record.Id, row.Organization, row.Character,
                row.Role, period, ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            await SaveOrganizationTransitionAsync(context, record.Id, "Join", joined,
                request.JoinDescription, now, ct, request.ClearJoinDescription).ConfigureAwait(false);
            await SaveOrganizationTransitionAsync(context, record.Id, "Leave", left,
                request.LeaveDescription, now, ct, request.ClearLeaveDescription).ConfigureAwait(false);
            using var update = context.Command(
                "UPDATE [OrganizationMemberships] SET [PeriodKind]=?,[PeriodLowerBound]=?," +
                "[PeriodUpperBound]=?,[PeriodLowerInclusive]=?,[PeriodUpperInclusive]=?," +
                "[PeriodOriginalText]=?,[PeriodCalendarId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False");
            AddDate(update, period);
            update.Add(OleDbType.Date, now).Add(OleDbType.Integer, record.Id)
                .Add(OleDbType.Integer, request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new VaultCommandException("concurrency.conflict", "The membership changed during the update.");
            return new VaultMutationOutcome("OrganizationMembership", record.Id.ToString(),
                request.ExpectedVersion + 1, "transitions-set",
                new { joined, left, request.JoinDescription, request.LeaveDescription,
                    request.ClearJoinDescription, request.ClearLeaveDescription }, request.ExpectedVersion);
        }, token).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> TransitionOrganizationMembershipAsync(
        V4MembershipTransitionRequest request, DateTime effectiveAt, CancellationToken token = default)
    {
        if (!request.CreateReplacement && (request.NewRole is not null || request.Notes is not null ||
            request.ReplacementJoinDescription is not null))
            return new(false, "validation.membership", Message: "Replacement fields require createReplacement=true.");
        if (!ValidDescription(request.LeaveDescription, false) ||
            !ValidDescription(request.ReplacementJoinDescription, false) || request.NewRole?.Length > 255 ||
            request.Notes?.Length > V4ContractLimits.MaximumLongTextLength ||
            effectiveAt.Kind != DateTimeKind.Unspecified || effectiveAt < StoryDate.AccessMinimum)
            return new(false, "validation.membership", Message: "A transition field or effective time is invalid.");
        var continuity = session.RequireContinuityId();
        ResolvedVaultReference record;
        try { record = await references.ResolveAsync(request.MembershipRef, continuity, token,
            "OrganizationMembership").ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        { return new(false, "record.not_found", Message: exception.Message); }
        if (record.IsDeleted) return new(false, "record.deleted", Message: "Restore the membership before transitioning it.");
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception)
        { return new(false, "validation.mutation_token", Message: exception.Message); }
        return await coordinator.ExecuteAsync(operation, "v4.organization.membership.transition", request,
            "organization_membership_transition", session.ClientLabel, async (context, ct) =>
        {
            var row = await MembershipAsync(context, record.Id, continuity, request.ExpectedVersion, ct)
                .ConfigureAwait(false);
            if (row.Lower is { } lower && effectiveAt <= lower ||
                row.Upper is { } upper && effectiveAt > upper)
                throw new VaultCommandException("interval.invalid_transition",
                    "The effective time must be after the start and no later than the end.");
            using var join = context.Command(
                "SELECT [OccurredKind] FROM [OrganizationMembershipTransitions] " +
                "WHERE [MembershipId]=? AND [TransitionKind]='Join' AND [IsDeleted]=False")
                .Add(OleDbType.Integer, record.Id);
            var joinKinds = await join.QueryAsync(reader => reader.GetString(0), ct).ConfigureAwait(false);
            var periodKind = row.Lower is null ? "Before" :
                joinKinds.Count == 1 && joinKinds[0] is ("ExactDate" or "ExactInstant")
                    ? "KnownRange" : "UncertainRange";
            var now = DateTime.UtcNow;
            using var close = context.Command(
                "UPDATE [OrganizationMemberships] SET [PeriodKind]=?,[PeriodUpperBound]=?," +
                "[PeriodUpperInclusive]=False,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False")
                .Add(OleDbType.VarWChar, periodKind, 30)
                .Add(OleDbType.Date, effectiveAt).Add(OleDbType.Date, now)
                .Add(OleDbType.Integer, record.Id).Add(OleDbType.Integer, request.ExpectedVersion);
            if (await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new VaultCommandException("concurrency.conflict", "The membership changed during the transition.");
            await SaveOrganizationTransitionAsync(context, record.Id, "Leave",
                StoryDate.ExactInstant(effectiveAt), request.LeaveDescription, now, ct).ConfigureAwait(false);
            int? replacementId = null;
            if (request.CreateReplacement)
            {
                await RejectOrganizationOverlapAsync(context, record.Id, row.Organization,
                    row.Character, request.NewRole, new StoryDate(StoryDateKind.After, effectiveAt,
                        null), ct).ConfigureAwait(false);
                using var insert = context.Command(
                    "INSERT INTO [OrganizationMemberships] ([OrganizationId],[CharacterId],[Role],[Notes]," +
                    "[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive]," +
                    "[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
                    "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, row.Organization).Add(OleDbType.Integer, row.Character)
                    .Add(OleDbType.VarWChar, request.NewRole?.Trim(), 255)
                    .Add(OleDbType.LongVarWChar, request.Notes);
                AddDate(insert, new StoryDate(StoryDateKind.After, effectiveAt, null));
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                using var identity = context.Command("SELECT @@IDENTITY");
                replacementId = Convert.ToInt32(await identity.ExecuteScalarAsync(ct).ConfigureAwait(false));
                await SaveOrganizationTransitionAsync(context, replacementId.Value, "Join",
                    StoryDate.ExactInstant(effectiveAt), request.ReplacementJoinDescription, now, ct)
                    .ConfigureAwait(false);
            }
            return new VaultMutationOutcome("OrganizationMembership",
                (replacementId ?? record.Id).ToString(), replacementId is null ? request.ExpectedVersion + 1 : 1,
                request.CreateReplacement ? "transition" : "end",
                new { closedMembership = record.Reference, replacementId, effectiveAt,
                    request.LeaveDescription, request.ReplacementJoinDescription }, request.ExpectedVersion);
        }, token).ConfigureAwait(false);
    }

    internal static async Task SaveOrganizationTransitionAsync(VaultWriteContext context,
        int membershipId, string kind, StoryDate date, string? description, DateTime now,
        CancellationToken token, bool clearDescription = false)
    {
        using var find = context.Command(
            "SELECT [Id],[Version],[Description] FROM [OrganizationMembershipTransitions] " +
            "WHERE [MembershipId]=? AND [TransitionKind]=?")
            .Add(OleDbType.Integer, membershipId).Add(OleDbType.VarWChar, kind, 10);
        var rows = await find.QueryAsync(reader => new
        {
            Id = reader.GetInt32(0), Version = reader.GetInt32(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2)
        }, token).ConfigureAwait(false);
        var text = clearDescription ? null : description?.Trim() ?? rows.FirstOrDefault()?.Description;
        if (rows.Count == 0)
        {
            using var insert = context.Command(
                "INSERT INTO [OrganizationMembershipTransitions] ([MembershipId],[TransitionKind],[Description]," +
                "[OccurredKind],[OccurredLowerBound],[OccurredUpperBound],[OccurredLowerInclusive]," +
                "[OccurredUpperInclusive],[OccurredOriginalText],[OccurredCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)")
                .Add(OleDbType.Integer, membershipId).Add(OleDbType.VarWChar, kind, 10)
                .Add(OleDbType.LongVarWChar, text);
            AddDate(insert, date);
            insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return;
        }
        using var update = context.Command(
            "UPDATE [OrganizationMembershipTransitions] SET [Description]=?,[OccurredKind]=?," +
            "[OccurredLowerBound]=?,[OccurredUpperBound]=?,[OccurredLowerInclusive]=?," +
            "[OccurredUpperInclusive]=?,[OccurredOriginalText]=?,[OccurredCalendarId]=?," +
            "[IsDeleted]=False,[DeletedAtUtc]=NULL,[DeletedOperationId]=NULL," +
            "[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
            .Add(OleDbType.LongVarWChar, text);
        AddDate(update, date);
        update.Add(OleDbType.Date, now).Add(OleDbType.Integer, rows[0].Id)
            .Add(OleDbType.Integer, rows[0].Version);
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new VaultCommandException("concurrency.conflict", "A membership transition changed during the update.");
    }

    private async Task<(int Organization, int Character, string? Role, DateTime? Lower, DateTime? Upper)> MembershipAsync(
        VaultWriteContext context, int membershipId, int continuity, int expectedVersion, CancellationToken token)
    {
        using var command = context.Command(
            "SELECT m.[OrganizationId],m.[CharacterId],m.[Role],m.[PeriodLowerBound]," +
            "m.[PeriodUpperBound],m.[Version],m.[IsDeleted] FROM ([OrganizationMemberships] AS m " +
            "INNER JOIN [CanonEntities] AS c ON m.[OrganizationId]=c.[Id]) " +
            "INNER JOIN [CanonEntities] AS ch ON m.[CharacterId]=ch.[Id] " +
            "WHERE m.[Id]=? AND c.[ContinuityId]=? AND c.[IsDeleted]=False " +
            "AND ch.[ContinuityId]=c.[ContinuityId] AND ch.[IsDeleted]=False")
            .Add(OleDbType.Integer, membershipId).Add(OleDbType.Integer, continuity);
        var rows = await command.QueryAsync(reader => new
        {
            Organization = reader.GetInt32(0), Character = reader.GetInt32(1),
            Role = reader.IsDBNull(2) ? null : reader.GetString(2),
            Lower = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
            Upper = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
            Version = reader.GetInt32(5), Deleted = reader.GetBoolean(6)
        }, token).ConfigureAwait(false);
        if (rows.Count != 1 || rows[0].Deleted)
            throw new VaultCommandException("record.not_found", "The organization membership is unavailable.");
        if (rows[0].Version != expectedVersion)
            throw new VaultCommandException("concurrency.conflict",
                "The membership changed; read it again before retrying.", actualVersion: rows[0].Version);
        var row = rows[0];
        return (row.Organization, row.Character, row.Role, row.Lower, row.Upper);
    }

    private static async Task RejectOrganizationOverlapAsync(VaultWriteContext context,
        int membershipId, int organization, int character, string? role, StoryDate period, CancellationToken token)
    {
        using var command = context.Command(
            "SELECT [Role],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound]," +
            "[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] " +
            "FROM [OrganizationMemberships] WHERE [OrganizationId]=? AND [CharacterId]=? " +
            "AND [Id]<>? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, organization).Add(OleDbType.Integer, character)
            .Add(OleDbType.Integer, membershipId);
        var others = await command.QueryAsync(reader => new
        {
            Role = reader.IsDBNull(0) ? "" : reader.GetString(0),
            Date = new StoryDate(Enum.Parse<StoryDateKind>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.GetBoolean(4), reader.GetBoolean(5), CalendarId: reader.GetString(6))
        }, token).ConfigureAwait(false);
        var key = role is null ? "" : TextNormalization.CanonicalKey(role);
        if (others.Any(other => (other.Role.Length == 0 ? "" :
                TextNormalization.CanonicalKey(other.Role)) == key && other.Date.Overlaps(period)))
            throw new VaultCommandException("interval.overlap",
                "The possible membership interval overlaps another active membership with the same role.");
    }

    private static bool ValidDescription(string? value, bool clear) =>
        !(clear && value is not null) && (value is null || value.Length <= V4ContractLimits.MaximumLongTextLength) &&
        (value is null || !string.IsNullOrWhiteSpace(value));
}
