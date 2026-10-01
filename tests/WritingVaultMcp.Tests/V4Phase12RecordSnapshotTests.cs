using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;
using WritingVault.Client;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase12RecordSnapshotTests
{
    [Fact]
    public async Task ClockChangeSnapshotsEveryAffectedCharacterPage()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Clock world", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var store = new AccessV4PageSnapshotStore(vault.Factory);
        var before = await store.ReadAsync("Character", character, continuity, 1);
        Assert.Equal("TimelineUnset", before!.Content.Overview.Fields["age"]
            .GetProperty("status").GetString());

        var clock = await vault.Service.SetClockAsync(new(Guid.NewGuid().ToString(), continuity,
            DateTimeOffset.Parse("2025-01-01T12:00:00+00:00"), "UTC", 1));
        Assert.True(clock.Success, clock.Code);
        var after = await store.ReadAsync("Character", character, continuity, 2);
        Assert.NotNull(after);
        Assert.NotEqual("TimelineUnset", after.Content.Overview.Fields["age"]
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task RenameAssociationAndDeletionPreserveEarlierPagesAndStableReference()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Versioned names", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var oldRef = await references.ReferenceAsync("Character", character);
        var store = new AccessV4PageSnapshotStore(vault.Factory);

        var renamed = await vault.Service.PatchEntityAsync(new(Guid.NewGuid().ToString(),
            character, 1, Name: new(true, "Marianne")));
        Assert.True(renamed.Success, renamed.Code);
        var withAlias = await vault.Service.AddAliasAsync(new(Guid.NewGuid().ToString(),
            character, "Mara"));
        Assert.True(withAlias.Success, withAlias.Code);
        var deleted = await vault.Service.SoftDeleteEntityAsync(new(Guid.NewGuid().ToString(),
            character, 2));
        Assert.True(deleted.Success, deleted.Code);

        Assert.Equal("Mara", (await store.ReadAsync("Character", character, continuity, 1))!
            .Content.Overview.Summary.Label);
        Assert.Equal("Marianne", (await store.ReadAsync("Character", character, continuity, 2))!
            .Content.Overview.Summary.Label);
        Assert.Contains((await store.ReadAsync("Character", character, continuity, 3))!
            .Content.Overview.Sections["aliases"].Items, item => item.Label == "Mara");
        Assert.True((await store.ReadAsync("Character", character, continuity, 4))!
            .Content.Overview.Summary.IsDeleted);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Versioned names");
        Assert.Equal("record.deleted", (await Assert.ThrowsAsync<V4ResolutionException>(
            () => reads.LocateAsync(new(oldRef)))).Code);
        Assert.True((await reads.LocateAsync(new(oldRef, IncludeDeleted: true))).IsDeleted);
        Assert.Equal("Mara", (await reads.SnapshotAsync(new(oldRef, 1))).Overview.Summary.Label);
        Assert.Equal("snapshot.not_found", (await Assert.ThrowsAsync<V4ResolutionException>(
            () => reads.SnapshotAsync(new(oldRef, 99)))).Code);
    }

    [Fact]
    public async Task RemovingTagAssociationSnapshotsTheFormerOwner()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Tagged world", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var tag = int.Parse((await vault.Service.CreateTagAsync(new(
            Guid.NewGuid().ToString(), "mystery"))).ResourceKey!);
        Assert.True((await vault.Service.LinkEntityAsync("tag", new(
            Guid.NewGuid().ToString(), character, tag))).Success);
        var store = new AccessV4PageSnapshotStore(vault.Factory);
        var tagged = await store.ReadAsync("Character", character, continuity, 2);
        Assert.Contains(tagged!.Content.Overview.Sections["tags"].Items,
            item => item.Label == "mystery");

        var unlink = await vault.Service.UnlinkEntityAsync("tag", new(
            Guid.NewGuid().ToString(), character, tag));
        Assert.True(unlink.Success, unlink.Code);
        var after = await store.ReadAsync("Character", character, continuity, 3);
        Assert.Empty(after!.Content.Overview.Sections["tags"].Items);
        Assert.Contains(tagged.Content.Overview.Sections["tags"].Items,
            item => item.Label == "mystery");
    }

    [Fact]
    public async Task MergedRelationshipKeepsTheSourcesHistoricalPage()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Merge history", "UTC"))).ResourceKey!);
        async Task<int> Character(string name) => int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!);
        var first = await Character("Ari");
        var second = await Character("Bo");
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(new(
            Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        async Task<int> Relationship(int year, string notes) => int.Parse((await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, first, second, type,
            StoryDate.ExactDate(new DateOnly(year, 9, 8)), notes))).ResourceKey!);
        var source = await Relationship(2021, "Early chapter");
        var target = await Relationship(2022, "Later chapter");
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Merge history");
        var sourceRef = await references.ReferenceAsync("CharacterRelationship", source);
        var targetRef = await references.ReferenceAsync("CharacterRelationship", target);
        var merge = new AccessV4RelationshipMergeService(vault.Factory, vault.Coordinator,
            references, session);
        var preview = await merge.PreviewAsync(new(sourceRef, targetRef));
        Assert.True(preview.CanMerge, string.Join("; ", preview.Conflicts));
        var applied = await merge.ApplyAsync(new("merge-history", sourceRef, targetRef,
            preview.ReviewToken!));
        Assert.True(applied.Success, applied.Code + ": " + applied.Message);

        var earlier = await reads.SnapshotAsync(new(sourceRef, 1));
        Assert.Equal("Early chapter", earlier.Overview.Fields["notes"].GetString());
        Assert.False(earlier.Overview.Summary.IsDeleted);
        var store = new AccessV4PageSnapshotStore(vault.Factory);
        var archived = await store.ReadAsync("CharacterRelationship", source, continuity, 2);
        Assert.True(archived!.Content.Overview.Summary.IsDeleted);
        Assert.NotNull(await store.ReadAsync("CharacterRelationship", target, continuity, 2));
        Assert.Equal(targetRef, (await reads.GetAsync(new(sourceRef))).Summary.Ref);
    }

    [Fact]
    public async Task VerifiedBackupRestoresHistoricalPageVersions()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Backup history", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var reference = await new VaultReferenceService(vault.Factory, vault.Coordinator)
            .ReferenceAsync("Character", character);
        Assert.True((await vault.Service.PatchEntityAsync(new(Guid.NewGuid().ToString(),
            character, 1, Name: new(true, "Marianne")))).Success);
        Directory.CreateDirectory(vault.StorageRoot);
        var backup = await new AccessBackupService().CreateAsync(vault.DatabasePath,
            vault.StorageRoot);
        await new AccessBackupService().VerifyAsync(backup.ManifestPath);
        var restoredDatabase = Path.Combine(vault.Directory, "snapshot-restored.accdb");
        var restoredAssets = Path.Combine(vault.Directory, "snapshot-restored-assets");
        await new AccessBackupService().RestoreToNewPathsAsync(backup.ManifestPath,
            restoredDatabase, Path.Combine(restoredAssets, "assets"));
        await using var client = await new McpVaultReadClientFactory(new(
            TestServer.AssemblyPath, restoredDatabase, restoredAssets))
            .ConnectAsync("snapshot restore reader");
        await client.SetSessionAsync(new("Backup history"));
        Assert.Equal("Mara", (await client.GetRecordSnapshotAsync(reference, 1))
            .Overview.Summary.Label);
        Assert.Equal("Marianne", (await client.GetRecordSnapshotAsync(reference, 2))
            .Overview.Summary.Label);
    }

    [Fact]
    public async Task IntegrityAuditDetectsTamperedHistoricalPage()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        Assert.True((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Tamper audit", "UTC"))).Success);
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [RecordPageSnapshots] SET [PageSha256]=? " +
                "WHERE [RecordType]='Continuity' AND [SnapshotVersion]=1";
            command.Parameters.Add("hash", OleDbType.VarWChar).Value = new string('0', 64);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Contains((await vault.Integrity.VerifyAsync()).Issues,
            issue => issue.Code == "integrity.record_snapshot_corrupt");
    }

    [Fact]
    public async Task BaselineLabelsExistingPagesAndDoesNotInventEarlierVersions()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Existing world", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var note = int.Parse((await vault.Service.AddNoteAsync(
            new(Guid.NewGuid().ToString(), character, "Current text", "Memory"))).ResourceKey!);

        var capture = vault.EnableAutomaticPageSnapshots();
        await capture.EnsureBaselineAsync();
        await capture.EnsureBaselineAsync();
        var store = new AccessV4PageSnapshotStore(vault.Factory);
        var baseline = await store.ReadAsync("Character", character, continuity, 1);
        Assert.True(baseline!.IsBaseline);
        Assert.Equal("Current text", Assert.Single(baseline.Content.Notes.Values)
            .Fields["body"].GetString());
        Assert.Null(await store.ReadAsync("Character", character, continuity, 2));
        Assert.NotNull(await store.ReadAsync("EntityNote", note, continuity, 1));

        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Existing world");
        var characterRef = await references.ReferenceAsync("Character", character);
        var pinned = await reads.SnapshotAsync(new(characterRef, 1));
        Assert.True(pinned.IsBaseline);
        Assert.Equal("Current text", Assert.Single(pinned.Notes.Values)
            .Fields["body"].GetString());
        Assert.Equal(characterRef, pinned.LatestRef);
        var versions = await reads.SnapshotListAsync(new(characterRef));
        Assert.Equal([1], versions.Items.Select(item => item.SnapshotVersion));
        Assert.True(Assert.Single(versions.Items).IsBaseline);
    }

    [Fact]
    public async Task BaselineCapturesEveryContinuitySectionAcrossRelatedPages()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Paged notes world", "UTC"))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var app = new AccessV4ApplicationService(vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(
                vault.Factory, vault.Coordinator, references)), vault.Service);
        for (var index = 0; index < 21; index++)
        {
            var added = await app.AddNoteAsync(new(Guid.NewGuid().ToString(), continuity,
                "Paged notes world", $"Note body {index}", $"Note {index}"));
            Assert.True(added.Success, added.Code);
            var character = await vault.Service.CreateEntityAsync(new(
                Guid.NewGuid().ToString(), continuity, CanonEntityType.Character,
                $"Character {index}"));
            Assert.True(character.Success, character.Code);
        }

        var capture = vault.EnableAutomaticPageSnapshots();
        await capture.EnsureBaselineAsync();

        var page = await new AccessV4PageSnapshotStore(vault.Factory)
            .ReadAsync("Continuity", continuity, continuity, 1);
        Assert.NotNull(page);
        Assert.Equal(21, page.Content.Overview.Sections["notes"].Items.Count);
        Assert.Equal(21, page.Content.Notes.Count);
        Assert.False(page.Content.Overview.Sections["notes"].HasMore);
        Assert.Equal(21, page.Content.Overview.Sections["entities"].Items.Count);
        Assert.False(page.Content.Overview.Sections["entities"].HasMore);
    }

    [Fact]
    public async Task OrdinaryWritesAutomaticallyCaptureTheContinuityAndCharacterPages()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuityResult = await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Automatic world", "UTC"));
        Assert.True(continuityResult.Success, continuityResult.Code);
        var continuity = int.Parse(continuityResult.ResourceKey!);
        var characterResult = await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"));
        Assert.True(characterResult.Success, characterResult.Code);
        var character = int.Parse(characterResult.ResourceKey!);

        var store = new AccessV4PageSnapshotStore(vault.Factory);
        var initialWorld = await store.ReadAsync("Continuity", continuity, continuity, 1);
        var currentWorld = await store.ReadAsync("Continuity", continuity, continuity, 2);
        var characterPage = await store.ReadAsync("Character", character, continuity, 1);
        Assert.NotNull(initialWorld);
        Assert.Empty(initialWorld.Content.Overview.Sections["entities"].Items);
        Assert.Contains(currentWorld!.Content.Overview.Sections["entities"].Items,
            item => item.Label == "Mara");
        Assert.Equal("Mara", characterPage!.Content.Overview.Summary.Label);

        var noteResult = await vault.Service.AddNoteAsync(
            new(Guid.NewGuid().ToString(), character, "She remembers the key.", "Memory"));
        Assert.True(noteResult.Success, noteResult.Code);
        var withNote = await store.ReadAsync("Character", character, continuity, 2);
        Assert.Equal("She remembers the key.", Assert.Single(withNote!.Content.Notes.Values)
            .Fields["body"].GetString());
    }

    [Fact]
    public async Task RelationshipCreationCapturesTheSharedRecordAndBothCharacterPages()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Romance", "UTC"))).ResourceKey!);
        async Task<int> Character(string name) => int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name))).ResourceKey!);
        var mara = await Character("Mara");
        var chloe = await Character("Chloe");
        var type = int.Parse((await vault.Service.CreateRelationshipTypeAsync(
            new(Guid.NewGuid().ToString(), "partners", false))).ResourceKey!);
        var relationshipResult = await vault.Service.CreateRelationshipAsync(new(
            Guid.NewGuid().ToString(), continuity, mara, chloe, type,
            StoryDate.ExactDate(new DateOnly(2021, 9, 8)), "A shared romance."));
        Assert.True(relationshipResult.Success, relationshipResult.Code);
        var relationship = int.Parse(relationshipResult.ResourceKey!);

        var store = new AccessV4PageSnapshotStore(vault.Factory);
        var shared = await store.ReadAsync("CharacterRelationship", relationship, continuity, 1);
        var maraPage = await store.ReadAsync("Character", mara, continuity, 2);
        var chloePage = await store.ReadAsync("Character", chloe, continuity, 2);
        Assert.NotNull(shared);
        Assert.Contains("partners", shared.Content.Overview.Summary.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(maraPage!.Content.Overview.Sections["relationships"].Items,
            item => item.Kind == V4RecordKind.Relationship);
        Assert.Contains(chloePage!.Content.Overview.Sections["relationships"].Items,
            item => item.Kind == V4RecordKind.Relationship);
    }


    [Fact]
    public async Task SnapshotReadScopeSeesUncommittedRowsAndDoesNotOutliveRollback()
    {
        await using var vault = await TestVault.CreateAsync();
        var sawPendingRow = false;
        var result = await vault.Coordinator.ExecuteAsync(
            Guid.NewGuid().ToString(), "snapshot.scope.probe", new { name = "Transient" },
            "snapshot_scope_probe", null,
            async (context, token) =>
            {
                using (var insert = context.Command(
                    "INSERT INTO [Tags] ([Name],[NormalizedName],[CreatedAtUtc],[UpdatedAtUtc]) " +
                    "VALUES ('Transient','TRANSIENT',Now(),Now())"))
                    await insert.ExecuteNonQueryAsync(token);

                // Existing page projections open their own connection. The
                // scoped AccessCommand must read on this write transaction.
                await using var projectionConnection = vault.Factory.Create();
                await projectionConnection.OpenAsync(token);
                using (AccessCommand.UseTransactionForReads(context.Connection, context.Transaction))
                using (var projection = new AccessCommand(projectionConnection,
                           "SELECT COUNT(*) FROM [Tags] WHERE [NormalizedName]='TRANSIENT'"))
                    sawPendingRow = Convert.ToInt32(await projection.ExecuteScalarAsync(token)) == 1;

                throw new InvalidOperationException("Injected rollback after the projection.");
#pragma warning disable CS0162
                return new VaultMutationOutcome("Tag", "0", 1, "create", null);
#pragma warning restore CS0162
            });

        Assert.True(sawPendingRow);
        Assert.False(result.Success);
        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var check = new AccessCommand(connection,
            "SELECT COUNT(*) FROM [Tags] WHERE [NormalizedName]='TRANSIENT'");
        Assert.Equal(0, Convert.ToInt32(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task PageProjectionInsideWriteTransactionSeesNewFieldsWithoutCommittingThem()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Snapshot world", "UTC"))).ResourceKey!);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Snapshot world");
        var sawPendingPage = false;

        var result = await vault.Coordinator.ExecuteAsync(
            Guid.NewGuid().ToString(), "snapshot.page.probe", new { description = "Draft" },
            "snapshot_page_probe", null,
            async (context, token) =>
            {
                using (var update = context.Command(
                    "UPDATE [Continuities] SET [Description]='Draft' WHERE [Id]=?")
                    .Add(OleDbType.Integer, continuity))
                    Assert.Equal(1, await update.ExecuteNonQueryAsync(token));
                var page = await vault.Coordinator.ReadPendingWriteAsync(context,
                    () => reads.GetAsync(new V4GetRequest(), token));
                sawPendingPage = page.Fields["description"].GetString() == "Draft";
                throw new InvalidOperationException("Injected rollback after the page projection.");
#pragma warning disable CS0162
                return new VaultMutationOutcome("Continuity", continuity.ToString(), 2, "patch", null);
#pragma warning restore CS0162
            });

        Assert.True(sawPendingPage);
        Assert.False(result.Success);
        var current = await reads.GetAsync(new V4GetRequest());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, current.Fields["description"].ValueKind);
    }

    [Fact]
    public async Task PinnedPageStoreRetainsOlderFieldsAndRollsBackWithTheWrite()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Versioned world", "UTC"))).ResourceKey!);
        var (reads, session, _) = vault.V4();
        session.SelectContinuity(continuity, "Versioned world");
        var store = new AccessV4PageSnapshotStore(vault.Factory);
        Exception? captureError = null;

        async Task<VaultMutationResult> ChangeAsync(string description, bool rollback)
        {
            return await vault.Coordinator.ExecuteAsync(
                Guid.NewGuid().ToString(), "snapshot.page.change", new { description, rollback },
                "snapshot_page_change", null,
                async (context, token) =>
                {
                    using (var update = context.Command(
                        "UPDATE [Continuities] SET [Description]=? WHERE [Id]=?")
                        .Add(OleDbType.LongVarWChar, description)
                        .Add(OleDbType.Integer, continuity))
                        Assert.Equal(1, await update.ExecuteNonQueryAsync(token));
                    var page = await vault.Coordinator.ReadPendingWriteAsync(context,
                        () => reads.GetAsync(new V4GetRequest(), token));
                    try
                    {
                        await store.CaptureAsync(context, "Continuity", continuity, continuity,
                            new AccessV4PageSnapshotStore.Page(page,
                                new Dictionary<string, V4RecordOverview>()), false, token);
                    }
                    catch (Exception exception) { captureError = exception; throw; }
                    if (rollback) throw new InvalidOperationException("Injected rollback.");
                    return new VaultMutationOutcome("Continuity", continuity.ToString(),
                        1, "patch", new { description });
                });
        }

        Assert.True((await ChangeAsync("First", false)).Success, captureError?.ToString());
        Assert.True((await ChangeAsync("Second", false)).Success);
        Assert.False((await ChangeAsync("Rejected", true)).Success);

        var first = await store.ReadAsync("Continuity", continuity, continuity, 1);
        var second = await store.ReadAsync("Continuity", continuity, continuity, 2);
        Assert.Equal("First", first!.Content.Overview.Fields["description"].GetString());
        Assert.Equal("Second", second!.Content.Overview.Fields["description"].GetString());
        Assert.Null(await store.ReadAsync("Continuity", continuity, continuity, 3));
        Assert.Equal("Second", (await reads.GetAsync(new V4GetRequest()))
            .Fields["description"].GetString());
    }

    [Fact]
    public async Task MaterializedCharacterPageRetainsItsHistoricalMarkdownNote()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Notebook", "UTC"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(
            new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Mara"))).ResourceKey!);
        var note = int.Parse((await vault.Service.AddNoteAsync(
            new(Guid.NewGuid().ToString(), character, "First draft", "Memory"))).ResourceKey!);
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(
            vault.Factory, vault.Coordinator, references));
        var capture = new AccessV4PageSnapshotCapture(vault.Factory, vault.Coordinator,
            vault.Service, references, targets, vault.Cursors, vault.Changes);
        var store = new AccessV4PageSnapshotStore(vault.Factory);

        async Task<VaultMutationResult> EditAndCaptureAsync(string body)
        {
            return await vault.Coordinator.ExecuteAsync(Guid.NewGuid().ToString(),
                "snapshot.note.change", new { body }, "snapshot_note_change", null,
                async (context, token) =>
                {
                    using (var update = context.Command(
                        "UPDATE [EntityNotes] SET [Body]=? WHERE [Id]=?")
                        .Add(OleDbType.LongVarWChar, body).Add(OleDbType.Integer, note))
                        Assert.Equal(1, await update.ExecuteNonQueryAsync(token));
                    await capture.CaptureAsync(context, "Character", character, continuity,
                        false, token);
                    return new VaultMutationOutcome("EntityNote", note.ToString(), 1,
                        "patch", new { body });
                });
        }

        Assert.True((await EditAndCaptureAsync("First draft")).Success);
        Assert.True((await EditAndCaptureAsync("Second draft")).Success);
        var older = (await store.ReadAsync("Character", character, continuity, 1))!;
        var newer = (await store.ReadAsync("Character", character, continuity, 2))!;
        Assert.Single(older.Content.Overview.Sections["notes"].Items);
        Assert.False(older.Content.Overview.Sections["notes"].HasMore);
        Assert.Equal("First draft", Assert.Single(older.Content.Notes.Values)
            .Fields["body"].GetString());
        Assert.Equal("Second draft", Assert.Single(newer.Content.Notes.Values)
            .Fields["body"].GetString());
    }
}
