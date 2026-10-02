using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase7ProtocolTests
{
    [Fact]
    public async Task SurfaceSelectorAdvertisesExactV4CatalogAndReadOnlyOmitsMutations()
    {
        await using var vault=await TestVault.CreateAsync();
        await using var readOnly=await Client(vault,true);
        var reads=await readOnly.ListToolsAsync();
        var expectedReads=V4ContractCatalog.Tools.Where(x=>x.Access==V4ToolAccess.Read).Select(x=>x.Name).Order().ToArray();
        Assert.Equal(expectedReads,reads.Select(x=>x.Name).Order());
        Assert.Contains("v4",readOnly.ServerInstructions,StringComparison.OrdinalIgnoreCase);
        await using var full=await Client(vault,false);
        var all=await full.ListToolsAsync();
        Assert.Equal(V4ContractCatalog.Tools.Select(x=>x.Name).Order(),all.Select(x=>x.Name).Order());
        Assert.DoesNotContain(all.Select(x=>x.Name),x=>x.Contains("_patch",StringComparison.Ordinal));
        var frozen=V4ContractCatalog.ExportSchemas()["tools"]!.AsArray().OfType<JsonObject>().ToDictionary(
            tool=>tool["name"]!.GetValue<string>(),StringComparer.Ordinal);
        foreach(var tool in all)
        {
            Assert.Equal(frozen[tool.Name]["description"]!.GetValue<string>(),tool.Description);
            Assert.True(JsonNode.DeepEquals(frozen[tool.Name]["inputSchema"],JsonNode.Parse(tool.JsonSchema.GetRawText())),tool.Name+" schema differs from the frozen v4 contract.");
            Assert.NotNull(tool.ReturnJsonSchema);
            Assert.True(JsonNode.DeepEquals(frozen[tool.Name]["outputSchema"],JsonNode.Parse(tool.ReturnJsonSchema.Value.GetRawText())),tool.Name+" output schema differs from the frozen v4 contract.");
            if (tool.Name is "image_attach" or "story_image_attach" or "image_replace")
            {
                Assert.Equal("file", tool.ProtocolTool.Meta!["openai/fileParams"]![0]!.GetValue<string>());
                var file = tool.JsonSchema.GetProperty("properties").GetProperty("file");
                Assert.Equal("object", file.GetProperty("type").GetString());
                Assert.Equal(new[] { "download_url", "file_id" }, file.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(new[] { "download_url", "file_id", "file_name", "mime_type" }, file.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order());
                foreach (var property in file.GetProperty("properties").EnumerateObject())
                    Assert.Equal("string", property.Value.GetProperty("type").GetString());
                var required = tool.JsonSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert.DoesNotContain("image", required);
                Assert.DoesNotContain("file", required);
            }
        }
        var create=all.Single(x=>x.Name=="continuity_create");
        Assert.True(create.JsonSchema.TryGetProperty("properties",out var properties));
        Assert.True(properties.TryGetProperty("mutationToken",out _));
        Assert.False(properties.TryGetProperty("request",out _));
        var bypass=await readOnly.CallToolAsync("continuity_create",new Dictionary<string,object?>
        {
            ["mutationToken"]="read-only-bypass",["name"]="Must Not Exist",["defaultTimeZoneId"]="UTC"
        });
        Assert.True(bypass.IsError);
        Assert.Contains("read_only",string.Join(" ",bypass.Content.OfType<TextContentBlock>().Select(x=>x.Text)),StringComparison.OrdinalIgnoreCase);
        var (storageReads, _, _) = vault.V4();
        var stored = await storageReads.ContinuitiesAsync(new());
        Assert.DoesNotContain(stored.Items, item => item.Name == "Must Not Exist");
    }

    [Fact]
    public async Task HostFileArgumentsReachAllImportToolsThroughRealMcpAdapter()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "File protocol", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "File owner"));
        await using var client = await Client(vault, false);
        await client.CallToolAsync("session_set", new Dictionary<string, object?> { ["continuityName"] = "File protocol" });
        var health = await client.CallToolAsync("vault_health", new Dictionary<string, object?>());
        using var healthJson = JsonDocument.Parse(health.StructuredContent!.ToString()!);
        Assert.True(healthJson.RootElement.GetProperty("imageImportReady").GetBoolean());
        var seed = await client.CallToolAsync("image_attach", new Dictionary<string, object?>
        {
            ["mutationToken"] = "file-protocol-seed", ["entity"] = "File owner",
            ["image"] = new { mediaType = "image/png", dataBase64 = PngBase64() }
        });
        var (imageRef, version) = FirstAffected(seed);
        foreach (var name in new[] { "image_attach", "story_image_attach", "image_replace" })
        {
            var arguments = new Dictionary<string, object?>
            {
                ["mutationToken"] = "file-protocol-" + name,
                ["file"] = new { download_url = "https://127.0.0.1/private?secret=must-not-appear", file_id = "host-file" }
            };
            if (name == "image_attach") arguments["entity"] = "File owner";
            else if (name == "story_image_attach") arguments["owner"] = "File protocol";
            else { arguments["imageRef"] = imageRef; arguments["expectedVersion"] = version; }
            var result = await client.CallToolAsync(name, arguments);
            var content = result.StructuredContent!.ToString()!;
            Assert.Contains("image.url_not_allowed", content);
            Assert.DoesNotContain("must-not-appear", content);
            Assert.DoesNotContain("127.0.0.1", content);
        }
    }

    [Fact]
    public async Task V4OnboardingWorksThroughRealDebugMcpAdapter()
    {
        await using var vault=await TestVault.CreateAsync();
        await using var client=await Client(vault,false);
        var unscoped=await client.CallToolAsync("session_get",new Dictionary<string,object?>());
        Assert.NotEqual(true,unscoped.IsError);Assert.Contains("\"source\":\"none\"",unscoped.StructuredContent?.ToString()?.Replace(" ",string.Empty),StringComparison.OrdinalIgnoreCase);
        var create=await client.CallToolAsync("continuity_create",new Dictionary<string,object?>
        {
            ["mutationToken"]="phase7-onboarding",["name"]="V4 Protocol",["defaultTimeZoneId"]="UTC"
        });
        Assert.True(create.IsError != true, string.Join(" | ", create.Content.OfType<TextContentBlock>().Select(x=>x.Text)) + " :: " + create.StructuredContent);
        Assert.Contains("success",create.StructuredContent?.ToString(),StringComparison.OrdinalIgnoreCase);
        var select=await client.CallToolAsync("session_set",new Dictionary<string,object?>
        {
            ["continuityName"]="V4 Protocol"
        });
        Assert.NotEqual(true,select.IsError);
        var character=await client.CallToolAsync("entity_create",new Dictionary<string,object?>
        {
            ["mutationToken"]="phase7-character",["entityKind"]="Character",["name"]="Compact Date Person",
            ["fields"]=new Dictionary<string,object?>{{"birth","2000-01-02"}}
        });
        Assert.True(character.IsError!=true,string.Join(" | ",character.Content.OfType<TextContentBlock>().Select(x=>x.Text)));
        Assert.Contains("\"success\":true",character.StructuredContent?.ToString()?.Replace(" ",string.Empty),StringComparison.OrdinalIgnoreCase);
        var search=await client.CallToolAsync("search",new Dictionary<string,object?>
        {
            ["kinds"]=new[]{"Character"},["limit"]=10
        });
        Assert.NotEqual(true,search.IsError);
        Assert.Contains("Compact Date Person",search.StructuredContent?.ToString(),StringComparison.Ordinal);
        Assert.DoesNotContain("continuityId",search.ToString(),StringComparison.OrdinalIgnoreCase);
        using var searchJson=JsonDocument.Parse(search.StructuredContent?.ToString()??throw new Xunit.Sdk.XunitException("Missing search content."));
        var observedRevision=searchJson.RootElement.GetProperty("observedRevision").GetString();
        var changes=await client.CallToolAsync("changes_since",new Dictionary<string,object?>
        {
            ["cursor"]=observedRevision,["waitSeconds"]=0
        });
        Assert.NotEqual(true,changes.IsError);
        Assert.Contains("heartbeat",changes.StructuredContent?.ToString(),StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdvertisedOutputSchemasAcceptResponsesWithOmittedNullFields()
    {
        await using var vault = await TestVault.CreateAsync();
        await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Schema Probe", "UTC"));
        await using var client = await Client(vault, true);
        var schemas = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        async Task Check(string name, Dictionary<string, object?> arguments)
        {
            var result = await client.CallToolAsync(name, arguments);
            Assert.NotEqual(true, result.IsError);
            Assert.NotNull(result.StructuredContent);
            Assert.NotNull(schemas[name].ReturnJsonSchema);
            using var response = JsonDocument.Parse(result.StructuredContent.ToString()!);
            ValidateRequiredProperties(schemas[name].ReturnJsonSchema!.Value, response.RootElement, name);
        }

        await Check("session_get", new());
        await Check("continuity_list", new());
        await Check("session_set", new() { ["continuityName"] = "Schema Probe" });
        await Check("image_search", new() { ["acrossContinuities"] = true });
    }

    private static void ValidateRequiredProperties(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                    Assert.True(value.TryGetProperty(name, out _), $"{path}.{name} is required by the advertised output schema but absent from the result.");
            foreach (var property in value.EnumerateObject())
                if (properties.TryGetProperty(property.Name, out var child))
                    ValidateRequiredProperties(child, property.Value, path + "." + property.Name);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateRequiredProperties(items, item, $"{path}[{index++}]");
        }
    }

    [Fact]
    public async Task ImageViewReturnsAnMcpImageBlockAndMetadataWithoutEmbeddedBytes()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Image Protocol","UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Character,"Portrait Subject"));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Image Protocol");
        var imageService=new AccessV4ImageService(vault.Factory,vault.Coordinator,references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads,vault.Cursors,vault.StorageRoot,vault.DatabasePath);
        using var bitmap=new SKBitmap(2,2);using(var canvas=new SKCanvas(bitmap)){canvas.Clear(SKColors.Purple);canvas.Flush();}
        using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Png,100);
        var attached=await imageService.AttachAsync(new(Guid.NewGuid().ToString(),"Portrait Subject",new("image/png",Convert.ToBase64String(encoded.ToArray())),Title:"Portrait"));
        Assert.True(attached.Success,attached.Message);var imageRef=await references.ReferenceAsync("EntityImage",int.Parse(attached.ResourceKey!));
        await using var client=await Client(vault,true);
        Assert.NotEqual(true,(await client.CallToolAsync("session_set",new Dictionary<string,object?>{{"continuityName","Image Protocol"}})).IsError);
        var viewed=await client.CallToolAsync("image_view",new Dictionary<string,object?>{{"imageRef",imageRef},{"size","Thumbnail"}});
        Assert.NotEqual(true,viewed.IsError);Assert.Single(viewed.Content.OfType<ImageContentBlock>());
        var metadata=viewed.StructuredContent?.ToString()??string.Empty;
        Assert.Contains("Portrait",metadata,StringComparison.Ordinal);Assert.DoesNotContain("dataBase64",metadata,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"content\":",metadata,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteEditorialWorkflowUsesOnlyNamesAndOpaqueReferences()
    {
        await using var vault=await TestVault.CreateAsync();await using var client=await Client(vault,false);var observed=new List<string>();
        async Task<CallToolResult> Call(string name,Dictionary<string,object?> args,bool success=true)
        {
            var result=await client.CallToolAsync(name,args);var text=string.Join(" | ",result.Content.OfType<TextContentBlock>().Select(x=>x.Text));
            Assert.True(result.IsError!=true,$"{name} failed: {text} :: {result.StructuredContent}");var json=result.StructuredContent?.ToString()??throw new Xunit.Sdk.XunitException(name+" returned no structured content.");observed.Add(json);
            if(success)Assert.Contains("\"success\":true",json.Replace(" ",string.Empty),StringComparison.OrdinalIgnoreCase);return result;
        }
        await Call("continuity_create",new(){{"mutationToken","wf-continuity"},{"name","Workflow World"},{"defaultTimeZoneId","UTC"}});
        Assert.NotEqual(true,(await client.CallToolAsync("session_set",new Dictionary<string,object?>{{"continuityName","Workflow World"},{"timeAction","Set"},{"currentTime","2026-09-28T12:00:00+00:00"},{"referenceTimeZoneId","UTC"}})).IsError);
        var project=FirstAffected(await Call("entity_create",new(){{"mutationToken","wf-project"},{"entityKind","Project"},{"name","Novel"}}));
        var character=FirstAffected(await Call("entity_create",new(){{"mutationToken","wf-character"},{"entityKind","Character"},{"name","Morgan"},{"fields",new Dictionary<string,object?>{{"birth","2000-02-29"}}}}));
        Assert.NotEqual(true,(await client.CallToolAsync("search",new Dictionary<string,object?>{{"text","Morgan"},{"kinds",new[]{"Character"}}})).IsError);
        var page=await client.CallToolAsync("get",new Dictionary<string,object?>{{"ref",character.Ref}});Assert.NotEqual(true,page.IsError);observed.Add(page.StructuredContent?.ToString()??string.Empty);
        var note=FirstAffected(await Call("note_add",new(){{"mutationToken","wf-note"},{"target",character.Ref},{"title","Voice"},{"body","**Decisive** under pressure."}}));
        await Call("entity_alias_add",new(){{"mutationToken","wf-alias"},{"entity",character.Ref},{"alias","M"}});
        var source=FirstAffected(await Call("source_create",new(){{"mutationToken","wf-source"},{"title","Reference"},{"canonicalUrl","https://example.test/reference"}}));
        await Call("note_source_link",new(){{"mutationToken","wf-note-source"},{"note",note.Ref},{"source",source.Ref}});
        await Call("entity_source_link",new(){{"mutationToken","wf-entity-source"},{"entity",character.Ref},{"source",source.Ref}});
        await Call("tag_apply",new(){{"mutationToken","wf-tag"},{"tag","protagonist"},{"targets",new[]{character.Ref}},{"action","Add"},{"createIfMissing",true}});
        await Call("event_record",new(){{"mutationToken","wf-world-assigned"},{"title","Assigned Event"},{"occurred","2025-01-01"},{"participants",new[]{new Dictionary<string,object?>{{"entity",character.Ref},{"role","witness"}}}},{"projects",new[]{project.Ref}}});
        await Call("event_record",new(){{"mutationToken","wf-world-unassigned"},{"title","Unassigned Event"},{"occurred",new Dictionary<string,object?>{{"kind","Year"},{"value","2024"}}}});
        var local=FirstAffected(await Call("entity_event_add",new(){{"mutationToken","wf-local-event"},{"entity",character.Ref},{"title","Private realization"},{"occurred","2025-02-01"},{"projects",new[]{project.Ref}}}));
        await Call("event_project_apply",new(){{"mutationToken","wf-local-unassign"},{"event",local.Ref},{"projects",new[]{project.Ref}},{"action","Remove"}});
        var projectPage=await client.CallToolAsync("get",new Dictionary<string,object?>{{"ref",project.Ref}});Assert.Contains("Assigned Event",projectPage.StructuredContent?.ToString(),StringComparison.Ordinal);Assert.DoesNotContain("Unassigned Event",projectPage.StructuredContent?.ToString(),StringComparison.Ordinal);
        var timeline=await client.CallToolAsync("timeline_get",new Dictionary<string,object?>{{"limit",50}});Assert.Contains("Assigned Event",timeline.StructuredContent?.ToString(),StringComparison.Ordinal);Assert.Contains("Unassigned Event",timeline.StructuredContent?.ToString(),StringComparison.Ordinal);
        await Call("character_temporal_effect_create",new(){{"mutationToken","wf-effect"},{"character",character.Ref},{"name","Pause"},{"period",new Dictionary<string,object?>{{"kind","KnownRange"},{"lower","2020-01-01"},{"upper","2021-01-01"}}},{"biologicalRate",0d},{"experiencedRate",0d}});
        var age=await client.CallToolAsync("character_age",new Dictionary<string,object?>{{"character",character.Ref}});Assert.NotEqual(true,age.IsError);Assert.Contains("biological",age.StructuredContent?.ToString(),StringComparison.OrdinalIgnoreCase);
        var png=PngBase64();
        var attached=FirstAffected(await Call("image_attach",new(){{"mutationToken","wf-image"},{"entity",character.Ref},{"title","Morgan portrait"},{"image",new Dictionary<string,object?>{{"mediaType","image/png"},{"dataBase64",png}}}}));
        var image=await client.CallToolAsync("image_view",new Dictionary<string,object?>{{"imageRef",attached.Ref},{"size","Thumbnail"}});Assert.Single(image.Content.OfType<ImageContentBlock>());
        var backup=await Call("vault_backup_create",new(){{"mutationToken","wf-backup"},{"purpose","phase 7 workflow"}});Assert.Contains("schemaValid",backup.StructuredContent?.ToString(),StringComparison.OrdinalIgnoreCase);
        var health=await client.CallToolAsync("vault_health",new Dictionary<string,object?>());Assert.NotEqual(true,health.IsError);
        Assert.DoesNotContain("\"lastSuccessfulBackupUtc\":null",health.StructuredContent?.ToString()?.Replace(" ",string.Empty),StringComparison.OrdinalIgnoreCase);
        var disposable=FirstAffected(await Call("entity_create",new(){{"mutationToken","wf-disposable"},{"entityKind","Object"},{"name","Disposable"}}));
        var preview=await client.CallToolAsync("record_delete_preview",new Dictionary<string,object?>{{"ref",disposable.Ref}});Assert.Contains("\"canDelete\":true",preview.StructuredContent?.ToString()?.Replace(" ",string.Empty),StringComparison.OrdinalIgnoreCase);
        var deleted=FirstAffected(await Call("record_soft_delete",new(){{"mutationToken","wf-delete"},{"ref",disposable.Ref},{"expectedVersion",disposable.Version}}));
        await Call("record_restore",new(){{"mutationToken","wf-restore"},{"ref",disposable.Ref},{"expectedVersion",deleted.Version}});
        await Call("entity_create",new(){{"mutationToken","wf-echo-character"},{"entityKind","Character"},{"name","Echo"}});
        await Call("entity_create",new(){{"mutationToken","wf-echo-location"},{"entityKind","Location"},{"name","Echo"}});
        var ambiguous=await Call("note_add",new(){{"mutationToken","wf-ambiguous"},{"target","Echo"},{"body","Ambiguous target"}},false);
        var ambiguousJson=ambiguous.StructuredContent?.ToString()??string.Empty;Assert.Contains("record.ambiguous",ambiguousJson,StringComparison.Ordinal);Assert.Contains("candidates",ambiguousJson,StringComparison.OrdinalIgnoreCase);Assert.Contains("Recovery",ambiguousJson,StringComparison.OrdinalIgnoreCase);
        var all=string.Join('\n',observed);Assert.DoesNotMatch(new Regex("\\\"(?:id|operationId|continuityId|entityId|characterId|sourceId|tagId|recordId)\\\"\\s*:",RegexOptions.IgnoreCase),all);
        Assert.DoesNotContain(vault.DatabasePath,all,StringComparison.OrdinalIgnoreCase);
    }

    private static (string Ref,int Version) FirstAffected(CallToolResult result)
    {
        using var document=JsonDocument.Parse(result.StructuredContent?.ToString()??throw new Xunit.Sdk.XunitException("Missing structured content."));
        var item=document.RootElement.GetProperty("affected")[0];return(item.GetProperty("ref").GetString()!,item.TryGetProperty("version",out var version)&&version.ValueKind!=JsonValueKind.Null?version.GetInt32():1);
    }

    private static string PngBase64()
    {
        using var bitmap=new SKBitmap(2,2);using(var canvas=new SKCanvas(bitmap)){canvas.Clear(SKColors.Teal);canvas.Flush();}
        using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Png,100);return Convert.ToBase64String(encoded.ToArray());
    }

    private static async Task<McpClient> Client(TestVault vault,bool readOnly)
    {
        var dll=TestServer.AssemblyPath;
        var args=new List<string>{dll,"serve","--database",vault.DatabasePath,"--backup-root",vault.StorageRoot,"--client-label","v4-test","--tool-surface","v4"};
        if(readOnly)args.Add("--read-only");
        return await McpClient.CreateAsync(new StdioClientTransport(new(){Name="v4-test",Command="dotnet",Arguments=args,WorkingDirectory=Path.GetDirectoryName(dll)!,ShutdownTimeout=TimeSpan.FromSeconds(10)}));
    }
}
