using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class ProjectStoryEventTests
{
    private static string Token() => Guid.NewGuid().ToString();
    private static V4StoryDateInput Date(string value) => new(V4StoryDateKind.ExactDate, Value: value);

    [Fact]
    public async Task ProjectBoundariesDriveFuzzySpanAndLifecycleWithoutExtraAssignments()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Books", "UTC"))).ResourceKey!);
        var project = int.Parse((await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Project, "Book"))).ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Books");
        var app = new AccessV4ApplicationService(vault.Coordinator, refs,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, refs)), vault.Service);
        vault.EnableAutomaticPageSnapshots();
        var projectRef = await refs.ReferenceAsync("Project", project);
        var arrival = await app.RecordWorldEventAsync(new(Token(), "Arrival Day", Date("2024-09-23")), world, "test");
        Assert.True(arrival.Success, arrival.Message);
        var begin = await app.RecordEntityEventAsync(new(Token(), projectRef, "Story begins",
            new(V4StoryDateKind.Circa, Value: "2023-08-07", OriginalText: "Circa August 7, 2023"),
            ProjectBoundary: V4ProjectBoundary.StoryBegins), world, "test");
        Assert.True(begin.Success, begin.Message);
        var end = await app.RecordEntityEventAsync(new(Token(), projectRef, "Story ends", Date("2024-09-23"),
            WorldEvent: "Arrival Day", ProjectBoundary: V4ProjectBoundary.StoryEnds), world, "test");
        Assert.True(end.Success, end.Message);
        var extra = await app.RecordEntityEventAsync(new(Token(), projectRef, "Middle", Date("2024-01-01")), world, "test");
        Assert.True(extra.Success, extra.Message);
        var duplicate = await app.RecordEntityEventAsync(new(Token(), projectRef, "Another start", Date("2023-08-01"),
            ProjectBoundary: V4ProjectBoundary.StoryBegins), world, "test");
        Assert.False(duplicate.Success);

        var page = await reads.TimelineAsync(new(Projects: [projectRef]));
        Assert.Equal(4, page.Items.Count);
        var span = Assert.Single(page.Items, item => item.Kind == "Project");
        Assert.Equal("2023-08-07", span.Occurred.Lower);
        Assert.Equal("2024-09-24", span.Occurred.Upper);
        Assert.Equal(V4StoryDateKind.Circa, span.StoryBegins!.Kind);
        Assert.Equal("Circa August 7, 2023", span.StoryBegins.Display);
        Assert.Contains(page.Items, item => item.Title == "Story ends" && item.Related.Any(link => link.Label == "Arrival Day"));
        Assert.Equal(3, (await reads.RelatedAsync(new(projectRef, "events", Limit: 10))).Items.Count);
        var overview = await reads.GetAsync(new(projectRef));
        Assert.Equal("Circa", overview.Fields["storyBegins"].GetProperty("kind").GetString());
        Assert.Equal("2024-09-23", overview.Fields["storyEnds"].GetProperty("lower").GetString());
        var expanded = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Project], ExpandRanges: true));
        Assert.Equal(2, expanded.Items.Count);
        Assert.Equal(V4StoryDateKind.Circa, expanded.Items[0].BoundaryDate!.Kind);

        var endRef = await refs.ReferenceAsync("EntityEvent", int.Parse(end.ResourceKey!));
        var update = await app.UpdateEventAsync(new(Token(), endRef, 1, Occurred: Date("2024-09-25")), world, "test");
        Assert.True(update.Success, update.Message);
        Assert.Equal("2024-09-26", Assert.Single((await reads.TimelineAsync(new(Kinds: [V4RecordKind.Project]))).Items).Occurred.Upper);
        var reversed = await app.UpdateEventAsync(new(Token(), endRef, 2, Occurred: Date("2020-01-01")), world, "test");
        Assert.False(reversed.Success);
        var lifecycle = new AccessV4RecordService(vault.Coordinator, refs, session, vault.Service);
        Assert.True((await lifecycle.LifecycleAsync(new(Token(), endRef, 2), false)).Success);
        Assert.Null(Assert.Single((await reads.TimelineAsync(new(Kinds: [V4RecordKind.Project]))).Items).Occurred.Upper);
        Assert.True((await lifecycle.LifecycleAsync(new(Token(), endRef, 3), true)).Success);
        Assert.Equal("2024-09-26", Assert.Single((await reads.TimelineAsync(new(Kinds: [V4RecordKind.Project]))).Items).Occurred.Upper);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Theory]
    [InlineData(V4RecurrenceFrequency.Daily, "2024-01-02", "2024-01-04", 3)]
    [InlineData(V4RecurrenceFrequency.Weekly, "2024-01-01", "2024-01-22", 4)]
    [InlineData(V4RecurrenceFrequency.Monthly, "2024-01-01", "2024-04-01", 4)]
    [InlineData(V4RecurrenceFrequency.Yearly, "2024-01-01", "2027-01-01", 4)]
    public async Task RepeatsRespectViewportInclusiveStopAndStableAutomaticBounds(
        V4RecurrenceFrequency frequency, string from, string until, int count)
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Repeats", "UTC"))).ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Repeats");
        var app = new AccessV4ApplicationService(vault.Coordinator, refs,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, refs)), vault.Service);
        var added = await app.RecordWorldEventAsync(new(Token(), "Recurring", Date("2024-01-01"),
            Recurrence: new(frequency, Until: until)), world, "test");
        Assert.True(added.Success, added.Message);
        // Stop date is not an authored event and must not make the unbounded query grow.
        var automatic = await reads.TimelineAsync(new());
        Assert.Single(automatic.Items);
        Assert.False(automatic.Items[0].IsOccurrence);
        foreach (var resolution in new[] { V4TimelineResolution.Detail, V4TimelineResolution.Aggregate })
        {
            var visible = await reads.TimelineAsync(new(From: Date(from), To: Date("2030-01-01"), Resolution: resolution));
            Assert.Equal(count, visible.Items.Count);
            Assert.Equal(until, visible.Items[^1].Occurred.Lower);
            Assert.All(visible.Items, item => Assert.Equal(automatic.Items[0].Ref, item.Ref));
        }
        var eventRef = automatic.Items[0].Ref;
        Assert.True((await app.UpdateEventAsync(new(Token(), eventRef, 1, ClearRecurrence: true), world, "test")).Success);
        Assert.Single((await reads.TimelineAsync(new(From: Date("2024-01-01"), To: Date("2030-01-01")))).Items);
        Assert.False((await app.UpdateEventAsync(new(Token(), eventRef, 1, Recurrence: new(frequency)), world, "test")).Success);
    }

    [Fact]
    public void MonthlyAndLeapDayRepeatsSkipMissingDatesWithoutDrifting()
    {
        Assert.Equal([new DateOnly(2024, 1, 31), new DateOnly(2024, 3, 31), new DateOnly(2024, 5, 31)],
            AccessV4ReadService.RecurrenceDates(new(2024, 1, 31), new(V4RecurrenceFrequency.Monthly), new(2024, 1, 1), new(2024, 6, 1)));
        Assert.Equal([new DateOnly(2024, 2, 29), new DateOnly(2028, 2, 29)],
            AccessV4ReadService.RecurrenceDates(new(2024, 2, 29), new(V4RecurrenceFrequency.Yearly), new(2024, 1, 1), new(2030, 1, 1)));
    }

    [Fact]
    public async Task BirthdayFlagRepeatsAnnuallyThroughDeathAndRespondsToEdits()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Birthdays", "UTC"))).ResourceKey!);
        var created = await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Character, "Birthday person",
            Birth: StoryDate.ExactDate(new(2000, 6, 7)), Death: StoryDate.ExactDate(new(2024, 6, 7)), BirthdayRecurring: true));
        Assert.True(created.Success, created.Message);
        var character = int.Parse(created.ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Birthdays");
        var reference = await refs.ReferenceAsync("Character", character);
        var page = await reads.TimelineAsync(new(From: Date("2022-01-01"), To: Date("2030-12-31")));
        var birthdays = page.Items.Where(item => item.IsOccurrence).ToArray();
        Assert.Equal(["2022-06-07", "2023-06-07", "2024-06-07"], birthdays.Select(item => item.Occurred.Lower));
        Assert.All(birthdays, item => { Assert.Equal(reference, item.Ref); Assert.Equal("Birthday person — birthday", item.Title); });
        var automatic = await reads.TimelineAsync(new());
        Assert.DoesNotContain(automatic.Items, item => string.CompareOrdinal(item.Occurred.Lower, "2024-06-07") > 0);
        Assert.True((await vault.Service.PatchEntityAsync(new(Token(), character, 1,
            Death: new(true, StoryDate.ExactDate(new(2023, 5, 1)))))).Success);
        Assert.Single((await reads.TimelineAsync(new(From: Date("2022-01-01"), To: Date("2030-12-31")))).Items, item => item.IsOccurrence);
        Assert.True((await vault.Service.PatchEntityAsync(new(Token(), character, 2, BirthdayRecurring: new(true, false)))).Success);
        Assert.DoesNotContain((await reads.TimelineAsync(new())).Items, item => item.IsOccurrence);
        Assert.False((await vault.Service.PatchEntityAsync(new(Token(), character, 3,
            Birth: new(true, StoryDate.Year(2000)), BirthdayRecurring: new(true, true)))).Success);
        Assert.False((await reads.GetAsync(new(reference))).Fields["birthdayRecurring"].GetBoolean());
    }

    [Fact]
    public async Task RecurrenceFiltersAndPagingDoNotUseUnrelatedDatesToExtendRange()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Repeat scopes", "UTC"))).ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Repeat scopes");
        var app = new AccessV4ApplicationService(vault.Coordinator, refs,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, refs)), vault.Service);
        Assert.True((await app.RecordWorldEventAsync(new(Token(), "Repeat me", Date("2024-01-01"),
            Recurrence: new(V4RecurrenceFrequency.Daily)), world, "test")).Success);
        Assert.True((await app.RecordWorldEventAsync(new(Token(), "Other future event", Date("2030-01-01")), world, "test")).Success);
        Assert.Single((await reads.TimelineAsync(new(Text: "Repeat me"))).Items);
        var first = await reads.TimelineAsync(new(From: Date("2024-01-05"), To: Date("2024-01-09"), Limit: 2));
        var second = await reads.TimelineAsync(new(From: Date("2024-01-05"), To: Date("2024-01-09"), Limit: 2, Cursor: first.NextCursor));
        var third = await reads.TimelineAsync(new(From: Date("2024-01-05"), To: Date("2024-01-09"), Limit: 2, Cursor: second.NextCursor));
        Assert.Equal(["2024-01-05", "2024-01-06", "2024-01-07", "2024-01-08", "2024-01-09"],
            first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Occurred.Lower));
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task BuildProjectAndRecurrenceBrowserFixture()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Token(), "Project Events Preview", "UTC"))).ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Project Events Preview");
        var app = new AccessV4ApplicationService(vault.Coordinator, refs,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, refs)), vault.Service);
        var project = await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Project, "Among the Shattered Shadows"));
        Assert.True(project.Success, project.Message);
        var projectRef = await refs.ReferenceAsync("Project", int.Parse(project.ResourceKey!));
        Assert.True((await app.RecordWorldEventAsync(new(Token(), "Arrival Day", Date("2024-09-23"),
            Recurrence: new(V4RecurrenceFrequency.Yearly, Until: "2026-09-23")), world, "fixture")).Success);
        Assert.True((await app.RecordEntityEventAsync(new(Token(), projectRef, "Story begins",
            new(V4StoryDateKind.Circa, Value: "2023-08-07", OriginalText: "Circa August 7, 2023"),
            ProjectBoundary: V4ProjectBoundary.StoryBegins), world, "fixture")).Success);
        Assert.True((await app.RecordEntityEventAsync(new(Token(), projectRef, "Story ends", Date("2024-09-23"),
            WorldEvent: "Arrival Day", ProjectBoundary: V4ProjectBoundary.StoryEnds), world, "fixture")).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(Token(), world, CanonEntityType.Character, "Nora",
            Birth: StoryDate.ExactDate(new(2000, 9, 23)), Death: StoryDate.ExactDate(new(2025, 9, 22)), BirthdayRecurring: true))).Success);
        Assert.True((await app.RecordWorldEventAsync(new(Token(), "Daily bell", Date("2024-01-01"),
            Recurrence: new(V4RecurrenceFrequency.Daily, Until: "2024-01-05")), world, "fixture")).Success);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
        var root = Path.GetDirectoryName(TestServer.AssemblyPath)!;
        var output = Path.GetFullPath(Path.Combine(root, "..", "..", "..", "artifacts", "project-events-preview"));
        Directory.CreateDirectory(output);
        File.Copy(vault.DatabasePath, Path.Combine(output, "preview.accdb"), true);
        await File.WriteAllTextAsync(Path.Combine(output, "project-ref.txt"), projectRef);
    }

    [Fact]
    public async Task ForwardMigrationPreservesExistingDataAndCanResume()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = await vault.Service.CreateContinuityAsync(new(Token(), "Before upgrade", "UTC"));
        var oldCharacter = await vault.Service.CreateEntityAsync(new(Token(), int.Parse(world.ResourceKey!), CanonEntityType.Character, "Existing person"));
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            async Task Execute(string sql)
            { using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
            foreach (var table in new[] { "EntityEvents", "WorldEvents", "RelationshipEvents" })
                foreach (var column in new[] { "RecurrenceFrequency", "RecurrenceInterval", "RecurrenceUntil" })
                    await Execute($"ALTER TABLE [{table}] DROP COLUMN [{column}]");
            await Execute("ALTER TABLE [Characters] DROP COLUMN [BirthdayRecurring]");
            // Leave ProjectBoundary present to exercise a migration resumed after partial DDL.
            await Execute($"DELETE FROM [SchemaMigrations] WHERE [MigrationId]='{AccessSchemaDefinition.MigrationId}'");
        }
        var migration = await new AccessSchemaMigrator(vault.Factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(migration.Changed);
        Assert.True(migration.Verification.IsValid);
        Assert.Equal("Before upgrade", Assert.Single(await vault.Service.ListContinuitiesAsync()).Name);
        Assert.False((bool)(await vault.Service.GetEntityAsync(int.Parse(oldCharacter.ResourceKey!)))!.Fields["BirthdayRecurring"]!);
    }
}
