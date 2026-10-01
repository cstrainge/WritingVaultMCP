using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4ContractTests
{
    [Fact]
    public void CatalogIsCompleteUniqueAndUsesCommonMutationEnvelope()
    {
        Assert.Equal(86, V4ContractCatalog.Tools.Count);
        Assert.Equal(V4ContractCatalog.Tools.Count,
            V4ContractCatalog.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());

        var schemas = V4ContractCatalog.ExportSchemas();
        var tools = Assert.IsType<JsonArray>(schemas["tools"]);
        Assert.Equal(V4ContractCatalog.Tools.Count, tools.Count);

        foreach (var tool in V4ContractCatalog.Tools.Where(tool => tool.Access == V4ToolAccess.Write))
        {
            var schema = Tool(schemas, tool.Name);
            var properties = Assert.IsType<JsonObject>(schema["inputSchema"]?["properties"]);
            Assert.True(properties.ContainsKey("mutationToken"), $"{tool.Name} has no mutationToken.");
            Assert.True(JsonNode.DeepEquals(schema["outputSchema"], V4ContractCatalog.ExportCommonSchemas()["schemas"]?["mutationResult"]),
                $"{tool.Name} does not return the common mutation envelope.");
        }
    }

    [Fact]
    public void GeneratedSnapshotsExactlyMatchTheCatalog()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "WritingVaultMcp-V4Contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            V4ContractCatalog.WriteSnapshots(temporary);
            var checkedIn = Path.Combine(RepositoryRoot(), "contracts", "v4");
            foreach (var name in new[] { "tool-schemas.json", "common-schemas.json", "examples.json" })
                Assert.Equal(
                    File.ReadAllText(Path.Combine(checkedIn, name)).ReplaceLineEndings("\n"),
                    File.ReadAllText(Path.Combine(temporary, name)).ReplaceLineEndings("\n"));
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public void ExamplesAndSchemasCoverEveryToolWithoutStorageIdentity()
    {
        var schemas = V4ContractCatalog.ExportSchemas();
        var examples = V4ContractCatalog.ExportExamples();
        var schemaTools = Assert.IsType<JsonArray>(schemas["tools"]);
        var exampleTools = Assert.IsType<JsonArray>(examples["tools"]);
        Assert.Equal(schemaTools.Count, exampleTools.Count);
        Assert.Equal(
            schemaTools.Select(node => node?["name"]?.GetValue<string>()).Order(StringComparer.Ordinal),
            exampleTools.Select(node => node?["name"]?.GetValue<string>()).Order(StringComparer.Ordinal));

        var allowedDomainIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "referenceTimeZoneId", "timeZoneId", "defaultTimeZoneId", "calendarId"
        };
        foreach (var property in PropertyNames(schemas).Where(name => name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Id", StringComparison.Ordinal)))
            Assert.Contains(property, allowedDomainIdentifiers);

        var text = schemas.ToJsonString() + examples.ToJsonString();
        foreach (var forbidden in new[] { "operationId", "continuityId", "entityId", "characterId", "sourceId", "tagId", "recordId", "relationshipId" })
            Assert.DoesNotContain($"\"{forbidden}\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase), text);
    }

    [Fact]
    public void BoundsDatesEnumsAndImageTransportAreFrozen()
    {
        var schemas = V4ContractCatalog.ExportSchemas();
        Assert.Equal(500, Tool(schemas, "timeline_get")["inputSchema"]?["properties"]?["limit"]?["maximum"]?.GetValue<int>());
        Assert.Equal(20, Tool(schemas, "get")["inputSchema"]?["properties"]?["include"]?["maxItems"]?.GetValue<int>());
        Assert.Equal(100, Tool(schemas, "event_project_apply")["inputSchema"]?["properties"]?["projects"]?["maxItems"]?.GetValue<int>());

        var date = Tool(schemas, "event_record")["inputSchema"]?["properties"]?["occurred"];
        Assert.Equal(3, Assert.IsType<JsonArray>(date?["oneOf"]).Count);
        var dateKinds = Assert.IsType<JsonArray>(date?["oneOf"]?[2]?["properties"]?["kind"]?["enum"])
            .Select(node => node!.GetValue<string>()).ToArray();
        Assert.Contains("KnownRange", dateKinds);
        Assert.Contains("UncertainRange", dateKinds);
        Assert.DoesNotContain("Range", dateKinds);
        var actions = Assert.IsType<JsonArray>(Tool(schemas, "event_project_apply")["inputSchema"]?["properties"]?["action"]?["enum"]);
        Assert.Equal(new[] { "Add", "Remove" }, actions.Select(node => node!.GetValue<string>()));

        var image = Tool(schemas, "image_attach")["inputSchema"]?["properties"]?["image"];
        Assert.Equal(2, Assert.IsType<JsonArray>(image?["oneOf"]).Count);
        Assert.Equal("string", image?["properties"]?["dataBase64"]?["type"]?.GetValue<string>());
        Assert.Equal("string", image?["properties"]?["dataUrl"]?["type"]?.GetValue<string>());
        var story = Tool(schemas, "story_image_attach")["inputSchema"];
        Assert.Equal("string", story?["properties"]?["owner"]?["type"]?.GetValue<string>());
        Assert.NotNull(story?["properties"]?["image"]);
    }

    [Fact]
    public void EventContractsSupportOptionalProjectsAndLaterAtomicChanges()
    {
        var schemas = V4ContractCatalog.ExportSchemas();
        foreach (var name in new[] { "event_record", "entity_event_add" })
        {
            var input = Tool(schemas, name)["inputSchema"];
            Assert.NotNull(input?["properties"]?["projects"]);
            Assert.DoesNotContain("projects", Assert.IsType<JsonArray>(input?["required"]).Select(node => node!.GetValue<string>()));
        }

        var apply = Tool(schemas, "event_project_apply")["inputSchema"];
        var required = Assert.IsType<JsonArray>(apply?["required"]).Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.True(required.IsSupersetOf(new[] { "mutationToken", "event", "projects", "action" }));
    }

    [Fact]
    public void MembershipCreationRequiresOnePeriodOrTwoIndependentTransitions()
    {
        var schema = Tool(V4ContractCatalog.ExportSchemas(), "relationship_membership_period_add")["inputSchema"];
        Assert.Equal(2, Assert.IsType<JsonArray>(schema?["oneOf"]).Count);
        var fields = Assert.IsType<JsonObject>(schema?["properties"]);
        foreach (var name in new[] { "period", "joined", "left" })
            Assert.NotNull(fields[name]);
        var example = Assert.IsType<JsonObject>(V4ContractCatalog.ExportExamples()["tools"]!
            .AsArray().Single(item => item?["name"]?.GetValue<string>() == "relationship_membership_period_add")!["request"]);
        Assert.NotNull(example["period"]);
    }

    [Fact]
    public void UpdateContractsPreserveOmissionVersusExplicitNull()
    {
        var schemas = V4ContractCatalog.ExportSchemas();
        foreach (var name in new[]
                 {
                     "continuity_update", "variant_group_update", "entity_update", "tag_update",
                     "source_update", "claim_update", "note_update", "character_temporal_effect_update",
                     "image_update"
                 })
        {
            var input = Tool(schemas, name)["inputSchema"];
            Assert.Contains("changes", Assert.IsType<JsonArray>(input?["required"])
                .Select(node => node!.GetValue<string>()));
            var changes = Assert.IsType<JsonObject>(input?["properties"]?["changes"]);
            Assert.False(changes["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(1, changes["minProperties"]!.GetValue<int>());
        }

        var descriptionType = Assert.IsType<JsonArray>(
            Tool(schemas, "entity_update")["inputSchema"]?["properties"]?["changes"]?["properties"]?["description"]?["type"]);
        Assert.Contains(descriptionType, node => node?.GetValue<string>() == "null");
        Assert.Equal("string", Tool(schemas, "entity_update")["inputSchema"]?["properties"]?["changes"]?["properties"]?["name"]?["type"]?.GetValue<string>());

        foreach (var (tool, field) in new[]
                 {
                     ("entity_variant_group_set", "variantGroupRef"),
                     ("location_move", "parentLocation")
                 })
            Assert.Contains(field, Assert.IsType<JsonArray>(Tool(schemas, tool)["inputSchema"]?["required"])
                .Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task ImageProbeValidatesHashesAndRecordsMetadataWithoutContent()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl9sAAAAASUVORK5CYII=";
        var evidence = Path.Combine(Path.GetTempPath(), "WritingVaultMcp-Probe-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var probe = new V4ImageIngressProbeTools(new V4ImageIngressProbeOptions(evidence));
            var base64 = await probe.Probe("AutomatedTest", "image/png", dataBase64: png);
            Assert.True(base64.Success);
            Assert.Equal("dataBase64", base64.Transport);
            Assert.Equal(1, base64.Width);
            Assert.Equal(1, base64.Height);
            Assert.Matches("^[A-F0-9]{64}$", base64.Sha256!);

            var dataUrl = await probe.Probe("AutomatedTest", "image/png", dataUrl: "data:image/png;base64," + png);
            Assert.True(dataUrl.Success);
            Assert.Equal(base64.Sha256, dataUrl.Sha256);

            var ambiguous = await probe.Probe("AutomatedTest", "image/png", png, "data:image/png;base64," + png);
            Assert.False(ambiguous.Success);
            Assert.Equal("image.transport_invalid", ambiguous.Code);

            var invalid = await probe.Probe("AutomatedTest", "image/png", dataBase64: Convert.ToBase64String("not-png"u8));
            Assert.False(invalid.Success);
            Assert.Equal("image.content_invalid", invalid.Code);

            var tooLarge = await probe.Probe("AutomatedTest", "image/png",
                dataBase64: new string('A', V4ImageIngressProbeTools.MaximumEncodedCharacters + 1));
            Assert.False(tooLarge.Success);
            Assert.Equal("image.too_large", tooLarge.Code);

            var logged = await File.ReadAllTextAsync(evidence);
            Assert.DoesNotContain(png, logged, StringComparison.Ordinal);
            Assert.DoesNotContain("\"DataBase64\"", logged, StringComparison.Ordinal);
            Assert.DoesNotContain("\"DataUrl\"", logged, StringComparison.Ordinal);
            Assert.Contains(base64.Sha256!, logged, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(evidence)) File.Delete(evidence);
        }
    }

    [Fact]
    public async Task DebugImageProbeAdvertisesOnlyTheFlatNonPersistingToolAndAcceptsPngOverMcp()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl9sAAAAASUVORK5CYII=";
        var evidence = Path.Combine(Path.GetTempPath(), "WritingVaultMcp-McpProbe-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var serverDll = TestServer.AssemblyPath;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "v4-image-probe-test",
            Command = "dotnet",
            Arguments = [serverDll, "contract", "image-probe", "--evidence-output", evidence],
            WorkingDirectory = Path.GetDirectoryName(serverDll)!,
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        try
        {
            await using var client = await McpClient.CreateAsync(transport);
            var tool = Assert.Single(await client.ListToolsAsync());
            Assert.Equal("image_ingress_probe", tool.Name);
            var schema = tool.JsonSchema.GetRawText();
            Assert.Contains("\"clientName\"", schema, StringComparison.Ordinal);
            Assert.Contains("\"dataBase64\"", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("\"request\"", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("path", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http", schema, StringComparison.OrdinalIgnoreCase);

            var result = await client.CallToolAsync("image_ingress_probe", new Dictionary<string, object?>
            {
                ["clientName"] = "AutomatedTest",
                ["mediaType"] = "image/png",
                ["dataBase64"] = png
            });
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("\"Success\":true", result.StructuredContent?.ToString()?.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(evidence)) File.Delete(evidence);
        }
    }

    private static JsonObject Tool(JsonObject catalog, string name) =>
        Assert.Single(Assert.IsType<JsonArray>(catalog["tools"]).OfType<JsonObject>(),
            tool => tool["name"]?.GetValue<string>() == name);

    private static IEnumerable<string> PropertyNames(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
                foreach (var name in properties.Select(property => property.Key)) yield return name;
            foreach (var child in obj.Select(property => property.Value))
                foreach (var name in PropertyNames(child)) yield return name;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                foreach (var name in PropertyNames(child)) yield return name;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "V4_API_PLAN.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
