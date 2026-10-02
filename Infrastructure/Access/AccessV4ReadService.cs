using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Shared scoped read policies, continuity reads, and cross-domain projections.</summary>
public sealed partial class AccessV4ReadService(
    IAccessConnectionFactory connectionFactory,
    VaultWriteCoordinator coordinator,
    AccessVaultService vault,
    VaultReferenceService references,
    V4TargetResolver targets,
    VaultMcpResultMapper v3Mapper,
    VaultSessionContext session,
    V4CursorCodec cursors,
    VaultChangeNotifier changes)
{
    private const int MaximumChangePage = 500;

    public Task<V4Page<V4ContinuitySummary>> ContinuitiesAsync(
        V4ContinuityListRequest request, CancellationToken token = default) => MeasureAsync<V4Page<V4ContinuitySummary>>("continuity-list", async () =>
    {
        ValidateLimit(request.Limit, V4ContractLimits.MaximumPageSize);
        var scope = $"continuities:{request.DeletionState}";
        var after = request.Cursor is null ? 0 : checked((int)cursors.Decode(request.Cursor, "page", scope).Position);
        var revision = await RevisionAsync(token).ConfigureAwait(false);
        var rows = await coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new AccessCommand(connection,
                $"SELECT TOP {request.Limit + 1} c.[Id],c.[Name],c.[Description],c.[DefaultTimeZoneId],c.[Version],c.[IsDeleted],k.[CurrentInstantUtc],k.[ReferenceTimeZoneId],k.[Version] " +
                "FROM [Continuities] AS c INNER JOIN [ContinuityClocks] AS k ON c.[Id]=k.[ContinuityId] WHERE c.[Id]>?" +
                DeletionSql("c", request.DeletionState) + " ORDER BY c.[Id]")
                .Add(OleDbType.Integer, after);
            return await command.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Zone = reader.GetString(3), Version = reader.GetInt32(4), Deleted = reader.GetBoolean(5),
                Instant = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                ReferenceZone = reader.IsDBNull(7) ? null : reader.GetString(7), ClockVersion = reader.GetInt32(8)
            }, token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        var page = rows.Take(request.Limit).Select(row => new V4ContinuitySummary(
            row.Name, row.Description, row.Zone,
            Clock(row.Instant, row.ReferenceZone ?? row.Zone, "continuity"), row.Version, row.Deleted, revision)).ToArray();
        return new(page, rows.Count > request.Limit ? cursors.Encode("page", rows[request.Limit - 1].Id, scope) : null,
            rows.Count > request.Limit, revision);
    });


    public Task<V4LocalTimeResult> LocalTimeAsync(V4EntityLocalTimeRequest request, CancellationToken token = default) =>
        MeasureAsync<V4LocalTimeResult>("local-time", async () =>
        {
            var continuity = session.RequireContinuityId();
            var target = await targets.EntityAsync(request.Ref, continuity, token).ConfigureAwait(false);
            LocalCurrentTime? local;
            if (request.At is { } at)
            {
                ValidateZone(at.ReferenceTimeZoneId);
                local = await vault.ResolveLocalCurrentTimeAtAsync(target.StorageKey, at.CurrentTime, at.ReferenceTimeZoneId, token).ConfigureAwait(false);
            }
            else if (session.CurrentTimeOverride is { } overridden)
                local = await vault.ResolveLocalCurrentTimeAtAsync(target.StorageKey, overridden,
                    session.CurrentTimeZoneId ?? throw new InvalidOperationException("The session override timezone is missing."), token).ConfigureAwait(false);
            else local = await vault.ResolveLocalCurrentTimeAsync(target.StorageKey, token).ConfigureAwait(false);
            var revision = await RevisionAsync(token).ConfigureAwait(false);
            var summary = new V4ReferenceSummary(target.Reference, Kind(target.ResourceType), target.Label,
                ContinuityName: session.ContinuityName, IsDeleted: target.IsDeleted);
            if (request.At is null && session.CurrentDateOverride is { } date)
                return new(summary, "DateOnly", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    session.CurrentTimeZoneId, "session", revision);
            return local is null
                ? new(summary, "Unset", null, null, null, revision)
                : new(summary, "Set", local.Instant.ToString("O", CultureInfo.InvariantCulture), local.TimeZoneId,
                    local.TimeZoneSource, revision);
        });

    public async Task<JsonElement> CurrentTemporalStateAsync(string reference,CancellationToken token=default)
    {
        var continuity=session.RequireContinuityId();var target=await targets.EntityAsync(reference,continuity,token).ConfigureAwait(false);
        if (session.CurrentDateOverride is { } date)
            return JsonSerializer.SerializeToElement(new { status = "DateOnly",
                storyDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                message = "Current state can change during this day. Select a clock time for an exact state." });
        EntityTemporalState? state;
        if(session.CurrentTimeOverride is { } instant)
            state=await vault.GetEntityTemporalStateAtAsync(target.StorageKey,instant,session.CurrentTimeZoneId??throw new InvalidOperationException("The session timezone is missing."),token).ConfigureAwait(false);
        else state=await vault.GetEntityTemporalStateAsync(target.StorageKey,token).ConfigureAwait(false);
        if(state is null)return JsonSerializer.SerializeToElement(new{status="Unset",activeRecords=new Dictionary<string,object?>()});
        var active=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        foreach(var category in state.ActiveRecords)
        {
            var mapped=new List<IReadOnlyDictionary<string,object?>>();
            foreach(var row in category.Value)mapped.Add(await v3Mapper.DictionaryAsync(row,null,token).ConfigureAwait(false));
            active[category.Key]=mapped;
        }
        return JsonSerializer.SerializeToElement(new{status="Set",storyReferenceTime=state.StoryReferenceTime,
            artificialInstantUtc=state.ArtificialInstantUtc,referenceTimeZoneId=state.ReferenceTimeZoneId,
            timeSource=session.CurrentTimeOverride is null?"continuity":"session",activeRecords=active});
    }

    public Task<V4DeletePreview> DeletePreviewAsync(V4RecordRefRequest request, CancellationToken token = default) =>
        MeasureAsync<V4DeletePreview>("delete-preview", async () =>
        {
            var continuity = session.RequireContinuityId();
            var reference = string.Equals(request.Ref, session.ContinuityName, StringComparison.Ordinal)
                ? await references.ReferenceAsync("Continuity", continuity, token).ConfigureAwait(false)
                : request.Ref;
            var target = await ResolveAnyAsync(reference, continuity, true, token).ConfigureAwait(false);
            var summary = new V4ReferenceSummary(target.Reference, Kind(target.ResourceType), target.Label,
                ContinuityName: target.ContinuityId is null ? null : session.ContinuityName, IsDeleted: target.IsDeleted);
            IReadOnlyList<BlockingReference> found;
            if (Enum.TryParse<CanonEntityType>(target.ResourceType, true, out _))
            {
                var preview = await vault.PreviewDeleteAsync(target.Id, token).ConfigureAwait(false)
                    ?? throw new V4ResolutionException("record.not_found", "The record was not found.");
                found = preview.Blockers;
            }
            else found = await RecordDeleteBlockersAsync(target, token).ConfigureAwait(false);
            var blockers = found.Select(blocker => summary with
            {
                Context = $"{blocker.ResourceType}: {blocker.Count} dependent record(s)"
            }).ToArray();
            return new(summary, !target.IsDeleted && IsLifecycleSupported(target.ResourceType) && blockers.Length == 0,
                blockers, await RevisionAsync(token).ConfigureAwait(false));
        });

    private async Task<IReadOnlyList<BlockingReference>> RecordDeleteBlockersAsync(
        ResolvedVaultReference target, CancellationToken token)
    {
        var probes = target.ResourceType.ToUpperInvariant() switch
        {
            "CONTINUITY" => new[] { ("CanonEntities", "CanonEntities", "ContinuityId"), ("Claims", "Claims", "ContinuityId") },
            "VARIANTGROUP" => new[] { ("AssignedCanonEntities", "CanonEntities", "VariantGroupId") },
            "RELATIONSHIPTYPE" => new[] { ("CharacterRelationships", "CharacterRelationships", "RelationshipTypeId") },
            "OWNERSHIPPRINCIPAL" => new[] { ("OwnershipLinks", "ObjectOwnershipOwners", "PrincipalId"), ("CustodyPeriods", "ObjectCustodyPeriods", "PrincipalId") },
            _ => []
        };
        if (probes.Length == 0) return [];
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        var blockers = new List<BlockingReference>();
        foreach (var (name, table, column) in probes)
        {
            using var command = new AccessCommand(connection, $"SELECT COUNT(*) FROM [{table}] WHERE [{column}]=?")
                .Add(OleDbType.Integer, target.Id);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count > 0) blockers.Add(new(name, count));
        }
        return blockers;
    }

    private static bool IsLifecycleSupported(string type) =>
        Enum.TryParse<CanonEntityType>(type, true, out _) ||
        Enum.TryParse<VaultRecordType>(type, true, out _) ||
        Enum.TryParse<RelationshipRecordType>(type, true, out _) ||
        type is "ContinuityNote" or "CharacterTemporalEffect" or "EntityImage" or "StoryImage";

    internal async Task<V4ClockView> EffectiveClockAsync(int continuity, CancellationToken token)
    {
        if (session.CurrentDateOverride is { } date)
            return new("DateOnly", null, session.CurrentTimeZoneId, "session", CurrentDate: date);
        if (session.CurrentTimeOverride is { } instant)
            return new("Set", instant, session.CurrentTimeZoneId, "session");
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                "SELECT k.[CurrentInstantUtc],k.[ReferenceTimeZoneId],c.[DefaultTimeZoneId] FROM [ContinuityClocks] AS k INNER JOIN [Continuities] AS c ON k.[ContinuityId]=c.[Id] WHERE k.[ContinuityId]=?")
            .Add(OleDbType.Integer, continuity);
        var rows = await command.QueryAsync(r => new
        {
            Instant = r.IsDBNull(0) ? (DateTime?)null : r.GetDateTime(0),
            Zone = r.IsDBNull(1) ? r.GetString(2) : r.GetString(1)
        }, token).ConfigureAwait(false);
        return rows.Count == 0 ? new("Unset", null, null, "continuity") : Clock(rows[0].Instant, rows[0].Zone, "continuity");
    }

    private static StoryDate ParseInput(V4StoryDateInput input) => V4StoryDateParser.Parse(new(
        Kind: input.Kind is null ? null : Enum.Parse<StoryDateKind>(input.Kind.ToString()!), Value: input.Value,
        Lower: input.Lower, Upper: input.Upper, LowerInclusive: input.LowerInclusive,
        UpperInclusive: input.UpperInclusive, OriginalText: input.OriginalText, CalendarId: input.CalendarId ?? "Gregorian"));
    private static StoryDate ReadDate(DbDataReader reader, int offset, string prefix) => new(
        Enum.Parse<StoryDateKind>(reader.GetString(offset)),
        reader.IsDBNull(offset + 1) ? null : DateTime.SpecifyKind(reader.GetDateTime(offset + 1), DateTimeKind.Unspecified),
        reader.IsDBNull(offset + 2) ? null : DateTime.SpecifyKind(reader.GetDateTime(offset + 2), DateTimeKind.Unspecified),
        reader.GetBoolean(offset + 3), reader.GetBoolean(offset + 4), reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
        reader.GetString(offset + 6));
    internal static V4StoryDateView StoryDateView(StoryDate date)
    {
        // A range can have meaningful times. Preserve them for the viewer while
        // retaining date-only bounds for calendar-precision ranges.
        var timed = date.Kind == StoryDateKind.ExactInstant ||
            date.Kind is StoryDateKind.Range or StoryDateKind.KnownRange or StoryDateKind.UncertainRange or StoryDateKind.Circa or StoryDateKind.Before or StoryDateKind.After &&
            ((date.LowerBound?.TimeOfDay ?? TimeSpan.Zero) != TimeSpan.Zero ||
             (date.UpperBound?.TimeOfDay ?? TimeSpan.Zero) != TimeSpan.Zero);
        var format = timed ? "yyyy-MM-dd'T'HH:mm:ss.fffffff" : "yyyy-MM-dd";
        return new(Enum.Parse<V4StoryDateKind>(date.Kind.ToString()), DisplayDate(date),
            date.LowerBound?.ToString(format, CultureInfo.InvariantCulture),
            date.UpperBound?.ToString(format, CultureInfo.InvariantCulture),
            date.LowerInclusive, date.UpperInclusive, date.CalendarId, date.OriginalText);
    }
    private static string DisplayDate(StoryDate date) => date.OriginalText ?? date.Kind switch
    {
        StoryDateKind.Unknown => "Unknown",
        StoryDateKind.ExactInstant => date.LowerBound?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "Unknown",
        StoryDateKind.ExactDate => date.LowerBound?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown",
        StoryDateKind.Month => date.LowerBound?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? "Unknown",
        StoryDateKind.Year => date.LowerBound?.ToString("yyyy", CultureInfo.InvariantCulture) ?? "Unknown",
        StoryDateKind.Before => $"Before {date.UpperBound:yyyy-MM-dd}", StoryDateKind.After => $"After {date.LowerBound:yyyy-MM-dd}",
        StoryDateKind.Circa when date.UpperBound == date.LowerBound?.AddDays(1) => $"Circa {date.LowerBound:yyyy-MM-dd}",
        StoryDateKind.Circa => $"Circa {date.LowerBound:yyyy-MM-dd}–{date.UpperBound:yyyy-MM-dd}",
        _ => $"{date.LowerBound:yyyy-MM-dd}–{date.UpperBound:yyyy-MM-dd}"
    };
    private static void ValidateZone(string zone)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new VaultValidationException([new("timezone.invalid", "referenceTimeZoneId", "The timezone is not available on this host.")]); }
    }

    private static string EntityLabelExpression(string alias) =>
        $"IIf({alias}.[EntityType]='Project',p.[Name],IIf({alias}.[EntityType]='Location',l.[Name],IIf({alias}.[EntityType]='Character',IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]),IIf({alias}.[EntityType]='Organization',o.[Name],IIf({alias}.[EntityType]='Object',ob.[Name],IIf({alias}.[EntityType]='Species',sp.[Name],w.[Title]))))))";

    internal async Task<string> RevisionAsync(CancellationToken token)
    {
        var value = await coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = new AccessCommand(connection, "SELECT MAX([Id]) FROM [ChangeLog]");
            var scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }, token).ConfigureAwait(false);
        if (session.ContinuityId is { } continuity)
            return cursors.EncodeRevision("changes", value, $"c:{continuity}");
        return cursors.EncodeRevision("revision", value, "vault");
    }

    private V4ReferenceSummary EntitySummary(EntitySummary row, string? continuityName) => new(
        references.ReferenceFromKnownRecord(row.EntityType.ToString(), row.Id, row.Name), Kind(row.EntityType.ToString()),
        row.Name, ContinuityName: continuityName, Version: row.Version, IsDeleted: row.IsDeleted);

    private static long SearchKey(V4RecordKind kind, int id, int subtype = 0) => ((long)(int)kind << 48) | ((long)(ushort)subtype << 32) | (uint)id;
    private static string SearchScope(int continuity, V4SearchRequest request, IReadOnlyList<V4RecordKind> kinds) =>
        $"c:{continuity}|k:{string.Join(',', kinds.Order())}|t:{request.Text?.Trim()}|p:{request.Project}|tags:{string.Join(',', request.Tags ?? [])}|d:{request.DeletionState}|content:{request.IncludeContent}";
    private static string DeletionSql(string alias, V4DeletionState state) => state switch
    {
        V4DeletionState.Active => $" AND {alias}.[IsDeleted]=False",
        V4DeletionState.Deleted => $" AND {alias}.[IsDeleted]=True",
        _ => string.Empty
    };
    private static void ValidateLimit(int value, int max)
    {
        if (value < 1 || value > max) throw new VaultValidationException([new("page.limit", "limit", $"limit must be between 1 and {max}.")]);
    }
    private static string EscapeLike(string value) => value.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);
    private static (string Table, string Name) Subtype(CanonEntityType type) => type switch
    {
        CanonEntityType.Project => ("Projects", "Name"), CanonEntityType.Location => ("Locations", "Name"),
        CanonEntityType.Character => ("Characters", "GivenName"), CanonEntityType.Organization => ("Organizations", "Name"),
        CanonEntityType.Object => ("Objects", "Name"), CanonEntityType.WorldEvent => ("WorldEvents", "Title"),
        CanonEntityType.Species => ("Species", "Name"),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    private static CanonEntityType? EntityType(V4RecordKind kind) => kind switch
    {
        V4RecordKind.Project => CanonEntityType.Project, V4RecordKind.Location => CanonEntityType.Location,
        V4RecordKind.Character => CanonEntityType.Character, V4RecordKind.Organization => CanonEntityType.Organization,
        V4RecordKind.Object => CanonEntityType.Object, V4RecordKind.WorldEvent => CanonEntityType.WorldEvent, V4RecordKind.Species => CanonEntityType.Species, _ => null
    };
    internal static V4RecordKind Kind(string type) => type.ToUpperInvariant() switch
    {
        "CONTINUITY" => V4RecordKind.Continuity, "PROJECT" => V4RecordKind.Project, "LOCATION" => V4RecordKind.Location,
        "CHARACTER" => V4RecordKind.Character, "ORGANIZATION" => V4RecordKind.Organization, "OBJECT" => V4RecordKind.Object,
        "WORLDEVENT" => V4RecordKind.WorldEvent, "SPECIES" => V4RecordKind.Species, "SOURCE" => V4RecordKind.Source,"SOURCESNAPSHOT"=>V4RecordKind.SourceSnapshot,
        "CLAIM"=>V4RecordKind.Claim,"TAG" => V4RecordKind.Tag,"VARIANTGROUP"=>V4RecordKind.VariantGroup,
        "ENTITYNOTE" or "CONTINUITYNOTE" => V4RecordKind.Note, "ENTITYEVENT" => V4RecordKind.EntityEvent,
        "RELATIONSHIPEVENT" => V4RecordKind.RelationshipEvent,
        "RELATIONSHIPMEMBERSHIPPERIOD" => V4RecordKind.RelationshipMembershipPeriod,
        "RELATIONSHIPPARTICIPANT" => V4RecordKind.RelationshipParticipant,
        "ENTITYIMAGE" or "STORYIMAGE" => V4RecordKind.Image, "CHARACTERRELATIONSHIP" or "CHARACTERALIAS" or "ORGANIZATIONALIAS" or "PROJECTASSIGNMENT" or "NOTESOURCE" or "CONTINUITYNOTESOURCE" or "CLAIMEVIDENCE" or "ENTITYEVENTPROJECT" => V4RecordKind.Relationship,"RELATIONSHIPTYPE"=>V4RecordKind.RelationshipType,
        "CHARACTERRESIDENCE"=>V4RecordKind.Residence,"ORGANIZATIONMEMBERSHIP" => V4RecordKind.Membership,"ORGANIZATIONLOCATION"=>V4RecordKind.OrganizationLocation,
        "OBJECTOWNERSHIPPERIOD"=>V4RecordKind.Ownership,"OWNERSHIPPRINCIPAL"=>V4RecordKind.OwnershipPrincipal,
        "OBJECTCUSTODYPERIOD"=>V4RecordKind.Custody,"OBJECTLOCATIONPERIOD"=>V4RecordKind.ObjectLocation,
        "WORLDEVENTPARTICIPANT" or "WORLDEVENTLOCATION" => V4RecordKind.Relationship,
        "CHARACTERTEMPORALEFFECT" => V4RecordKind.TemporalEffect, _ => V4RecordKind.Relationship
    };
    internal static V4ClockView Clock(DateTime? utc, string? zone, string source) => utc is null
        ? new("Unset", null, zone, source)
        : new("Set", new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)), zone, source);
    private static IReadOnlyDictionary<string, JsonElement> ToJsonFields(IReadOnlyDictionary<string, object?> source, params string[] excluded)
    {
        var blocked = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return source.Where(pair => !blocked.Contains(pair.Key)).ToDictionary(
            pair => char.ToLowerInvariant(pair.Key[0]) + pair.Key[1..],
            pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.OrdinalIgnoreCase);
    }
    private static async Task<T> MeasureAsync<T>(string category, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await action().ConfigureAwait(false); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            VaultDiagnostics.Write("query.completed", commandType: category, code: elapsed.TotalMilliseconds >= 500 ? "slow" : "ok");
        }
    }

    private sealed record RelationRow(int Id, string Type, string Label, string? Context, int? Version, bool Deleted, long? SortKey = null, string? Role = null, string? Notes = null)
    {
        public long Key => SortKey ?? Id;
    }
    private sealed record HistoryRow(int Id, DateTime ChangedAtUtc, string? ClientLabel, string Action,
        string RecordType, string RecordKey, int? VersionBefore, int? VersionAfter, string? ChangeJson);
    private sealed record TimelineRow(
        int Id, string Type, string Title, string? Summary, V4TimelineLane Lane, StoryDate Date,
        double? NarrativeOrder, int Version, bool Deleted, int OwnerId,
        int? RelatedEntityId = null, string? RelatedEntityType = null, string? RelatedLabel = null,
        IReadOnlyList<string>? Warnings = null, string Discriminator = "record",
        bool RelationshipDirected = false, string? TransitionDescription = null,
        string? ProjectBoundary = null, StoryDate? StoryBegins = null, StoryDate? StoryEnds = null,
        V4EventRecurrence? Recurrence = null, bool IsOccurrence = false, StoryDate? BirthdayDeath = null,
        V4FactStatus FactStatus = V4FactStatus.Unspecified,
        V4FactStatus StoryBeginsStatus = V4FactStatus.Unspecified, V4FactStatus StoryEndsStatus = V4FactStatus.Unspecified);
    private sealed record TimelineDetailEntry(string Key, TimelineRow Row, string? Boundary = null, DateTime? BoundaryAt = null);
    private sealed record TimelinePageEntry(string Key, V4TimelineItem? Item, V4ReferenceSummary? Undated);
    private sealed record PeriodDefinition(string Table, string Type, string FallbackTitle, V4TimelineLane Lane,
        string OwnerColumn, string? RelatedColumn, string? TitleColumn = null);
}
