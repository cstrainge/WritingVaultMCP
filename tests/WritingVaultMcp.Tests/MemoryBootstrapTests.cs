using System.Text.Json;
using ModelContextProtocol.Client;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class MemoryBootstrapTests
{
    [Fact]
    public async Task SearchPinEditDeleteAndLimitsPreserveSharedVersions()
    {
        await using var vault = await TestVault.CreateAsync();
        var (_, session, refs) = vault.V4();
        var memories = new AccessV4MemoryService(vault.Factory, vault.Coordinator, refs, session, vault.Cursors);
        var note = new V4MemorySaveRequest("pin", V4MemoryScope.Global, "preferences", "Proactive updates: 100% useful, not a guess.", 0, true);
        Assert.True((await memories.SaveAsync(note)).Success);
        Assert.Single((await memories.ReadAsync(new(V4MemoryScope.Global, Text: "PROACTIVE"))).Items);
        Assert.Empty((await memories.ReadAsync(new(V4MemoryScope.Global, Text: "%missing"))).Items);
        Assert.Equal(note.Body, Assert.Single((await memories.HealthAsync()).PinnedMemories!.Global).Body);
        var oversized = await memories.SaveAsync(note with { MutationToken = "over-limit", ExpectedVersion = 1, Body = new string('x', 32769) });
        Assert.False(oversized.Success);
        Assert.Equal(note.Body, Assert.Single((await memories.PinnedAsync()).Global).Body);
        Assert.True((await memories.SaveAsync(note with { MutationToken = "edit-pin", ExpectedVersion = 1, Body = "Edited standing instructions", Pinned = null })).Success);
        Assert.Equal("Edited standing instructions", Assert.Single((await memories.PinnedAsync()).Global).Body);
        Assert.False((await memories.DeleteAsync(new("stale-delete", V4MemoryScope.Global, "preferences", 1))).Success);
        var delete = new V4MemoryDeleteRequest("delete", V4MemoryScope.Global, "preferences", 2);
        Assert.True((await memories.DeleteAsync(delete)).Success);
        Assert.True((await memories.DeleteAsync(delete)).Replayed);
        Assert.Empty((await memories.PinnedAsync()).Global);
        Assert.Equal(0, (await memories.HealthAsync()).GlobalCount);
        var tombstone = Assert.Single((await memories.ReadAsync(new(V4MemoryScope.Global, IncludeDeleted: true))).Items);
        Assert.True(tombstone.IsDeleted); Assert.Equal("", tombstone.Body); Assert.Equal(3, tombstone.Version);
        Assert.False((await memories.SaveAsync(note with { MutationToken = "old-create" })).Success);
        Assert.True((await memories.SaveAsync(note with { MutationToken = "reuse", ExpectedVersion = 3, Pinned = false })).Success);
        Assert.Empty((await memories.PinnedAsync()).Global);
    }

    [Fact]
    public async Task StartupSessionAndHealthDeliverBodiesAcrossIndependentClients()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Pinned world", "UTC"));
        await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Other pinned world", "UTC"));
        var (_, session, refs) = vault.V4(); session.SelectContinuity(int.Parse(world.ResourceKey!), "Pinned world");
        var memories = new AccessV4MemoryService(vault.Factory, vault.Coordinator, refs, session, vault.Cursors);
        Assert.True((await memories.SaveAsync(new("standing", V4MemoryScope.Global, "standing", "GLOBAL-STARTUP-INSTRUCTION", 0, true))).Success);
        Assert.True((await memories.SaveAsync(new("world-standing", V4MemoryScope.Continuity, "world", "LOCAL-STARTUP-INSTRUCTION", 0, true))).Success);
        async Task<McpClient> Client(string name, bool readOnly = false) => await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = name, Command = "dotnet", WorkingDirectory = Path.GetDirectoryName(TestServer.AssemblyPath)!,
            Arguments = new[] { TestServer.AssemblyPath, "serve", "--database", vault.DatabasePath, "--backup-root", vault.StorageRoot, "--tool-surface", "v4", "--client-label", name }
                .Concat(readOnly ? ["--read-only"] : Array.Empty<string>()).ToArray()
        }));
        await using var a = await Client("client-a");
        await using var b = await Client("client-b", true);
        Assert.Contains("GLOBAL-STARTUP-INSTRUCTION", a.ServerInstructions);
        Assert.Contains("GLOBAL-STARTUP-INSTRUCTION", b.ServerInstructions);
        Assert.DoesNotContain("LOCAL-STARTUP-INSTRUCTION", b.ServerInstructions);
        var selected = await b.CallToolAsync("session_set", new Dictionary<string, object?> { ["continuityName"] = "Pinned world" });
        Assert.Contains("LOCAL-STARTUP-INSTRUCTION", selected.StructuredContent!.ToString());
        Assert.Contains("GLOBAL-STARTUP-INSTRUCTION", selected.StructuredContent!.ToString());
        var switched = await b.CallToolAsync("session_set", new Dictionary<string, object?> { ["continuityName"] = "Other pinned world" });
        Assert.DoesNotContain("LOCAL-STARTUP-INSTRUCTION", switched.StructuredContent!.ToString());
        var deleted = await a.CallToolAsync("memory_delete", new Dictionary<string, object?> {
            ["mutationToken"] = "cross-client-delete", ["scope"] = "Global", ["key"] = "standing", ["expectedVersion"] = 1 });
        Assert.Contains("\"success\":true", deleted.StructuredContent!.ToString()!.Replace(" ", ""));
        var health = await b.CallToolAsync("vault_health", new Dictionary<string, object?>());
        Assert.DoesNotContain("GLOBAL-STARTUP-INSTRUCTION", health.StructuredContent!.ToString());
        var capability = await b.CallToolAsync("vault_capabilities", new Dictionary<string, object?>());
        using var json = JsonDocument.Parse(capability.StructuredContent!.ToString()!);
        Assert.True(json.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.False(json.RootElement.GetProperty("features").GetProperty("memoryDelete").GetBoolean());
        Assert.DoesNotContain("memory_save", json.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(64, json.RootElement.GetProperty("catalogFingerprint").GetString()!.Length);
    }

    [Fact]
    public async Task ExactDatesCanBeTentativeInEventsProjectSpansAndHistoricalPages()
    {
        await using var vault = await TestVault.CreateAsync();
        var world = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Tentative books", "UTC"))).ResourceKey!);
        var project = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), world, CanonEntityType.Project, "Provisional book"))).ResourceKey!);
        var (reads, session, refs) = vault.V4(); session.SelectContinuity(world, "Tentative books");
        var app = new AccessV4ApplicationService(vault.Coordinator, refs, new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, refs)), vault.Service);
        vault.EnableAutomaticPageSnapshots();
        var projectRef = await refs.ReferenceAsync("Project", project);
        var saved = await app.RecordEntityEventAsync(new("tentative-end", projectRef, "Guessed ending", new(V4StoryDateKind.ExactDate, "2025-12-31"),
            ProjectBoundary: V4ProjectBoundary.StoryEnds, FactStatus: V4FactStatus.Tentative), world, "test");
        Assert.True(saved.Success, saved.Message);
        var eventRef = await refs.ReferenceAsync("EntityEvent", int.Parse(saved.ResourceKey!));
        var overview = await reads.GetAsync(new(eventRef));
        Assert.Equal("Tentative", overview.Fields["factStatus"].GetString());
        Assert.Equal("Tentative", (await reads.GetAsync(new(projectRef))).Fields["storyEndsStatus"].GetString());
        var timeline = await reads.TimelineAsync(new());
        Assert.All(timeline.Items.Where(i => i.Ref == projectRef || i.Ref == eventRef), i => Assert.Equal(V4FactStatus.Tentative, i.FactStatus));
        Assert.Equal(V4StoryDateKind.ExactDate, Assert.Single(timeline.Items, i => i.Ref == eventRef).Occurred.Kind);
        Assert.True((await app.UpdateEventAsync(new("confirm-end", eventRef, 1, FactStatus: V4FactStatus.Confirmed), world, "test")).Success);
        Assert.Equal("Confirmed", (await reads.GetAsync(new(projectRef))).Fields["storyEndsStatus"].GetString());
        Assert.Equal("Tentative", (await reads.SnapshotAsync(new(eventRef, 1))).Overview.Fields["factStatus"].GetString());
        // Leave the optional browser fixture visibly provisional.
        Assert.True((await app.UpdateEventAsync(new("tentative-again", eventRef, 2, FactStatus: V4FactStatus.Tentative), world, "test")).Success);
        var fixture = Environment.GetEnvironmentVariable("WRITINGVAULT_TENTATIVE_FIXTURE");
        if (!string.IsNullOrWhiteSpace(fixture)) { Directory.CreateDirectory(fixture); File.Copy(vault.DatabasePath, Path.Combine(fixture, "preview.accdb"), true); }
    }
}
