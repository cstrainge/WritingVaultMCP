namespace WritingVaultMcp.Tests;

public sealed class V4Phase7DeploymentTests
{
    [Fact]
    public void BackgroundTunnelModeIsNonInteractiveProfileLockedAndActionable()
    {
        var root=RepositoryRoot();var script=File.ReadAllText(Path.Combine(root,"tools","Run-WritingVaultTunnel.ps1"));
        Assert.Contains("[switch] $Background",script,StringComparison.Ordinal);
        Assert.Contains("--tool-surface v4",script,StringComparison.Ordinal);
        Assert.Contains("Background mode requires the saved Windows-encrypted API key",script,StringComparison.Ordinal);
        Assert.Contains("if (-not $Background) { $runArguments += '--open-web-ui' }",script,StringComparison.Ordinal);
        Assert.Contains("Local\\WritingVaultMCP-OpenAITunnel-$profileName",script,StringComparison.Ordinal);
        Assert.Contains("'writing-vault-debug'",script,StringComparison.Ordinal);
        Assert.Contains("'writing-vault'",script,StringComparison.Ordinal);
        Assert.Contains("exit $failureExitCode",script,StringComparison.Ordinal);
        foreach(var code in new[]{10,11,12,20,21,22,30,31,40})Assert.Contains($"{code}",script,StringComparison.Ordinal);
        var batch=File.ReadAllText(Path.Combine(root,"Run-WritingVault-Tunnel.bat"));
        Assert.Contains("BACKGROUND_MODE",batch,StringComparison.Ordinal);Assert.Contains("if not defined BACKGROUND_MODE pause",batch,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V4DocumentationNamesEveryPublishedTool()
    {
        var root=RepositoryRoot();var guide=File.ReadAllText(Path.Combine(root,"docs","V4_TOOLS.md"));
        foreach(var tool in Mcp.V4.V4ContractCatalog.Tools)Assert.Contains($"`{tool.Name}`",guide,StringComparison.Ordinal);
        var installation=File.ReadAllText(Path.Combine(root,"docs","MCP_CLIENT_INSTALLATION.md"));
        Assert.Contains("--tool-surface",installation,StringComparison.Ordinal);Assert.Contains("-Background",installation,StringComparison.Ordinal);
        Assert.Contains("`21`",installation,StringComparison.Ordinal);Assert.Contains("DPAPI",installation,StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"V4_API_PLAN.md")))directory=directory.Parent;
        return directory?.FullName??throw new InvalidOperationException("Could not locate repository root.");
    }
}
