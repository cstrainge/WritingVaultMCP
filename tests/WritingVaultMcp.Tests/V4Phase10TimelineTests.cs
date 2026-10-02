using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase10TimelineTests
{
    [Fact]
    public void TimelineDateViewPreservesClockPrecisionOnlyWhenItExists()
    {
        var timed = AccessV4ReadService.StoryDateView(new StoryDate(StoryDateKind.KnownRange,
            new DateTime(2026, 9, 5, 8, 0, 0), new DateTime(2026, 9, 5, 22, 30, 0), true, true));
        Assert.Equal("2026-09-05T08:00:00.0000000", timed.Lower);
        Assert.Equal("2026-09-05T22:30:00.0000000", timed.Upper);

        var calendar = AccessV4ReadService.StoryDateView(new StoryDate(StoryDateKind.KnownRange,
            new DateTime(2026, 9, 5), new DateTime(2026, 9, 8), true, true));
        Assert.Equal("2026-09-05", calendar.Lower);
        Assert.Equal("2026-09-08", calendar.Upper);
        Assert.Equal("2026-09-05", AccessV4ReadService.StoryDateView(
            StoryDate.ExactDate(new DateOnly(2026, 9, 5))).Lower);
        Assert.Equal("2026-09-08T22:30:00.0000000", AccessV4ReadService.StoryDateView(
            new StoryDate(StoryDateKind.Before, null, new DateTime(2026, 9, 8, 22, 30, 0))).Upper);
    }

    [Fact]
    public async Task TimedWorldEventRangeReachesTheTimelineReadModel()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Timed range world", "UTC"))).ResourceKey!);
        Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.WorldEvent, "Evening vigil",
            Occurred: new StoryDate(StoryDateKind.KnownRange, new DateTime(2026, 9, 5, 8, 0, 0),
                new DateTime(2026, 9, 5, 22, 30, 0), true, true)))).Success);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Timed range world");
        var eventDate = Assert.Single((await reads.TimelineAsync(new())).Items).Occurred;
        Assert.Equal("2026-09-05T08:00:00.0000000", eventDate.Lower);
        Assert.Equal("2026-09-05T22:30:00.0000000", eventDate.Upper);
    }

    [Fact]
    public async Task OnlyKnownIntervalsExpandEvenWhenOriginalWordingSuggestsOtherwise()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Explicit range meanings", "UTC"))).ResourceKey!);
        async Task Create(string title, StoryDate occurred) => Assert.True((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, title,
                Occurred: occurred))).Success);
        await Create("Known with wording", new(StoryDateKind.KnownRange,
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 4),
            OriginalText: "the three long days"));
        await Create("Uncertain without wording", new(StoryDateKind.UncertainRange,
            new DateTime(2026, 9, 2), new DateTime(2026, 9, 6)));
        await Create("Legacy ambiguous", new(StoryDateKind.Range,
            new DateTime(2026, 9, 3), new DateTime(2026, 9, 7)));

        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Explicit range meanings");
        var items = (await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail,
            ExpandRanges: true))).Items;
        Assert.Equal(4, items.Count);
        Assert.Equal(["Start", "End"], items.Where(item => item.Title == "Known with wording")
            .Select(item => item.Boundary));
        Assert.Null(Assert.Single(items, item => item.Title == "Uncertain without wording").Boundary);
        Assert.Null(Assert.Single(items, item => item.Title == "Legacy ambiguous").Boundary);
    }

    [Fact]
    public async Task ExpandedDetailPagesRangeBoundariesInChronologicalOrderWithoutSplittingOneDayRanges()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Range boundary world", "UTC"))).ResourceKey!);
        async Task Create(string title, StoryDate occurred) => Assert.True((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, title,
                Occurred: occurred))).Success);
        await Create("Long vigil", new(StoryDateKind.KnownRange, new DateTime(2026, 9, 5),
            new DateTime(2026, 9, 8), true, true));
        await Create("Intervening visit", StoryDate.ExactDate(new DateOnly(2026, 9, 6)));
        await Create("One evening", new(StoryDateKind.KnownRange, new DateTime(2026, 9, 7, 8, 0, 0),
            new DateTime(2026, 9, 7, 22, 30, 0), true, true));
        await Create("One calendar day", new(StoryDateKind.KnownRange, new DateTime(2026, 9, 9),
            new DateTime(2026, 9, 10), true, false));
        await Create("Until midnight", new(StoryDateKind.KnownRange, new DateTime(2026, 9, 11, 8, 0, 0),
            new DateTime(2026, 9, 13), true, false));

        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Range boundary world");
        var ordinary = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail));
        Assert.Equal(5, ordinary.Items.Count);

        var rows = new List<V4TimelineItem>();
        string? cursor = null;
        do
        {
            var page = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail,
                ExpandRanges: true, Limit: 1, Cursor: cursor));
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(["Long vigil", "Intervening visit", "One evening", "Long vigil", "One calendar day",
            "Until midnight", "Until midnight"],
            rows.Select(row => row.Title));
        Assert.Equal(["Start", null, null, "End", null, "Start", "End"], rows.Select(row => row.Boundary));
        Assert.Equal("2026-09-05", rows[0].BoundaryDate?.Lower);
        Assert.Equal("2026-09-08", rows[3].BoundaryDate?.Lower);
        Assert.Equal(rows[0].Ref, rows[3].Ref);
        Assert.Equal("2026-09-11T08:00:00.0000000", rows[5].BoundaryDate?.Lower);
        Assert.Equal("2026-09-12", rows[6].BoundaryDate?.Lower);

        var selected = await reads.TimelineAsync(new(
            From: new(V4StoryDateKind.ExactDate, Value: "2026-09-06"),
            To: new(V4StoryDateKind.ExactDate, Value: "2026-09-06"),
            Resolution: V4TimelineResolution.Detail, ExpandRanges: true));
        Assert.Contains(selected.Items, row => row.Title == "Intervening visit");
        Assert.Contains(selected.Items, row => row.Title == "Long vigil" && row.Boundary == "Ongoing");
    }

    [Fact]
    public async Task SearchFromTimelineFindsUnicodeCharacterAcrossDefaultKinds()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Search navigation world", "UTC"))).ResourceKey!);
        Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.Character, "Chloë Bell"))).Success);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Search navigation world");
        var result = await reads.SearchAsync(new(Text: "Chloë Bell", IncludeContent: true));
        Assert.Contains(result.Items, item => item.Kind == V4RecordKind.Character && item.Label == "Chloë Bell");
    }

    [Fact]
    public async Task EntityEventKindFilterSeparatesCharacterAndLocationEvents()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Entity event filter world", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.Character, "Aster"))).ResourceKey!);
        var location = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.Location, "Square"))).ResourceKey!);
        Assert.True((await vault.Service.AddEntityEventAsync(new(Guid.NewGuid().ToString(),
            character, "Aster arrives", StoryDate.ExactDate(new DateOnly(2024, 2, 1))))).Success);
        Assert.True((await vault.Service.AddEntityEventAsync(new(Guid.NewGuid().ToString(),
            location, "Square floods", StoryDate.ExactDate(new DateOnly(2024, 2, 2))))).Success);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Entity event filter world");
        var characterEvents = await reads.TimelineAsync(new(Kinds: [V4RecordKind.EntityEvent],
            EntityEventKinds: [V4RecordKind.Character]));
        Assert.Equal("Aster arrives", Assert.Single(characterEvents.Items).Title);
        var locationEvents = await reads.TimelineAsync(new(Kinds: [V4RecordKind.EntityEvent],
            EntityEventKinds: [V4RecordKind.Location]));
        Assert.Equal("Square floods", Assert.Single(locationEvents.Items).Title);
        await Assert.ThrowsAsync<VaultValidationException>(() => reads.TimelineAsync(new(
            EntityEventKinds: [V4RecordKind.WorldEvent])));
    }

    [Fact]
    public async Task CharacterBirthUsesTheResolvableCharacterReference()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Birth timeline world", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.Character, "Aster", Birth: StoryDate.Unknown()))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Birth timeline world");
        var timeline = await reads.TimelineAsync(new());
        var birth = Assert.Single(timeline.Undated);
        Assert.Equal(await references.ReferenceAsync("Character", character), birth.Ref);
    }

    [Fact]
    public async Task NewWriteInvalidatesCachedTimelineAndOldPageCursor()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Changing timeline world", "UTC"))).ResourceKey!);
        async Task Create(string name, int day) => Assert.True((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, name,
                Occurred: StoryDate.ExactDate(new DateOnly(2024, 2, day))))).Success);
        await Create("First", 1);
        await Create("Second", 2);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Changing timeline world");
        var first = await reads.TimelineAsync(new(Limit: 1));
        Assert.NotNull(first.NextCursor);
        await Create("Third", 3);
        await Assert.ThrowsAsync<V4CursorException>(() => reads.TimelineAsync(new(
            Cursor: first.NextCursor, Limit: 1)));
        var fresh = await reads.TimelineAsync(new());
        Assert.Equal(["First", "Second", "Third"], fresh.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task LargePagedTimelineRestartsWithoutGapsAfterAConcurrentWrite()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Paged write world", "UTC"))).ResourceKey!);
        const int seededEvents = 1_200;
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var canon = new OleDbCommand(
                "INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc],[Version],[IsDeleted]) VALUES (?,?,?,?,?,?)",
                connection, transaction);
            canon.Parameters.Add("continuity", OleDbType.Integer).Value = continuity;
            canon.Parameters.Add("kind", OleDbType.VarWChar).Value = "WorldEvent";
            canon.Parameters.Add("created", OleDbType.Date).Value = DateTime.UtcNow;
            canon.Parameters.Add("updated", OleDbType.Date).Value = DateTime.UtcNow;
            canon.Parameters.Add("version", OleDbType.Integer).Value = 1;
            canon.Parameters.Add("deleted", OleDbType.Boolean).Value = false;
            using var identity = new OleDbCommand("SELECT @@IDENTITY", connection, transaction);
            using var worldEvent = new OleDbCommand(
                "INSERT INTO [WorldEvents] ([EntityId],[Title],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventCalendarId]) VALUES (?,?,?,?,?,?,?,?)",
                connection, transaction);
            worldEvent.Parameters.Add("id", OleDbType.Integer);
            worldEvent.Parameters.Add("title", OleDbType.VarWChar);
            worldEvent.Parameters.Add("kind", OleDbType.VarWChar).Value = "ExactDate";
            worldEvent.Parameters.Add("lower", OleDbType.Date);
            worldEvent.Parameters.Add("upper", OleDbType.Date);
            worldEvent.Parameters.Add("lowerInclusive", OleDbType.Boolean).Value = true;
            worldEvent.Parameters.Add("upperInclusive", OleDbType.Boolean).Value = false;
            worldEvent.Parameters.Add("calendar", OleDbType.VarWChar).Value = "Gregorian";
            for (var index = 0; index < seededEvents; index++)
            {
                await canon.ExecuteNonQueryAsync();
                var day = new DateTime(2020, 1, 1).AddDays(index);
                worldEvent.Parameters[0].Value = Convert.ToInt32(await identity.ExecuteScalarAsync());
                worldEvent.Parameters[1].Value = $"Seeded {index:0000}";
                worldEvent.Parameters[3].Value = day;
                worldEvent.Parameters[4].Value = day.AddDays(1);
                await worldEvent.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }

        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Paged write world");
        var request = new V4TimelineRequest(Resolution: V4TimelineResolution.Detail,
            IncludeUndated: false, Limit: 100);
        var first = await reads.TimelineAsync(request);
        Assert.Equal(100, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = await reads.TimelineAsync(request with { Cursor = first.NextCursor });
        Assert.Equal(100, second.Items.Count);
        Assert.NotNull(second.NextCursor);

        Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.WorldEvent, "Newly committed", Occurred: StoryDate.ExactDate(new DateOnly(2024, 1, 1))))).Success);
        await Assert.ThrowsAsync<V4CursorException>(() => reads.TimelineAsync(
            request with { Cursor = second.NextCursor }));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await reads.TimelineAsync(request with { Cursor = cursor });
            Assert.InRange(page.Items.Count, 1, 100);
            Assert.All(page.Items, item => Assert.True(seen.Add(item.Ref), $"Duplicate timeline item {item.Ref}."));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(seededEvents + 1, seen.Count);
    }

    [Fact]
    public async Task DeletedEventDoesNotAppearInNormalChronology()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Deleted timeline world", "UTC"))).ResourceKey!);
        var eventId = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.WorldEvent, "Retired scene",
            Occurred: StoryDate.ExactDate(new DateOnly(2024, 2, 3))))).ResourceKey!);
        Assert.True((await vault.Service.SoftDeleteEntityAsync(
            new(Guid.NewGuid().ToString(), eventId, 1))).Success);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Deleted timeline world");
        var timeline = await reads.TimelineAsync(new());
        Assert.Empty(timeline.Items);
    }

    [Fact]
    public async Task DenseGraphAggregationBoundsItsResponseAndReferenceSamples()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Dense timeline world", "UTC"))).ResourceKey!);
        var linkedEventId = 0;
        for (var index = 0; index < 520; index++)
        {
            var created = await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
                continuity, CanonEntityType.WorldEvent, $"Entry {index:000}",
                Occurred: index switch
                {
                    0 => new StoryDate(StoryDateKind.Before, null, new DateTime(2024, 1, 1)),
                    1 => new StoryDate(StoryDateKind.After, new DateTime(2024, 1, 2), null),
                    _ => StoryDate.ExactDate(new DateOnly(2024, 1, 1).AddDays(index))
                }));
            Assert.True(created.Success);
            if (index == 519) linkedEventId = int.Parse(created.ResourceKey!);
        }
        var witness = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.Character, "Late witness"))).ResourceKey!);
        Assert.True((await vault.Service.AddWorldEventParticipantAsync(new(Guid.NewGuid().ToString(),
            linkedEventId, witness, "witness"))).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
            continuity, CanonEntityType.WorldEvent, "No known date", Occurred: StoryDate.Unknown()))).Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Dense timeline world");
        var firstDetail = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail, Limit: 100));
        Assert.Empty(firstDetail.Undated);
        var undated = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail,
            UndatedOnly: true, Limit: 100));
        Assert.Contains(undated.Undated, item => item.Label == "No known date");
        Assert.Contains(undated.Undated, item => item.Label == "Late witness — birth");
        var page = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Aggregate,
            IncludeUndated: false, Limit: 500));
        Assert.False(page.HasMore);
        Assert.InRange(page.Items.Count, 1, 500);
        Assert.All(page.Items, item =>
        {
            Assert.Equal("Aggregate", item.Kind);
            Assert.InRange(item.Related.Count, 1, 20);
        });
        Assert.Contains(page.Items, item => item.Occurred.Kind == V4StoryDateKind.Before &&
            item.Occurred.Lower is null && item.Occurred.Upper is not null);
        Assert.Contains(page.Items, item => item.Occurred.Kind == V4StoryDateKind.After &&
            item.Occurred.Lower is not null && item.Occurred.Upper is null);
        Assert.DoesNotContain(page.Items, item => item.ContainsHighlight);
        var witnessRef = await references.ReferenceAsync("Character", witness);
        var highlighted = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Aggregate,
            IncludeUndated: false, Limit: 500, HighlightRef: witnessRef));
        Assert.Contains(highlighted.Items, item => item.ContainsHighlight);
        Assert.Equal(page.Items.Count, highlighted.Items.Count);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WritingVaultMcp.csproj")))
            root = root.Parent;
        var output = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root missing."),
            "artifacts", "phase10-dense");
        Directory.CreateDirectory(output);
        File.Copy(vault.DatabasePath, Path.Combine(output, "WritingVault.Phase10Dense.accdb"), true);
        Directory.CreateDirectory(Path.Combine(output, "backup"));
    }

    [Fact]
    public async Task BrushedDateRangeExcludesNextDayAndUndatedEntries()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Date boundary world", "UTC"))).ResourceKey!);
        foreach (var (title, date) in new[]
        {
            ("Before", new StoryDate(StoryDateKind.Before, null, new DateTime(2024, 2, 1))),
            ("Inside", StoryDate.ExactDate(new DateOnly(2024, 2, 3))),
            ("Next day", StoryDate.ExactDate(new DateOnly(2024, 2, 4))),
            ("After", new StoryDate(StoryDateKind.After, new DateTime(2024, 2, 5), null)),
            ("Unknown", StoryDate.Unknown())
        })
            Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
                continuity, CanonEntityType.WorldEvent, title, Occurred: date))).Success);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Date boundary world");
        var full = await reads.TimelineAsync(new());
        Assert.Equal(["Before", "Inside", "Next day", "After"], full.Items.Select(item => item.Title));
        var day = new V4StoryDateInput(V4StoryDateKind.ExactDate, "2024-02-03");
        var page = await reads.TimelineAsync(new(From: day, To: day));
        Assert.Equal("Inside", Assert.Single(page.Items).Title);
        Assert.Empty(page.Undated);
    }

    [Fact]
    public async Task TimelineKeepsTheSupportedEarlyAndLateDateBoundaries()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Date boundaries", "UTC"))).ResourceKey!);
        foreach (var (title, date) in new[]
        {
            ("Earliest supported", new DateOnly(100, 1, 1)),
            ("Latest supported", new DateOnly(9999, 12, 30))
        })
            Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
                continuity, CanonEntityType.WorldEvent, title, Occurred: StoryDate.ExactDate(date)))).Success);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Date boundaries");
        var graph = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Aggregate,
            IncludeUndated: false));
        Assert.Equal(2, graph.Items.Count);
        Assert.Equal("0100-01-01", graph.Items[0].Occurred.Lower);
        Assert.Equal("9999-12-31", graph.Items[1].Occurred.Upper);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WritingVaultMcp.csproj")))
            root = root.Parent;
        var output = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository root missing."),
            "artifacts", "phase10-boundary");
        Directory.CreateDirectory(output);
        File.Copy(vault.DatabasePath, Path.Combine(output, "WritingVault.Phase10Boundary.accdb"), true);
        Directory.CreateDirectory(Path.Combine(output, "backup"));
    }

    [Fact]
    public async Task EventTimelineListsEveryParticipantLocationAndProjectOnce()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Timeline world", "UTC"))).ResourceKey!);
        async Task<int> Create(CanonEntityType kind, string name, StoryDate? date = null) =>
            int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),
                continuity, kind, name, Occurred: date))).ResourceKey!);

        var eventId = await Create(CanonEntityType.WorldEvent, "The crossing",
            StoryDate.ExactDate(new DateOnly(2024, 2, 3)));
        var first = await Create(CanonEntityType.Character, "First witness");
        var second = await Create(CanonEntityType.Character, "Second witness");
        var location = await Create(CanonEntityType.Location, "Bridge");
        var project = await Create(CanonEntityType.Project, "Bridge story");
        Assert.True((await vault.Service.AddWorldEventParticipantAsync(
            new(Guid.NewGuid().ToString(), eventId, first, "witness"))).Success);
        Assert.True((await vault.Service.AddWorldEventParticipantAsync(
            new(Guid.NewGuid().ToString(), eventId, second, "witness"))).Success);
        Assert.True((await vault.Service.AddWorldEventLocationAsync(
            new(Guid.NewGuid().ToString(), eventId, location, true))).Success);

        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Timeline world");
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(
            vault.Factory, vault.Coordinator, references));
        var app = new AccessV4ApplicationService(vault.Coordinator, references, targets, vault.Service);
        Assert.True((await app.ApplyEventProjectsAsync(new(Guid.NewGuid().ToString(), continuity,
            "The crossing", ["Bridge story"], true, "climax", "The decisive scene."))).Success);

        var page = await reads.TimelineAsync(new(Kinds: [V4RecordKind.WorldEvent]));
        var item = Assert.Single(page.Items);
        Assert.Equal("The crossing", item.Title);
        Assert.Equal(4, item.Related.Count);
        Assert.Equal(4, item.Related.Select(link => link.Ref).Distinct().Count());
        Assert.Contains(item.Related, link => link.Label == "First witness");
        Assert.Contains(item.Related, link => link.Label == "Second witness");
        Assert.Contains(item.Related, link => link.Label == "Bridge");
        var assignment = Assert.Single(item.Projects!);
        Assert.Equal("Bridge story", assignment.Label);
        Assert.Equal("climax", assignment.AssociationRole);
        Assert.Equal("The decisive scene.", assignment.AssociationNotes);

        var witnessRef = await references.ReferenceAsync("Character", second);
        var focused = await reads.TimelineAsync(new(FocusRefs: [witnessRef]));
        Assert.Contains(focused.Items, entry => entry.Ref == item.Ref);
        var projectRef = await references.ReferenceAsync("Project", project);
        var projectPage = await reads.TimelineAsync(new(Projects: [projectRef]));
        Assert.Contains(projectPage.Items, entry => entry.Ref == item.Ref);

        Assert.True((await vault.Service.CreateRelationshipTypeAsync(
            new(Guid.NewGuid().ToString(), "friend of", false))).Success);
        Assert.True((await app.CreateCharacterRelationshipAsync(new(Guid.NewGuid().ToString(),
            continuity, "First witness", "Second witness", "friend of",
            StoryDate.ExactDate(new DateOnly(2024, 2, 4))))).Success);
        var relationships = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship]));
        // Phase 11 represents even a one-day relationship with its own entry
        // and exit, so both rows must retain the same shared identity and links.
        Assert.Equal(["Start", "End"], relationships.Items.Select(entry => entry.Boundary));
        var relationship = relationships.Items[0];
        Assert.All(relationships.Items, entry => Assert.Equal(relationship.Ref, entry.Ref));
        Assert.Contains("First witness", relationship.Title);
        Assert.Contains("Second witness", relationship.Title);
        Assert.Contains("friend of", relationship.Title);
        Assert.Contains(relationship.Related, link => link.Label == "First witness");
        Assert.Contains(relationship.Related, link => link.Label == "Second witness");
        Assert.True((await vault.Service.CreateRelationshipTypeAsync(
            new(Guid.NewGuid().ToString(), "colleague of", false))).Success);
        Assert.True((await app.CreateCharacterRelationshipAsync(new(Guid.NewGuid().ToString(),
            continuity, "First witness", "Second witness", "colleague of",
            StoryDate.Unknown()))).Success);
        var undatedRelationship = Assert.Single((await reads.TimelineAsync(new(
            Kinds: [V4RecordKind.Relationship], UndatedOnly: true))).Undated);
        Assert.Contains("First witness", undatedRelationship.Label);
        Assert.Contains("Second witness", undatedRelationship.Label);
        Assert.Contains("colleague of", undatedRelationship.Label);
        Assert.True((await vault.Service.CreateRelationshipTypeAsync(
            new(Guid.NewGuid().ToString(), "parent of", true, "child of"))).Success);
        Assert.True((await app.CreateCharacterRelationshipAsync(new(Guid.NewGuid().ToString(),
            continuity, "First witness", "Second witness", "parent of",
            StoryDate.ExactDate(new DateOnly(2024, 2, 5))))).Success);
        var directed = (await reads.TimelineAsync(new(
            Kinds: [V4RecordKind.Relationship], From: new(V4StoryDateKind.ExactDate, Value: "2024-02-05"),
            To: new(V4StoryDateKind.ExactDate, Value: "2024-02-05")))).Items;
        Assert.Equal(2, directed.Count);
        Assert.Equal(["Start", "End"], directed.Select(entry => entry.Boundary));
        Assert.All(directed, entry => Assert.Equal("First witness — parent of — Second witness", entry.Title));
    }
}
