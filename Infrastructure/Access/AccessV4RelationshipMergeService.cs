using System.Data.Common;
using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>
/// Explicitly consolidates an old two-person relationship into another relationship.
/// The old row is archived, never deleted: its reference, Markdown, claims and audit
/// remain inspectable, while period and event references follow the surviving group.
/// </summary>
public sealed class AccessV4RelationshipMergeService(
    IAccessConnectionFactory factory, VaultWriteCoordinator writes,
    VaultReferenceService references, VaultSessionContext session)
{
    private const int MaximumPreviewChildren = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Parent(int Id, int Continuity, int TypeId, string TypeName,
        bool Directed, int SourceCharacter, int TargetCharacter, string? Notes,
        int Version, bool Deleted);
    private sealed record Member(int Id, int Character, int Version, bool Deleted);
    private sealed record Period(int Id, int MemberId, int Character, StoryDate Date,
        string? Notes, int Version, bool Deleted);
    private sealed record Transition(int PeriodId, string Kind, StoryDate Date, int Version, bool Deleted);
    private sealed record Event(int Id, string Title, string? Description, StoryDate Date, int Version, bool Deleted,
        int? WorldEvent);
    private sealed record EventProject(int EventId, int ProjectId, int Version, bool Deleted);
    private sealed record StoryImage(int Id, int Version, bool Deleted, bool Primary);
    private sealed record Snapshot(Parent Parent, IReadOnlyList<Member> Members,
        IReadOnlyList<Period> Periods, IReadOnlyList<Transition> Transitions,
        IReadOnlyList<Event> Events,
        IReadOnlyList<EventProject> EventProjects, IReadOnlyList<int> Claims,
        IReadOnlyList<StoryImage> Images,
        int HistoryEntries, int IncomingRedirects, int OutgoingRedirects);

    public async Task<V4RelationshipMergePreview> PreviewAsync(
        V4RelationshipMergePreviewRequest request, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        int sourceId, targetId;
        try { (sourceId, targetId) = await ResolvePairAsync(request.SourceRef, request.TargetRef, continuity, token); }
        catch (ArgumentException exception) { throw new V4ResolutionException("reference.invalid", exception.Message); }
        catch (KeyNotFoundException exception) { throw new V4ResolutionException("record.not_found", exception.Message); }
        (Snapshot Source, Snapshot Target) pair;
        try { pair = await writes.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = factory.Create();
            await connection.OpenAsync(token).ConfigureAwait(false);
            return (Source: await ReadAsync(connection, null, sourceId, token),
                Target: await ReadAsync(connection, null, targetId, token));
        }, token); }
        catch (VaultCommandException exception) { throw new V4ResolutionException(exception.Code, exception.Message); }
        var conflicts = Conflicts(pair.Source, pair.Target, continuity);
        return new(conflicts.Count == 0,
            conflicts.Count == 0 ? ReviewToken(pair.Source, pair.Target) : null,
            conflicts,
            await CandidateAsync(request.SourceRef, pair.Source, token),
            await CandidateAsync(request.TargetRef, pair.Target, token),
            "The source becomes an archived, readable redirect. Its Markdown, claim links and audit remain on the source; its membership periods, events and images retain their references under the target.");
    }

    public async Task<VaultMutationResult> ApplyAsync(
        V4RelationshipMergeApplyRequest request, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        if (string.IsNullOrWhiteSpace(request.ReviewToken) || !request.ReviewToken.StartsWith("rm1.", StringComparison.Ordinal))
            return new(false, "merge.review_required", Message: "Preview this exact pair and supply its reviewToken.");
        int sourceId, targetId;
        try { (sourceId, targetId) = await ResolvePairAsync(request.SourceRef, request.TargetRef, continuity, token); }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        { return new(false, "record.not_found", Message: exception.Message); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException exception)
        { return new(false, "validation.mutation_token", Message: exception.Message); }

        return await writes.ExecuteAsync(operation, "v4.relationship.merge", request,
            "relationship_merge_apply", session.ClientLabel, async (context, ct) =>
        {
            var source = await ReadAsync(context.Connection, context.Transaction, sourceId, ct);
            var target = await ReadAsync(context.Connection, context.Transaction, targetId, ct);
            var conflicts = Conflicts(source, target, continuity);
            if (conflicts.Count > 0)
                throw new VaultCommandException("merge.conflict", string.Join(" ", conflicts));
            if (!string.Equals(ReviewToken(source, target), request.ReviewToken, StringComparison.Ordinal))
                throw new VaultCommandException("merge.preview_stale", "One of the relationships changed. Preview the merge again.");

            var now = DateTime.UtcNow;
            var targetMembers = target.Members.Where(member => !member.Deleted)
                .ToDictionary(member => member.Character);
            foreach (var period in source.Periods)
            {
                using var move = context.Command(
                    "UPDATE [RelationshipMembershipPeriods] SET [ParticipantId]=?,[Version]=[Version]+1," +
                    "[UpdatedAtUtc]=? WHERE [Id]=? AND [ParticipantId]=? AND [Version]=?")
                    .Add(OleDbType.Integer, targetMembers[period.Character].Id)
                    .Add(OleDbType.Date, now).Add(OleDbType.Integer, period.Id)
                    .Add(OleDbType.Integer, period.MemberId).Add(OleDbType.Integer, period.Version);
                if (await move.ExecuteNonQueryAsync(ct) != 1) throw Changed();
                await JournalAsync(context, "RelationshipMembershipPeriod", period.Id,
                    period.Version, period.Version + 1, request, now, ct);
            }
            foreach (var item in source.Events)
            {
                using var move = context.Command(
                    "UPDATE [RelationshipEvents] SET [RelationshipId]=?,[Version]=[Version]+1," +
                    "[UpdatedAtUtc]=? WHERE [Id]=? AND [RelationshipId]=? AND [Version]=?")
                    .Add(OleDbType.Integer, targetId).Add(OleDbType.Date, now)
                    .Add(OleDbType.Integer, item.Id).Add(OleDbType.Integer, sourceId)
                    .Add(OleDbType.Integer, item.Version);
                if (await move.ExecuteNonQueryAsync(ct) != 1) throw Changed();
                await JournalAsync(context, "RelationshipEvent", item.Id,
                    item.Version, item.Version + 1, request, now, ct);
            }
            var targetHasPrimaryImage = target.Images.Any(image => !image.Deleted && image.Primary);
            foreach (var image in source.Images)
            {
                var promote = !image.Deleted && image.Primary && !targetHasPrimaryImage;
                using var move = context.Command(
                    "UPDATE [StoryImages] SET [RelationshipId]=?,[IsPrimary]=?," +
                    "[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND " +
                    "[OwnerKind]='Relationship' AND [RelationshipId]=? AND [Version]=?")
                    .Add(OleDbType.Integer, targetId).Add(OleDbType.Boolean, promote)
                    .Add(OleDbType.Date, now).Add(OleDbType.Integer, image.Id)
                    .Add(OleDbType.Integer, sourceId).Add(OleDbType.Integer, image.Version);
                if (await move.ExecuteNonQueryAsync(ct) != 1) throw Changed();
                if (promote) targetHasPrimaryImage = true;
                await JournalAsync(context, "StoryImage", image.Id,
                    image.Version, image.Version + 1, request, now, ct);
            }
            foreach (var member in source.Members.Where(member => !member.Deleted))
            {
                using var archive = context.Command(
                    "UPDATE [RelationshipParticipants] SET [IsDeleted]=True,[DeletedAtUtc]=?," +
                    "[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                    "WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Date, now).Add(OleDbType.VarWChar, context.OperationId, 36)
                    .Add(OleDbType.Date, now).Add(OleDbType.Integer, member.Id)
                    .Add(OleDbType.Integer, member.Version);
                if (await archive.ExecuteNonQueryAsync(ct) != 1) throw Changed();
                await JournalAsync(context, "RelationshipParticipant", member.Id,
                    member.Version, member.Version + 1, request, now, ct);
            }
            using (var archive = context.Command(
                "UPDATE [CharacterRelationships] SET [IsDeleted]=True,[DeletedAtUtc]=?," +
                "[DeletedOperationId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False")
                .Add(OleDbType.Date, now).Add(OleDbType.VarWChar, context.OperationId, 36)
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, sourceId)
                .Add(OleDbType.Integer, source.Parent.Version))
                if (await archive.ExecuteNonQueryAsync(ct) != 1) throw Changed();
            using (var bump = context.Command(
                "UPDATE [CharacterRelationships] SET [UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                "WHERE [Id]=? AND [Version]=? AND [IsDeleted]=False")
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, targetId)
                .Add(OleDbType.Integer, target.Parent.Version))
                if (await bump.ExecuteNonQueryAsync(ct) != 1) throw Changed();
            using (var redirect = context.Command(
                "INSERT INTO [RelationshipMergeRedirects] ([SourceRelationshipId],[TargetRelationshipId]," +
                "[SourceVersionBefore],[TargetVersionBefore],[CreatedAtUtc],[UpdatedAtUtc]) " +
                "VALUES (?,?,?,?,?,?)")
                .Add(OleDbType.Integer, sourceId).Add(OleDbType.Integer, targetId)
                .Add(OleDbType.Integer, source.Parent.Version)
                .Add(OleDbType.Integer, target.Parent.Version)
                .Add(OleDbType.Date, now).Add(OleDbType.Date, now))
                await redirect.ExecuteNonQueryAsync(ct);
            await JournalAsync(context, "CharacterRelationship", sourceId,
                source.Parent.Version, source.Parent.Version + 1, request, now, ct);
            return new VaultMutationOutcome("CharacterRelationship", targetId.ToString(CultureInfo.InvariantCulture),
                target.Parent.Version + 1, "merge", new
                {
                    source = request.SourceRef, target = request.TargetRef,
                    movedPeriods = source.Periods.Count, movedEvents = source.Events.Count,
                    movedImages = source.Images.Count,
                    preservedSourceNotes = source.Parent.Notes is not null,
                    preservedSourceClaims = source.Claims.Count
                }, target.Parent.Version);
        }, token);
    }

    private async Task<(int Source, int Target)> ResolvePairAsync(
        string sourceRef, string targetRef, int continuity, CancellationToken token)
    {
        var source = await references.ResolveAsync(sourceRef, continuity, token, "CharacterRelationship");
        var target = await references.ResolveAsync(targetRef, continuity, token, "CharacterRelationship");
        return (source.Id, target.Id);
    }

    private async Task<V4RelationshipMergeCandidate> CandidateAsync(
        string reference, Snapshot snapshot, CancellationToken token)
    {
        var personRefs = new Dictionary<int, string>();
        foreach (var member in snapshot.Members)
            if (!personRefs.ContainsKey(member.Character))
                personRefs[member.Character] = await references.ReferenceAsync("Character", member.Character, token);
        var periods = snapshot.Periods.Select(period =>
        {
            var transitions = snapshot.Transitions.Where(item => item.PeriodId == period.Id && !item.Deleted).ToArray();
            var joined = transitions.FirstOrDefault(item => item.Kind == "Join");
            var left = transitions.FirstOrDefault(item => item.Kind == "Leave");
            return new V4RelationshipMergePeriod(
                references.ReferenceFromKnownRecord("RelationshipMembershipPeriod", period.Id, "membership period"),
                personRefs[period.Character], AccessV4ReadService.StoryDateView(period.Date),
                period.Notes, period.Deleted,
                joined is null ? null : AccessV4ReadService.StoryDateView(joined.Date),
                left is null ? null : AccessV4ReadService.StoryDateView(left.Date));
        }).ToArray();
        var events = new List<V4RelationshipMergeEvent>();
        foreach (var item in snapshot.Events)
        {
            var world = item.WorldEvent is { } worldId
                ? await references.ReferenceAsync("WorldEvent", worldId, token) : null;
            var projects = new List<string>();
            foreach (var project in snapshot.EventProjects.Where(project => project.EventId == item.Id && !project.Deleted))
                projects.Add(await references.ReferenceAsync("Project", project.ProjectId, token));
            events.Add(new(references.ReferenceFromKnownRecord("RelationshipEvent", item.Id, item.Title),
                item.Title, item.Description, AccessV4ReadService.StoryDateView(item.Date), item.Deleted, world, projects));
        }
        var claims = new List<string>();
        foreach (var claim in snapshot.Claims)
            claims.Add(await references.ReferenceAsync("Claim", claim, token));
        var images = new List<string>();
        foreach (var image in snapshot.Images)
            images.Add(await references.ReferenceAsync("StoryImage", image.Id, token));
        return new(reference, snapshot.Parent.TypeName,
            snapshot.Members.Where(member => !member.Deleted).Select(member => personRefs[member.Character]).ToArray(),
            snapshot.Parent.Notes, periods, events, claims, snapshot.HistoryEntries, images);
    }

    private static IReadOnlyList<string> Conflicts(Snapshot source, Snapshot target, int continuity)
    {
        var conflicts = new List<string>();
        if (source.Parent.Id == target.Parent.Id) conflicts.Add("Choose two different relationship records.");
        if (source.Parent.Deleted || target.Parent.Deleted) conflicts.Add("Both relationships must be active.");
        if (source.Parent.Continuity != continuity || target.Parent.Continuity != continuity)
            conflicts.Add("Both relationships must belong to the selected continuity.");
        if (source.Parent.TypeId != target.Parent.TypeId)
            conflicts.Add("The relationship types differ.");
        if (source.IncomingRedirects > 0 || source.OutgoingRedirects > 0 || target.OutgoingRedirects > 0)
            conflicts.Add("A relationship in this pair is already part of a merge redirect.");
        var activeSource = source.Members.Where(member => !member.Deleted).ToArray();
        var activeTarget = target.Members.Where(member => !member.Deleted).ToArray();
        if (source.Members.Count != 2 || activeSource.Length != 2)
            conflicts.Add("The source must be an intact two-character legacy relationship.");
        if (activeSource.Any(member => !activeTarget.Any(other => other.Character == member.Character)))
            conflicts.Add("Every source character must already be in the target relationship.");
        if (source.Parent.Directed != target.Parent.Directed ||
            source.Parent.Directed && (source.Parent.SourceCharacter != target.Parent.SourceCharacter ||
                                       source.Parent.TargetCharacter != target.Parent.TargetCharacter))
            conflicts.Add("Directed relationships must preserve the same source and target roles.");
        if (source.Periods.Where(period => !period.Deleted).Any(period =>
                target.Periods.Where(other => !other.Deleted && other.Character == period.Character)
                    .Any(other => period.Date.Overlaps(other.Date))))
            conflicts.Add("At least one character has possibly overlapping membership periods. Correct the dates before merging.");
        return conflicts;
    }

    private static string ReviewToken(Snapshot source, Snapshot target)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { source, target }, JsonOptions)));
        return "rm1." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static VaultCommandException Changed() => new("merge.preview_stale", "The relationship changed. Preview the merge again.");

    private async Task JournalAsync(VaultWriteContext context, string type, int id,
        int before, int after, V4RelationshipMergeApplyRequest request, DateTime now, CancellationToken token)
    {
        using var journal = context.Command(
            "INSERT INTO [ChangeLog] ([OperationId],[ChangedAtUtc],[ClientLabel],[ToolName],[Action]," +
            "[RecordType],[RecordKey],[VersionBefore],[VersionAfter],[ChangeJson]) VALUES (?,?,?,?,?,?,?,?,?,?)")
            .Add(OleDbType.VarWChar, context.OperationId, 36).Add(OleDbType.Date, now)
            .Add(OleDbType.VarWChar, session.ClientLabel, 100)
            .Add(OleDbType.VarWChar, "relationship_merge_apply", 100)
            .Add(OleDbType.VarWChar, "merge-move", 50)
            .Add(OleDbType.VarWChar, type, 100)
            .Add(OleDbType.VarWChar, id.ToString(CultureInfo.InvariantCulture), 100)
            .Add(OleDbType.Integer, before).Add(OleDbType.Integer, after)
            .Add(OleDbType.LongVarWChar, VaultJournalPayload.Serialize(new
                { source = request.SourceRef, target = request.TargetRef }, JsonOptions));
        await journal.ExecuteNonQueryAsync(token);
    }

    private static async Task<Snapshot> ReadAsync(OleDbConnection connection,
        OleDbTransaction? transaction, int id, CancellationToken token)
    {
        using var parentQuery = new AccessCommand(connection,
            "SELECT r.[Id],r.[ContinuityId],r.[RelationshipTypeId],t.[Name],t.[IsDirected]," +
            "r.[SourceCharacterId],r.[TargetCharacterId],r.[Notes],r.[Version],r.[IsDeleted] " +
            "FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t " +
            "ON r.[RelationshipTypeId]=t.[Id] WHERE r.[Id]=?", transaction)
            .Add(OleDbType.Integer, id);
        var parents = await parentQuery.QueryAsync(r => new Parent(r.GetInt32(0), r.GetInt32(1),
            r.GetInt32(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5), r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8), r.GetBoolean(9)), token);
        if (parents.Count != 1) throw new VaultCommandException("record.not_found", "A relationship was not found.");

        using var membersQuery = new AccessCommand(connection,
            "SELECT [Id],[CharacterId],[Version],[IsDeleted] FROM [RelationshipParticipants] " +
            "WHERE [RelationshipId]=? ORDER BY [Id]", transaction).Add(OleDbType.Integer, id);
        var members = await membersQuery.QueryAsync(r => new Member(r.GetInt32(0), r.GetInt32(1),
            r.GetInt32(2), r.GetBoolean(3)), token);
        using var periodsQuery = new AccessCommand(connection,
            $"SELECT TOP {MaximumPreviewChildren + 1} m.[Id],m.[ParticipantId],p.[CharacterId]," +
            "m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound],m.[PeriodLowerInclusive]," +
            "m.[PeriodUpperInclusive],m.[PeriodOriginalText],m.[PeriodCalendarId],m.[Notes]," +
            "m.[Version],m.[IsDeleted] FROM [RelationshipMembershipPeriods] AS m " +
            "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] " +
            "WHERE p.[RelationshipId]=? ORDER BY m.[Id]", transaction).Add(OleDbType.Integer, id);
        var periods = await periodsQuery.QueryAsync(r => new Period(r.GetInt32(0), r.GetInt32(1),
            r.GetInt32(2), Date(r, 3), r.IsDBNull(10) ? null : r.GetString(10),
            r.GetInt32(11), r.GetBoolean(12)), token);
        using var transitionsQuery = new AccessCommand(connection,
            $"SELECT TOP {MaximumPreviewChildren * 2 + 1} t.[MembershipPeriodId],t.[TransitionKind]," +
            "t.[OccurredKind],t.[OccurredLowerBound],t.[OccurredUpperBound]," +
            "t.[OccurredLowerInclusive],t.[OccurredUpperInclusive],t.[OccurredOriginalText]," +
            "t.[OccurredCalendarId],t.[Version],t.[IsDeleted] " +
            "FROM ([RelationshipMembershipTransitions] AS t INNER JOIN [RelationshipMembershipPeriods] AS m " +
            "ON t.[MembershipPeriodId]=m.[Id]) INNER JOIN [RelationshipParticipants] AS p " +
            "ON m.[ParticipantId]=p.[Id] WHERE p.[RelationshipId]=? ORDER BY t.[Id]", transaction)
            .Add(OleDbType.Integer, id);
        var transitions = await transitionsQuery.QueryAsync(r => new Transition(r.GetInt32(0),
            r.GetString(1), Date(r, 2), r.GetInt32(9), r.GetBoolean(10)), token);
        using var eventsQuery = new AccessCommand(connection,
            $"SELECT TOP {MaximumPreviewChildren + 1} [Id],[Title],[Description],[EventKind],[EventLowerBound]," +
            "[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText]," +
            "[EventCalendarId],[Version],[IsDeleted],[WorldEventId] FROM [RelationshipEvents] " +
            "WHERE [RelationshipId]=? ORDER BY [Id]", transaction).Add(OleDbType.Integer, id);
        var events = await eventsQuery.QueryAsync(r => new Event(r.GetInt32(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2), Date(r, 3), r.GetInt32(10),
            r.GetBoolean(11), r.IsDBNull(12) ? null : r.GetInt32(12)), token);
        using var projectsQuery = new AccessCommand(connection,
            $"SELECT TOP {MaximumPreviewChildren + 1} x.[RelationshipEventId],x.[ProjectId]," +
            "x.[Version],x.[IsDeleted] FROM [RelationshipEventProjects] AS x " +
            "INNER JOIN [RelationshipEvents] AS e ON x.[RelationshipEventId]=e.[Id] " +
            "WHERE e.[RelationshipId]=? ORDER BY x.[Id]", transaction).Add(OleDbType.Integer, id);
        var projects = await projectsQuery.QueryAsync(r => new EventProject(r.GetInt32(0), r.GetInt32(1),
            r.GetInt32(2), r.GetBoolean(3)), token);
        using var claimsQuery = new AccessCommand(connection,
            $"SELECT TOP {MaximumPreviewChildren + 1} [ClaimId] FROM [ClaimRelationships] " +
            "WHERE [RelationshipId]=? ORDER BY [ClaimId]", transaction).Add(OleDbType.Integer, id);
        var claims = await claimsQuery.QueryAsync(r => r.GetInt32(0), token);
        using var imagesQuery = new AccessCommand(connection,
            $"SELECT TOP {MaximumPreviewChildren + 1} [Id],[Version],[IsDeleted],[IsPrimary] " +
            "FROM [StoryImages] WHERE [OwnerKind]='Relationship' AND [RelationshipId]=? ORDER BY [Id]",
            transaction).Add(OleDbType.Integer, id);
        var images = await imagesQuery.QueryAsync(r => new StoryImage(r.GetInt32(0),
            r.GetInt32(1), r.GetBoolean(2), r.GetBoolean(3)), token);
        if (periods.Count > MaximumPreviewChildren || events.Count > MaximumPreviewChildren ||
            transitions.Count > MaximumPreviewChildren * 2 || images.Count > MaximumPreviewChildren)
            throw new VaultCommandException("merge.preview_too_large", "A relationship has more than 500 periods or events; split this merge into smaller reviewed operations.");
        if (projects.Count > MaximumPreviewChildren || claims.Count > MaximumPreviewChildren ||
            (parents[0].Notes?.Length ?? 0) + periods.Sum(period => period.Notes?.Length ?? 0) +
            events.Sum(item => item.Description?.Length ?? 0) > 250_000)
            throw new VaultCommandException("merge.preview_too_large", "The relationship has too much linked material for one safe preview.");
        var history = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM [ChangeLog] WHERE [RecordType]='CharacterRelationship' AND [RecordKey]=?", id, token, OleDbType.VarWChar);
        var incoming = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM [RelationshipMergeRedirects] WHERE [TargetRelationshipId]=?", id, token);
        var outgoing = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM [RelationshipMergeRedirects] WHERE [SourceRelationshipId]=?", id, token);
        return new(parents[0], members, periods, transitions, events, projects, claims, images,
            history, incoming, outgoing);
    }

    private static StoryDate Date(DbDataReader r, int offset) => new(
        Enum.Parse<StoryDateKind>(r.GetString(offset)),
        r.IsDBNull(offset + 1) ? null : DateTime.SpecifyKind(r.GetDateTime(offset + 1), DateTimeKind.Unspecified),
        r.IsDBNull(offset + 2) ? null : DateTime.SpecifyKind(r.GetDateTime(offset + 2), DateTimeKind.Unspecified),
        r.GetBoolean(offset + 3), r.GetBoolean(offset + 4),
        r.IsDBNull(offset + 5) ? null : r.GetString(offset + 5), r.GetString(offset + 6));

    private static async Task<int> CountAsync(OleDbConnection connection, OleDbTransaction? transaction,
        string sql, int id, CancellationToken token, OleDbType type = OleDbType.Integer)
    {
        using var command = new AccessCommand(connection, sql, transaction)
            .Add(type, type == OleDbType.Integer ? id : id.ToString(CultureInfo.InvariantCulture));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }
}
