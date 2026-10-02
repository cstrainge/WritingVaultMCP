using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using WritingVaultMcp.Infrastructure.Access.Schema;

namespace WritingVaultMcp.Mcp.V4;

public sealed record V4ServerCapabilities(bool ReadOnly = false)
{
    public V4CapabilitiesResult Get()
    {
        var tools = V4ContractCatalog.Tools.Where(t => !ReadOnly || t.Access == V4ToolAccess.Read)
            .Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            V4ContractCatalog.ExportSchemas().ToJsonString() + "\n" + string.Join('\n', tools))));
        return new(V4ContractCatalog.SurfaceVersion,
            typeof(V4ServerCapabilities).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            AccessSchemaDefinition.MigrationId, hash, ReadOnly, tools,
            new Dictionary<string, bool> {
                ["sharedGlobalMemories"] = true, ["sharedContinuityMemories"] = true,
                ["memorySearch"] = true, ["memoryEdit"] = !ReadOnly, ["memoryDelete"] = !ReadOnly,
                ["pinnedMemoryStartup"] = true, ["pinnedMemorySessionDelivery"] = true,
                ["imageRead"] = true, ["imageImport"] = !ReadOnly, ["recordSnapshots"] = true,
                ["tentativeEventFacts"] = true, ["tentativeProjectBoundaries"] = true
            },
            "Global pinned bodies are sent in MCP initialize instructions. All global and selected-continuity pinned bodies are also returned by vault_health, session_set, and session_get. Clients control whether initialize instructions enter model context; the server cannot force a host to load them.",
            "Compare serverBuild and catalogFingerprint with cached declarations. If different, reconnect and refresh tools/list. This response describes the running connection, not the client's cached tool list.");
    }
}
