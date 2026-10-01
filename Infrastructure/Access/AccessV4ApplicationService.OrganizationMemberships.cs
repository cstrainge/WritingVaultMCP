using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ApplicationService
{
    public async Task<VaultMutationResult> AddOrganizationMembershipAsync(
        V4OrganizationMembershipAddCommand request, CancellationToken cancellationToken = default)
    {
        var hasTransitions = request.Joined is not null || request.Left is not null;
        if (hasTransitions && (request.Joined is null || request.Left is null || request.Period is not null) ||
            !hasTransitions && request.Period is null)
            return new(false, "validation.period", Message: "Provide either one period or both joined and left dates.");
        if (request.Joined?.Validate().Count > 0 || request.Left?.Validate().Count > 0 ||
            request.Period?.Validate().Count > 0)
            return new(false, "validation.period", Message: "A membership date is invalid.");
        if (hasTransitions && !string.Equals(request.Joined!.CalendarId, request.Left!.CalendarId,
            StringComparison.OrdinalIgnoreCase))
            return new(false, "validation.calendar", Message: "Join and leave dates must use the same calendar.");
        var period = hasTransitions
            ? AccessV4RecordService.PossibleOccupancy(request.Joined!, request.Left!) : request.Period;
        if (period is null)
            return new(false, "validation.transition_order", Message: "The character cannot leave before every possible join date.");
        if (request.Role?.Length > 255 || request.Notes?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.JoinDescription?.Length > V4ContractLimits.MaximumLongTextLength ||
            request.LeaveDescription?.Length > V4ContractLimits.MaximumLongTextLength ||
            !hasTransitions && (request.JoinDescription is not null || request.LeaveDescription is not null) ||
            request.JoinDescription is not null && string.IsNullOrWhiteSpace(request.JoinDescription) ||
            request.LeaveDescription is not null && string.IsNullOrWhiteSpace(request.LeaveDescription))
            return new(false, "validation.membership", Message: "Role, notes, or transition descriptions are invalid.");

        V4ResolvedTarget organization, character;
        try
        {
            organization = await targets.EntityAsync(request.Organization, request.ContinuityKey, cancellationToken)
                .ConfigureAwait(false);
            character = await targets.EntityAsync(request.Character, request.ContinuityKey, cancellationToken)
                .ConfigureAwait(false);
            if (organization.ResourceType != "Organization" || character.ResourceType != "Character")
                return new(false, "reference.type_invalid", Message: "An organization and character are required.");
        }
        catch (V4ResolutionException exception) { return ResolutionFailure(exception); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception)
        { return new(false, "validation.mutation_token", Message: exception.Message); }

        return await writes.ExecuteAsync(operation, "v4.organization.membership.add", request,
            "organization_membership_add", request.ClientLabel, async (context, token) =>
            {
                await RequireSelectedContinuityAsync(context, request.ContinuityKey, token).ConfigureAwait(false);
                await RequireCanonEntityAsync(context, organization.StorageKey, request.ContinuityKey,
                    "Organization", token).ConfigureAwait(false);
                await RequireCanonEntityAsync(context, character.StorageKey, request.ContinuityKey,
                    "Character", token).ConfigureAwait(false);
                using var siblings = context.Command(
                    "SELECT [Role],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound]," +
                    "[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodCalendarId] " +
                    "FROM [OrganizationMemberships] WHERE [OrganizationId]=? AND [CharacterId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, organization.StorageKey).Add(OleDbType.Integer, character.StorageKey);
                var others = await siblings.QueryAsync(reader => new
                {
                    Role = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Date = new StoryDate(Enum.Parse<StoryDateKind>(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                        reader.GetBoolean(4), reader.GetBoolean(5), CalendarId: reader.GetString(6))
                }, token).ConfigureAwait(false);
                var role = request.Role is null ? "" : TextNormalization.CanonicalKey(request.Role);
                if (others.Any(other => (other.Role.Length == 0 ? "" :
                        TextNormalization.CanonicalKey(other.Role)) == role && other.Date.Overlaps(period)))
                    throw new VaultCommandException("interval.overlap",
                        "Membership overlaps an existing membership with the same role.");
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                    "INSERT INTO [OrganizationMemberships] ([OrganizationId],[CharacterId],[Role],[Notes]," +
                    "[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive]," +
                    "[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) " +
                    "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, organization.StorageKey)
                    .Add(OleDbType.Integer, character.StorageKey)
                    .Add(OleDbType.VarWChar, request.Role?.Trim(), 255)
                    .Add(OleDbType.LongVarWChar, request.Notes);
                AddDate(insert, period);
                insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                if (hasTransitions)
                {
                    await AccessV4RecordService.SaveOrganizationTransitionAsync(context, id, "Join",
                        request.Joined!, request.JoinDescription, now, token).ConfigureAwait(false);
                    await AccessV4RecordService.SaveOrganizationTransitionAsync(context, id, "Leave",
                        request.Left!, request.LeaveDescription, now, token).ConfigureAwait(false);
                }
                return new VaultMutationOutcome("OrganizationMembership", id.ToString(), 1, "add",
                    new { organization = organization.Reference, character = character.Reference,
                        period, request.Joined, request.Left,
                        request.JoinDescription, request.LeaveDescription });
            }, cancellationToken).ConfigureAwait(false);
    }
}
