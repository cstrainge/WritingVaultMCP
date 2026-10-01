using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;

namespace WritingVaultMcp.Tests;

public sealed class McpProtocolTests
{
    private static readonly string[] ReadToolNames =
    [
        "session_continuity_set", "session_get", "session_time_set", "session_time_clear",
        "vault_health", "continuity_list", "entity_search", "entity_get", "variant_group_list",
        "source_search", "tag_search", "entity_graph", "source_graph", "character_relationships",
        "character_age", "entity_local_time", "entity_temporal_state", "entity_delete_preview",
        "record_history", "operation_history"
    ];

    private static readonly string[] WriteToolNames =
    [
        "continuity_create", "continuity_patch", "variant_group_create", "variant_group_patch",
        "entity_variant_group_set", "entity_create", "entity_patch", "entity_duplicate_to_continuity",
        "continuity_clock_set", "tag_create", "tag_patch", "source_create", "source_patch",
        "source_snapshot_add", "claim_create", "entity_note_add", "entity_event_add", "entity_alias_add",
        "note_source_link", "entity_tag_link", "entity_tag_unlink", "entity_source_link",
        "entity_source_unlink", "project_entity_link", "source_tag_link", "source_tag_unlink",
        "location_move", "character_residence_add", "character_residence_transition",
        "organization_membership_add", "organization_membership_transition", "organization_location_add",
        "relationship_type_create", "character_relationship_create", "ownership_principal_create",
        "object_ownership_add", "object_ownership_replace_owners", "object_ownership_transfer",
        "object_location_add", "object_custody_add", "world_event_participant_add",
        "world_event_location_add", "entity_soft_delete", "entity_restore", "relationship_soft_delete",
        "relationship_restore", "vault_record_soft_delete", "vault_record_restore"
    ];

    [Fact]
    public async Task ReadOnlyAdapterDiscoversOnlySemanticReadTools()
    {
        Assert.Null(typeof(AccessVaultService).Assembly.GetType("WritingVaultMcp.Mcp.VaultReadTools"));
        Assert.Null(typeof(AccessVaultService).Assembly.GetType("WritingVaultMcp.Mcp.VaultWriteTools"));
        await using var vault = await TestVault.CreateAsync();
        await using var client = await CreateClient(vault, "read-only", true);
        var tools = await client.ListToolsAsync();
        Assert.Equal(ReadToolNames.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Contains("session_continuity_set", client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("never guess", client.ServerInstructions, StringComparison.OrdinalIgnoreCase);

        var schemas = string.Join('\n', tools.Select(tool => tool.ToString()));
        foreach (var forbidden in new[] { "operationId", "continuityId", "entityId", "characterId", "sourceId", "tagId", "recordId", "relationshipId", "operation GUID" })
            Assert.DoesNotContain(forbidden, schemas, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vault.DatabasePath, schemas, StringComparison.OrdinalIgnoreCase);

        var health = await client.CallToolAsync("vault_health", new Dictionary<string, object?>());
        Assert.NotEqual(true, health.IsError);
        Assert.DoesNotContain(vault.DatabasePath, health.ToString(), StringComparison.OrdinalIgnoreCase);
        AssertNoStorageIdentity(Content(health));

        var unscopedSearch = await client.CallToolAsync("entity_search", new Dictionary<string, object?>
        {
            ["entityType"] = "Character",
            ["limit"] = 10
        });
        Assert.Equal(true, unscopedSearch.IsError);
        var unscopedError = ErrorContent(unscopedSearch);
        Assert.Contains("session_continuity_set", unscopedError, StringComparison.Ordinal);
        Assert.Contains("continuityName", unscopedError, StringComparison.Ordinal);
        AssertNoStorageIdentity(unscopedError);
    }

    [Fact]
    public async Task TwoAdaptersShareBackendWithIndependentContinuityAndTimeState()
    {
        await using var vault = await TestVault.CreateAsync();
        await using var first = await CreateClient(vault, "chatgpt", false);
        await using var second = await CreateClient(vault, "claude", false);

        var tools = await first.ListToolsAsync();
        Assert.Equal(ReadToolNames.Concat(WriteToolNames).Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

        var createContinuity = await first.CallToolAsync("continuity_create", Args(new
        {
            requestToken = "protocol-create-canon", name = "Protocol Canon", defaultTimeZoneId = "UTC"
        }));
        AssertSuccess(createContinuity);
        Assert.Contains("Protocol Canon", Content(createContinuity), StringComparison.Ordinal);
        AssertNoStorageIdentity(Content(createContinuity));

        foreach (var client in new[] { first, second })
        {
            var selected = await client.CallToolAsync("session_continuity_set", new Dictionary<string, object?> { ["continuityName"] = "Protocol Canon" });
            Assert.NotEqual(true, selected.IsError);
        }

        var createCharacter = await first.CallToolAsync("entity_create", Args(new
        {
            requestToken = "protocol-create-taylor", entityType = "Character", name = "Taylor",
            birth = new
            {
                kind = "ExactDate", lowerBound = "2000-06-15T00:00:00", upperBound = "2000-06-16T00:00:00",
                lowerInclusive = true, upperInclusive = false, calendarId = "Gregorian"
            }
        }));
        AssertSuccess(createCharacter);
        var characterReference = FindString(createCharacter, "resourceReference");
        Assert.Matches("^character:taylor~[A-Z2-9]{10}$", characterReference);
        AssertNoStorageIdentity(Content(createCharacter));

        await first.CallToolAsync("session_time_set", new Dictionary<string, object?>
        {
            ["currentInstant"] = "2025-06-14T12:00:00+00:00", ["referenceTimeZoneId"] = "UTC"
        });
        await second.CallToolAsync("session_time_set", new Dictionary<string, object?>
        {
            ["currentInstant"] = "2030-06-15T12:00:00+00:00", ["referenceTimeZoneId"] = "UTC"
        });
        var firstAge = await first.CallToolAsync("character_age", new Dictionary<string, object?> { ["characterReference"] = characterReference });
        var secondAge = await second.CallToolAsync("character_age", new Dictionary<string, object?> { ["characterReference"] = characterReference });
        Assert.Contains("24", Content(firstAge), StringComparison.Ordinal);
        Assert.Contains("30", Content(secondAge), StringComparison.Ordinal);

        var replay = await second.CallToolAsync("entity_create", Args(new
        {
            requestToken = "protocol-create-taylor", entityType = "Character", name = "Taylor",
            birth = new
            {
                kind = "ExactDate", lowerBound = "2000-06-15T00:00:00", upperBound = "2000-06-16T00:00:00",
                lowerInclusive = true, upperInclusive = false, calendarId = "Gregorian"
            }
        }));
        AssertSuccess(replay);
        Assert.Contains("\"replayed\":true", Content(replay).Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackendStopsAfterLastAdapterDisconnects()
    {
        await using var vault = await TestVault.CreateAsync();
        var client = await CreateClient(vault, "shutdown-test", true);
        Assert.True(VaultProcessHost.IsBackendRunning(vault.DatabasePath));
        await client.DisposeAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (VaultProcessHost.IsBackendRunning(vault.DatabasePath) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(VaultProcessHost.IsBackendRunning(vault.DatabasePath));
    }

    [Fact]
    public async Task BackendStartedWithoutAnAdapterDoesNotRemainOrphaned()
    {
        await using var vault = await TestVault.CreateAsync();
        var serverDll = TestServer.AssemblyPath;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!
        };
        foreach (var argument in new[]
                 {
                     serverDll, "backend", "--database", vault.DatabasePath,
                     "--backup-root", vault.StorageRoot
                 }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Xunit.Sdk.XunitException("Backend process did not start.");

        var startupDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!VaultProcessHost.IsBackendRunning(vault.DatabasePath) && !process.HasExited && DateTime.UtcNow < startupDeadline)
            await Task.Delay(50);
        Assert.True(VaultProcessHost.IsBackendRunning(vault.DatabasePath));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
        Assert.False(VaultProcessHost.IsBackendRunning(vault.DatabasePath));
    }

    [Fact]
    public async Task ProtocolRejectsMalformedOversizedAndCancelledCallsWithoutDisclosure()
    {
        await using var vault = await TestVault.CreateAsync();
        await using var client = await CreateClient(vault, "hostile-protocol", false);

        var malformed = await client.CallToolAsync("continuity_create", Args(new { requestToken = "missing-fields" }));
        Assert.NotEqual(true, malformed.IsError);
        Assert.Contains("validation.failed", Content(malformed), StringComparison.Ordinal);
        AssertNoStorageIdentity(Content(malformed));

        var oversized = await client.CallToolAsync("continuity_create", Args(new
        {
            requestToken = new string('x', 101), name = "Oversized", defaultTimeZoneId = "UTC"
        }));
        Assert.NotEqual(true, oversized.IsError);
        Assert.Contains("validation.reference", Content(oversized), StringComparison.Ordinal);
        AssertNoStorageIdentity(Content(oversized));

        var invalidReference = await client.CallToolAsync("entity_get", new Dictionary<string, object?>
        {
            ["entityReference"] = "not-a-reference"
        });
        Assert.Equal(true, invalidReference.IsError);
        var error = invalidReference.ToString() ?? string.Empty;
        Assert.DoesNotContain(vault.DatabasePath, error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OLEDB", error, StringComparison.OrdinalIgnoreCase);
        AssertNoStorageIdentity(error);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.CallToolAsync("vault_health", new Dictionary<string, object?>(), cancellationToken: cancelled.Token));
    }

    private static async Task<McpClient> CreateClient(TestVault vault, string label, bool readOnly)
    {
        var serverDll = TestServer.AssemblyPath;
        var arguments = new List<string> { serverDll, "serve", "--database", vault.DatabasePath, "--backup-root", vault.StorageRoot, "--client-label", label };
        if (readOnly) arguments.Add("--read-only");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = label, Command = "dotnet", Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!, ShutdownTimeout = TimeSpan.FromSeconds(10)
        });
        return await McpClient.CreateAsync(transport);
    }

    private static Dictionary<string, object?> Args(object request) => new() { ["request"] = request };

    private static void AssertSuccess(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"success\":true", result.StructuredContent?.ToString()?.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private static string FindString(ModelContextProtocol.Protocol.CallToolResult result, string property)
    {
        using var document = JsonDocument.Parse(result.StructuredContent?.ToString() ?? throw new Xunit.Sdk.XunitException("Missing structured content."));
        return document.RootElement.GetProperty(property).GetString() ?? throw new Xunit.Sdk.XunitException($"Missing {property}.");
    }

    private static string Content(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent?.ToString() ?? throw new Xunit.Sdk.XunitException("Missing structured content.");

    private static string ErrorContent(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static void AssertNoStorageIdentity(string text)
    {
        Assert.DoesNotMatch(new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase), text);
        Assert.DoesNotMatch(new Regex("\\\"(?:id|operationId|continuityId|entityId|characterId|sourceId|tagId|recordId)\\\"\\s*:", RegexOptions.IgnoreCase), text);
    }
}
