using System.Data.Common;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

internal static class AccessTransactionInvariantGuard
{
    public static async Task VerifyAsync(VaultWriteContext context, CancellationToken token)
    {
        await VerifyCyclesAsync(context, token).ConfigureAwait(false);
        await VerifyPeriodsAsync(context, token).ConfigureAwait(false);
        await VerifyOwnershipAsync(context, token).ConfigureAwait(false);
        await VerifyContinuityAsync(context, token).ConfigureAwait(false);
        await VerifyShapesAsync(context, token).ConfigureAwait(false);
    }

    private static async Task VerifyCyclesAsync(VaultWriteContext context, CancellationToken token)
    {
        using var command = context.Command("SELECT [EntityId],[ParentLocationId] FROM [Locations]");
        var rows = await command.QueryAsync(reader => (Id: reader.GetInt32(0), Parent: reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1)), token).ConfigureAwait(false);
        var parents = rows.ToDictionary(row => row.Id, row => row.Parent);
        foreach (var id in parents.Keys)
        {
            var seen = new HashSet<int>();
            int? cursor = id;
            while (cursor is { } current && parents.TryGetValue(current, out cursor))
                if (!seen.Add(current)) throw new VaultCommandException("location.cycle", "The write would leave a location hierarchy cycle.");
        }
    }

    private static async Task VerifyPeriodsAsync(VaultWriteContext context, CancellationToken token)
    {
        await VerifyPeriodTableAsync(context, "ObjectOwnershipPeriods", "ObjectId", null, token).ConfigureAwait(false);
        await VerifyPeriodTableAsync(context, "ObjectCustodyPeriods", "ObjectId", null, token).ConfigureAwait(false);
        await VerifyPeriodTableAsync(context, "ObjectLocationPeriods", "ObjectId", null, token).ConfigureAwait(false);
        await VerifyPeriodTableAsync(context, "CharacterResidences", "CharacterId", "[IsPrimary]=True", token).ConfigureAwait(false);
        await VerifyPeriodTableAsync(context, "OrganizationLocations", "OrganizationId", "[IsPrimary]=True", token).ConfigureAwait(false);

        using var memberships = context.Command("SELECT [Id],[OrganizationId],[CharacterId],[Role],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive] FROM [OrganizationMemberships] WHERE [IsDeleted]=False");
        var membershipRows = await memberships.QueryAsync(reader => new Period(
            reader.GetInt32(0), $"{reader.GetInt32(1)}:{reader.GetInt32(2)}:{(reader.IsDBNull(3) ? string.Empty : Application.TextNormalization.CanonicalKey(reader.GetString(3)))}",
            reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.GetBoolean(6), reader.GetBoolean(7)), token).ConfigureAwait(false);
        RejectOverlaps("OrganizationMemberships", membershipRows);

        using var relationships = context.Command("SELECT r.[Id],r.[SourceCharacterId],r.[TargetCharacterId],r.[RelationshipTypeId],t.[IsDirected],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[PeriodLowerInclusive],r.[PeriodUpperInclusive] FROM [CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id] WHERE r.[IsDeleted]=False AND t.[AllowsOverlappingPeriods]=False");
        var relationshipRows = await relationships.QueryAsync(reader =>
        {
            var source = reader.GetInt32(1);
            var target = reader.GetInt32(2);
            if (!reader.GetBoolean(4) && source > target) (source, target) = (target, source);
            return new Period(
                reader.GetInt32(0), $"{source}:{target}:{reader.GetInt32(3)}",
                reader.IsDBNull(5) ? null : reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.GetBoolean(7), reader.GetBoolean(8));
        }, token).ConfigureAwait(false);
        RejectOverlaps("CharacterRelationships", relationshipRows);
    }

    private static async Task VerifyPeriodTableAsync(VaultWriteContext context, string table, string groupColumn, string? filter, CancellationToken token)
    {
        var where = filter is null ? "[IsDeleted]=False" : $"[IsDeleted]=False AND {filter}";
        using var command = context.Command($"SELECT [Id],[{groupColumn}],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive] FROM [{table}] WHERE {where}");
        var rows = await command.QueryAsync(reader => new Period(
            reader.GetInt32(0), reader.GetInt32(1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.GetBoolean(4), reader.GetBoolean(5)), token).ConfigureAwait(false);
        RejectOverlaps(table, rows);
    }

    private static void RejectOverlaps(string table, IReadOnlyList<Period> rows)
    {
        foreach (var group in rows.GroupBy(row => row.Group))
        {
            var ordered = group.OrderBy(row => row.Lower ?? DateTime.MinValue).ThenBy(row => row.Id).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            for (var other = index + 1; other < ordered.Length; other++)
                if (ordered[index].AsStoryDate().Overlaps(ordered[other].AsStoryDate()))
                    throw new VaultCommandException("interval.overlap", $"The write would leave overlapping records in {table}.");
        }
    }

    private static async Task VerifyOwnershipAsync(VaultWriteContext context, CancellationToken token)
    {
        using var command = context.Command("SELECT p.[Id],p.[OwnerState],o.[PrincipalId],o.[SharePartsPerMillion] FROM [ObjectOwnershipPeriods] AS p LEFT JOIN [ObjectOwnershipOwners] AS o ON p.[Id]=o.[OwnershipPeriodId] WHERE p.[IsDeleted]=False");
        var rows = await command.QueryAsync(reader => (
            Id: reader.GetInt32(0), State: reader.GetString(1), Principal: reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
            Share: reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3)), token).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(row => (row.Id, row.State)))
        {
            var owners = group.Where(row => row.Principal is not null).ToArray();
            if (group.Key.State.Equals("Owned", StringComparison.OrdinalIgnoreCase) != (owners.Length > 0))
                throw new VaultCommandException("ownership.invalid_group", "Owned periods need owners; unknown and unowned periods cannot have them.");
            var shares = owners.Where(row => row.Share is not null).Select(row => row.Share!.Value).ToArray();
            if (shares.Length != 0 && (shares.Length != owners.Length || shares.Sum() != 1_000_000))
                throw new VaultCommandException("ownership.invalid_shares", "Owner shares must all be omitted or total 1,000,000 parts.");
        }
    }

    private static async Task VerifyContinuityAsync(VaultWriteContext context, CancellationToken token)
    {
        foreach (var probe in AccessInvariantProbes.Continuity)
        {
            using var command = context.Command(probe.Sql);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
                throw new VaultCommandException("continuity.mismatch", $"The write would leave an invalid continuity boundary at {probe.Name}.");
        }
    }

    private static async Task VerifyShapesAsync(VaultWriteContext context, CancellationToken token)
    {
        foreach (var probe in AccessInvariantProbes.Shapes)
        {
            using var command = context.Command(probe.Sql);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
                throw new VaultCommandException("integrity.invalid_shape", probe.Detail);
        }

        foreach (var (table, type) in new[]
        {
            ("Projects","Project"), ("Locations","Location"), ("Characters","Character"),
            ("Organizations","Organization"), ("Objects","Object"), ("WorldEvents","WorldEvent")
        })
        {
            using var missing = context.Command($"SELECT COUNT(*) FROM [CanonEntities] AS c LEFT JOIN [{table}] AS s ON c.[Id]=s.[EntityId] WHERE c.[EntityType]='{type}' AND s.[EntityId] IS NULL");
            if (Convert.ToInt32(await missing.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
                throw new VaultCommandException("integrity.missing_subtype", $"A {type} canon record is missing its subtype row.");
            using var wrong = context.Command($"SELECT COUNT(*) FROM [{table}] AS s INNER JOIN [CanonEntities] AS c ON s.[EntityId]=c.[Id] WHERE c.[EntityType]<>'{type}'");
            if (Convert.ToInt32(await wrong.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0)
                throw new VaultCommandException("integrity.entity_type_mismatch", $"A {table} row has the wrong canon type.");
        }
    }

    private sealed record Period(int Id, string Group, DateTime? Lower, DateTime? Upper, bool LowerInclusive, bool UpperInclusive)
    {
        public StoryDate AsStoryDate() => new(StoryDateKind.Range, Lower, Upper, LowerInclusive, UpperInclusive);
    }
}
