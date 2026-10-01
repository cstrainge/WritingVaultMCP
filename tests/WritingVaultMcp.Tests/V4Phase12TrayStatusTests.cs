using WritingVault.Tray;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase12TrayStatusTests
{
    private static VaultTaskState Task(string name, bool installed, string state, bool process) =>
        new(name, installed, state, process);

    [Fact]
    public void HealthyRequiresBothOwnedTasksProcessesAndProbes()
    {
        var status = new VaultStatus("Debug",
            Task("Viewer", true, "Running", true),
            Task("Tunnel", true, "Running", true),
            Task("Tray", true, "Running", true), true, true, null);
        Assert.Equal(VaultBadge.Healthy, status.Badge);
        Assert.True(status.Tooltip.Length <= 63);
        var starting = status with { TunnelReady = false };
        Assert.Equal(VaultBadge.Partial, starting.Badge);
        Assert.True(starting.PossibleStartup);
        Assert.Contains("tunnel starting/check", starting.Tooltip);
        Assert.True(starting.Tooltip.Length <= 63);
        Assert.Equal(VaultBadge.Partial, (status with { Problem = "Status probe failed." }).Badge);
    }

    [Fact]
    public void UnknownOrForeignListenerNeverAppearsStopped()
    {
        var unavailable = new VaultStatus("Release",
            Task("Viewer", false, "Missing", false),
            Task("Tunnel", false, "Missing", false),
            Task("Tray", false, "Missing", false), false, false, "Task status unavailable.");
        Assert.Equal(VaultBadge.Partial, unavailable.Badge);
        var foreignListener = unavailable with {
            ViewerTask = Task("Viewer", true, "Ready", false),
            TunnelTask = Task("Tunnel", true, "Ready", false),
            ViewerReachable = true,
            Problem = null
        };
        Assert.Equal(VaultBadge.Partial, foreignListener.Badge);
        Assert.Contains("different process may own", foreignListener.Description);
    }

    [Fact]
    public void InstalledTasksWithNoProcessesOrListenersAreStopped()
    {
        var status = new VaultStatus("Release",
            Task("Viewer", true, "Ready", false),
            Task("Tunnel", true, "Ready", false),
            Task("Tray", true, "Running", true), false, false, null);
        Assert.Equal(VaultBadge.Stopped, status.Badge);
    }

    [Fact]
    public void TunnelHealthAcceptsTheClientsActualSuccessfulResult()
    {
        Assert.True(VaultServiceController.TunnelHealthSucceeded("{\"result\":\"ok\"}"));
        Assert.True(VaultServiceController.TunnelHealthSucceeded("{\"result\":\"pass\"}"));
        Assert.False(VaultServiceController.TunnelHealthSucceeded("{\"result\":\"fail\"}"));
        Assert.False(VaultServiceController.TunnelHealthSucceeded("{\"readyz\":{\"ok\":true}}"));
    }
}
