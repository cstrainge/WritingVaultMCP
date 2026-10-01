using System.Diagnostics;
using WritingVault.Client;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase11DiagnosticsTests
{
    [Fact]
    public async Task ReadClientConfigurationErrorsDoNotExposePrivatePaths()
    {
        var privateName = "private-sentinel-" + Guid.NewGuid().ToString("N");
        var missing = Path.Combine(Path.GetTempPath(), privateName + ".accdb");
        var factory = new McpVaultReadClientFactory(new(
            TestServer.AssemblyPath, missing, Path.GetTempPath()));
        var error = await Assert.ThrowsAsync<VaultReadException>(
            () => factory.ConnectAsync("diagnostic-probe"));
        Assert.Equal("configuration.invalid", error.Code);
        Assert.DoesNotContain(privateName, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missing, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupFailureMessagesDoNotRevealConfiguredDatabasePaths()
    {
        var server = TestServer.AssemblyPath;
        var root = new DirectoryInfo(Path.GetDirectoryName(server)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WritingVaultMcp.csproj")))
            root = root.Parent;
        var repository = root?.FullName ?? throw new InvalidOperationException("Repository root missing.");
        var viewer = Path.Combine(repository, "WritingVault.Web", "bin", "Debug", "net10.0-windows", "WritingVault.Web.dll");
        var privateName = "private-sentinel-" + Guid.NewGuid().ToString("N");
        var missingDatabase = Path.Combine(Path.GetTempPath(), privateName + ".accdb");
        var backupRoot = Path.Combine(Path.GetTempPath(), "writing-vault-diagnostic-probe");

        var serverFailure = await RunAsync(server, "serve", "--database", missingDatabase,
            "--backup-root", backupRoot);
        Assert.NotEqual(0, serverFailure.ExitCode);
        Assert.Contains("required local file", serverFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateName, serverFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingDatabase, serverFailure.Stderr, StringComparison.OrdinalIgnoreCase);

        var viewerFailure = await RunAsync(viewer, "--server", server, "--database", missingDatabase,
            "--backup-root", backupRoot);
        Assert.Equal(64, viewerFailure.ExitCode);
        Assert.Contains("viewer configuration failed", viewerFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateName, viewerFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingDatabase, viewerFailure.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownAdministrativeTokensAreAbsentFromDiagnostics()
    {
        var secretToken = "private-sentinel-" + Guid.NewGuid().ToString("N");
        var missingDatabase = Path.Combine(Path.GetTempPath(), "writing-vault-missing.accdb");
        var groupFailure = await RunAsync(TestServer.AssemblyPath,
            secretToken, "status", "--database", missingDatabase);
        var actionFailure = await RunAsync(TestServer.AssemblyPath,
            "schema", secretToken, "--database", missingDatabase);
        Assert.NotEqual(0, groupFailure.ExitCode);
        Assert.NotEqual(0, actionFailure.ExitCode);
        Assert.DoesNotContain(secretToken, groupFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretToken, actionFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", groupFailure.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", actionFailure.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Stderr)> RunAsync(string assembly, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the diagnostic probe.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new Xunit.Sdk.XunitException("The diagnostic probe did not exit.");
        }
        _ = await stdout;
        return (process.ExitCode, await stderr);
    }
}
