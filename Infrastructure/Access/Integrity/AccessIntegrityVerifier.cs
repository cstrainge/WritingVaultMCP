using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access.Integrity;

public sealed record IntegrityIssue(string Code, string ObjectName, string Detail);

public sealed record IntegrityVerificationResult(IReadOnlyList<IntegrityIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class AccessIntegrityVerifier(IAccessConnectionFactory connectionFactory)
{
    public async Task<IntegrityVerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var issues = new List<IntegrityIssue>();
        async Task VerifyStep(string name, Func<Task> run)
        {
            var started = Stopwatch.GetTimestamp();
            await run().ConfigureAwait(false);
            VaultDiagnostics.Write("integrity.step_completed", commandType: name,
                durationMilliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        await VerifyStep("locations", () => VerifyLocationHierarchyAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("intervals", () => VerifyIntervalsAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("ownership", () => VerifyOwnershipGroupsAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("boundaries", () => VerifyContinuityBoundariesAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("story_dates", () => VerifyStoryDatesAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("time_zones", () => VerifyTimeZonesAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("images", () => VerifyImageStorageAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("relationship_merges", () => VerifyRelationshipMergesAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("relationship_transitions", () => VerifyRelationshipTransitionsAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        await VerifyStep("record_snapshots", () => VerifyRecordPageSnapshotsAsync(connection, issues, cancellationToken)).ConfigureAwait(false);
        return new IntegrityVerificationResult(issues
            .OrderBy(issue => issue.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ToArray());
    }

    private static async Task VerifyRecordPageSnapshotsAsync(OleDbConnection connection,
        ICollection<IntegrityIssue> issues, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT [RecordType],[RecordKey],[ContextContinuityId]," +
            "[SnapshotVersion],[PageBytes],[PageSha256],[PageJson] " +
            "FROM [RecordPageSnapshots] ORDER BY [RecordType],[RecordKey]," +
            "[ContextContinuityId],[SnapshotVersion]";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Snapshot integrity query returned no reader.");
        (string Type, int Key, int Context)? previous = null;
        var expectedVersion = 1;
        var corrupt = 0;
        var gaps = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var identity = (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2));
            if (previous != identity) { previous = identity; expectedVersion = 1; }
            var version = reader.GetInt32(3);
            if (version != expectedVersion) gaps++;
            expectedVersion = version + 1;
            var body = reader.GetString(6);
            var bytes = Encoding.UTF8.GetBytes(body);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (bytes.Length is < 1 or > 1_048_576 || bytes.Length != reader.GetInt32(4) ||
                !string.Equals(hash, reader.GetString(5), StringComparison.Ordinal))
            {
                corrupt++;
                continue;
            }
            try { using var document = JsonDocument.Parse(body); }
            catch (JsonException) { corrupt++; }
        }
        if (corrupt > 0)
            issues.Add(new IntegrityIssue("integrity.record_snapshot_corrupt",
                "RecordPageSnapshots", $"{corrupt} saved page(s) failed integrity verification."));
        if (gaps > 0)
            issues.Add(new IntegrityIssue("integrity.record_snapshot_gap",
                "RecordPageSnapshots", $"{gaps} saved page version gap(s) were found."));
    }

    private static async Task VerifyRelationshipMergesAsync(
        OleDbConnection connection, ICollection<IntegrityIssue> issues, CancellationToken token)
    {
        // This is a startup/health audit. The merge application path validates
        // the same invariants inside its write transaction; these broad joins
        // must not run after every unrelated high-volume write.
        var checks = new (string Name, string Sql)[]
        {
            ("continuity", "SELECT COUNT(*) FROM ([RelationshipMergeRedirects] AS x INNER JOIN [CharacterRelationships] AS s ON x.[SourceRelationshipId]=s.[Id]) INNER JOIN [CharacterRelationships] AS t ON x.[TargetRelationshipId]=t.[Id] WHERE s.[ContinuityId]<>t.[ContinuityId]"),
            ("type", "SELECT COUNT(*) FROM ([RelationshipMergeRedirects] AS x INNER JOIN [CharacterRelationships] AS s ON x.[SourceRelationshipId]=s.[Id]) INNER JOIN [CharacterRelationships] AS t ON x.[TargetRelationshipId]=t.[Id] WHERE s.[RelationshipTypeId]<>t.[RelationshipTypeId]"),
            ("source_state", "SELECT COUNT(*) FROM [RelationshipMergeRedirects] AS x INNER JOIN [CharacterRelationships] AS s ON x.[SourceRelationshipId]=s.[Id] WHERE s.[IsDeleted]=False"),
            ("source_members", "SELECT COUNT(*) FROM [RelationshipMergeRedirects] AS x INNER JOIN [RelationshipParticipants] AS p ON x.[SourceRelationshipId]=p.[RelationshipId] WHERE p.[IsDeleted]=False"),
            ("redirect_chain", "SELECT COUNT(*) FROM [RelationshipMergeRedirects] AS a INNER JOIN [RelationshipMergeRedirects] AS b ON a.[TargetRelationshipId]=b.[SourceRelationshipId]")
        };
        foreach (var (name, sql) in checks)
        {
            using var command = new AccessCommand(connection, sql);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            if (count > 0)
                issues.Add(new IntegrityIssue("integrity.relationship_merge", "RelationshipMergeRedirects." + name,
                    $"{count} invalid relationship merge redirect(s)."));
        }
    }

    private static async Task VerifyRelationshipTransitionsAsync(
        OleDbConnection connection, ICollection<IntegrityIssue> issues, CancellationToken token)
    {
        static StoryDate Date(DbDataReader reader, int offset) => new(
            Enum.Parse<StoryDateKind>(reader.GetString(offset)),
            reader.IsDBNull(offset + 1) ? null : DateTime.SpecifyKind(reader.GetDateTime(offset + 1), DateTimeKind.Unspecified),
            reader.IsDBNull(offset + 2) ? null : DateTime.SpecifyKind(reader.GetDateTime(offset + 2), DateTimeKind.Unspecified),
            reader.GetBoolean(offset + 3), reader.GetBoolean(offset + 4),
            reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5), reader.GetString(offset + 6));
        using var command = new AccessCommand(connection,
            "SELECT m.[Id],m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound]," +
            "m.[PeriodLowerInclusive],m.[PeriodUpperInclusive],m.[PeriodOriginalText]," +
            "m.[PeriodCalendarId],t.[TransitionKind],t.[OccurredKind]," +
            "t.[OccurredLowerBound],t.[OccurredUpperBound],t.[OccurredLowerInclusive]," +
            "t.[OccurredUpperInclusive],t.[OccurredOriginalText],t.[OccurredCalendarId] " +
            "FROM [RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipMembershipTransitions] AS t " +
            "ON m.[Id]=t.[MembershipPeriodId] WHERE t.[IsDeleted]=False");
        var rows = await command.QueryAsync(reader => new
        {
            Id = reader.GetInt32(0), Period = Date(reader, 1),
            Kind = reader.GetString(8), Transition = Date(reader, 9)
        }, token).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => row.Id))
        {
            var joined = group.SingleOrDefault(row => row.Kind == "Join");
            var left = group.SingleOrDefault(row => row.Kind == "Leave");
            if (group.Count() != 2 || joined is null || left is null)
            {
                issues.Add(new IntegrityIssue("integrity.relationship_transition_pair",
                    $"RelationshipMembershipPeriods.{group.Key}", "An active membership has an incomplete transition pair."));
                continue;
            }
            if (!joined.Transition.CalendarId.Equals(left.Transition.CalendarId, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new IntegrityIssue("integrity.relationship_transition_calendar",
                    $"RelationshipMembershipPeriods.{group.Key}", "Join and leave use different calendars."));
                continue;
            }
            var possible = AccessV4RecordService.PossibleOccupancy(joined.Transition, left.Transition);
            var actual = joined.Period;
            if (possible is null || !EquivalentEnvelopeKind(actual.Kind, possible.Kind) ||
                actual.LowerBound != possible.LowerBound || actual.UpperBound != possible.UpperBound ||
                actual.LowerInclusive != possible.LowerInclusive || actual.UpperInclusive != possible.UpperInclusive ||
                !actual.CalendarId.Equals(possible.CalendarId, StringComparison.OrdinalIgnoreCase))
                issues.Add(new IntegrityIssue("integrity.relationship_transition_envelope",
                    $"RelationshipMembershipPeriods.{group.Key}", "The stored period does not match its join and leave dates."));
        }
        using var organizations = new AccessCommand(connection,
            "SELECT m.[Id],m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound]," +
            "m.[PeriodLowerInclusive],m.[PeriodUpperInclusive],m.[PeriodOriginalText]," +
            "m.[PeriodCalendarId],t.[TransitionKind],t.[OccurredKind]," +
            "t.[OccurredLowerBound],t.[OccurredUpperBound],t.[OccurredLowerInclusive]," +
            "t.[OccurredUpperInclusive],t.[OccurredOriginalText],t.[OccurredCalendarId] " +
            "FROM [OrganizationMemberships] AS m INNER JOIN [OrganizationMembershipTransitions] AS t " +
            "ON m.[Id]=t.[MembershipId] WHERE t.[IsDeleted]=False");
        var organizationRows = await organizations.QueryAsync(reader => new
        {
            Id = reader.GetInt32(0), Period = Date(reader, 1),
            Kind = reader.GetString(8), Transition = Date(reader, 9)
        }, token).ConfigureAwait(false);
        foreach (var group in organizationRows.GroupBy(row => row.Id))
        {
            var joined = group.SingleOrDefault(row => row.Kind == "Join");
            var left = group.SingleOrDefault(row => row.Kind == "Leave");
            if (joined is null || left is null) continue; // A legacy period may gain one explicit boundary.
            if (!joined.Transition.CalendarId.Equals(left.Transition.CalendarId,
                StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new IntegrityIssue("integrity.organization_transition_calendar",
                    $"OrganizationMemberships.{group.Key}", "Join and leave use different calendars."));
                continue;
            }
            var possible = AccessV4RecordService.PossibleOccupancy(joined.Transition,
                left.Transition);
            var actual = joined.Period;
            if (possible is null || !EquivalentEnvelopeKind(actual.Kind, possible.Kind) ||
                actual.LowerBound != possible.LowerBound || actual.UpperBound != possible.UpperBound ||
                actual.LowerInclusive != possible.LowerInclusive ||
                actual.UpperInclusive != possible.UpperInclusive ||
                !actual.CalendarId.Equals(possible.CalendarId, StringComparison.OrdinalIgnoreCase))
                issues.Add(new IntegrityIssue("integrity.organization_transition_envelope",
                    $"OrganizationMemberships.{group.Key}",
                    "The stored membership does not match its join and leave dates."));
        }
    }

    private static bool EquivalentEnvelopeKind(StoryDateKind stored, StoryDateKind computed) =>
        stored == computed || stored == StoryDateKind.Range &&
        computed is StoryDateKind.KnownRange or StoryDateKind.UncertainRange;

    private static async Task VerifyLocationHierarchyAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(connection, "SELECT [EntityId], [ParentLocationId] FROM [Locations]");
        var rows = await command.QueryAsync(
            reader => (Id: reader.GetInt32(0), Parent: reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1)),
            cancellationToken).ConfigureAwait(false);
        var parents = rows.ToDictionary(row => row.Id, row => row.Parent);
        foreach (var id in parents.Keys)
        {
            var seen = new HashSet<int>();
            int? cursor = id;
            while (cursor is { } current && parents.TryGetValue(current, out cursor))
            {
                if (!seen.Add(current))
                {
                    issues.Add(new IntegrityIssue(
                        "integrity.location_cycle",
                        $"Locations.{id}",
                        "Location ancestry contains a cycle."));
                    break;
                }
            }
        }
    }

    private static async Task VerifyIntervalsAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        await VerifyNoOverlapAsync(connection, "ObjectOwnershipPeriods", "ObjectId", null, issues, cancellationToken)
            .ConfigureAwait(false);
        await VerifyMembershipOverlapAsync(connection, issues, cancellationToken).ConfigureAwait(false);
        await VerifyRelationshipOverlapAsync(connection, issues, cancellationToken).ConfigureAwait(false);
        await VerifyNoOverlapAsync(connection, "CharacterResidences", "CharacterId", null, issues, cancellationToken, "[IsPrimary] = True")
            .ConfigureAwait(false);
        await VerifyNoOverlapAsync(connection, "OrganizationLocations", "OrganizationId", null, issues, cancellationToken, "[IsPrimary] = True")
            .ConfigureAwait(false);
        await VerifyNoOverlapAsync(connection, "ObjectCustodyPeriods", "ObjectId", null, issues, cancellationToken)
            .ConfigureAwait(false);
        await VerifyNoOverlapAsync(connection, "ObjectLocationPeriods", "ObjectId", null, issues, cancellationToken)
            .ConfigureAwait(false);
        await VerifyNoOverlapAsync(connection, "CharacterTemporalEffects", "CharacterId", null, issues, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyContinuityBoundariesAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var probe in AccessInvariantProbes.Continuity)
        {
            using var command = new AccessCommand(connection, probe.Sql);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count > 0) issues.Add(new IntegrityIssue("integrity.continuity_mismatch", probe.Name, $"{count} cross-continuity relationship(s) found."));
        }

        foreach (var probe in AccessInvariantProbes.Shapes)
        {
            using var command = new AccessCommand(connection, probe.Sql);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count > 0) issues.Add(new IntegrityIssue("integrity.invalid_shape", probe.Name, $"{count} invalid row(s). {probe.Detail}"));
        }

        foreach (var (table, type) in new[]
        {
            ("Projects","Project"), ("Locations","Location"), ("Characters","Character"),
            ("Organizations","Organization"), ("Objects","Object"), ("WorldEvents","WorldEvent")
        })
        {
            using var command = new AccessCommand(connection, $"SELECT COUNT(*) FROM [{table}] AS s INNER JOIN [CanonEntities] AS c ON s.[EntityId]=c.[Id] WHERE c.[EntityType]<>'{type}'");
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (count > 0) issues.Add(new IntegrityIssue("integrity.entity_type_mismatch", table, $"{count} subtype row(s) have the wrong canon type."));
            using var missing = new AccessCommand(connection, $"SELECT COUNT(*) FROM [CanonEntities] AS c LEFT JOIN [{table}] AS s ON c.[Id]=s.[EntityId] WHERE c.[EntityType]='{type}' AND s.[EntityId] IS NULL");
            var missingCount = Convert.ToInt32(await missing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (missingCount > 0) issues.Add(new IntegrityIssue("integrity.missing_subtype", table, $"{missingCount} canon row(s) are missing their subtype row."));
        }
    }

    private static async Task VerifyRelationshipOverlapAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(connection,
            "SELECT r.[Id],r.[SourceCharacterId],r.[TargetCharacterId],r.[RelationshipTypeId],t.[IsDirected],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[PeriodLowerInclusive],r.[PeriodUpperInclusive] FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] WHERE r.[IsDeleted]=False AND t.[AllowsOverlappingPeriods]=False");
        var rows = await command.QueryAsync(reader =>
        {
            var source = reader.GetInt32(1);
            var target = reader.GetInt32(2);
            if (!reader.GetBoolean(4) && source > target) (source, target) = (target, source);
            return new
            {
                Id = reader.GetInt32(0), Source = source, Target = target, Type = reader.GetInt32(3),
                Lower = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                Upper = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                LowerInclusive = reader.GetBoolean(7), UpperInclusive = reader.GetBoolean(8)
            };
        }, cancellationToken).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => (row.Source, row.Target, row.Type)))
        {
            var ordered = group.OrderBy(row => row.Lower ?? DateTime.MinValue).ThenBy(row => row.Id).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            for (var other = index + 1; other < ordered.Length; other++)
            {
                var left = new StoryDate(StoryDateKind.Range, ordered[index].Lower, ordered[index].Upper, ordered[index].LowerInclusive, ordered[index].UpperInclusive);
                var right = new StoryDate(StoryDateKind.Range, ordered[other].Lower, ordered[other].Upper, ordered[other].LowerInclusive, ordered[other].UpperInclusive);
                if (left.Overlaps(right))
                    issues.Add(new IntegrityIssue("integrity.interval_overlap", $"CharacterRelationships.{ordered[index].Id},{ordered[other].Id}", "A relationship type that forbids overlap has overlapping intervals."));
            }
        }
    }

    private static async Task VerifyMembershipOverlapAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(connection,
            "SELECT [Id],[OrganizationId],[CharacterId],[Role],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive] FROM [OrganizationMemberships] WHERE [IsDeleted]=False");
        var rows = await command.QueryAsync(reader => new
        {
            Id = reader.GetInt32(0), OrganizationId = reader.GetInt32(1), CharacterId = reader.GetInt32(2),
            Role = reader.IsDBNull(3) ? string.Empty : Application.TextNormalization.CanonicalKey(reader.GetString(3)),
            Lower = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
            Upper = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
            LowerInclusive = reader.GetBoolean(6), UpperInclusive = reader.GetBoolean(7)
        }, cancellationToken).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => (row.OrganizationId, row.CharacterId, row.Role)))
        {
            var ordered = group.OrderBy(row => row.Lower ?? DateTime.MinValue).ThenBy(row => row.Id).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            for (var other = index + 1; other < ordered.Length; other++)
            {
                var left = new PeriodRow(ordered[index].Id, 0, 0, ordered[index].Lower, ordered[index].Upper, ordered[index].LowerInclusive, ordered[index].UpperInclusive);
                var right = new PeriodRow(ordered[other].Id, 0, 0, ordered[other].Lower, ordered[other].Upper, ordered[other].LowerInclusive, ordered[other].UpperInclusive);
                if (Overlaps(left, right))
                    issues.Add(new IntegrityIssue("integrity.interval_overlap", $"OrganizationMemberships.{left.Id},{right.Id}", "Memberships with the same normalized role overlap."));
            }
        }
    }

    private static async Task VerifyNoOverlapAsync(
        OleDbConnection connection,
        string table,
        string groupColumn,
        string? secondGroupColumn,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken,
        string? filter = null)
    {
        var second = secondGroupColumn is null ? "0 AS [SecondGroup]" : $"[{secondGroupColumn}] AS [SecondGroup]";
        var where = filter is null
            ? " WHERE [IsDeleted]=False"
            : $" WHERE [IsDeleted]=False AND {filter}";
        using var command = new AccessCommand(
            connection,
            $"SELECT [Id], [{groupColumn}], {second}, [PeriodLowerBound], [PeriodUpperBound], [PeriodLowerInclusive], [PeriodUpperInclusive] FROM [{table}]{where}");
        var rows = await command.QueryAsync(MapPeriod, cancellationToken).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => (row.Group, row.SecondGroup)))
        {
            var ordered = group.OrderBy(row => row.Lower ?? DateTime.MinValue).ThenBy(row => row.Id).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                for (var other = index + 1; other < ordered.Length; other++)
                {
                    if (Overlaps(ordered[index], ordered[other]))
                    {
                        issues.Add(new IntegrityIssue(
                            "integrity.interval_overlap",
                            $"{table}.{ordered[index].Id},{ordered[other].Id}",
                            "Exclusive temporal records overlap."));
                    }
                }
            }
        }
    }

    private static PeriodRow MapPeriod(DbDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetInt32(1),
        reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        reader.GetBoolean(5), reader.GetBoolean(6));

    private static bool Overlaps(PeriodRow left, PeriodRow right)
    {
        return new StoryDate(StoryDateKind.Range, left.Lower, left.Upper, left.LowerInclusive, left.UpperInclusive)
            .Overlaps(new StoryDate(StoryDateKind.Range, right.Lower, right.Upper, right.LowerInclusive, right.UpperInclusive));
    }

    private static async Task VerifyOwnershipGroupsAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(
            connection,
            "SELECT p.[Id], p.[OwnerState], o.[PrincipalId], o.[SharePartsPerMillion] " +
            "FROM [ObjectOwnershipPeriods] AS p LEFT JOIN [ObjectOwnershipOwners] AS o ON p.[Id] = o.[OwnershipPeriodId]");
        var rows = await command.QueryAsync(
            reader => (
                Id: reader.GetInt32(0),
                State: reader.GetString(1),
                PrincipalId: reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                Share: reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3)),
            cancellationToken).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => (row.Id, row.State)))
        {
            var owners = group.Count(row => row.PrincipalId is not null);
            var shares = group.Where(row => row.Share is not null).Select(row => row.Share!.Value).ToArray();
            if (group.Key.State is not ("Owned" or "Unknown" or "Unowned"))
            {
                issues.Add(new IntegrityIssue("integrity.ownership_state", $"ObjectOwnershipPeriods.{group.Key.Id}", "Ownership state is not recognized."));
            }
            else if (group.Key.State.Equals("Owned", StringComparison.OrdinalIgnoreCase) && owners == 0)
            {
                issues.Add(new IntegrityIssue("integrity.ownership_missing_owner", $"ObjectOwnershipPeriods.{group.Key.Id}", "Owned period has no owners."));
            }
            else if (!group.Key.State.Equals("Owned", StringComparison.OrdinalIgnoreCase) && owners > 0)
            {
                issues.Add(new IntegrityIssue("integrity.ownership_unexpected_owner", $"ObjectOwnershipPeriods.{group.Key.Id}", "Unknown or unowned period has owners."));
            }

            if (shares.Length > 0 && (shares.Length != owners || shares.Sum() != 1_000_000))
            {
                issues.Add(new IntegrityIssue("integrity.ownership_shares", $"ObjectOwnershipPeriods.{group.Key.Id}", "Shares must be omitted for every owner or total 1,000,000 parts."));
            }
        }
    }

    private static async Task VerifyStoryDatesAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var (table, key, prefix) in new[]
        {
            ("Characters","EntityId","Birth"), ("Characters","EntityId","Death"),
            ("WorldEvents","EntityId","Event"), ("EntityEvents","Id","Event"),
            ("CharacterResidences","Id","Period"), ("OrganizationMemberships","Id","Period"),
            ("OrganizationLocations","Id","Period"), ("CharacterRelationships","Id","Period"),
            ("ObjectOwnershipPeriods","Id","Period"), ("ObjectCustodyPeriods","Id","Period"),
            ("ObjectLocationPeriods","Id","Period"), ("CharacterTemporalEffects","Id","Period"),
            ("RelationshipMembershipPeriods","Id","Period"),
              ("RelationshipMembershipTransitions","Id","Occurred"),
              ("OrganizationMembershipTransitions","Id","Occurred")
        })
        {
            using var command = new AccessCommand(connection,
                $"SELECT [{key}],[{prefix}Kind],[{prefix}LowerBound],[{prefix}UpperBound],[{prefix}LowerInclusive],[{prefix}UpperInclusive],[{prefix}OriginalText],[{prefix}CalendarId] FROM [{table}]");
            var rows = await command.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Kind = reader.GetString(1),
                Lower = reader.IsDBNull(2) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Unspecified),
                Upper = reader.IsDBNull(3) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Unspecified),
                LowerInclusive = reader.GetBoolean(4), UpperInclusive = reader.GetBoolean(5),
                Original = reader.IsDBNull(6) ? null : reader.GetString(6), Calendar = reader.GetString(7)
            }, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!Enum.TryParse<StoryDateKind>(row.Kind, out var kind))
                {
                    issues.Add(new IntegrityIssue("integrity.story_date_kind", $"{table}.{row.Id}.{prefix}", "Story-date kind is not recognized."));
                    continue;
                }
                var validation = new StoryDate(kind, row.Lower, row.Upper, row.LowerInclusive, row.UpperInclusive, row.Original, row.Calendar).Validate();
                foreach (var detail in validation)
                    issues.Add(new IntegrityIssue("integrity.story_date", $"{table}.{row.Id}.{prefix}", detail));
            }
        }
    }

    private static async Task VerifyTimeZonesAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var (table, key, column, allowNull) in new[]
        {
            ("Continuities","Id","DefaultTimeZoneId",false),
            ("ContinuityClocks","ContinuityId","ReferenceTimeZoneId",true),
            ("Locations","EntityId","TimeZoneId",true)
        })
        {
            using var command = new AccessCommand(connection, $"SELECT [{key}],[{column}] FROM [{table}]");
            var rows = await command.QueryAsync(reader => (Id: reader.GetInt32(0), Zone: reader.IsDBNull(1) ? null : reader.GetString(1)), cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.Zone is null && allowNull) continue;
                try
                {
                    if (string.IsNullOrWhiteSpace(row.Zone)) throw new TimeZoneNotFoundException();
                    _ = TimeZoneInfo.FindSystemTimeZoneById(row.Zone);
                }
                catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    issues.Add(new IntegrityIssue("integrity.timezone", $"{table}.{row.Id}.{column}", "Named timezone is not available on this host."));
                }
            }
        }
    }

    private static async Task VerifyImageStorageAsync(
        OleDbConnection connection,
        ICollection<IntegrityIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "EntityImages", "StoryImages" })
        {
            using var images = new AccessCommand(connection,
                $"SELECT [Id],[OriginalRelativePath],[OriginalSha256] FROM [{table}]");
            var rows = await images.QueryAsync(reader => (
                Id: reader.GetInt32(0), Path: reader.GetString(1), Hash: reader.GetString(2)), cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var segments = row.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                if (Path.IsPathRooted(row.Path) || segments.Any(segment => segment == ".."))
                    issues.Add(new IntegrityIssue("integrity.image_path", $"{table}.{row.Id}.OriginalRelativePath", "Image master path is not a safe relative path."));
                try
                {
                    if (row.Hash.Length != 64 || Convert.FromHexString(row.Hash).Length != 32)
                        throw new FormatException();
                }
                catch (FormatException)
                {
                    issues.Add(new IntegrityIssue("integrity.image_hash", $"{table}.{row.Id}.OriginalSha256", "Image master hash is not a SHA-256 value."));
                }
            }
        }

        foreach (var table in new[] { "ImageRenditions", "StoryImageRenditions" })
        {
            using var renditions = new AccessCommand(connection,
                $"SELECT [ImageId],[RenditionKind],[ByteSize],[Content] FROM [{table}]");
            var renditionRows = await renditions.QueryAsync(reader => (
                ImageId: reader.GetInt32(0), Kind: reader.GetString(1), Declared: reader.GetInt32(2),
                Actual: reader.GetBytes(3, 0, null, 0, 0)), cancellationToken).ConfigureAwait(false);
            foreach (var row in renditionRows.Where(row => row.Declared != row.Actual))
                issues.Add(new IntegrityIssue(
                    "integrity.image_rendition_size",
                    $"{table}.{row.ImageId}.{row.Kind}",
                    "Stored rendition byte count does not match its content."));
        }

        using var versionCommand = new AccessCommand(connection,
            "SELECT [ImageKind],[ImageId],[CurrentRevision] FROM [ImageContentVersions]");
        var versions = await versionCommand.QueryAsync(reader => (
            Kind: reader.GetString(0), Id: reader.GetInt32(1),
            Revision: reader.GetInt32(2)), cancellationToken).ConfigureAwait(false);
        var versionMap = versions.ToDictionary(row => (row.Kind, row.Id), row => row.Revision);
        foreach (var (kind, table) in new[]
            { ("EntityImage", "EntityImages"), ("StoryImage", "StoryImages") })
        {
            using var images = new AccessCommand(connection, $"SELECT [Id] FROM [{table}]");
            var ids = await images.QueryAsync(reader => reader.GetInt32(0),
                cancellationToken).ConfigureAwait(false);
            var idSet = ids.ToHashSet();
            foreach (var id in ids.Where(id => !versionMap.ContainsKey((kind, id))))
                issues.Add(new IntegrityIssue("integrity.image_revision_missing",
                    $"{table}.{id}", "The image has no content revision mapping."));
            foreach (var row in versions.Where(row => row.Kind == kind &&
                         !idSet.Contains(row.Id)))
                issues.Add(new IntegrityIssue("integrity.image_revision_orphan",
                    $"ImageContentVersions.{kind}.{row.Id}",
                    "A content revision mapping has no image."));
        }
        using var historyCommand = new AccessCommand(connection,
            "SELECT [ImageKind],[ImageId],[ContentRevision],[OriginalRelativePath]," +
            "[OriginalSha256],[DisplayBytes],[DisplaySha256],[DisplayContent]," +
            "[ThumbnailBytes],[ThumbnailSha256],[ThumbnailContent] FROM [ImageHistoricalContent]");
        var history = await historyCommand.QueryAsync(reader => (
            Kind: reader.GetString(0), Id: reader.GetInt32(1),
            Revision: reader.GetInt32(2), Path: reader.GetString(3),
            Hash: reader.GetString(4), DisplayBytes: reader.GetInt32(5),
            DisplayHash: reader.GetString(6), Display: (byte[])reader.GetValue(7),
            ThumbBytes: reader.GetInt32(8), ThumbHash: reader.GetString(9),
            Thumb: (byte[])reader.GetValue(10)),
            cancellationToken).ConfigureAwait(false);
        foreach (var group in history.GroupBy(row => (row.Kind, row.Id)))
        {
            if (!versionMap.TryGetValue(group.Key, out var latest))
            {
                issues.Add(new IntegrityIssue("integrity.image_revision_orphan",
                    $"ImageHistoricalContent.{group.Key.Kind}.{group.Key.Id}",
                    "Historical image content has no current image."));
                continue;
            }
            var numbers = group.Select(row => row.Revision).Order().ToArray();
            if (numbers.Length != latest - 1 ||
                numbers.Where((value, index) => value != index + 1).Any())
                issues.Add(new IntegrityIssue("integrity.image_revision_gap",
                    $"ImageHistoricalContent.{group.Key.Kind}.{group.Key.Id}",
                    "Retained content revisions are incomplete."));
            foreach (var row in group)
            {
                var segments = row.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                if (Path.IsPathRooted(row.Path) || segments.Any(segment => segment == "..") ||
                    row.Hash.Length != 64 || !row.Hash.All(Uri.IsHexDigit) ||
                    row.DisplayBytes != row.Display.Length ||
                    row.ThumbBytes != row.Thumb.Length ||
                    row.DisplayHash.Length != 64 || !row.DisplayHash.All(Uri.IsHexDigit) ||
                    row.ThumbHash.Length != 64 || !row.ThumbHash.All(Uri.IsHexDigit) ||
                    !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Security.Cryptography.SHA256.HashData(row.Display),
                        Convert.FromHexString(row.DisplayHash)) ||
                    !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Security.Cryptography.SHA256.HashData(row.Thumb),
                        Convert.FromHexString(row.ThumbHash)))
                    issues.Add(new IntegrityIssue("integrity.image_revision_corrupt",
                        $"ImageHistoricalContent.{row.Kind}.{row.Id}.{row.Revision}",
                        "Historical image content metadata or rendition size is invalid."));
            }
        }
        foreach (var row in versions.Where(row => row.Revision > 1 &&
                     !history.Any(item => item.Kind == row.Kind && item.Id == row.Id)))
            issues.Add(new IntegrityIssue("integrity.image_revision_gap",
                $"ImageContentVersions.{row.Kind}.{row.Id}",
                "Earlier image content revisions are missing."));
    }

    private sealed record PeriodRow(int Id, int Group, int SecondGroup, DateTime? Lower, DateTime? Upper, bool LowerInclusive, bool UpperInclusive);
}
