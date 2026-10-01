using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace WritingVaultMcp.Mcp.V4;

/// <summary>
/// Keeps the public v4 wire shape identical to the frozen contract.  The MCP
/// SDK represents a single record parameter as { request: { ... } }; v4 exposes
/// the record's properties directly because the extra wrapper is needless
/// model-facing ceremony and was explicitly excluded from the Phase 1 schema.
/// </summary>
internal static class V4McpContractFilters
{
    public static IMcpServerBuilder UseFlatV4Arguments(this IMcpServerBuilder builder, bool readOnlyConnection = false)
    {
        var names = V4ContractCatalog.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var mutationNames = V4ContractCatalog.Tools
            .Where(tool => tool.Access == V4ToolAccess.Write)
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var definitions=V4ContractCatalog.Tools.ToDictionary(tool=>tool.Name,StringComparer.Ordinal);
        var published=V4ContractCatalog.ExportSchemas()["tools"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(tool=>tool["name"]!.GetValue<string>(),tool=>JsonDocument.Parse(tool["inputSchema"]!.ToJsonString()).RootElement.Clone(),StringComparer.Ordinal);
        var publishedOutputs=V4ContractCatalog.ExportSchemas()["tools"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(tool=>tool["name"]!.GetValue<string>(),tool=>JsonDocument.Parse(tool["outputSchema"]!.ToJsonString()).RootElement.Clone(),StringComparer.Ordinal);
        return builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                foreach (var tool in result.Tools.Where(tool => names.Contains(tool.Name)))
                {
                    tool.InputSchema=published[tool.Name];
                    tool.OutputSchema=publishedOutputs[tool.Name];
                    tool.Description=definitions[tool.Name].Description;
                }
                return result;
            });
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var name = context.Params?.Name ?? string.Empty;
                if (readOnlyConnection && mutationNames.Contains(name))
                    throw new ModelContextProtocol.McpException("read_only: This connection has no mutation capability. Reconnect through an explicitly write-enabled client to make changes.");
                if (names.Contains(name))
                {
                    var arguments = context.Params?.Arguments ?? new Dictionary<string, JsonElement>();
                    if (arguments.ContainsKey("request"))
                        throw new ModelContextProtocol.McpException("V4 arguments are direct properties; remove the obsolete request wrapper and retry.");
                    context.Params!.Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["request"] = JsonSerializer.SerializeToElement(arguments)
                    };
                }
                return await next(context, cancellationToken).ConfigureAwait(false);
            });
        });
    }

}
