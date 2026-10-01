using System.Diagnostics;

namespace WritingVault.Tray;

internal sealed class VaultTrayContext : ApplicationContext
{
    private readonly VaultServiceController controller;
    private readonly string configuration;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 8_000 };
    private readonly List<ToolStripMenuItem> actionItems = [];
    private readonly string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WritingVault.ico");
    private readonly ToolStripMenuItem statusItem;
    private Icon? currentIcon;
    private VaultStatus? lastStatus;
    private bool busy;
    private bool checking;
    private bool disposed;
    private DateTimeOffset? recoverySince;

    public VaultTrayContext(string projectRoot, string configuration)
    {
        this.configuration = configuration;
        controller = new VaultServiceController(projectRoot, configuration);
        var buildLabel = new ToolStripMenuItem($"Writer's Vault {configuration}") { Enabled = false };
        menu.Items.Add(buildLabel);
        menu.Items.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem($"Open Writer's Vault {configuration}");
        open.Click += (_, _) => OpenViewer();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Command("Start All", "start"));
        menu.Items.Add(Command("Stop All", "stop"));
        menu.Items.Add(Command("Restart All", "restart"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(ServiceMenu("Viewer", "viewer"));
        menu.Items.Add(ServiceMenu("ChatGPT Tunnel", "tunnel"));
        menu.Items.Add(new ToolStripSeparator());
        statusItem = new ToolStripMenuItem("Status: checking…");
        statusItem.Click += async (_, _) => await ShowStatusAsync();
        menu.Items.Add(statusItem);
        var exit = new ToolStripMenuItem("Exit Tray");
        exit.Click += async (_, _) => await ExitTrayAsync();
        menu.Items.Add(exit);

        tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = $"Writer's Vault {configuration} · Checking viewer and tunnel",
            Visible = true
        };
        tray.DoubleClick += (_, _) => OpenViewer();
        SetIcon(VaultBadge.Busy);
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        _ = RefreshAsync();
    }

    private ToolStripMenuItem ServiceMenu(string label, string suffix)
    {
        var item = new ToolStripMenuItem(label);
        item.DropDownItems.Add(Command("Start", $"start-{suffix}"));
        item.DropDownItems.Add(Command("Stop", $"stop-{suffix}"));
        item.DropDownItems.Add(Command("Restart", $"restart-{suffix}"));
        return item;
    }

    private ToolStripMenuItem Command(string label, string action)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += async (_, _) => await RunActionAsync(action);
        actionItems.Add(item);
        return item;
    }

    private async Task RunActionAsync(string action)
    {
        if (busy || disposed) return;
        busy = true;
        actionItems.ForEach(item => item.Enabled = false);
        SetIcon(VaultBadge.Busy);
        statusItem.Text = "Status: starting or recovering…";
        try
        {
            var result = await controller.RunActionAsync(action, CancellationToken.None);
            if (!result.Success)
                MessageBox.Show(result.Message, $"Writer's Vault {configuration} service action failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            actionItems.ForEach(item => item.Enabled = true);
            await RefreshAsync();
        }
    }

    private async Task ShowStatusAsync()
    {
        if (disposed) return;
        await RefreshAsync();
        MessageBox.Show(lastStatus?.Description ?? "The Writer's Vault service status is unavailable.",
            $"Writer's Vault {configuration} status", MessageBoxButtons.OK,
            lastStatus?.Badge == VaultBadge.Healthy ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task ExitTrayAsync()
    {
        if (busy || disposed) return;
        busy = true;
        timer.Stop();
        // Disabling the tray task before exiting prevents its watchdog from
        // interpreting an intentional Exit Tray as a crash. Service tasks stay up.
        var result = await controller.RunActionAsync("disable-tray", CancellationToken.None);
        if (!result.Success)
        {
            busy = false;
            timer.Start();
            MessageBox.Show(result.Message, "Could not exit Writer's Vault tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ExitThread();
    }

    private async Task RefreshAsync()
    {
        if (disposed || checking || busy) return;
        checking = true;
        try
        {
            var snapshot = await controller.ReadStatusAsync(CancellationToken.None);
            if (disposed) return;
            lastStatus = snapshot;
            recoverySince = snapshot.PossibleStartup ? recoverySince ?? DateTimeOffset.UtcNow : null;
            var badge = recoverySince is { } since &&
                DateTimeOffset.UtcNow - since < TimeSpan.FromSeconds(30)
                ? VaultBadge.Busy : snapshot.Badge;
            statusItem.Text = badge switch
            {
                VaultBadge.Healthy => "Status: both healthy",
                VaultBadge.Stopped => "Status: both stopped",
                VaultBadge.Busy => "Status: starting or recovering…",
                _ => "Status: attention needed"
            };
            tray.Text = snapshot.Tooltip;
            SetIcon(badge);
        }
        catch (Exception exception)
        {
            if (disposed) return;
            statusItem.Text = "Status: unavailable";
            tray.Text = $"Writer's Vault {configuration} · Status unavailable";
            SetIcon(VaultBadge.Partial);
            var prefix = configuration == "Debug" ? "Writing Vault Debug" : "Writing Vault";
            lastStatus = new(
                configuration,
                new($"{prefix} Viewer", false, "Unknown", false),
                new($"{prefix} ChatGPT Tunnel", false, "Unknown", false),
                new($"{prefix} Tray", false, "Unknown", false),
                false, false, exception.Message);
        }
        finally { checking = false; }
    }

    private void SetIcon(VaultBadge badge)
    {
        var replacement = VaultIcon.Create(iconPath, badge);
        tray.Icon = replacement;
        currentIcon?.Dispose();
        currentIcon = replacement;
    }

    private void OpenViewer()
    {
        try
        {
            Process.Start(new ProcessStartInfo(controller.ViewerUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Could not open Writer's Vault", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            timer.Stop();
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            currentIcon?.Dispose();
            menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
