using System.Diagnostics;
using WritingVault.Tray;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase12WindowlessStartupTests
{
    [Theory]
    [InlineData("viewer", "Run-WritingVaultWeb.ps1")]
    [InlineData("tunnel", "Run-WritingVaultTunnel.ps1")]
    public void BackgroundServiceUsesHiddenPowerShellChild(string service, string script)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var start = ServiceProcessHost.CreateStartInfo(service, "Release", root);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Contains(Path.Combine(root, "tools", script), start.ArgumentList);
        Assert.Contains("-Background", start.ArgumentList);
        Assert.DoesNotContain("-DebugBuild", start.ArgumentList);

        var debug = ServiceProcessHost.CreateStartInfo(service, "Debug", root);
        Assert.Contains("-DebugBuild", debug.ArgumentList);
    }
}
