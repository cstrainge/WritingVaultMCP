using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase11RelationshipTests
{
    [Fact]
    public async Task RelationshipIntegrityProbesExecuteOnAccess()
    {
        await using var vault = await TestVault.CreateAsync();
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        foreach (var probe in AccessInvariantProbes.Continuity.Concat(
            AccessInvariantProbes.Shapes.Select(shape => (shape.Name, shape.Sql))))
        {
            using var command = connection.CreateCommand();
            command.CommandText = probe.Sql;
            try { _ = await command.ExecuteScalarAsync(); }
            catch (OleDbException exception) { Assert.Fail($"{probe.Name}: {exception.Message}"); }
        }
    }

    [Fact]
    public async Task GroupCreationStoresOneIdentityAndThreeInitialMemberships()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuityResult = await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Group test", "UTC"));
        Assert.True(continuityResult.Success, $"{continuityResult.Code}: {continuityResult.Message}");
        var continuity = int.Parse(continuityResult.ResourceKey!);
        var characters = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam" })
            characters.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var result = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, characters, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8)), "Shared notes."));
        Assert.True(result.Success, result.Message);
        var relationship = int.Parse(result.ResourceKey!);
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [RelationshipParticipants] WHERE [RelationshipId]={relationship} AND [IsDeleted]=False";
        Assert.Equal(3, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = $"SELECT COUNT(*) FROM [RelationshipMembershipPeriods] AS m INNER JOIN [RelationshipParticipants] AS p ON m.[ParticipantId]=p.[Id] WHERE p.[RelationshipId]={relationship} AND m.[IsDeleted]=False";
        Assert.Equal(3, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = $"SELECT [Notes] FROM [CharacterRelationships] WHERE [Id]={relationship}";
        Assert.Equal("Shared notes.", await command.ExecuteScalarAsync());
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Group test");
        var links = new List<string>();
        foreach (var character in characters)
        {
            var person = await references.ReferenceAsync("Character", character);
            links.Add(Assert.Single((await reads.RelatedAsync(new(person, "relationships"))).Items).Ref);
            Assert.Single((await reads.GetAsync(new(person))).Sections["relationshipMembershipPeriods"].Items);
        }
        Assert.Single(links.Distinct());
        var detail = await reads.GetAsync(new(links[0]));
        Assert.Equal(3, detail.Sections["characters"].Items.Count);
        var foundByThirdMember = await reads.SearchAsync(new(
            Text: "Cam", Kinds: [V4RecordKind.Relationship]));
        Assert.Equal(links[0], Assert.Single(foundByThirdMember.Items).Ref);
        Assert.Single((await reads.SearchAsync(new(
            Text: "Cam", Kinds: [V4RecordKind.RelationshipParticipant]))).Items);
        Assert.Single((await reads.SearchAsync(new(
            Text: "Cam", Kinds: [V4RecordKind.RelationshipMembershipPeriod]))).Items);
        command.CommandText = $"SELECT [Id] FROM [RelationshipParticipants] WHERE [RelationshipId]={relationship} AND [CharacterId]={characters[2]}";
        var thirdParticipant = await references.ReferenceAsync("RelationshipParticipant",
            Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = $"SELECT [Id] FROM [RelationshipParticipants] WHERE [RelationshipId]={relationship} AND [CharacterId]={characters[1]}";
        var secondParticipant = await references.ReferenceAsync("RelationshipParticipant",
            Convert.ToInt32(await command.ExecuteScalarAsync()));
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var removed = await records.LifecycleAsync(new("participant-remove", thirdParticipant, 1), false);
        Assert.True(removed.Success, $"{removed.Code}: {removed.Message}");
        Assert.Equal(2, (await reads.GetAsync(new(links[0]))).Sections["characters"].Items.Count);
        Assert.Empty((await reads.SearchAsync(new(
            Text: "Cam", Kinds: [V4RecordKind.RelationshipParticipant]))).Items);
        Assert.Single((await reads.SearchAsync(new(
            Text: "Cam", Kinds: [V4RecordKind.RelationshipParticipant],
            DeletionState: V4DeletionState.Deleted))).Items);
        var tooFew = await records.LifecycleAsync(new("participant-remove-too-many", secondParticipant, 1), false);
        Assert.Equal("delete.blocked", tooFew.Code);
        var restored = await records.LifecycleAsync(new(
            "participant-restore", thirdParticipant, removed.Version!.Value), true);
        Assert.True(restored.Success, $"{restored.Code}: {restored.Message}");
        Assert.Equal(3, (await reads.GetAsync(new(links[0]))).Sections["characters"].Items.Count);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task DirectedGroupAndCrossContinuityParticipantAreRejected()
    {
        await using var vault = await TestVault.CreateAsync();
        var firstResult = await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "First group", "UTC"));
        Assert.True(firstResult.Success, $"{firstResult.Code}: {firstResult.Message}");
        var first = int.Parse(firstResult.ResourceKey!);
        var second = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Second group", "UTC"))).ResourceKey!);
        var characters = new List<int>();
        foreach (var (continuity, name) in new[] { (first, "Ari"), (first, "Bo"), (first, "Cam"), (second, "Dee") })
            characters.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var directed = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "parent of", true, "child of"))).ResourceKey!);
        var undirected = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var directedResult = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), first, characters.Take(3).ToArray(),
            directed, StoryDate.Unknown()));
        Assert.False(directedResult.Success);
        Assert.Equal("relationship.directed_pair", directedResult.Code);
        var foreign = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), first, [characters[0], characters[1], characters[3]],
            undirected, StoryDate.Unknown()));
        Assert.False(foreign.Success);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task LargeGroupUsesBoundedHumanLabelsOnRecordAndTimeline()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Large group", "UTC"))).ResourceKey!);
        var people = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam", "Dee", "Eli" })
            people.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var group = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, people, type, StoryDate.Unknown()));
        Assert.True(group.Success, $"{group.Code}: {group.Message}");
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Large group");
        var groupRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(group.ResourceKey!));
        const string expected = "Ari, Bo, Cam +2 more — friends";
        Assert.Equal(expected, (await reads.GetAsync(new(groupRef))).Summary.Label);
        var timeline = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: true));
        Assert.Equal(expected, Assert.Single(timeline.Undated, item => item.Ref == groupRef).Label);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task MembershipCanRestartAndRejectsOverlapOrStaleVersion()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Membership test", "UTC"))).ResourceKey!);
        var characters = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam" })
            characters.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, characters[0], characters[1], type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8))))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references));
        var application = new AccessV4ApplicationService(vault.Coordinator, references, targets, vault.Service);
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", relationship);
        var ariRef = await references.ReferenceAsync("Character", characters[0]);
        var camRef = await references.ReferenceAsync("Character", characters[2]);
        var restart = await application.AddRelationshipMembershipPeriodAsync(new(
            "restart-1", continuity, relationshipRef, ariRef, 1,
            StoryDate.ExactDate(new DateOnly(2021, 9, 10)), "first note"));
        Assert.True(restart.Success, $"{restart.Code}: {restart.Message}");
        Assert.Equal(1, restart.Version);
        var overlap = await application.AddRelationshipMembershipPeriodAsync(new(
            "restart-2", continuity, relationshipRef, ariRef, 2,
            StoryDate.ExactDate(new DateOnly(2021, 9, 10))));
        Assert.False(overlap.Success);
        Assert.Equal("interval.overlap", overlap.Code);
        var stale = await application.AddRelationshipParticipantAsync(new(
            "third-1", continuity, relationshipRef, camRef, 1));
        Assert.False(stale.Success);
        Assert.Equal("concurrency.conflict", stale.Code);
        var added = await application.AddRelationshipParticipantAsync(new(
            "third-2", continuity, relationshipRef, camRef, 2,
            StoryDate.ExactDate(new DateOnly(2021, 9, 11))));
        Assert.True(added.Success, $"{added.Code}: {added.Message}");
        Assert.Equal(1, added.Version);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Membership test");
        var chronology = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipMembershipPeriod],
            FocusRefs: [ariRef], Resolution: V4TimelineResolution.Detail,
            IncludeUndated: false));
        Assert.Equal(4, chronology.Items.Count);
        Assert.Equal(2, chronology.Items.Count(item => item.Boundary == "Start"));
        Assert.Equal(2, chronology.Items.Count(item => item.Boundary == "End"));
        Assert.Equal(2, (await reads.RelatedAsync(new(
            ariRef, "relationshipMembershipPeriods"))).Items.Count);
        Assert.All(chronology.Items, item => Assert.Equal(ariRef,
            Assert.Single(item.Related, link => link.Kind == V4RecordKind.Character).Ref));
        Assert.All(chronology.Items, item => Assert.Equal("Ari, Bo, Cam — friends",
            Assert.Single(item.Related, link => link.Kind == V4RecordKind.Relationship).Label));
        var periodRef = await references.ReferenceAsync("RelationshipMembershipPeriod",
            int.Parse(restart.ResourceKey!));
        Assert.Equal("first note", (await reads.GetAsync(new(periodRef))).Fields["notes"].GetString());
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var collidingCorrection = await records.UpdateRelationshipMembershipPeriodAsync(new(
            "period-correction-overlap", periodRef, 1,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-08")),
            StoryDate.ExactDate(new DateOnly(2021, 9, 8)));
        Assert.False(collidingCorrection.Success);
        Assert.Equal("interval.overlap", collidingCorrection.Code);
        var corrected = await records.UpdateRelationshipMembershipPeriodAsync(new(
            "period-correction-valid", periodRef, 1,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-12")),
            StoryDate.ExactDate(new DateOnly(2021, 9, 12)));
        Assert.True(corrected.Success, $"{corrected.Code}: {corrected.Message}");
        Assert.Equal("first note", (await reads.GetAsync(new(periodRef))).Fields["notes"].GetString());
        var staleCorrection = await records.UpdateRelationshipMembershipPeriodAsync(new(
            "period-correction-stale", periodRef, 1,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-13")),
            StoryDate.ExactDate(new DateOnly(2021, 9, 13)));
        Assert.Equal("concurrency.conflict", staleCorrection.Code);
        var deleted = await records.LifecycleAsync(new(
            "period-delete", periodRef, corrected.Version!.Value), false);
        Assert.True(deleted.Success, $"{deleted.Code}: {deleted.Message}");
        Assert.Equal(2, (await reads.TimelineAsync(new(
            Kinds: [V4RecordKind.RelationshipMembershipPeriod], FocusRefs: [ariRef],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: false))).Items.Count);
        var restored = await records.LifecycleAsync(new(
            "period-restore", periodRef, deleted.Version!.Value), true);
        Assert.True(restored.Success, $"{restored.Code}: {restored.Message}");
        var cleared = await records.UpdateRelationshipMembershipPeriodAsync(new(
            "period-clear-note", periodRef, restored.Version!.Value,
            new(V4StoryDateKind.ExactDate, Value: "2021-09-12"), ClearNotes: true),
            StoryDate.ExactDate(new DateOnly(2021, 9, 12)));
        Assert.True(cleared.Success, $"{cleared.Code}: {cleared.Message}");
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            (await reads.GetAsync(new(periodRef))).Fields["notes"].ValueKind);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task UndatedGroupAllowsStaggeredMemberSpansWithoutInventingJoins()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Staggered group", "UTC"))).ResourceKey!);
        var people = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam" })
            people.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var group = await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, people, type, null));
        Assert.True(group.Success, $"{group.Code}: {group.Message}");
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references));
        var application = new AccessV4ApplicationService(vault.Coordinator, references, targets, vault.Service);
        var groupRef = await references.ReferenceAsync("CharacterRelationship", int.Parse(group.ResourceKey!));
        var version = 1;
        foreach (var (person, start, end) in new[]
                 { (people[0], 1, 10), (people[1], 3, 5), (people[2], 7, 9) })
        {
            var personRef = await references.ReferenceAsync("Character", person);
            var added = await application.AddRelationshipMembershipPeriodAsync(new(
                Guid.NewGuid().ToString(), continuity, groupRef, personRef, version,
                Joined: StoryDate.ExactDate(new DateOnly(2021, 9, start)),
                Left: StoryDate.ExactDate(new DateOnly(2021, 9, end - 1))));
            Assert.True(added.Success, $"{added.Code}: {added.Message}");
            version++;
        }
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Staggered group");
        var page = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: false));
        Assert.Equal(4, page.Items.Count);
        Assert.Equal(2, page.Items.Count(item => item.Boundary == "Start"));
        Assert.Equal(2, page.Items.Count(item => item.Boundary == "End"));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task GroupTimelineDerivesTwoSeparateActiveSpansFromMemberReentries()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Rocky group", "UTC"))).ResourceKey!);
        var characters = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam" })
            characters.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, characters, type, null))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references));
        var application = new AccessV4ApplicationService(vault.Coordinator, references, targets, vault.Service);
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", relationship);
        var version = 1;
        foreach (var (from, to) in new[] { (1, 4), (6, 9) })
        foreach (var character in characters)
        {
            var characterRef = await references.ReferenceAsync("Character", character);
            var added = await application.AddRelationshipMembershipPeriodAsync(new(
                Guid.NewGuid().ToString(), continuity, relationshipRef, characterRef, version,
                Joined: StoryDate.ExactDate(new DateOnly(2021, 9, from)),
                Left: StoryDate.ExactDate(new DateOnly(2021, 9, to - 1))));
            Assert.True(added.Success, $"{added.Code}: {added.Message}");
            version++;
        }
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Rocky group");
        var page = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: false));
        Assert.Equal(4, page.Items.Count);
        Assert.Equal(2, page.Items.Count(item => item.Boundary == "Start"));
        Assert.Equal(2, page.Items.Count(item => item.Boundary == "End"));
        Assert.All(page.Items, item => Assert.Equal(3,
            item.Related.Count(link => link.Kind == V4RecordKind.Character)));
        var focused = await reads.TimelineAsync(new(Kinds: [V4RecordKind.Relationship],
            FocusRefs: [relationshipRef], Resolution: V4TimelineResolution.Detail,
            IncludeUndated: false));
        Assert.Equal(page.Items.Count, focused.Items.Count);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task RelationshipEventAllowsUndatedAndOutOfMembershipHistoryButRejectsForeignContext()
    {
        await using var vault = await TestVault.CreateAsync();
        var first = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Event boundaries", "UTC"))).ResourceKey!);
        var second = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Elsewhere", "UTC"))).ResourceKey!);
        var people = new List<int>();
        foreach (var name in new[] { "Ari", "Bo" })
            people.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), first, CanonEntityType.Character, name))).ResourceKey!));
        var foreignWorld = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), second, CanonEntityType.WorldEvent, "Foreign storm"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), first, people, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 1))))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references));
        var application = new AccessV4ApplicationService(vault.Coordinator, references, targets, vault.Service);
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", relationship);
        var foreignRef = await references.ReferenceAsync("WorldEvent", foreignWorld);
        var foreign = await application.RecordRelationshipEventAsync(new(
            "foreign-context", relationshipRef, "Foreign event",
            new(V4StoryDateKind.ExactDate, Value: "2021-10-01"), foreignRef), first, "test");
        Assert.False(foreign.Success);
        var later = await application.RecordRelationshipEventAsync(new(
            "later-event", relationshipRef, "A later meeting",
            new(V4StoryDateKind.ExactDate, Value: "2021-10-01")), first, "test");
        Assert.True(later.Success, $"{later.Code}: {later.Message}");
        var undated = await application.RecordRelationshipEventAsync(new(
            "undated-event", relationshipRef, "An undated memory",
            new(V4StoryDateKind.Unknown)), first, "test");
        Assert.True(undated.Success, $"{undated.Code}: {undated.Message}");
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(first, "Event boundaries");
        var page = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipEvent],
            Resolution: V4TimelineResolution.Detail));
        Assert.Single(page.Items);
        Assert.Single(page.Undated);
        Assert.Equal("A later meeting", page.Items[0].Title);
        Assert.Equal("Ari and Bo — friends", Assert.Single(page.Items[0].Related,
            link => link.Kind == V4RecordKind.Relationship).Label);
        Assert.Equal("An undated memory", page.Undated[0].Label);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task RelationshipEventAppearsForEveryMemberAndLinkedProjectAndWorldEvent()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Event test", "UTC"))).ResourceKey!);
        var characters = new List<int>();
        foreach (var name in new[] { "Ari", "Bo", "Cam" })
            characters.Add(int.Parse((await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!));
        var world = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, "Storm"))).ResourceKey!);
        var project = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Project, "Book"))).ResourceKey!);
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "friends", false))).ResourceKey!);
        var relationship = int.Parse((await vault.Service.CreateRelationshipGroupAsync(new(
            Guid.NewGuid().ToString(), continuity, characters, type, StoryDate.Unknown()))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references));
        var application = new AccessV4ApplicationService(vault.Coordinator, references, targets, vault.Service);
        var relationshipRef = await references.ReferenceAsync("CharacterRelationship", relationship);
        var projectRef = await references.ReferenceAsync("Project", project);
        var worldRef = await references.ReferenceAsync("WorldEvent", world);
        var created = await application.RecordRelationshipEventAsync(new(
            "reconciliation", relationshipRef, "Reconciled", new(V4StoryDateKind.ExactDate, Value: "2021-09-09"),
            worldRef, "The group reunited.", Projects: [projectRef]), continuity, "test");
        Assert.True(created.Success, $"{created.Code}: {created.Message}");
        var eventRef = await references.ReferenceAsync("RelationshipEvent", int.Parse(created.ResourceKey!));
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Event test");
        Assert.Contains((await reads.RelatedAsync(new(relationshipRef, "events"))).Items,
            item => item.Ref == eventRef);
        foreach (var character in characters)
        {
            var personRef = await references.ReferenceAsync("Character", character);
            Assert.Contains((await reads.RelatedAsync(new(personRef, "relationshipEvents"))).Items,
                item => item.Ref == eventRef);
        }
        Assert.Contains((await reads.RelatedAsync(new(worldRef, "relationshipEvents"))).Items,
            item => item.Ref == eventRef);
        Assert.Contains((await reads.RelatedAsync(new(projectRef, "events"))).Items,
            item => item.Ref == eventRef);
        Assert.Equal(eventRef, (await reads.GetAsync(new(eventRef))).Summary.Ref);
        var dated = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipEvent],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: false));
        Assert.Equal(eventRef, Assert.Single(dated.Items).Ref);
        var camRef = await references.ReferenceAsync("Character", characters[2]);
        var focused = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipEvent],
            FocusRefs: [camRef], Resolution: V4TimelineResolution.Detail, IncludeUndated: false));
        Assert.Equal(eventRef, Assert.Single(focused.Items).Ref);
        var focusedOnRelationship = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipEvent],
            FocusRefs: [relationshipRef], Resolution: V4TimelineResolution.Detail,
            IncludeUndated: false));
        Assert.Equal(eventRef, Assert.Single(focusedOnRelationship.Items).Ref);
        var inProject = await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipEvent],
            Projects: [projectRef], Resolution: V4TimelineResolution.Detail, IncludeUndated: false));
        Assert.Equal(eventRef, Assert.Single(inProject.Items).Ref);
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        var eventVersion = (await reads.GetAsync(new(eventRef))).Summary.Version!.Value;
        var deleted = await records.LifecycleAsync(new(
            "relationship-event-delete", eventRef, eventVersion), false);
        Assert.True(deleted.Success, $"{deleted.Code}: {deleted.Message}");
        Assert.Empty((await reads.TimelineAsync(new(Kinds: [V4RecordKind.RelationshipEvent],
            Resolution: V4TimelineResolution.Detail, IncludeUndated: false))).Items);
        var restored = await records.LifecycleAsync(new(
            "relationship-event-restore", eventRef, deleted.Version!.Value), true);
        Assert.True(restored.Success, $"{restored.Code}: {restored.Message}");
        Assert.Equal(eventRef, Assert.Single((await reads.TimelineAsync(new(
            Kinds: [V4RecordKind.RelationshipEvent], Resolution: V4TimelineResolution.Detail,
            IncludeUndated: false))).Items).Ref);
        var relationshipVersion = (await reads.GetAsync(new(relationshipRef))).Summary.Version!.Value;
        var parentDeleted = await records.LifecycleAsync(new(
            "relationship-parent-delete", relationshipRef, relationshipVersion), false);
        Assert.True(parentDeleted.Success, $"{parentDeleted.Code}: {parentDeleted.Message}");
        Assert.Empty((await reads.SearchAsync(new(Kinds: [V4RecordKind.RelationshipEvent]))).Items);
        Assert.Empty((await reads.RelatedAsync(new(projectRef, "events"))).Items);
        Assert.Empty((await reads.RelatedAsync(new(worldRef, "relationshipEvents"))).Items);
        var firstPerson = await references.ReferenceAsync("Character", characters[0]);
        Assert.Empty((await reads.RelatedAsync(new(firstPerson, "relationshipEvents"))).Items);
        Assert.True(Assert.Single((await reads.SearchAsync(new(
            Kinds: [V4RecordKind.RelationshipEvent],
            DeletionState: V4DeletionState.Deleted))).Items).IsDeleted);
        var parentRestored = await records.LifecycleAsync(new(
            "relationship-parent-restore", relationshipRef, parentDeleted.Version!.Value), true);
        Assert.True(parentRestored.Success, $"{parentRestored.Code}: {parentRestored.Message}");
        Assert.Equal(eventRef, Assert.Single((await reads.SearchAsync(new(
            Kinds: [V4RecordKind.RelationshipEvent]))).Items).Ref);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }
}
