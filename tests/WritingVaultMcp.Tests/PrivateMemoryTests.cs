using System.Text.Json;
using ModelContextProtocol.Client;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class PrivateMemoryTests
{
    [Fact]
    public async Task ScopesVersionsRetriesAndPrivacyArePreserved()
    {
        await using var vault = await TestVault.CreateAsync();
        vault.EnableAutomaticPageSnapshots();
        var (reads, session, refs) = vault.V4();
        var memories = new AccessV4MemoryService(vault.Factory, vault.Coordinator, refs, session, vault.Cursors);
        Assert.Null((await memories.HealthAsync()).SelectedContinuityCount);
        var save = new V4MemorySaveRequest("global-memory", V4MemoryScope.Global, "instructions", "private-global-canary", 0);
        Assert.True((await memories.SaveAsync(save)).Success);
        Assert.True((await memories.SaveAsync(save)).Replayed);
        Assert.False((await memories.SaveAsync(save with { Body = "changed" })).Success);
        Assert.False((await memories.SaveAsync(save with { MutationToken = "stale" })).Success);
        Assert.True((await memories.SaveAsync(save with { MutationToken = "edit", ExpectedVersion = 1, Body = "private-edited-canary" })).Success);
        Assert.Equal(2, Assert.Single((await memories.ReadAsync(new(V4MemoryScope.Global))).Items).Version);
        var world = await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Memory world", "UTC"));
        var other = await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Other memory world", "UTC"));
        session.SelectContinuity(int.Parse(world.ResourceKey!), "Memory world");
        var local = new V4MemorySaveRequest("local-memory", V4MemoryScope.Continuity, "instructions", "private-local-canary", 0);
        var before = await reads.ChangesSinceAsync(new());
        Assert.True((await memories.SaveAsync(local)).Success);
        Assert.Empty((await reads.ChangesSinceAsync(new(before.Cursor))).Changes);
        var health = await memories.HealthAsync();
        Assert.Equal(1, health.GlobalCount); Assert.Equal(1, health.SelectedContinuityCount);
        Assert.Equal("private-edited-canary", Assert.Single((await memories.ReadAsync(health.GlobalRead)).Items).Body);
        Assert.Equal("private-local-canary", Assert.Single((await memories.ReadAsync(health.SelectedContinuityRead!)).Items).Body);
        Assert.Empty((await reads.SearchAsync(new(Text: "private-local-canary", IncludeContent: true))).Items);
        Assert.DoesNotContain("private-local-canary", JsonSerializer.Serialize(await reads.GetAsync(new())));
        Assert.Empty((await reads.HistoryAsync(new(MutationToken: "local-memory"))).Items);
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var journal = new AccessCommand(connection, "SELECT [ChangeJson] FROM [ChangeLog] WHERE [RecordType]='PrivateMemory'");
            Assert.All(await journal.QueryAsync(r => r.IsDBNull(0) ? "" : r.GetString(0), default), text => Assert.DoesNotContain("private-", text));
            using var snapshots = new AccessCommand(connection, "SELECT COUNT(*) FROM [RecordPageSnapshots] WHERE [RecordType]='PrivateMemory'");
            Assert.Equal(0, Convert.ToInt32(await snapshots.ExecuteScalarAsync(default)));
        }
        session.SelectContinuity(int.Parse(other.ResourceKey!), "Other memory world");
        Assert.Empty((await memories.ReadAsync(new(V4MemoryScope.Continuity))).Items);
        Assert.Equal(0, (await memories.HealthAsync()).SelectedContinuityCount);
        Assert.False((await memories.SaveAsync(local)).Success); // token cannot move writes between scopes
        Assert.True((await memories.SaveAsync(local with { MutationToken = "other-memory" })).Success);
        Assert.True((await vault.Schema.VerifyAsync()).IsValid);
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task ConcurrentSavesProtectTheCurrentBodyAndRetryOnlyOnce()
    {
        await using var vault = await TestVault.CreateAsync();
        var (_, session, refs) = vault.V4();
        var memories = new AccessV4MemoryService(vault.Factory, vault.Coordinator, refs, session, vault.Cursors);
        var create = new V4MemorySaveRequest("concurrent-create", V4MemoryScope.Global, "instructions", "Initial", 0);
        var repeated = await Task.WhenAll(memories.SaveAsync(create), memories.SaveAsync(create));
        Assert.All(repeated, r => Assert.True(r.Success, r.Message));
        Assert.Single(repeated, r => r.Replayed);
        var writes = await Task.WhenAll(
            memories.SaveAsync(create with { MutationToken = "writer-one", ExpectedVersion = 1, Body = "One" }),
            memories.SaveAsync(create with { MutationToken = "writer-two", ExpectedVersion = 1, Body = "Two" }));
        Assert.Single(writes, r => r.Success);
        Assert.Equal("version.conflict", Assert.Single(writes, r => !r.Success).Code);
        Assert.Equal(2, Assert.Single((await memories.ReadAsync(new(V4MemoryScope.Global))).Items).Version);
    }

    [Fact]
    public async Task FullBodiesPageWithoutTruncationAndRejectCrossScopeCursors()
    {
        await using var vault = await TestVault.CreateAsync();
        var (_, session, refs) = vault.V4();
        var memories = new AccessV4MemoryService(vault.Factory, vault.Coordinator, refs, session, vault.Cursors);
        var body = new string('x', 65536);
        for (var n = 0; n < 3; n++)
            Assert.True((await memories.SaveAsync(new("page-" + n, V4MemoryScope.Global, "note-" + n, body, 0))).Success);
        var first = await memories.ReadAsync(new(V4MemoryScope.Global, Limit: 2));
        Assert.True(first.HasMore); Assert.All(first.Items, note => Assert.Equal(body, note.Body));
        var last = await memories.ReadAsync(new(V4MemoryScope.Global, Cursor: first.NextCursor, Limit: 2));
        Assert.False(last.HasMore); Assert.Equal("note-2", Assert.Single(last.Items).Key);
        await Assert.ThrowsAsync<V4CursorException>(() => memories.ReadAsync(new(V4MemoryScope.Global, Key: "note-1", Cursor: first.NextCursor)));
        Assert.False((await memories.SaveAsync(new("too-long", V4MemoryScope.Global, "long", body + "x", 0))).Success);
        Assert.False((await memories.SaveAsync(new("bad-key", V4MemoryScope.Global, "Bad Key", "body", 0))).Success);
        await Assert.ThrowsAnyAsync<Exception>(() => memories.ReadAsync(new(V4MemoryScope.Continuity)));
    }

    [Fact]
    public async Task ExistingDatabaseUpgradesWithoutChangingEarlierFingerprints()
    {
        await using var vault = await TestVault.CreateAsync();
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            foreach (var sql in new[] { "DROP TABLE [PrivateMemories]", $"DELETE FROM [SchemaMigrations] WHERE [MigrationId]='{AccessSchemaDefinition.MigrationId}'" })
            { using var cmd = connection.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        }
        var migrator = new AccessSchemaMigrator(vault.Factory, TimeProvider.System);
        Assert.True((await migrator.MigrateAsync(false)).Verification.IsValid);
        Assert.True((await migrator.MigrateAsync(false)).Verification.IsValid);
        Assert.Equal(AccessSchemaDefinition.SpeciesChecksum, AccessSchemaMigrations.All.Single(m => m.MigrationId == AccessSchemaDefinition.SpeciesMigrationId).Checksum);
    }

    [Fact]
    public async Task HealthArgumentsLoadMemoriesOverRealMcpAndReadOnlyOmitsSave()
    {
        await using var vault = await TestVault.CreateAsync();
        await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Memory protocol", "UTC"));
        async Task<McpClient> Client(bool readOnly) => await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "memory-test", Command = "dotnet", WorkingDirectory = Path.GetDirectoryName(TestServer.AssemblyPath)!,
            Arguments = new[] { TestServer.AssemblyPath, "serve", "--database", vault.DatabasePath, "--backup-root", vault.StorageRoot, "--tool-surface", "v4" }
                .Concat(readOnly ? ["--read-only"] : Array.Empty<string>()).ToArray()
        }));
        await using (var client = await Client(false))
        {
            await client.CallToolAsync("session_set", new Dictionary<string, object?> { ["continuityName"] = "Memory protocol" });
            foreach (var scope in new[] { "Global", "Continuity" })
            {
                var saved = await client.CallToolAsync("memory_save", new Dictionary<string, object?> {
                    ["mutationToken"] = "protocol-" + scope, ["scope"] = scope, ["key"] = "instructions", ["body"] = "Context for " + scope, ["expectedVersion"] = 0 });
                Assert.NotEqual(true, saved.IsError);
                using var result = JsonDocument.Parse(saved.StructuredContent!.ToString()!);
                Assert.True(result.RootElement.GetProperty("success").GetBoolean(), saved.StructuredContent.ToString());
            }
            var health = await client.CallToolAsync("vault_health", new Dictionary<string, object?>());
            using var json = JsonDocument.Parse(health.StructuredContent!.ToString()!);
            var memory = json.RootElement.GetProperty("memories");
            Assert.Equal(1, memory.GetProperty("globalCount").GetInt32());
            Assert.Equal(1, memory.GetProperty("selectedContinuityCount").GetInt32());
            foreach (var property in new[] { "globalRead", "selectedContinuityRead" })
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(memory.GetProperty(property).GetRawText())!;
                var loaded = await client.CallToolAsync(memory.GetProperty("readTool").GetString()!, args);
                Assert.NotEqual(true, loaded.IsError);
                Assert.Contains("Context for", loaded.StructuredContent!.ToString());
            }
        }
        await using var reader = await Client(true);
        var tools = await reader.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "memory_read"); Assert.DoesNotContain(tools, t => t.Name == "memory_save");
        var persisted = await reader.CallToolAsync("memory_read", new Dictionary<string, object?> { ["scope"] = "Global" });
        Assert.Contains("Context for Global", persisted.StructuredContent!.ToString());
    }
}
