using WritingVaultMcp.Domain;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4RelationshipTransitionTests
{
    [Fact]
    public async Task RelationshipTransitionDescriptionsOverrideAndClearIndependently()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Relationship descriptions", "UTC"))).ResourceKey!);
        var first = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Sam"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Aurora"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8))));
        Assert.True(relationship.Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Relationship descriptions");
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship",
            int.Parse(relationship.ResourceKey!));
        var periodRef = (await reads.GetAsync(new(relationshipRef))).Sections["membershipPeriods"].Items[0].Ref;
        var version = (await reads.GetAsync(new(periodRef))).Summary.Version!.Value;
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var date = StoryDate.ExactDate(new DateOnly(2021, 9, 8));
        var changed = await records.SetRelationshipMembershipTransitionsAsync(new(
            Guid.NewGuid().ToString(), periodRef, version,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-08"),
            new(V4StoryDateKind.ExactDate, Value: "2021-09-08"),
            JoinDescription: "Sam arrived at the café.",
            LeaveDescription: "Sam departed at dusk."), date, date);
        Assert.True(changed.Success, $"{changed.Code}: {changed.Message}");
        var timeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipMembershipPeriod],
            Resolution: V4TimelineResolution.Detail, Limit: 100));
        Assert.Contains(timeline.Items, item => item.Ref == periodRef &&
            item.Title == "Sam arrived at the café." && item.HasCustomDescription);
        Assert.Contains(timeline.Items, item => item.Ref == periodRef &&
            item.Title == "Sam departed at dusk." && item.HasCustomDescription);
        version = (await reads.GetAsync(new(periodRef))).Summary.Version!.Value;
        changed = await records.SetRelationshipMembershipTransitionsAsync(new(
            Guid.NewGuid().ToString(), periodRef, version,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-08"),
            new(V4StoryDateKind.ExactDate, Value: "2021-09-08"),
            ClearJoinDescription: true), date, date);
        Assert.True(changed.Success, $"{changed.Code}: {changed.Message}");
        timeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipMembershipPeriod],
            Resolution: V4TimelineResolution.Detail, Limit: 100));
        Assert.Contains(timeline.Items, item => item.Ref == periodRef &&
            item.Title == "Sam entered a relationship." && !item.HasCustomDescription);
        Assert.Contains(timeline.Items, item => item.Ref == periodRef &&
            item.Title == "Sam departed at dusk." && item.HasCustomDescription);
    }

    [Fact]
    public async Task OrganizationTransitionsKeepIndependentDatesAndDescriptions()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Organization transitions", "UTC"))).ResourceKey!);
        var person = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Sam"))).ResourceKey!);
        var organization = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Organization, "The Society"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Organization transitions");
        var application = new AccessV4ApplicationService(vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            vault.Service);
        var fuzzyJoin = new StoryDate(StoryDateKind.Month, new DateTime(2021, 9, 1),
            new DateTime(2021, 10, 1));
        var leave = StoryDate.ExactDate(new DateOnly(2021, 12, 10));
        var added = await application.AddOrganizationMembershipAsync(new(
            Guid.NewGuid().ToString(), continuity,
            await references.ReferenceAsync("Organization", organization),
            await references.ReferenceAsync("Character", person),
            Joined: fuzzyJoin, Left: leave,
            JoinDescription: "Sam joined under an assumed name."));
        Assert.True(added.Success, $"{added.Code}: {added.Message}");
        var membershipRef = await references.ReferenceAsync("OrganizationMembership", int.Parse(added.ResourceKey!));
        var record = await reads.GetAsync(new(membershipRef));
        Assert.Equal("Month", record.Fields["joined"].GetProperty("kind").GetString());
        Assert.Equal("ExactDate", record.Fields["left"].GetProperty("kind").GetString());
        Assert.Equal("Sam joined under an assumed name.", record.Fields["joinDescription"].GetString());
        var timeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Membership],
            Resolution: V4TimelineResolution.Detail, Limit: 100));
        Assert.Contains(timeline.Items, item => item.Ref == membershipRef &&
            item.Title == "Sam joined under an assumed name." && item.HasCustomDescription);
        Assert.Contains(timeline.Items, item => item.Ref == membershipRef &&
            item.Title == "Sam left organization The Society." && !item.HasCustomDescription);

        var changed = await new AccessV4RecordService(vault.Coordinator, references, session, vault.Service)
            .SetOrganizationMembershipTransitionsAsync(new(Guid.NewGuid().ToString(), membershipRef,
                record.Summary.Version!.Value,
                new(V4StoryDateKind.ExactDate, Value: "2021-09-08"),
                new(V4StoryDateKind.ExactDate, Value: "2021-12-10"),
                LeaveDescription: "Sam left after the meeting.", ClearJoinDescription: true),
                StoryDate.ExactDate(new DateOnly(2021, 9, 8)), leave);
        Assert.True(changed.Success, $"{changed.Code}: {changed.Message}");
        timeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Membership],
            Resolution: V4TimelineResolution.Detail, Limit: 100));
        Assert.Contains(timeline.Items, item => item.Ref == membershipRef &&
            item.Title == "Sam joined organization The Society." && !item.HasCustomDescription);
        Assert.Contains(timeline.Items, item => item.Ref == membershipRef &&
            item.Title == "Sam left after the meeting." && item.HasCustomDescription);
        record = await reads.GetAsync(new(membershipRef));
        var ended = await new AccessV4RecordService(vault.Coordinator, references, session, vault.Service)
            .TransitionOrganizationMembershipAsync(new(Guid.NewGuid().ToString(), membershipRef,
                record.Summary.Version!.Value, "2021-11-01T12:00:00", false,
                LeaveDescription: "Sam resigned at noon."),
                new DateTime(2021, 11, 1, 12, 0, 0));
        Assert.True(ended.Success, $"{ended.Code}: {ended.Message}");
        timeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Membership],
            Resolution: V4TimelineResolution.Detail, Limit: 100));
        Assert.Contains(timeline.Items, item => item.Ref == membershipRef &&
            item.Title == "Sam resigned at noon." && item.HasCustomDescription);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task ExactInstantGroupHasBothTransitionsWhileLegacyRangeDoesNotInventThem()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Instant and range", "UTC"))).ResourceKey!);
        var ari = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var bo = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var moment = new DateTime(2021, 9, 8, 8, 30, 0);
        var instant = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type, StoryDate.ExactInstant(moment)));
        var fuzzy = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type,
            new StoryDate(StoryDateKind.Range, new DateTime(2022, 1, 1), new DateTime(2022, 2, 1))));
        Assert.True(instant.Success);
        Assert.True(fuzzy.Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Instant and range");
        var instantRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(instant.ResourceKey!));
        var fuzzyRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(fuzzy.ResourceKey!));
        var groupTimeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: true, ExpandRanges: true));
        Assert.Equal(new[] { "Start", "End" }, groupTimeline.Items.Where(item => item.Ref == instantRef)
            .Select(item => item.Boundary).OrderBy(item => item == "Start" ? 0 : 1).ToArray());
        Assert.Contains(groupTimeline.Undated, item => item.Ref == fuzzyRef);
        var membershipTimeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipMembershipPeriod],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: true, ExpandRanges: true));
        Assert.Equal(2, membershipTimeline.Items.Count(item => item.Occurred.Kind == V4StoryDateKind.Range));
        Assert.DoesNotContain(membershipTimeline.Items,
            item => item.Occurred.Kind == V4StoryDateKind.Range && item.Boundary is not null);

        var timedType = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "bonded", false))).ResourceKey!);
        var timed = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, [ari, bo], timedType, null));
        Assert.True(timed.Success, $"{timed.Code}: {timed.Message}");
        var timedRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(timed.ResourceKey!));
        var application = new AccessV4ApplicationService(vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            vault.Service);
        foreach (var (person, version) in new[] { (ari, 1), (bo, 2) })
        {
            var added = await application.AddRelationshipMembershipPeriodAsync(new(
                Guid.NewGuid().ToString(), continuity, timedRef,
                await references.ReferenceAsync("Character", person), version,
                Joined: StoryDate.ExactInstant(new DateTime(2023, 5, 1, 8, 0, 0)),
                Left: StoryDate.ExactInstant(new DateTime(2023, 5, 1, 22, 0, 0))));
            Assert.True(added.Success, $"{added.Code}: {added.Message}");
        }
        groupTimeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: true));
        var timedItems = groupTimeline.Items.Where(item => item.Ref == timedRef).ToArray();
        Assert.Equal(2, timedItems.Length);
        Assert.Contains(timedItems, item => item.Boundary == "Start" &&
            item.BoundaryDate?.Lower?.StartsWith("2023-05-01T08:00:00", StringComparison.Ordinal) == true);
        Assert.Contains(timedItems, item => item.Boundary == "End" &&
            item.BoundaryDate?.Lower?.StartsWith("2023-05-01T22:00:00", StringComparison.Ordinal) == true);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task UncertainThirdMemberDoesNotEraseTwoCertainMembersActiveSpan()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Mixed certainty", "UTC"))).ResourceKey!);
        var people = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam" })
            people.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var group = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, people, type, null));
        Assert.True(group.Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Mixed certainty");
        var groupRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(group.ResourceKey!));
        var application = new AccessV4ApplicationService(vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            vault.Service);
        for (var index = 0; index < people.Count; index++)
        {
            var joined = index < 2
                ? StoryDate.ExactDate(new DateOnly(2021, 9, 1))
                : new StoryDate(StoryDateKind.Month, new DateTime(2021, 9, 1), new DateTime(2021, 10, 1));
            var added = await application.AddRelationshipMembershipPeriodAsync(new(
                "mixed-member-" + index, continuity, groupRef,
                await references.ReferenceAsync("Character", people[index]), index + 1,
                Joined: joined, Left: StoryDate.ExactDate(new DateOnly(2021, 9, 10))));
            Assert.True(added.Success, $"{added.Code}: {added.Message}");
        }
        var page = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: true));
        Assert.Equal(2, page.Items.Count(item => item.Ref == groupRef));
        Assert.Contains(page.Undated, item => item.Ref == groupRef);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task OppositeFuzzyAndUnknownBoundariesStayIndependent()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Independent boundaries", "UTC"))).ResourceKey!);
        var otherContinuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Other world", "UTC"))).ResourceKey!);
        var ari = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var bo = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var relationship = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 1))));
        Assert.True(relationship.Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Independent boundaries");
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(relationship.ResourceKey!));
        Assert.Contains("Ari — partners — Bo", (await reads.GetAsync(new(relationshipRef))).Summary.Label);
        var periodRef = (await reads.GetAsync(new(relationshipRef))).Sections["membershipPeriods"].Items[0].Ref;
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var version = (await reads.GetAsync(new(periodRef))).Summary.Version!.Value;

        var fuzzyJoin = await records.SetRelationshipMembershipTransitionsAsync(new(
            "fuzzy-join-exact-leave", periodRef, version,
            new(V4StoryDateKind.Month, Value: "2021-08"),
            new(V4StoryDateKind.ExactDate, Value: "2021-10-10")),
            new StoryDate(StoryDateKind.Month, new DateTime(2021, 8, 1), new DateTime(2021, 9, 1)),
            StoryDate.ExactDate(new DateOnly(2021, 10, 10)));
        Assert.True(fuzzyJoin.Success, $"{fuzzyJoin.Code}: {fuzzyJoin.Message}");
        var view = await reads.GetAsync(new(periodRef));
        Assert.Equal("Month", view.Fields["joined"].GetProperty("kind").GetString());
        Assert.Equal("ExactDate", view.Fields["left"].GetProperty("kind").GetString());

        var unknownJoin = await records.SetRelationshipMembershipTransitionsAsync(new(
            "unknown-join", periodRef, view.Summary.Version!.Value,
            new(V4StoryDateKind.Unknown), new(V4StoryDateKind.ExactDate, Value: "2021-10-10")),
            StoryDate.Unknown(), StoryDate.ExactDate(new DateOnly(2021, 10, 10)));
        Assert.True(unknownJoin.Success, $"{unknownJoin.Code}: {unknownJoin.Message}");
        view = await reads.GetAsync(new(periodRef));
        Assert.Equal("Before", view.Fields["periodKind"].GetString());
        Assert.Equal("Unknown", view.Fields["joined"].GetProperty("kind").GetString());

        var unknownLeave = await records.SetRelationshipMembershipTransitionsAsync(new(
            "unknown-leave", periodRef, view.Summary.Version!.Value,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-01"), new(V4StoryDateKind.Unknown)),
            StoryDate.ExactDate(new DateOnly(2021, 9, 1)), StoryDate.Unknown());
        Assert.True(unknownLeave.Success, $"{unknownLeave.Code}: {unknownLeave.Message}");
        view = await reads.GetAsync(new(periodRef));
        Assert.Equal("After", view.Fields["periodKind"].GetString());

        var sameDay = await records.SetRelationshipMembershipTransitionsAsync(new(
            "same-day-boundaries", periodRef, view.Summary.Version!.Value,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-01"),
            new(V4StoryDateKind.ExactDate, Value: "2021-09-01")),
            StoryDate.ExactDate(new DateOnly(2021, 9, 1)),
            StoryDate.ExactDate(new DateOnly(2021, 9, 1)));
        Assert.True(sameDay.Success, $"{sameDay.Code}: {sameDay.Message}");
        var chronology = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail,
            Kinds: [V4RecordKind.RelationshipMembershipPeriod], Limit: 100));
        Assert.Equal(new[] { "Start", "End" }, chronology.Items.Where(item => item.Ref == periodRef)
            .Select(item => item.Boundary).OrderBy(item => item == "Start" ? 0 : 1).ToArray());
        Assert.Contains(chronology.Items, item => item.Ref == periodRef && item.Boundary == "Start" &&
            item.Title == "Ari entered a relationship.");
        Assert.Contains(chronology.Items, item => item.Ref == periodRef && item.Boundary == "End" &&
            item.Title == "Ari left a relationship.");
        var graph = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Aggregate,
            Kinds: [V4RecordKind.RelationshipMembershipPeriod], Limit: 100));
        Assert.Contains(graph.Items, item => item.Ref == periodRef && item.Boundary == "Start" &&
            item.Title == "Ari entered a relationship.");
        Assert.Contains(graph.Items, item => item.Ref == periodRef && item.Boundary == "End" &&
            item.Title == "Ari left a relationship.");

        var application = new AccessV4ApplicationService(vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            vault.Service);
        var relationshipVersion = (await reads.GetAsync(new(relationshipRef))).Summary.Version!.Value;
        var repeat = await application.AddRelationshipMembershipPeriodAsync(new(
            "atomic-fuzzy-reentry", continuity, relationshipRef,
            await references.ReferenceAsync("Character", ari), relationshipVersion,
            Joined: new StoryDate(StoryDateKind.Month, new DateTime(2022, 1, 1), new DateTime(2022, 2, 1)),
            Left: StoryDate.ExactDate(new DateOnly(2022, 3, 1))));
        Assert.True(repeat.Success, $"{repeat.Code}: {repeat.Message}");
        var replay = await application.AddRelationshipMembershipPeriodAsync(new(
            "atomic-fuzzy-reentry", continuity, relationshipRef,
            await references.ReferenceAsync("Character", ari), relationshipVersion,
            Joined: new StoryDate(StoryDateKind.Month, new DateTime(2022, 1, 1), new DateTime(2022, 2, 1)),
            Left: StoryDate.ExactDate(new DateOnly(2022, 3, 1))));
        Assert.True(replay.Success);
        Assert.True(replay.Replayed);
        var repeatRef = await references.ReferenceAsync("RelationshipMembershipPeriod", int.Parse(repeat.ResourceKey!));
        var repeatView = await reads.GetAsync(new(repeatRef));
        Assert.Equal("Month", repeatView.Fields["joined"].GetProperty("kind").GetString());
        Assert.Equal("ExactDate", repeatView.Fields["left"].GetProperty("kind").GetString());
        var versionAfterRepeat = (await reads.GetAsync(new(relationshipRef))).Summary.Version!.Value;
        var collision = await application.AddRelationshipMembershipPeriodAsync(new(
            "atomic-overlap", continuity, relationshipRef,
            await references.ReferenceAsync("Character", ari), versionAfterRepeat,
            Joined: StoryDate.ExactDate(new DateOnly(2022, 2, 1)),
            Left: StoryDate.ExactDate(new DateOnly(2022, 2, 5))));
        Assert.False(collision.Success);
        Assert.Equal("interval.overlap", collision.Code);
        Assert.Equal(versionAfterRepeat, (await reads.GetAsync(new(relationshipRef))).Summary.Version);

        var finalVersion = (await reads.GetAsync(new(periodRef))).Summary.Version!.Value;
        session.SelectContinuity(otherContinuity, "Other world");
        var crossContinuity = await records.SetRelationshipMembershipTransitionsAsync(new(
            "cross-continuity", periodRef, finalVersion,
            new(V4StoryDateKind.Unknown), new(V4StoryDateKind.Unknown)),
            StoryDate.Unknown(), StoryDate.Unknown());
        Assert.False(crossContinuity.Success);
        Assert.Equal("record.not_found", crossContinuity.Code);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task JoinAndLeaveKeepIndependentPrecisionAndConservativeOccupancy()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Transition test", "UTC"))).ResourceKey!);
        var ari = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ari"))).ResourceKey!);
        var bo = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Bo"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var relationship = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, ari, bo, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 1))));
        Assert.True(relationship.Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Transition test");
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(relationship.ResourceKey!));
        var periodRef = (await reads.GetAsync(new(relationshipRef))).Sections["membershipPeriods"].Items[0].Ref;
        var before = await reads.GetAsync(new(periodRef));
        Assert.Contains("membership", before.Summary.Label);
        Assert.Contains("Ari", before.Summary.Label);
        Assert.Equal("Ari", Assert.Single(before.Sections["participant"].Items).Label);
        var linkedRelationship = Assert.Single(before.Sections["relationship"].Items);
        Assert.Equal(relationshipRef, linkedRelationship.Ref);
        Assert.Equal("Ari — partners — Bo", linkedRelationship.Label);
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var joined = StoryDate.ExactDate(new DateOnly(2021, 9, 1));
        var left = new StoryDate(StoryDateKind.Month,
            new DateTime(2021, 10, 1), new DateTime(2021, 11, 1));
        var request = new V4RelationshipMembershipTransitionsSetRequest(
            "transitions-fuzzy-leave", periodRef, before.Summary.Version!.Value,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-01"),
            new(V4StoryDateKind.Month, Value: "2021-10"));
        var set = await records.SetRelationshipMembershipTransitionsAsync(request, joined, left);
        Assert.True(set.Success, $"{set.Code}: {set.Message}");
        var after = await reads.GetAsync(new(periodRef));
        Assert.Equal("ExactDate", after.Fields["joined"].GetProperty("kind").GetString());
        Assert.Equal("Month", after.Fields["left"].GetProperty("kind").GetString());
        Assert.Equal("UncertainRange", after.Fields["periodKind"].GetString());
        Assert.Equal("2021-09-01", after.Fields["joined"].GetProperty("lower").GetString());
        Assert.Equal("2021-10-01", after.Fields["left"].GetProperty("lower").GetString());
        var chronology = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail,
            Kinds: [V4RecordKind.RelationshipMembershipPeriod], Limit: 100));
        var transitions = chronology.Items.Where(item => item.Ref == periodRef).ToArray();
        Assert.True(transitions.Length == 2,
            $"Expected two transitions for {periodRef}; got: " +
            string.Join("; ", chronology.Items.Select(item => $"{item.Ref} {item.Kind} {item.Title} {item.Boundary}")));
        Assert.Contains(transitions, item => item.Boundary == "Start" && item.Occurred.Kind == V4StoryDateKind.ExactDate);
        Assert.Contains(transitions, item => item.Boundary == "End" && item.Occurred.Kind == V4StoryDateKind.Month);
        var groupTimeline = await reads.TimelineAsync(new(Resolution: V4TimelineResolution.Detail,
            Kinds: [V4RecordKind.Relationship], IncludeUndated: true, Limit: 100));
        Assert.Contains(groupTimeline.Undated, item => item.Ref == relationshipRef);

        var corrected = await records.UpdateRelationshipMembershipPeriodAsync(new(
            "whole-period-correction", periodRef, after.Summary.Version!.Value,
            new(V4StoryDateKind.ExactDate, Value: "2021-12-01")),
            StoryDate.ExactDate(new DateOnly(2021, 12, 1)));
        Assert.True(corrected.Success, $"{corrected.Code}: {corrected.Message}");
        var correctedView = await reads.GetAsync(new(periodRef));
        Assert.False(correctedView.Fields.ContainsKey("joined"));
        Assert.False(correctedView.Fields.ContainsKey("left"));
        var reset = await records.SetRelationshipMembershipTransitionsAsync(
            request with { MutationToken = "transitions-reset", ExpectedVersion = correctedView.Summary.Version!.Value },
            joined, left);
        Assert.True(reset.Success, $"{reset.Code}: {reset.Message}");

        var replay = await records.SetRelationshipMembershipTransitionsAsync(request, joined, left);
        Assert.True(replay.Success);
        Assert.True(replay.Replayed);
        var invalid = await records.SetRelationshipMembershipTransitionsAsync(
            request with { MutationToken = "invalid-order", ExpectedVersion = (await reads.GetAsync(new(periodRef))).Summary.Version!.Value },
            StoryDate.ExactDate(new DateOnly(2022, 1, 1)),
            StoryDate.ExactDate(new DateOnly(2021, 1, 1)));
        Assert.False(invalid.Success);
        Assert.Equal("validation.transition_order", invalid.Code);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }
}
