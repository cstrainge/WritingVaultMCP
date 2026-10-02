using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>Chronology projection and temporal filters shared by timeline reads.</summary>
public sealed partial class AccessV4ReadService
{
    private readonly SemaphoreSlim timelineCacheGate = new(1, 1);
    private TimelineSnapshot? timelineCache;

    private sealed record TimelineSnapshot(int Continuity, string Revision,
        IReadOnlyList<TimelineRow> Rows, TimelineAssociationIndex Associations)
    {
        private readonly Dictionary<string, TimelineDetailEntry[]> detailQueries = [];
        private readonly object detailGate = new();

        public TimelineDetailEntry[]? Detail(string scope)
        {
            lock (detailGate) return detailQueries.GetValueOrDefault(scope);
        }

        public void RememberDetail(string scope, TimelineDetailEntry[] rows)
        {
            lock (detailGate)
            {
                if (detailQueries.Count >= 4) detailQueries.Clear();
                detailQueries[scope] = rows;
            }
        }
    }

    private static bool TimelineType(string type) => type.ToUpperInvariant() is
        "PROJECT" or "WORLDEVENT" or "ENTITYEVENT" or "RELATIONSHIPEVENT" or "CHARACTER" or "CHARACTERRELATIONSHIP" or
        "RELATIONSHIPMEMBERSHIPPERIOD" or
        "CHARACTERRESIDENCE" or "ORGANIZATIONMEMBERSHIP" or "ORGANIZATIONLOCATION" or
        "OBJECTOWNERSHIPPERIOD" or "OBJECTCUSTODYPERIOD" or "OBJECTLOCATIONPERIOD" or
        "CHARACTERTEMPORALEFFECT" or "CONTINUITYCLOCK";

    public Task<V4TimelineResult> TimelineAsync(V4TimelineRequest request, CancellationToken token = default) =>
        MeasureAsync<V4TimelineResult>("timeline", async () =>
        {
            var continuity = session.RequireContinuityId();
            ValidateLimit(request.Limit, V4ContractLimits.MaximumTimelinePageSize);
            if (request.Kinds is { Count: > 100 } || request.EntityEventKinds is { Count: > 100 } ||
                request.FocusRefs is { Count: > 100 } || request.Tags is { Count: > 100 } ||
                request.Projects is { Count: > 100 } || request.Locations is { Count: > 100 })
                throw new VaultValidationException([new("timeline.filter_too_large", "filters", "A timeline filter list cannot exceed 100 values.")]);
            if (request.EntityEventKinds is { } ownerKinds && ownerKinds.Any(kind =>
                    kind is not (V4RecordKind.Character or V4RecordKind.Location or
                        V4RecordKind.Organization or V4RecordKind.Object or V4RecordKind.Project or V4RecordKind.Species)))
                throw new VaultValidationException([new("timeline.entity_event_kind", "entityEventKinds",
                    "Entity event kinds must be Character, Location, Organization, Object, Project, or Species.")]);
            var from = request.From is null ? null : ParseInput(request.From);
            var to = request.To is null ? null : ParseInput(request.To);
            var viewportLower = from?.LowerBound ?? from?.UpperBound;
            var viewportUpper = to?.UpperBound ?? to?.LowerBound;
            var lowerInclusive = from?.LowerBound is not null ? from.LowerInclusive : from?.UpperInclusive ?? true;
            var upperInclusive = to?.UpperBound is not null ? to.UpperInclusive : to?.LowerInclusive ?? true;
            if (viewportLower is not null && viewportUpper is not null && viewportLower > viewportUpper)
                throw new VaultValidationException([new("timeline.viewport", "from", "The timeline start must not follow its end.")]);
            var revision = await RevisionAsync(token).ConfigureAwait(false);
            var scope = TimelineScope(continuity, request, revision);
            var after = request.Cursor is null ? null : cursors.DecodeText(request.Cursor, "timeline", scope).Position;
            var focus = await ResolveTimelineFocusAsync(request.FocusRefs, continuity, token).ConfigureAwait(false);
            (HashSet<int> Entities, HashSet<string> Relationships)? highlight = request.HighlightRef is null ? null :
                await ResolveTimelineFocusAsync([request.HighlightRef], continuity, token).ConfigureAwait(false);
            var projectFilter = await ResolveEntityFilterAsync(request.Projects, continuity, token, "Project").ConfigureAwait(false);
            var locationFilter = await ResolveEntityFilterAsync(request.Locations, continuity, token, "Location").ConfigureAwait(false);
            var tagFilter = await ResolveTagFilterAsync(request.Tags, token).ConfigureAwait(false);
            var taggedOwners = tagFilter.Count == 0 ? null : await LoadOwnersWithAllTagsAsync(continuity, tagFilter, token).ConfigureAwait(false);
            var snapshot = await TimelineSnapshotAsync(continuity, revision, token).ConfigureAwait(false);
            var rows = snapshot.Rows;
            var associations = snapshot.Associations;
            bool MatchesFocus(TimelineRow row, (HashSet<int> Entities, HashSet<string> Relationships) selected) =>
                selected.Entities.Contains(row.OwnerId) ||
                selected.Entities.Contains(row.RelatedEntityId ?? -1) ||
                associations.Matches(row, selected.Entities) ||
                selected.Relationships.Count > 0 &&
                (row.Type == "CharacterRelationship" && selected.Relationships.Contains(
                     references.ReferenceFromKnownRecord(row.Type, row.Id, row.Title)) ||
                 associations.Related(row).Any(link => selected.Relationships.Contains(link.Ref)));
            bool ContainsHighlight(TimelineRow row) => highlight is not null && MatchesFocus(row, highlight.Value);
            var cachedDetail = request.Resolution == V4TimelineResolution.Aggregate ? null : snapshot.Detail(scope);
            var filtered = new List<(string Key, TimelineRow Row)>();
            var candidates = new List<TimelineRow>();
            if (cachedDetail is null) foreach (var row in rows)
            {
                if (row.Deleted) continue;
                if (request.UndatedOnly && row.Date.Kind != StoryDateKind.Unknown) continue;
                if (!request.IncludeUndated && row.Date.Kind == StoryDateKind.Unknown) continue;
                if (request.Lanes is { Count: > 0 } && !request.Lanes.Contains(row.Lane)) continue;
                if (request.Kinds is { Count: > 0 } && !request.Kinds.Contains(Kind(row.Type))) continue;
                if (row.Type == "EntityEvent" && request.EntityEventKinds is { Count: > 0 } &&
                    (row.RelatedEntityType is null || !request.EntityEventKinds.Contains(Kind(row.RelatedEntityType)))) continue;
                if (!string.IsNullOrWhiteSpace(request.Text) &&
                    !row.Title.Contains(request.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !(row.Summary?.Contains(request.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                if ((focus.Entities.Count > 0 || focus.Relationships.Count > 0) && !MatchesFocus(row, focus)) continue;
                if (taggedOwners is not null && !taggedOwners.Contains(row.OwnerId)) continue;
                if (projectFilter.Count > 0 && !associations.MatchesProjects(row, projectFilter)) continue;
                if (locationFilter.Count > 0 && !locationFilter.Contains(row.RelatedEntityId ?? -1) &&
                    !associations.Matches(row, locationFilter)) continue;
                candidates.Add(row);
            }
            if (cachedDetail is null) foreach (var row in request.ExpandRecurrences ? ExpandRecurringRows(candidates, viewportLower, viewportUpper) : candidates)
            {
                if (!TimelineOverlaps(row.Date, viewportLower, lowerInclusive, viewportUpper, upperInclusive)) continue;
                var key = TimelineKey(row, request.Mode);
                filtered.Add((key, row));
            }
            if (request.Resolution != V4TimelineResolution.Aggregate)
            {
                var sorted = cachedDetail ?? ExpandDetailEntries(filtered, request, viewportLower, lowerInclusive,
                    viewportUpper, upperInclusive).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
                if (cachedDetail is null) snapshot.RememberDetail(scope, sorted);
                var offset = 0;
                if (after is not null)
                {
                    var high = sorted.Length;
                    while (offset < high)
                    {
                        var middle = offset + (high - offset) / 2;
                        if (string.CompareOrdinal(sorted[middle].Key, after) <= 0) offset = middle + 1;
                        else high = middle;
                    }
                }
                var take = Math.Min(request.Limit, sorted.Length - offset);
                var selectedRows = sorted.AsSpan(offset, take).ToArray();
                var detailItems = selectedRows.Where(item => item.Row.Date.Kind != StoryDateKind.Unknown)
                    .Select(item =>
                    {
                        var projected = ProjectTimelineItem(item.Row, associations);
                        if (item.Boundary is null) return projected;
                        return projected with { Boundary = item.Boundary,
                            BoundaryDate = item.BoundaryAt is { } at
                                ? item.Row.Type == "Project" && item.Boundary == "Start" && item.Row.StoryBegins is { } begins
                                    ? StoryDateView(begins)
                                    : item.Row.Type == "Project" && item.Boundary == "End" && item.Row.StoryEnds is { } ends
                                        ? StoryDateView(ends) : BoundaryDateView(item.Row.Date, at) : StoryDateView(item.Row.Date) };
                    }).ToArray();
                var detailUndated = selectedRows.Where(item => item.Row.Date.Kind == StoryDateKind.Unknown)
                    .Select(item => TimelineSummary(item.Row, associations)).ToArray();
                var detailClock = await EffectiveClockAsync(continuity, token).ConfigureAwait(false);
                if (revision != await RevisionAsync(token).ConfigureAwait(false)) throw TimelineChanged();
                var more = offset + take < sorted.Length;
                return new(detailItems, more ? cursors.EncodeText("timeline", selectedRows[^1].Key, scope) : null,
                    more, detailUndated, detailClock, revision, false);
            }
            var entries = new List<TimelinePageEntry>();
            {
                var dated = filtered.Where(item => item.Row.Date.Kind != StoryDateKind.Unknown).ToArray();
                if (dated.Length > 500)
                {
                    var firstTick = dated.Min(item => (item.Row.Date.LowerBound ?? item.Row.Date.UpperBound)!.Value.Ticks);
                    var lastTick = dated.Max(item => (item.Row.Date.UpperBound ?? item.Row.Date.LowerBound)!.Value.Ticks);
                    // One bounded density bucket per lane and time slice. Graph reads never return
                    // tens of thousands of references for a crowded day.
                    var slices = Math.Clamp(request.Limit / Enum.GetValues<V4TimelineLane>().Length, 1, 40);
                    var width = Math.Max(1L, (lastTick - firstTick) / slices + 1);
                    foreach (var group in dated.GroupBy(item => (
                                 item.Row.Lane,
                                 Slice: (int)(((item.Row.Date.LowerBound ?? item.Row.Date.UpperBound)!.Value.Ticks - firstTick) / width),
                                 OpenStart: item.Row.Date.LowerBound is null,
                                 OpenEnd: item.Row.Date.UpperBound is null)))
                    {
                        var members = group.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
                        var first = ProjectTimelineItem(members[0].Row, associations);
                        var lower = group.Key.OpenStart ? null : members.Min(item => item.Row.Date.LowerBound);
                        var upper = group.Key.OpenEnd ? null : members.Max(item => item.Row.Date.UpperBound);
                        var display = group.Key.OpenStart ? $"Before {upper:yyyy-MM-dd}" :
                            group.Key.OpenEnd ? $"After {lower:yyyy-MM-dd}" :
                            $"{lower:yyyy-MM-dd} – {upper:yyyy-MM-dd}";
                        var kind = group.Key.OpenStart ? V4StoryDateKind.Before :
                            group.Key.OpenEnd ? V4StoryDateKind.After : V4StoryDateKind.KnownRange;
                        var interval = new V4StoryDateView(kind, display,
                            lower?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            upper?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true, true, "Gregorian");
                        var sample = members.Take(20).Select(item => TimelineSummary(item.Row, associations)).ToArray();
                        var warnings = members.Length > sample.Length
                            ? new[] { $"Density cluster contains {members.Length} entries; narrow the viewport to inspect them." }
                            : [];
                        entries.Add(new(members[0].Key, new V4TimelineItem(first.Ref, "Aggregate",
                            $"{members.Length} {group.Key.Lane} entries", null, group.Key.Lane, interval,
                            sample, IsDeleted: members.All(item => item.Row.Deleted), Warnings: warnings,
                            ContainsHighlight: members.Any(item => ContainsHighlight(item.Row))), null));
                    }
                }
                else
                {
                    foreach (var group in dated.GroupBy(item => new
                             {
                                 item.Row.Lane, item.Row.Date.Kind, item.Row.Date.LowerBound, item.Row.Date.UpperBound,
                                 item.Row.Date.LowerInclusive, item.Row.Date.UpperInclusive, item.Row.Date.CalendarId,
                                 // Keep each character's join/leave visible to the viewer so it can
                                 // combine only changes to the same relationship at the same instant.
                                 Transition = item.Row.Type is "RelationshipMembershipPeriod" or "OrganizationMembership" &&
                                     item.Row.Discriminator.StartsWith("transition-", StringComparison.Ordinal)
                                     ? $"{item.Row.Id}:{item.Row.Discriminator}" : null
                             }))
                    {
                        var members = group.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
                        var projected = members.Select(item => ProjectTimelineItem(item.Row, associations)).ToArray();
                        var first = projected[0];
                        var aggregate = projected.Length == 1 ? first : new V4TimelineItem(
                            first.Ref, "Aggregate", $"{projected.Length} {group.Key.Lane} items", null, group.Key.Lane, first.Occurred,
                            projected.Take(20).Select(item => new V4ReferenceSummary(item.Ref, Kind(item.Kind), item.Title,
                                ContinuityName: session.ContinuityName, IsDeleted: item.IsDeleted)).ToArray(),
                            projected.Where(item => item.NarrativeOrder is not null).Select(item => item.NarrativeOrder).Min(),
                            projected.All(item => item.IsDeleted),
                            projected.SelectMany(item => item.Warnings ?? []).Distinct(StringComparer.Ordinal).ToArray());
                        entries.Add(new(members[0].Key, aggregate with
                        {
                            ContainsHighlight = members.Any(item => ContainsHighlight(item.Row))
                        }, null));
                    }
                }
                if (request.IncludeUndated)
                    entries.AddRange(filtered.Where(item => item.Row.Date.Kind == StoryDateKind.Unknown)
                        .Select(item => new TimelinePageEntry(item.Key, null, TimelineSummary(item.Row, associations))));
            }
            var ordered = entries.Where(item => after is null || string.CompareOrdinal(item.Key, after) > 0)
                .OrderBy(item => item.Key, StringComparer.Ordinal).Take(request.Limit + 1).ToArray();
            var selected = ordered.Take(request.Limit).ToArray();
            var items = selected.Where(item => item.Item is not null).Select(item => item.Item!).ToArray();
            var undated = selected.Where(item => item.Undated is not null).Select(item => item.Undated!).ToArray();
            var clock = await EffectiveClockAsync(continuity, token).ConfigureAwait(false);
            if (!string.Equals(revision, await RevisionAsync(token).ConfigureAwait(false), StringComparison.Ordinal))
                throw TimelineChanged();
            return new(items, ordered.Length > request.Limit ? cursors.EncodeText("timeline", selected[^1].Key, scope) : null,
                ordered.Length > request.Limit, undated, clock, revision, request.Resolution == V4TimelineResolution.Aggregate);
        });

    private V4TimelineItem ProjectTimelineItem(TimelineRow row, TimelineAssociationIndex associations)
    {
        var reference = row.Type == "Character" && associations.References.TryGetValue(row.Id, out var character)
            ? character.Ref : references.ReferenceFromKnownRecord(row.Type, row.Id,
                row.Type == "RelationshipMembershipPeriod" ? "membership period" : row.Title);
        var related = new List<V4ReferenceSummary>();
        if (associations.References.TryGetValue(row.OwnerId, out var owner) && owner.Ref != reference)
            related.Add(owner);
        if (row.RelatedEntityId is { } relatedId)
        {
            if (associations.References.TryGetValue(relatedId, out var linked)) related.Add(linked);
            else if (row.RelatedEntityType is { } relatedType && row.RelatedLabel is { } relatedLabel)
                related.Add(new(references.ReferenceFromKnownRecord(relatedType, relatedId, relatedLabel),
                    Kind(relatedType), relatedLabel, ContinuityName: session.ContinuityName));
        }
        related.AddRange(associations.Related(row));
        var projects = associations.Projects(row);
        related.AddRange(projects);
        var boundary = row.Type is "RelationshipMembershipPeriod" or "OrganizationMembership" ? row.Discriminator switch
        {
            "transition-join" => "Start",
            "transition-leave" => "End",
            _ => null
        } : null;
        return new(reference, row.Type, TimelineTitle(row, associations), row.Summary, row.Lane, StoryDateView(row.Date),
            related.DistinctBy(item => item.Ref).ToArray(), row.NarrativeOrder, row.Deleted, row.Warnings, projects,
            Boundary: boundary, HasCustomDescription: !string.IsNullOrWhiteSpace(row.TransitionDescription),
            IsMembershipTransition: boundary is not null,
            StoryBegins: row.StoryBegins is null ? null : StoryDateView(row.StoryBegins),
            StoryEnds: row.StoryEnds is null ? null : StoryDateView(row.StoryEnds),
            Recurrence: row.Recurrence, IsOccurrence: row.IsOccurrence);
    }

    private static IEnumerable<TimelineDetailEntry> ExpandDetailEntries(
        IEnumerable<(string Key, TimelineRow Row)> filtered, V4TimelineRequest request,
        DateTime? viewportLower, bool lowerInclusive, DateTime? viewportUpper, bool upperInclusive)
    {
        foreach (var (key, row) in filtered)
        {
            var date = row.Date;
            if (row.Type is "RelationshipMembershipPeriod" or "OrganizationMembership" &&
                row.Discriminator.StartsWith("transition-", StringComparison.Ordinal))
            {
                // This is one uncertain transition date, not the duration of
                // the membership. Never split its fuzzy bounds into invented
                // start and end events.
                yield return new(key, row,
                    row.Discriminator == "transition-join" ? "Start" : "End");
                continue;
            }
            if (row.Type == "RelationshipMembershipPeriod" &&
                date.Kind is not (StoryDateKind.ExactDate or StoryDateKind.ExactInstant) ||
                row.Type == "CharacterRelationship" &&
                !row.Discriminator.StartsWith("active-", StringComparison.Ordinal))
            {
                // An older whole-period range or manually authored relationship
                // date does not establish the actual join and leave moments.
                yield return new(key, row);
                continue;
            }
            if ((row.Type == "RelationshipMembershipPeriod" ||
                 row.Type == "CharacterRelationship" &&
                 row.Discriminator.StartsWith("active-", StringComparison.Ordinal)) &&
                (date.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant or StoryDateKind.KnownRange) &&
                date.LowerBound is { } entry && date.UpperBound is { } exit)
            {
                var exitAt = !date.UpperInclusive && exit.TimeOfDay == TimeSpan.Zero
                    ? exit.AddTicks(-1) : exit;
                if (BoundaryInWindow(entry))
                    yield return new(TimelineBoundaryKey(row, request.Mode, entry, "0"), row, "Start", entry);
                if (BoundaryInWindow(exitAt))
                    yield return new(TimelineBoundaryKey(row, request.Mode, exitAt, "1"), row, "End", exitAt);
                continue;
            }
            if (!request.ExpandRanges || date.Kind != StoryDateKind.KnownRange ||
                !date.CalendarId.Equals("Gregorian", StringComparison.OrdinalIgnoreCase) ||
                date.LowerBound is not { } start || date.UpperBound is not { } end)
            {
                yield return new(key, row);
                continue;
            }

            // A date-only exclusive upper bound at midnight names the day after
            // the last included day. Do not turn a single-day range into two rows.
            var endAt = !date.UpperInclusive && end.TimeOfDay == TimeSpan.Zero
                ? end.AddTicks(-1) : end;
            if (start.Date == endAt.Date)
            {
                yield return new(key, row);
                continue;
            }

            var startVisible = BoundaryInWindow(start);
            var endVisible = BoundaryInWindow(endAt);
            if (startVisible) yield return new(TimelineBoundaryKey(row, request.Mode, start, "0"), row, "Start", start);
            if (endVisible) yield return new(TimelineBoundaryKey(row, request.Mode, endAt, "1"), row, "End", endAt);
            if (!startVisible && !endVisible && (viewportLower is not null || viewportUpper is not null))
            {
                // A selected window can lie wholly inside a long range. Keep one
                // contextual entry in that window without inventing a boundary.
                var visibleAt = viewportLower ?? viewportUpper!.Value.AddTicks(-1);
                yield return new(TimelineBoundaryKey(row, request.Mode, visibleAt, "2"), row, "Ongoing", visibleAt);
            }

            bool BoundaryInWindow(DateTime at) =>
                (viewportLower is null || at > viewportLower || at == viewportLower && lowerInclusive) &&
                (viewportUpper is null || at < viewportUpper || at == viewportUpper && upperInclusive);
        }
    }

    private static V4StoryDateView BoundaryDateView(StoryDate range, DateTime at)
    {
        // An exclusive midnight upper bound closes the preceding calendar day.
        // Do not present its last tick as an invented 11:59:59 PM event time.
        var dateOnlyEnd = !range.UpperInclusive && range.UpperBound is { } upper &&
            upper.TimeOfDay == TimeSpan.Zero && at.Date == upper.AddTicks(-1).Date;
        var timed = !dateOnlyEnd && StoryDateView(range).Lower?.Contains('T') == true;
        var value = at.ToString(timed ? "yyyy-MM-dd'T'HH:mm:ss.fffffff" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new(timed ? V4StoryDateKind.ExactInstant : V4StoryDateKind.ExactDate,
            value, value, value, true, true, range.CalendarId);
    }

    private static string TimelineBoundaryKey(TimelineRow row, V4TimelineMode mode, DateTime at, string phase)
    {
        if (mode == V4TimelineMode.Narrative && row.NarrativeOrder is not null)
            return $"{TimelineKey(row, mode)}|{phase}";
        return $"1{at.Ticks.ToString("D19", CultureInfo.InvariantCulture)}|{row.Type.ToUpperInvariant()}|{row.Id:D10}|{row.Discriminator}|{phase}";
    }

    private static string TimelineTitle(TimelineRow row, TimelineAssociationIndex associations)
    {
        if (row.Type == "RelationshipMembershipPeriod")
        {
            var name = associations.References.GetValueOrDefault(row.OwnerId)?.Label;
            var title = name is null ? $"Membership in {row.Title}" : $"{name} in {row.Title}";
            return row.Discriminator switch
            {
                "transition-join" => row.TransitionDescription ?? $"{name ?? "A character"} entered a relationship.",
                "transition-leave" => row.TransitionDescription ?? $"{name ?? "A character"} left a relationship.",
                _ => title
            };
        }
        if (row.Type == "OrganizationMembership" &&
            row.Discriminator.StartsWith("transition-", StringComparison.Ordinal))
        {
            var character = row.RelatedEntityId is { } id
                ? associations.References.GetValueOrDefault(id)?.Label : null;
            var organization = associations.References.GetValueOrDefault(row.OwnerId)?.Label;
            return row.TransitionDescription ?? (row.Discriminator == "transition-join"
                ? $"{character ?? "A character"} joined organization {organization ?? "unknown"}."
                : $"{character ?? "A character"} left organization {organization ?? "unknown"}.");
        }
        if (row.Type != "CharacterRelationship") return row.Title;
        var members = associations.Related(row)
            .Where(link => link.Kind == V4RecordKind.Character)
            .Select(link => link.Label).Distinct(StringComparer.Ordinal).ToArray();
        if (!row.RelationshipDirected && members.Length >= 2)
            return members.Length == 2
                ? $"{members[0]} and {members[1]} — {row.Title}"
                : $"{string.Join(", ", members.Take(3))}" +
                  $"{(members.Length > 3 ? $" +{members.Length - 3} more" : "")} — {row.Title}";
        var first = associations.References.GetValueOrDefault(row.OwnerId)?.Label;
        var second = row.RelatedEntityId is { } other
            ? associations.References.GetValueOrDefault(other)?.Label : null;
        return first is not null && second is not null
            ? row.RelationshipDirected
                ? $"{first} — {row.Title} — {second}"
                : $"{first} and {second} — {row.Title}"
            : row.Title;
    }

    private V4ReferenceSummary TimelineSummary(TimelineRow row, TimelineAssociationIndex associations) => new(
        row.Type == "Character" && associations.References.TryGetValue(row.Id, out var character)
            ? character.Ref : references.ReferenceFromKnownRecord(row.Type, row.Id,
                row.Type == "RelationshipMembershipPeriod" ? "membership period" : row.Title),
        Kind(row.Type), TimelineTitle(row, associations), ContinuityName: session.ContinuityName,
        Version: row.Version, IsDeleted: row.Deleted);

    private async Task<TimelineSnapshot> TimelineSnapshotAsync(int continuity, string revision, CancellationToken token)
    {
        var cached = Volatile.Read(ref timelineCache);
        if (cached is not null && cached.Continuity == continuity && cached.Revision == revision) return cached;
        await timelineCacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = timelineCache;
            if (cached is not null && cached.Continuity == continuity && cached.Revision == revision) return cached;
            var loaded = await coordinator.ExecuteConsistentReadAsync(async () =>
            {
                if (await RevisionAsync(token).ConfigureAwait(false) != revision)
                    throw TimelineChanged();
                var records = await LoadTimelineRowsAsync(continuity, token).ConfigureAwait(false);
                var associations = await LoadTimelineAssociationsAsync(records, token).ConfigureAwait(false);
                if (await RevisionAsync(token).ConfigureAwait(false) != revision)
                    throw TimelineChanged();
                return new TimelineSnapshot(continuity, revision, records, associations);
            }, token).ConfigureAwait(false);
            Volatile.Write(ref timelineCache, loaded);
            return loaded;
        }
        finally { timelineCacheGate.Release(); }
    }

    private static VaultValidationException TimelineChanged() => new([
        new("timeline.revision_changed", "cursor", "The timeline changed while it was being read. Restart this timeline query from the first page.")]);

    private sealed class TimelineAssociationIndex
    {
        private readonly Dictionary<(string Type, int Id), List<(int Id, V4ReferenceSummary Reference)>> links = [];
        private readonly Dictionary<(string Type, int Id), List<(int Id, V4ReferenceSummary Reference)>> projects = [];
        public Dictionary<int, V4ReferenceSummary> References { get; } = [];

        public void Add(string type, int eventId, int targetId, bool project = false, string? role = null, string? notes = null)
        {
            if (!References.TryGetValue(targetId, out var target)) return;
            var index = project ? projects : links;
            var key = (type, eventId);
            if (!index.TryGetValue(key, out var group)) index[key] = group = [];
            group.Add((targetId, target with { AssociationRole = role, AssociationNotes = notes }));
        }

        public void AddDirect(string type, int eventId, int localId, V4ReferenceSummary target)
        {
            var key = (type, eventId);
            if (!links.TryGetValue(key, out var group)) links[key] = group = [];
            group.Add((-localId, target));
        }

        public bool Matches(TimelineRow row, IReadOnlySet<int> ids) =>
            (links.TryGetValue((row.Type, row.Id), out var related) && related.Any(item => ids.Contains(item.Id))) ||
            (projects.TryGetValue((row.Type, row.Id), out var assigned) && assigned.Any(item => ids.Contains(item.Id)));

        public bool MatchesProjects(TimelineRow row, IReadOnlySet<int> ids) =>
            projects.TryGetValue((row.Type, row.Id), out var assigned) && assigned.Any(item => ids.Contains(item.Id));

        public IReadOnlyList<V4ReferenceSummary> Related(TimelineRow row) =>
            links.TryGetValue((row.Type, row.Id), out var related)
                ? related.Select(item => item.Reference).DistinctBy(item => item.Ref).ToArray() : [];

        public IReadOnlyList<V4ReferenceSummary> Projects(TimelineRow row) =>
            projects.TryGetValue((row.Type, row.Id), out var assigned)
                ? assigned.Select(item => item.Reference).DistinctBy(item => item.Ref).ToArray() : [];
    }

    private async Task<TimelineAssociationIndex> LoadTimelineAssociationsAsync(
        IReadOnlyList<TimelineRow> timelineRows, CancellationToken token)
    {
        return await coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(token).ConfigureAwait(false);
            var index = new TimelineAssociationIndex();
            var activeRows = timelineRows.Where(row => !row.Deleted)
                .Select(row => (row.Type, row.Id)).ToHashSet();
            var neededIds = new HashSet<int>();
            var relationshipLinks = new List<(string Type, int Record, int Relationship)>();
            foreach (var row in timelineRows)
            {
                if (row.Deleted) continue;
                if (row.Type != "WorldEvent") neededIds.Add(row.OwnerId);
                if (row.RelatedEntityId is { } relatedId) neededIds.Add(relatedId);
            }
            var pendingLinks = new List<(string Type, int Event, int Target, bool Project, string? Role, string? Notes)>();

            async Task AddLinks(string sql, string type, bool projects = false, bool context = false)
            {
                using var command = new AccessCommand(connection, sql);
                var rows = await command.QueryAsync(r => (Event: r.GetInt32(0), Target: r.GetInt32(1),
                    Role: context && !r.IsDBNull(2) ? r.GetString(2) : null,
                    Notes: context && !r.IsDBNull(3) ? r.GetString(3) : null), token).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    if (!activeRows.Contains((type, row.Event))) continue;
                    neededIds.Add(row.Target);
                    pendingLinks.Add((type, row.Event, row.Target, projects, row.Role,
                        row.Notes is { Length: > 255 } ? row.Notes[..255] + "…" : row.Notes));
                }
            }

            await AddLinks("SELECT [WorldEventId],[ParticipantEntityId],[Role],[Notes] FROM [WorldEventParticipants] WHERE [IsDeleted]=False",
                "WorldEvent", context: true).ConfigureAwait(false);
            await AddLinks("SELECT [WorldEventId],[LocationId],[Role],[Notes] FROM [WorldEventLocations] WHERE [IsDeleted]=False",
                "WorldEvent", context: true).ConfigureAwait(false);
            await AddLinks("SELECT [MemberEntityId],[ProjectId],[Role],[Notes] FROM [ProjectEntities] WHERE [IsDeleted]=False",
                "WorldEvent", projects: true, context: true).ConfigureAwait(false);
            await AddLinks("SELECT [EntityEventId],[ProjectId],[Role],[Notes] FROM [EntityEventProjects] WHERE [IsDeleted]=False",
                "EntityEvent", projects: true, context: true).ConfigureAwait(false);
            await AddLinks("SELECT [RelationshipEventId],[ProjectId],[Role],[Notes] FROM [RelationshipEventProjects] WHERE [IsDeleted]=False",
                "RelationshipEvent", projects: true, context: true).ConfigureAwait(false);
            await AddLinks("SELECT [Id],[WorldEventId] FROM [EntityEvents] WHERE [WorldEventId] Is Not Null AND [IsDeleted]=False",
                "EntityEvent").ConfigureAwait(false);
            await AddLinks("SELECT [Id],[WorldEventId] FROM [RelationshipEvents] WHERE [WorldEventId] Is Not Null AND [IsDeleted]=False",
                "RelationshipEvent").ConfigureAwait(false);
            await AddLinks("SELECT e.[Id],p.[CharacterId] FROM [RelationshipEvents] AS e INNER JOIN [RelationshipParticipants] AS p ON e.[RelationshipId]=p.[RelationshipId] WHERE e.[IsDeleted]=False AND p.[IsDeleted]=False",
                "RelationshipEvent").ConfigureAwait(false);
            await AddLinks("SELECT r.[Id],p.[CharacterId] FROM [CharacterRelationships] AS r INNER JOIN [RelationshipParticipants] AS p ON r.[Id]=p.[RelationshipId] WHERE r.[IsDeleted]=False AND p.[IsDeleted]=False",
                "CharacterRelationship").ConfigureAwait(false);
            using (var relationshipEvents = new AccessCommand(connection,
                        "SELECT [Id],[RelationshipId] FROM [RelationshipEvents] WHERE [IsDeleted]=False"))
                foreach (var link in await relationshipEvents.QueryAsync(r => (Event: r.GetInt32(0), Relationship: r.GetInt32(1)), token).ConfigureAwait(false))
                    if (activeRows.Contains(("RelationshipEvent", link.Event)))
                        relationshipLinks.Add(("RelationshipEvent", link.Event, link.Relationship));
            using (var membershipPeriods = new AccessCommand(connection,
                       "SELECT m.[Id],p.[RelationshipId] FROM [RelationshipMembershipPeriods] AS m " +
                       "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] " +
                       "WHERE m.[IsDeleted]=False AND p.[IsDeleted]=False"))
                foreach (var link in await membershipPeriods.QueryAsync(r => (Period: r.GetInt32(0), Relationship: r.GetInt32(1)), token).ConfigureAwait(false))
                    if (activeRows.Contains(("RelationshipMembershipPeriod", link.Period)))
                        relationshipLinks.Add(("RelationshipMembershipPeriod", link.Period, link.Relationship));
            using (var ownership = new AccessCommand(connection,
                       "SELECT x.[OwnershipPeriodId],p.[Id],p.[CharacterId],p.[OrganizationId],p.[Label] FROM [ObjectOwnershipOwners] AS x INNER JOIN [OwnershipPrincipals] AS p ON x.[PrincipalId]=p.[Id] WHERE p.[IsDeleted]=False"))
                foreach (var owner in await ownership.QueryAsync(r => (Period: r.GetInt32(0), Principal: r.GetInt32(1),
                             Character: r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
                             Organization: r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                             Label: r.IsDBNull(4) ? null : r.GetString(4)), token).ConfigureAwait(false))
                    AddPrincipal("ObjectOwnershipPeriod", owner.Period, owner.Principal,
                        owner.Character, owner.Organization, owner.Label);
            using (var custody = new AccessCommand(connection,
                       "SELECT c.[Id],p.[Id],p.[CharacterId],p.[OrganizationId],p.[Label] FROM [ObjectCustodyPeriods] AS c INNER JOIN [OwnershipPrincipals] AS p ON c.[PrincipalId]=p.[Id] WHERE c.[IsDeleted]=False AND p.[IsDeleted]=False"))
                foreach (var custodian in await custody.QueryAsync(r => (Period: r.GetInt32(0), Principal: r.GetInt32(1),
                             Character: r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
                             Organization: r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                             Label: r.IsDBNull(4) ? null : r.GetString(4)), token).ConfigureAwait(false))
                    AddPrincipal("ObjectCustodyPeriod", custodian.Period, custodian.Principal,
                        custodian.Character, custodian.Organization, custodian.Label);

            void AddPrincipal(string type, int period, int principal, int? character, int? organization, string? label)
            {
                if (!activeRows.Contains((type, period))) return;
                if (character is { } characterId)
                {
                    neededIds.Add(characterId);
                    pendingLinks.Add((type, period, characterId, false, null, null));
                }
                else if (organization is { } organizationId)
                {
                    neededIds.Add(organizationId);
                    pendingLinks.Add((type, period, organizationId, false, null, null));
                }
                else if (!string.IsNullOrWhiteSpace(label))
                    index.AddDirect(type, period, principal, new V4ReferenceSummary(
                        references.ReferenceFromKnownRecord("OwnershipPrincipal", principal, label),
                        V4RecordKind.OwnershipPrincipal, label, ContinuityName: session.ContinuityName));
            }
            foreach (var ids in neededIds.Chunk(100))
                foreach (var row in await EntityRowsAsync(connection, ids, V4DeletionState.All, token).ConfigureAwait(false))
                    index.References[row.Id] = new(references.ReferenceFromKnownRecord(row.Type, row.Id, row.Label),
                        Kind(row.Type), row.Label, ContinuityName: session.ContinuityName,
                        Version: row.Version, IsDeleted: row.Deleted);
            foreach (var row in timelineRows.Where(row => !row.Deleted &&
                (row.Type == "Project" || row.Type == "EntityEvent" && row.RelatedEntityType == "Project")))
                index.Add(row.Type, row.Id, row.OwnerId, project: true);
            foreach (var link in pendingLinks)
                index.Add(link.Type, link.Event, link.Target, link.Project, link.Role, link.Notes);
            var relationshipLabels = timelineRows.Where(row => row.Type == "CharacterRelationship")
                .GroupBy(row => row.Id)
                .ToDictionary(group => group.Key, group => TimelineTitle(group.First(), index));
            foreach (var link in relationshipLinks)
                index.AddDirect(link.Type, link.Record, link.Relationship,
                    new V4ReferenceSummary(
                        references.ReferenceFromKnownRecord("CharacterRelationship", link.Relationship, "relationship"),
                        V4RecordKind.Relationship,
                        relationshipLabels.GetValueOrDefault(link.Relationship, "Relationship"),
                        ContinuityName: session.ContinuityName));
            return index;
        }, token).ConfigureAwait(false);
    }

    private static StoryDate ProjectSpan(StoryDate? start, StoryDate? end)
    {
        var display = $"{(start is null ? "Unspecified start" : DisplayDate(start))} – {(end is null ? "Unspecified end" : DisplayDate(end))}";
        return new(start?.LowerBound is null && end?.UpperBound is null ? StoryDateKind.Unknown : StoryDateKind.KnownRange,
            start?.LowerBound, end?.UpperBound, start?.LowerInclusive ?? true, end?.UpperInclusive ?? false,
            display, start?.CalendarId ?? end?.CalendarId ?? "Gregorian");
    }

    private async Task<IReadOnlyList<TimelineRow>> LoadTimelineRowsAsync(int continuity, CancellationToken token)
    {
        return await coordinator.ExecuteConsistentReadAsync(async () =>
        {
            await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
            var rows = new List<TimelineRow>();
            async Task AddEntityDates(string sql, string type, V4TimelineLane lane, string prefix, bool narrative)
            {
                using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, continuity);
                rows.AddRange(await command.QueryAsync(reader => new TimelineRow(
                    reader.GetInt32(0), type, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), lane,
                    ReadDate(reader, 3, prefix), narrative && !reader.IsDBNull(10) ? reader.GetDouble(10) : null,
                    reader.GetInt32(narrative ? 11 : 10), reader.GetBoolean(narrative ? 12 : 11), reader.GetInt32(0)), token).ConfigureAwait(false));
            }
            await AddEntityDates(
                "SELECT w.[EntityId],w.[Title],w.[Description],w.[EventKind],w.[EventLowerBound],w.[EventUpperBound],w.[EventLowerInclusive],w.[EventUpperInclusive],w.[EventOriginalText],w.[EventCalendarId],w.[NarrativeOrder],c.[Version],c.[IsDeleted] FROM [WorldEvents] AS w INNER JOIN [CanonEntities] AS c ON w.[EntityId]=c.[Id] WHERE c.[ContinuityId]=?",
                "WorldEvent", V4TimelineLane.WorldEvents, "Event", true).ConfigureAwait(false);
            using (var events = new AccessCommand(connection,
                       "SELECT e.[Id],e.[Title],e.[Description],e.[EventKind],e.[EventLowerBound],e.[EventUpperBound],e.[EventLowerInclusive],e.[EventUpperInclusive],e.[EventOriginalText],e.[EventCalendarId],e.[NarrativeOrder],e.[Version],(e.[IsDeleted] OR c.[IsDeleted]),e.[EntityId],c.[EntityType]," + EntityLabelExpression("c") + ",e.[ProjectBoundary]" +
                       " FROM ((((((( [EntityEvents] AS e INNER JOIN [CanonEntities] AS c ON e.[EntityId]=c.[Id]) LEFT JOIN [Projects] AS p ON c.[Id]=p.[EntityId]) LEFT JOIN [Locations] AS l ON c.[Id]=l.[EntityId]) LEFT JOIN [Characters] AS ch ON c.[Id]=ch.[EntityId]) LEFT JOIN [Organizations] AS o ON c.[Id]=o.[EntityId]) LEFT JOIN [Objects] AS ob ON c.[Id]=ob.[EntityId]) LEFT JOIN [WorldEvents] AS w ON c.[Id]=w.[EntityId]) LEFT JOIN [Species] AS sp ON c.[Id]=sp.[EntityId] WHERE c.[ContinuityId]=?")
                   .Add(OleDbType.Integer, continuity))
            {
                rows.AddRange(await events.QueryAsync(r => new TimelineRow(r.GetInt32(0), "EntityEvent", r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2), V4TimelineLane.EntityEvents, ReadDate(r, 3, "Event"),
                    r.IsDBNull(10) ? null : r.GetDouble(10), r.GetInt32(11), Convert.ToBoolean(r.GetValue(12), CultureInfo.InvariantCulture), r.GetInt32(13),
                    r.GetInt32(13), r.GetString(14), r.GetString(15), ProjectBoundary: r.IsDBNull(16) ? null : r.GetString(16)), token).ConfigureAwait(false));
            }
            using (var projects = new AccessCommand(connection,
                "SELECT p.[EntityId],p.[Name],c.[Version],c.[IsDeleted] FROM [Projects] AS p INNER JOIN [CanonEntities] AS c ON p.[EntityId]=c.[Id] WHERE c.[ContinuityId]=?").Add(OleDbType.Integer, continuity))
            {
                var boundaries = rows.Where(row => row.Type == "EntityEvent" && !row.Deleted && row.ProjectBoundary is not null)
                    .ToLookup(row => row.OwnerId);
                foreach (var project in await projects.QueryAsync(r => (Id: r.GetInt32(0), Name: r.GetString(1), Version: r.GetInt32(2), Deleted: r.GetBoolean(3)), token))
                {
                    var start = boundaries[project.Id].SingleOrDefault(row => row.ProjectBoundary == "StoryBegins")?.Date;
                    var end = boundaries[project.Id].SingleOrDefault(row => row.ProjectBoundary == "StoryEnds")?.Date;
                    if (start is null && end is null) continue;
                    var span = ProjectSpan(start, end);
                    rows.Add(new(project.Id, "Project", project.Name, span.OriginalText, V4TimelineLane.Projects, span,
                        null, project.Version, project.Deleted, project.Id, StoryBegins: start, StoryEnds: end));
                }
            }
            using (var relationshipEvents = new AccessCommand(connection,
                       "SELECT e.[Id],e.[Title],e.[Description],e.[EventKind],e.[EventLowerBound],e.[EventUpperBound]," +
                       "e.[EventLowerInclusive],e.[EventUpperInclusive],e.[EventOriginalText],e.[EventCalendarId]," +
                       "e.[NarrativeOrder],e.[Version],e.[IsDeleted],r.[SourceCharacterId],r.[IsDeleted] " +
                       "FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] " +
                       "WHERE r.[ContinuityId]=?")
                   .Add(OleDbType.Integer, continuity))
            {
                rows.AddRange(await relationshipEvents.QueryAsync(r => new TimelineRow(r.GetInt32(0),
                    "RelationshipEvent", r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    V4TimelineLane.RelationshipEvents, ReadDate(r, 3, "Event"),
                    r.IsDBNull(10) ? null : r.GetDouble(10), r.GetInt32(11),
                    r.GetBoolean(12) || r.GetBoolean(14), r.GetInt32(13)), token).ConfigureAwait(false));
            }
            using (var characters = new AccessCommand(connection,
                       "SELECT ch.[EntityId],IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]),c.[Version],c.[IsDeleted],ch.[BirthKind],ch.[BirthLowerBound],ch.[BirthUpperBound],ch.[BirthLowerInclusive],ch.[BirthUpperInclusive],ch.[BirthOriginalText],ch.[BirthCalendarId],ch.[DeathKind],ch.[DeathLowerBound],ch.[DeathUpperBound],ch.[DeathLowerInclusive],ch.[DeathUpperInclusive],ch.[DeathOriginalText],ch.[DeathCalendarId],ch.[BirthdayRecurring] FROM [Characters] AS ch INNER JOIN [CanonEntities] AS c ON ch.[EntityId]=c.[Id] WHERE c.[ContinuityId]=?")
                   .Add(OleDbType.Integer, continuity))
            {
                foreach (var row in await characters.QueryAsync(r => new
                         {
                             Id = r.GetInt32(0), Name = r.GetString(1), Version = r.GetInt32(2), Deleted = r.GetBoolean(3),
                             Birth = ReadDate(r, 4, "Birth"), Death = ReadDate(r, 11, "Death"), RepeatBirthday = r.GetBoolean(18)
                         }, token).ConfigureAwait(false))
                {
                    rows.Add(new(row.Id, "Character", row.Name + " — birth", null, V4TimelineLane.Characters, row.Birth,
                        null, row.Version, row.Deleted, row.Id, Discriminator: "birth",
                        Recurrence: row.RepeatBirthday ? new V4EventRecurrence(V4RecurrenceFrequency.Yearly,
                            Until: row.Death.UpperBound is { } deathEnd
                                ? (row.Death.UpperInclusive ? deathEnd : deathEnd.AddTicks(-1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null) : null,
                        BirthdayDeath: row.RepeatBirthday ? row.Death : null));
                    if (row.Death.Kind != StoryDateKind.Unknown)
                        rows.Add(new(row.Id, "Character", row.Name + " — death", null, V4TimelineLane.Characters, row.Death,
                            null, row.Version, row.Deleted, row.Id, Discriminator: "death"));
                }
            }
            foreach (var (table, type, key) in new[] { ("WorldEvents", "WorldEvent", "EntityId"), ("EntityEvents", "EntityEvent", "Id"), ("RelationshipEvents", "RelationshipEvent", "Id") })
            {
                using var schedules = new AccessCommand(connection, $"SELECT [{key}],[RecurrenceFrequency],[RecurrenceInterval],[RecurrenceUntil] FROM [{table}] WHERE [RecurrenceFrequency] IS NOT NULL");
                var byId = (await schedules.QueryAsync(r => (Id: r.GetInt32(0), Recurrence: new V4EventRecurrence(
                    Enum.Parse<V4RecurrenceFrequency>(r.GetString(1)), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetDateTime(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), token)).ToDictionary(row => row.Id, row => row.Recurrence);
                for (var i = 0; i < rows.Count; i++)
                    if (rows[i].Type == type && byId.TryGetValue(rows[i].Id, out var recurrence)) rows[i] = rows[i] with { Recurrence = recurrence };
            }
            await AddPeriodRowsAsync(connection, continuity, rows, token).ConfigureAwait(false);
            return rows;
        }, token).ConfigureAwait(false);
    }

    private static async Task AddPeriodRowsAsync(
        OleDbConnection connection, int continuity, ICollection<TimelineRow> rows, CancellationToken token)
    {
        var organizationTransitions = new Dictionary<int, List<(string Kind, StoryDate Date, string? Description)>>();
        using (var command = new AccessCommand(connection,
                   "SELECT t.[MembershipId],t.[TransitionKind],t.[Description],t.[OccurredKind]," +
                   "t.[OccurredLowerBound],t.[OccurredUpperBound],t.[OccurredLowerInclusive]," +
                   "t.[OccurredUpperInclusive],t.[OccurredOriginalText],t.[OccurredCalendarId] " +
                   "FROM ([OrganizationMembershipTransitions] AS t INNER JOIN " +
                   "[OrganizationMemberships] AS m ON t.[MembershipId]=m.[Id]) " +
                   "INNER JOIN [CanonEntities] AS c ON m.[OrganizationId]=c.[Id] " +
                   "WHERE c.[ContinuityId]=? AND t.[IsDeleted]=False")
                   .Add(OleDbType.Integer, continuity))
            foreach (var item in await command.QueryAsync(reader => new
            {
                Membership = reader.GetInt32(0), Kind = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Date = ReadDate(reader, 3, "Occurred")
            }, token).ConfigureAwait(false))
            {
                if (!organizationTransitions.TryGetValue(item.Membership, out var list))
                    organizationTransitions[item.Membership] = list = [];
                list.Add((item.Kind, item.Date, item.Description));
            }
        var definitions = new[]
        {
            new PeriodDefinition("CharacterResidences", "CharacterResidence", "residence", V4TimelineLane.Residences, "CharacterId", "LocationId"),
            new PeriodDefinition("OrganizationMemberships", "OrganizationMembership", "membership", V4TimelineLane.Memberships, "OrganizationId", "CharacterId"),
            new PeriodDefinition("OrganizationLocations", "OrganizationLocation", "organization location", V4TimelineLane.OrganizationLocations, "OrganizationId", "LocationId"),
            new PeriodDefinition("ObjectOwnershipPeriods", "ObjectOwnershipPeriod", "ownership", V4TimelineLane.Ownership, "ObjectId", null),
            new PeriodDefinition("ObjectCustodyPeriods", "ObjectCustodyPeriod", "custody", V4TimelineLane.Custody, "ObjectId", null),
            new PeriodDefinition("ObjectLocationPeriods", "ObjectLocationPeriod", "object location", V4TimelineLane.ObjectLocations, "ObjectId", "LocationId")
        };
        foreach (var definition in definitions)
        {
            var related = definition.RelatedColumn is null ? "Null" : $"r.[{definition.RelatedColumn}]";
            var title = definition.TitleColumn is null ? $"'{definition.FallbackTitle}'" : $"r.[{definition.TitleColumn}]";
            using var command = new AccessCommand(connection,
                    $"SELECT r.[Id],{title},r.[PeriodKind],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[PeriodLowerInclusive],r.[PeriodUpperInclusive],r.[PeriodOriginalText],r.[PeriodCalendarId],r.[Version],r.[IsDeleted],r.[{definition.OwnerColumn}],{related} FROM [{definition.Table}] AS r INNER JOIN [CanonEntities] AS c ON r.[{definition.OwnerColumn}]=c.[Id] WHERE c.[ContinuityId]=?")
                .Add(OleDbType.Integer, continuity);
            foreach (var row in await command.QueryAsync(r => new TimelineRow(
                r.GetInt32(0), definition.Type, r.GetString(1), null, definition.Lane, ReadDate(r, 2, "Period"), null,
                r.GetInt32(9), r.GetBoolean(10), r.GetInt32(11), r.IsDBNull(12) ? null : r.GetInt32(12)), token).ConfigureAwait(false))
            {
                if (definition.Type == "OrganizationMembership" &&
                    organizationTransitions.TryGetValue(row.Id, out var transitions))
                {
                    if (transitions.Count != 2) rows.Add(row);
                    foreach (var transition in transitions)
                        rows.Add(row with { Date = transition.Date,
                            Discriminator = transition.Kind == "Join" ? "transition-join" : "transition-leave",
                            TransitionDescription = transition.Description });
                }
                else rows.Add(row);
            }
        }
        List<TimelineRow> relationshipRows;
        using (var relationships = new AccessCommand(connection,
                   "SELECT r.[Id],t.[Name],r.[PeriodKind],r.[PeriodLowerBound],r.[PeriodUpperBound],r.[PeriodLowerInclusive],r.[PeriodUpperInclusive],r.[PeriodOriginalText],r.[PeriodCalendarId],r.[Version],r.[IsDeleted],r.[SourceCharacterId],r.[TargetCharacterId],t.[IsDirected] FROM ([CharacterRelationships] AS r INNER JOIN [RelationshipTypes] AS t ON r.[RelationshipTypeId]=t.[Id]) INNER JOIN [CanonEntities] AS c ON r.[SourceCharacterId]=c.[Id] WHERE c.[ContinuityId]=?")
                   .Add(OleDbType.Integer, continuity))
            relationshipRows = (await relationships.QueryAsync(r => new TimelineRow(
                r.GetInt32(0), "CharacterRelationship", r.GetString(1), null, V4TimelineLane.Relationships,
                ReadDate(r, 2, "Period"), null, r.GetInt32(9), r.GetBoolean(10), r.GetInt32(11),
                r.GetInt32(12), RelationshipDirected: r.GetBoolean(13)), token).ConfigureAwait(false)).ToList();
        var relationshipDescriptions = new Dictionary<int, string>();
        using (var descriptions = new AccessCommand(connection,
                   "SELECT [TransitionId],[Description] FROM [RelationshipTransitionDescriptions]"))
            foreach (var description in await descriptions.QueryAsync(reader =>
                (Id: reader.GetInt32(0), Text: reader.GetString(1)), token).ConfigureAwait(false))
                relationshipDescriptions[description.Id] = description.Text;
        var membershipTransitions = new Dictionary<int, List<(string Kind, StoryDate Date, string? Description)>>();
        using (var transitions = new AccessCommand(connection,
                   "SELECT t.[Id],t.[MembershipPeriodId],t.[TransitionKind],t.[OccurredKind]," +
                   "t.[OccurredLowerBound],t.[OccurredUpperBound],t.[OccurredLowerInclusive]," +
                   "t.[OccurredUpperInclusive],t.[OccurredOriginalText],t.[OccurredCalendarId] " +
                   "FROM (([RelationshipMembershipTransitions] AS t INNER JOIN " +
                   "[RelationshipMembershipPeriods] AS m ON t.[MembershipPeriodId]=m.[Id]) " +
                   "INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id]) " +
                   "INNER JOIN [CharacterRelationships] AS r ON p.[RelationshipId]=r.[Id] " +
                   "WHERE r.[ContinuityId]=? AND t.[IsDeleted]=False")
                   .Add(OleDbType.Integer, continuity))
            foreach (var item in await transitions.QueryAsync(reader => new
            {
                Id = reader.GetInt32(0), Period = reader.GetInt32(1), Kind = reader.GetString(2),
                Date = ReadDate(reader, 3, "Occurred")
            }, token).ConfigureAwait(false))
            {
                if (!membershipTransitions.TryGetValue(item.Period, out var dates))
                    membershipTransitions[item.Period] = dates = [];
                dates.Add((item.Kind, item.Date, relationshipDescriptions.GetValueOrDefault(item.Id)));
            }
        var activeMemberships = new List<(int Relationship, StoryDate Date, bool Certain)>();
        using (var membershipPeriods = new AccessCommand(connection,
                   "SELECT m.[Id],t.[Name],m.[PeriodKind],m.[PeriodLowerBound],m.[PeriodUpperBound]," +
                   "m.[PeriodLowerInclusive],m.[PeriodUpperInclusive],m.[PeriodOriginalText],m.[PeriodCalendarId]," +
                   "m.[Version],m.[IsDeleted],p.[CharacterId],p.[IsDeleted],r.[IsDeleted],r.[Id] " +
                   "FROM (([RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p " +
                   "ON m.[ParticipantId]=p.[Id]) INNER JOIN [CharacterRelationships] AS r " +
                   "ON p.[RelationshipId]=r.[Id]) INNER JOIN [RelationshipTypes] AS t " +
                   "ON r.[RelationshipTypeId]=t.[Id] WHERE r.[ContinuityId]=?")
                   .Add(OleDbType.Integer, continuity))
            foreach (var member in await membershipPeriods.QueryAsync(r => new
            {
                Row = new TimelineRow(r.GetInt32(0), "RelationshipMembershipPeriod", r.GetString(1), null,
                    V4TimelineLane.Relationships, ReadDate(r, 2, "Period"), null,
                    r.GetInt32(9), r.GetBoolean(10) || r.GetBoolean(12) || r.GetBoolean(13),
                    r.GetInt32(11)),
                Relationship = r.GetInt32(14)
            }, token).ConfigureAwait(false))
            {
                if (membershipTransitions.TryGetValue(member.Row.Id, out var dates))
                {
                    foreach (var date in dates)
                        rows.Add(member.Row with
                        {
                            Date = date.Date,
                            Discriminator = date.Kind == "Join" ? "transition-join" : "transition-leave",
                            TransitionDescription = date.Description
                        });
                }
                else rows.Add(member.Row);
                // The envelope is deliberately broad when either transition is
                // fuzzy. It must never be promoted into an exact group boundary.
                if (!member.Row.Deleted)
                {
                    var hasTransitions = membershipTransitions.TryGetValue(member.Row.Id, out var boundaries);
                    var certain = hasTransitions
                        ? boundaries!.Count == 2 && boundaries.Any(item => item.Kind == "Join" &&
                            item.Date.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant) &&
                          boundaries.Any(item => item.Kind == "Leave" &&
                            item.Date.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant)
                        : member.Row.Date.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant;
                    activeMemberships.Add((member.Relationship, member.Row.Date, certain));
                }
            }
        foreach (var relationship in relationshipRows)
        {
            var memberships = activeMemberships.Where(item => item.Relationship == relationship.Id).ToArray();
            var uncertain = memberships.Any(item => !item.Certain);
            var derived = DeriveRelationshipIntervals(memberships.Where(item => item.Certain)
                .Select(item => item.Date).ToArray());
            if (derived is null)
            {
                rows.Add(memberships.Length >= 2 && uncertain
                    ? relationship with
                    {
                        Date = StoryDate.Unknown(),
                        Warnings = ["Membership timing is uncertain; no exact group transition is asserted."]
                    }
                    : relationship);
                continue;
            }
            if (derived.Count == 0)
            {
                rows.Add(relationship with
                {
                    Date = StoryDate.Unknown(),
                    Warnings = [uncertain
                        ? "Membership timing is uncertain; no exact group transition is asserted."
                        : "No exact interval has two or more participating characters."]
                });
                continue;
            }
            for (var index = 0; index < derived.Count; index++)
                rows.Add(relationship with
                {
                    Date = derived[index],
                    Discriminator = $"active-{index}"
                });
            if (uncertain)
                rows.Add(relationship with
                {
                    Date = StoryDate.Unknown(),
                    Discriminator = "uncertain-membership",
                    Warnings = ["Other membership dates are uncertain; additional group activity is possible."]
                });
        }
        using var effects=new AccessCommand(connection,"SELECT e.[Id],e.[Name],e.[PeriodKind],e.[PeriodLowerBound],e.[PeriodUpperBound],e.[PeriodLowerInclusive],e.[PeriodUpperInclusive],e.[PeriodOriginalText],e.[PeriodCalendarId],e.[BiologicalRate],e.[ExperiencedRate],e.[Version],e.[IsDeleted],e.[CharacterId],IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]) FROM [CharacterTemporalEffects] AS e INNER JOIN ([Characters] AS ch INNER JOIN [CanonEntities] AS c ON ch.[EntityId]=c.[Id]) ON e.[CharacterId]=ch.[EntityId] WHERE c.[ContinuityId]=?").Add(OleDbType.Integer,continuity);
        foreach(var effect in await effects.QueryAsync(r=>new{Id=r.GetInt32(0),Name=r.GetString(1),Period=ReadDate(r,2,"Period"),Bio=r.GetDouble(9),Experienced=r.GetDouble(10),Version=r.GetInt32(11),Deleted=r.GetBoolean(12),Character=r.GetInt32(13),CharacterName=r.GetString(14)},token).ConfigureAwait(false))
        {
            var warnings=new List<string>{$"Biological rate {effect.Bio:0.###}; experienced rate {effect.Experienced:0.###}."};
            if(effect.Period.LowerBound is null||effect.Period.UpperBound is null)warnings.Add("This temporal effect has an open boundary.");
            if(effect.Period.Kind is StoryDateKind.Circa or StoryDateKind.Month or StoryDateKind.Year or StoryDateKind.Before or StoryDateKind.After)warnings.Add("This temporal effect has uncertain timing; age results are bounded.");
            rows.Add(new(effect.Id,"CharacterTemporalEffect",effect.Name,null,V4TimelineLane.TemporalEffects,effect.Period,null,effect.Version,effect.Deleted,
                effect.Character,effect.Character,"Character",effect.CharacterName,warnings));
        }
    }

    private async Task<HashSet<int>> ResolveEntityFilterAsync(
        IReadOnlyList<string>? values, int continuity, CancellationToken token, string? requiredType = null)
    {
        var result = new HashSet<int>();
        if (values is null) return result;
        foreach (var value in values)
        {
            var target = await targets.EntityAsync(value, continuity, token).ConfigureAwait(false);
            if (requiredType is not null && !target.ResourceType.Equals(requiredType, StringComparison.OrdinalIgnoreCase))
                throw new V4ResolutionException("reference.type_invalid", $"A {requiredType} is required.");
            result.Add(target.StorageKey);
        }
        return result;
    }

    private async Task<(HashSet<int> Entities, HashSet<string> Relationships)> ResolveTimelineFocusAsync(
        IReadOnlyList<string>? values, int continuity, CancellationToken token)
    {
        var entities = new HashSet<int>();
        var relationships = new HashSet<string>(StringComparer.Ordinal);
        if (values is null) return (entities, relationships);
        foreach (var value in values)
        {
            ResolvedVaultReference selected;
            try { selected = await references.ResolveAsync(value, continuity, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
            {
                throw new V4ResolutionException("reference.invalid", exception.Message);
            }
            if (selected.IsDeleted)
                throw new V4ResolutionException("record.deleted", "A timeline focus record is deleted.");
            if (selected.ResourceType.Equals("CharacterRelationship", StringComparison.OrdinalIgnoreCase))
                relationships.Add(selected.Reference);
            else if (Enum.TryParse<CanonEntityType>(selected.ResourceType, true, out _))
                entities.Add(selected.Id);
            else
                throw new V4ResolutionException("reference.type_invalid",
                    "Timeline focus requires a canon entity or character relationship reference.");
        }
        return (entities, relationships);
    }

    private async Task<HashSet<int>> ResolveTagFilterAsync(IReadOnlyList<string>? values, CancellationToken token)
    {
        var result = new HashSet<int>();
        if (values is null) return result;
        foreach (var value in values) result.Add((await targets.TagAsync(value, token).ConfigureAwait(false)).StorageKey);
        return result;
    }

    private async Task<HashSet<int>> LoadOwnersWithAllTagsAsync(int continuity, IReadOnlySet<int> tags, CancellationToken token)
    {
        await using var connection = connectionFactory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
            "SELECT x.[EntityId],x.[TagId] FROM [EntityTags] AS x INNER JOIN [CanonEntities] AS c ON x.[EntityId]=c.[Id] WHERE c.[ContinuityId]=? AND c.[IsDeleted]=False")
            .Add(OleDbType.Integer, continuity);
        var rows = await command.QueryAsync(r => (Owner:r.GetInt32(0),Tag:r.GetInt32(1)), token).ConfigureAwait(false);
        return rows.GroupBy(x=>x.Owner).Where(g=>tags.All(tag=>g.Any(x=>x.Tag==tag))).Select(g=>g.Key).ToHashSet();
    }

    private static bool TimelineOverlaps(StoryDate date, DateTime? lower, bool lowerInclusive,
        DateTime? upper, bool upperInclusive)
    {
        if (date.Kind == StoryDateKind.Unknown) return lower is null && upper is null;
        if (upper is { } end && date.LowerBound is { } itemStart &&
            (itemStart > end || itemStart == end && !(date.LowerInclusive && upperInclusive))) return false;
        if (lower is { } start && date.UpperBound is { } itemEnd &&
            (itemEnd < start || itemEnd == start && !(date.UpperInclusive && lowerInclusive))) return false;
        return true;
    }

    // The caller supplies only periods with individually exact join and leave dates.
    // Convert inclusive instant exits to a half-open tick for the sweep, then restore
    // that inclusive instant on the resulting group interval.
    private static IReadOnlyList<StoryDate>? DeriveRelationshipIntervals(IReadOnlyList<StoryDate> periods)
    {
        if (periods.Count < 2) return null;
        if (periods.Any(date => date.Kind is not (StoryDateKind.ExactDate or StoryDateKind.ExactInstant or StoryDateKind.KnownRange) ||
            date.LowerBound is null || date.UpperBound is null ||
            !date.LowerInclusive || date.UpperInclusive && date.UpperBound == DateTime.MaxValue ||
            !date.CalendarId.Equals("Gregorian", StringComparison.OrdinalIgnoreCase))) return null;

        var deltas = periods.SelectMany(date => new[]
            {
                (At: date.LowerBound!.Value, Change: 1, InclusiveEnd: false),
                (At: date.UpperInclusive ? date.UpperBound!.Value.AddTicks(1) : date.UpperBound!.Value,
                    Change: -1, InclusiveEnd: date.UpperInclusive)
            })
            .GroupBy(point => point.At)
            .OrderBy(group => group.Key)
            .Select(group => (At: group.Key, Change: group.Sum(point => point.Change),
                InclusiveEnd: group.Any(point => point.Change < 0 && point.InclusiveEnd)))
            .ToArray();
        var intervals = new List<StoryDate>();
        var active = 0;
        DateTime? started = null;
        foreach (var boundary in deltas)
        {
            var next = active + boundary.Change;
            if (active < 2 && next >= 2) started = boundary.At;
            if (active >= 2 && next < 2 && started is { } start && boundary.At > start)
            {
                var end = boundary.InclusiveEnd ? boundary.At.AddTicks(-1) : boundary.At;
                intervals.Add(start == end
                    ? StoryDate.ExactInstant(start)
                    : new StoryDate(StoryDateKind.KnownRange, start, end,
                        UpperInclusive: boundary.InclusiveEnd));
                started = null;
            }
            active = next;
        }
        return intervals;
    }
    private static string TimelineKey(TimelineRow row, V4TimelineMode mode)
    {
        var primary = mode == V4TimelineMode.Narrative && row.NarrativeOrder is { } order
            ? "0" + SortableDouble(order)
            : "1" + (row.Date.LowerBound ?? row.Date.UpperBound ?? DateTime.MaxValue).Ticks.ToString("D19", CultureInfo.InvariantCulture);
        return $"{primary}|{row.Type.ToUpperInvariant()}|{row.Id:D10}|{row.Discriminator}";
    }

    private static string SortableDouble(double value)
    {
        if (!double.IsFinite(value)) throw new VaultValidationException([new("timeline.narrative_order", "narrativeOrder", "Narrative order must be finite.")]);
        var bits = BitConverter.DoubleToInt64Bits(value);
        var sortable = bits < 0 ? ~unchecked((ulong)bits) : unchecked((ulong)bits) ^ 0x8000000000000000UL;
        return sortable.ToString("X16", CultureInfo.InvariantCulture);
    }
    private static string TimelineScope(int continuity, V4TimelineRequest request, string revision)
    {
        var fields = $"c:{continuity}|revision:{revision}|m:{request.Mode}|r:{request.Resolution}|expandRanges:{request.ExpandRanges}|expandRecurrences:{request.ExpandRecurrences}|from:{request.From}|to:{request.To}|lanes:{string.Join(',', request.Lanes ?? [])}|kinds:{string.Join(',', request.Kinds ?? [])}|entityEventKinds:{string.Join(',', request.EntityEventKinds ?? [])}|focus:{string.Join(',', request.FocusRefs ?? [])}|highlight:{request.HighlightRef}|tags:{string.Join(',', request.Tags ?? [])}|projects:{string.Join(',', request.Projects ?? [])}|locations:{string.Join(',', request.Locations ?? [])}|text:{request.Text}|undated:{request.IncludeUndated}|undatedOnly:{request.UndatedOnly}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields)));
    }
}
