using System.Text.Json;
using ModelContextProtocol.Client;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Tests;

public sealed class LiveCutoverSmokeTests
{
    [Fact]
    public async Task ReadOnlyThenWriteEnabledMcpSmokeIsReversibleAndJournaled()
    {
        TestVault? disposable = null;
        var database = Environment.GetEnvironmentVariable("WRITINGVAULT_CUTOVER_DATABASE");
        var backupRoot = Environment.GetEnvironmentVariable("WRITINGVAULT_CUTOVER_BACKUP_ROOT");
        if (string.IsNullOrWhiteSpace(database))
        {
            disposable = await TestVault.CreateAsync();
            database = disposable.DatabasePath;
            backupRoot = disposable.StorageRoot;
        }
        try
        {
            await using (var readOnly = await CreateClient(database, backupRoot!, "cutover-read-only", true))
            {
                var tools = await readOnly.ListToolsAsync();
                Assert.Contains(tools, tool => tool.Name == "vault_health");
                Assert.DoesNotContain(tools, tool => tool.Name == "continuity_create");
                var health = await readOnly.CallToolAsync("vault_health", new Dictionary<string, object?>());
                Assert.NotEqual(true, health.IsError);
                Assert.Contains("\"ready\":true", Content(health).Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
            }

            await using var writable = await CreateClient(database, backupRoot!, "cutover-write", false);
            var suffix = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            var continuityName = $"Cutover Verification {suffix}";
            var prefix = $"phase10-{suffix}";
            AssertSuccess(await writable.CallToolAsync("continuity_create", Args(new
            {
                requestToken = $"{prefix}-create", name = continuityName, defaultTimeZoneId = "UTC"
            })));
            await writable.CallToolAsync("session_continuity_set", new Dictionary<string, object?>
            {
                ["continuityName"] = continuityName
            });
            var clockRequest = new
            {
                requestToken = $"{prefix}-clock-set", currentInstant = "2003-06-01T12:00:00+00:00",
                referenceTimeZoneId = "UTC", expectedVersion = 1
            };
            AssertSuccess(await writable.CallToolAsync("continuity_clock_set", Args(clockRequest)));
            var replay = await writable.CallToolAsync("continuity_clock_set", Args(clockRequest));
            AssertSuccess(replay);
            Assert.Contains("\"replayed\":true", Content(replay).Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
            AssertSuccess(await writable.CallToolAsync("continuity_clock_set", Args(new
            {
                requestToken = $"{prefix}-clock-clear", currentInstant = (string?)null,
                referenceTimeZoneId = (string?)null, expectedVersion = 2
            })));
            var history = await writable.CallToolAsync("operation_history", new Dictionary<string, object?>
            {
                ["requestToken"] = $"{prefix}-clock-set"
            });
            Assert.NotEqual(true, history.IsError);
            Assert.Contains("continuity_clock_set", Content(history), StringComparison.Ordinal);

            AssertSuccess(await writable.CallToolAsync("vault_record_soft_delete", Args(new
            {
                requestToken = $"{prefix}-delete", recordType = "Continuity",
                recordReference = continuityName, expectedVersion = 1
            })));
            AssertSuccess(await writable.CallToolAsync("vault_record_restore", Args(new
            {
                requestToken = $"{prefix}-restore", recordType = "Continuity",
                recordReference = continuityName, expectedVersion = 2
            })));
            var finalHealth = await writable.CallToolAsync("vault_health", new Dictionary<string, object?>());
            Assert.Contains("\"pendingWrites\":0", Content(finalHealth).Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (disposable is not null) await disposable.DisposeAsync();
        }
    }

    private static async Task<McpClient> CreateClient(string database, string backupRoot, string label, bool readOnly)
    {
        var serverDll = TestServer.AssemblyPath;
        var arguments = new List<string>
        {
            serverDll, "serve", "--database", database, "--backup-root", backupRoot, "--client-label", label
        };
        if (readOnly) arguments.Add("--read-only");
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = label, Command = "dotnet", Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!, ShutdownTimeout = TimeSpan.FromSeconds(10)
        }));
    }

    private static Dictionary<string, object?> Args(object request) => new() { ["request"] = request };
    private static string Content(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent?.ToString() ?? throw new Xunit.Sdk.XunitException("Missing structured content.");
    private static void AssertSuccess(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        using var document = JsonDocument.Parse(Content(result));
        Assert.True(document.RootElement.GetProperty("success").GetBoolean(), Content(result));
    }
}
