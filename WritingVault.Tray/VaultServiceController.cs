using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace WritingVault.Tray;

internal sealed record VaultTaskState(string Name, bool Installed, string State, bool ProcessRunning)
{
    public bool Running => Installed && State.Equals("Running", StringComparison.OrdinalIgnoreCase);
}

internal sealed record VaultStatus(
    string Configuration,
    VaultTaskState ViewerTask,
    VaultTaskState TunnelTask,
    VaultTaskState TrayTask,
    bool ViewerReachable,
    bool TunnelReady,
    string? Problem)
{
    public bool ViewerHealthy => ViewerTask.Running && ViewerTask.ProcessRunning && ViewerReachable;
    public bool TunnelHealthy => TunnelTask.Running && TunnelTask.ProcessRunning && TunnelReady;
    public bool PossibleStartup => Problem is null &&
        (ViewerTask.Running && ViewerTask.ProcessRunning && !ViewerReachable ||
         TunnelTask.Running && TunnelTask.ProcessRunning && !TunnelReady);
    public VaultBadge Badge => Problem is null && ViewerHealthy && TunnelHealthy ? VaultBadge.Healthy :
        Problem is null && !ViewerTask.Running && !TunnelTask.Running &&
        !ViewerTask.ProcessRunning && !TunnelTask.ProcessRunning &&
        !ViewerReachable && !TunnelReady &&
        ViewerTask.Installed && TunnelTask.Installed
            ? VaultBadge.Stopped : VaultBadge.Partial;

    public string Tooltip => $"Vault {Configuration}: viewer {Label(ViewerTask, ViewerHealthy)}, tunnel {Label(TunnelTask, TunnelHealthy)}";

    private static string Label(VaultTaskState task, bool healthy) => healthy ? "OK" :
        task.Running && task.ProcessRunning ? "starting/check" :
        task.Installed && !task.Running && !task.ProcessRunning ? "stopped" : "check";

    public string Description =>
        $"Build: {Configuration}\nViewer task: {ViewerTask.State}\nViewer process: {(ViewerTask.ProcessRunning ? "running" : "not running")}\nViewer URL: {(ViewerReachable ? "reachable" : "unreachable")}\n" +
        $"Tunnel task: {TunnelTask.State}\nTunnel process: {(TunnelTask.ProcessRunning ? "running" : "not running")}\nTunnel control plane: {(TunnelReady ? "ready" : "not ready")}\n" +
        $"Tray task: {TrayTask.State}" +
        (Problem is null ? string.Empty : $"\n\n{Problem}") +
        (ViewerReachable && !ViewerTask.ProcessRunning ? "\nA different process may own the viewer URL." : string.Empty) +
        (!ViewerTask.Installed || !TunnelTask.Installed ? "\nInstall the missing task with Configure-WritingVault-Startup.bat." : string.Empty) +
        (ViewerTask.Running && !ViewerReachable ? "\nThe viewer task is running but its fixed URL is unavailable. Check the viewer log." : string.Empty) +
        (TunnelTask.Running && !TunnelReady ? "\nThe tunnel task is running but its control plane is not ready. Check the tunnel log and saved credential." : string.Empty);
}

internal sealed class VaultServiceController(string projectRoot, string configuration)
{
    public string ViewerUrl => configuration == "Debug" ? "http://127.0.0.1:5285" : "http://127.0.0.1:5284";
    private string TaskPrefix => configuration == "Debug" ? "Writing Vault Debug" : "Writing Vault";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly string startupScript = Path.Combine(projectRoot, "tools", "Configure-WritingVaultStartup.ps1");
    private readonly string tunnelClient = Path.Combine(projectRoot, "mcp-tunnel", "tunnel-client.exe");
    private readonly string runtimeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WritingVaultMCP", "Runtime", configuration);

    public static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "Configure-WritingVaultStartup.ps1")) &&
                File.Exists(Path.Combine(directory.FullName, "WritingVault.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("The tray could not locate its Writer's Vault installation.");
    }

    public async Task<(bool Success, string Message)> RunActionAsync(string action, CancellationToken token)
    {
        if (!File.Exists(startupScript)) return (false, "The Writer's Vault startup controller is missing.");
        try
        {
            var result = await RunProcessAsync("powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
                    "-File", startupScript, action, "-Configuration", configuration], TimeSpan.FromSeconds(75), token);
            var message = (result.ExitCode == 0 ? result.Output : result.Error).Trim();
            return (result.ExitCode == 0,
                string.IsNullOrWhiteSpace(message) ? result.ExitCode == 0 ? "Done." : "The startup action failed." : message);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return (false, exception.Message);
        }
    }

    public async Task<VaultStatus> ReadStatusAsync(CancellationToken token)
    {
        var tasks = ReadTasksAsync(token);
        var viewer = ProbeViewerAsync(token);
        var tunnel = ProbeTunnelAsync(token);
        await Task.WhenAll(tasks, viewer, tunnel);
        var (taskStates, problem) = tasks.Result;
        return new(
            configuration,
            Find(taskStates, $"{TaskPrefix} Viewer"),
            Find(taskStates, $"{TaskPrefix} ChatGPT Tunnel"),
            Find(taskStates, $"{TaskPrefix} Tray"),
            viewer.Result, tunnel.Result, problem);
    }

    private static VaultTaskState Find(IReadOnlyList<VaultTaskState> tasks, string name) =>
        tasks.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        new(name, false, "Missing", false);

    private async Task<(IReadOnlyList<VaultTaskState> Tasks, string? Problem)> ReadTasksAsync(CancellationToken token)
    {
        try
        {
            var result = await RunProcessAsync("powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
                    "-File", startupScript, "status", "-Configuration", configuration], TimeSpan.FromSeconds(8), token);
            if (result.ExitCode != 0) return ([], "The scheduled-task status check failed.");
            using var json = JsonDocument.Parse(result.Output);
            if (!json.RootElement.TryGetProperty("tasks", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return ([], "The scheduled-task status was incomplete.");
            return (rows.EnumerateArray().Select(row => new VaultTaskState(
                row.GetProperty("name").GetString() ?? string.Empty,
                row.GetProperty("installed").GetBoolean(),
                row.GetProperty("state").GetString() ?? "Unknown",
                row.TryGetProperty("processRunning", out var running) && running.GetBoolean())).ToArray(), null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return ([], $"Scheduled-task status is unavailable: {exception.Message}");
        }
    }

    private async Task<bool> ProbeViewerAsync(CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(ViewerUrl + "/", token);
            return response.StatusCode == HttpStatusCode.OK &&
                response.Headers.TryGetValues("X-WritingVault-Build", out var builds) &&
                builds.Contains(configuration, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private async Task<bool> ProbeTunnelAsync(CancellationToken token)
    {
        var urlFile = Path.Combine(runtimeRoot, "tunnel-health.url");
        var pidFile = Path.Combine(runtimeRoot, "tunnel.pid");
        if (!File.Exists(tunnelClient) || !File.Exists(urlFile) || !File.Exists(pidFile)) return false;
        try
        {
            var result = await RunProcessAsync(tunnelClient,
                ["health", "--json", "--url-file", urlFile, "--pid-file", pidFile, "--require-control-plane-poll"],
                TimeSpan.FromSeconds(5), token);
            if (result.ExitCode != 0) return false;
            return TunnelHealthSucceeded(result.Output);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool TunnelHealthSucceeded(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.TryGetProperty("result", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            (value.GetString() is "ok" or "pass");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("A Writer's Vault helper could not start.");
        var output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var error = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("A Writer's Vault helper timed out.");
        }
    }
}
