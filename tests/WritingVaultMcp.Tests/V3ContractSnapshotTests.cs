using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Tests;

public sealed class V3ContractSnapshotTests
{
    [Fact]
    public async Task AdvertisedV3SchemasMatchDisposableDatabaseSnapshot()
    {
        await using var vault = await TestVault.CreateAsync();
        await using var readOnly = await CreateClient(vault, "v3-contract-read", true);
        await using var readWrite = await CreateClient(vault, "v3-contract-write", false);

        var snapshot = new JsonObject
        {
            ["surfaceVersion"] = "3.0",
            ["capturedAgainst"] = "disposable-migrated-database",
            ["readOnly"] = await Capture(readOnly),
            ["readWrite"] = await Capture(readWrite)
        };
        var actual = snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        var path = Path.Combine(RepositoryRoot(), "contracts", "v3", "tool-schemas.json");

        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACT_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        Assert.True(File.Exists(path), "Missing v3 snapshot. Run this test once with UPDATE_CONTRACT_SNAPSHOTS=1.");
        // Git checks these text snapshots out with LF, while Windows serialization uses CRLF.
        Assert.Equal((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    private static async Task<JsonArray> Capture(McpClient client)
    {
        var result = new JsonArray();
        foreach (var tool in (await client.ListToolsAsync()).OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            result.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                ["outputSchema"] = tool.ReturnJsonSchema is { } output && output.ValueKind != JsonValueKind.Undefined
                    ? JsonNode.Parse(output.GetRawText())
                    : null
            });
        }
        return result;
    }

    private static async Task<McpClient> CreateClient(TestVault vault, string label, bool readOnly)
    {
        var serverDll = TestServer.AssemblyPath;
        var arguments = new List<string>
        {
            serverDll, "serve", "--database", vault.DatabasePath, "--backup-root", vault.StorageRoot,
            "--client-label", label
        };
        if (readOnly) arguments.Add("--read-only");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = label,
            Command = "dotnet",
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(serverDll)!,
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "V4_API_PLAN.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
