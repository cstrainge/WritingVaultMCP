using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WritingVault.Client;
using WritingVaultMcp.Application;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase8LiveRefreshTests
{
    [Fact]
    public async Task DedicatedReadOnlyWatcherSeesAnotherMcpConnectionsWriteAndBackendDrains()
    {
        await using var vault=await TestVault.CreateAsync();
        var created=await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Live Refresh","UTC"));
        Assert.True(created.Success);
        var server=TestServer.AssemblyPath;
        Directory.CreateDirectory(vault.StorageRoot);
        var factory=new McpVaultReadClientFactory(new(server,vault.DatabasePath,vault.StorageRoot,ClientLabel:"Phase 8 browser"));
        await using var interactive=await factory.ConnectAsync("Phase 8 interactive");
        await using var watcher=await factory.ConnectAsync("Phase 8 watcher");
        await interactive.SetSessionAsync(new("Live Refresh"));
        await watcher.SetSessionAsync(new("Live Refresh"));
        var firstPaint=await interactive.GetAsync();
        Assert.True(VaultProcessHost.IsBackendRunning(vault.DatabasePath));

        var pending=watcher.ChangesSinceAsync(firstPaint.ObservedRevision,15);
        await using var writer=await WritableClient(vault);
        var selected=await writer.CallToolAsync("session_set",new Dictionary<string,object?>{{"continuityName","Live Refresh"}});
        Assert.NotEqual(true,selected.IsError);
        var write=await writer.CallToolAsync("entity_create",new Dictionary<string,object?>
        {
            ["mutationToken"]="phase8-live-refresh",
            ["entityKind"]="Character",
            ["name"]="Watcher Proof"
        });
        Assert.NotEqual(true,write.IsError);

        var changed=await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(changed.Heartbeat);
        Assert.False(changed.CursorExpired);
        Assert.Contains(changed.Changes,item=>item.Kind.Equals("Character",StringComparison.OrdinalIgnoreCase));
        Assert.Contains((await interactive.SearchAsync(new("Watcher Proof"))).Items,item=>item.Label=="Watcher Proof");

        await writer.DisposeAsync();
        await watcher.DisposeAsync();
        await interactive.DisposeAsync();
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(VaultProcessHost.IsBackendRunning(vault.DatabasePath)&&DateTime.UtcNow<deadline)await Task.Delay(100);
        Assert.False(VaultProcessHost.IsBackendRunning(vault.DatabasePath));
    }

    private static async Task<McpClient> WritableClient(TestVault vault)
    {
        var dll=TestServer.AssemblyPath;
        var args=new[]{dll,"serve","--database",vault.DatabasePath,"--backup-root",vault.StorageRoot,
            "--client-label","Phase 8 external writer","--tool-surface","v4"};
        return await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name="phase8-external-writer",Command="dotnet",Arguments=args,
            WorkingDirectory=Path.GetDirectoryName(dll)!,ShutdownTimeout=TimeSpan.FromSeconds(10)
        }));
    }
}
