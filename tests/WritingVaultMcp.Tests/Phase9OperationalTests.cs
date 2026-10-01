using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Mcp;

namespace WritingVaultMcp.Tests;

public sealed class Phase9OperationalTests
{
    [Fact]
    public async Task StructuredDiagnosticsContainCorrelationButExcludeStoryContentAndPaths()
    {
        await using var vault = await TestVault.CreateAsync();
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var secret = "private story text that must not be logged";
            var operation = Guid.NewGuid().ToString();
            var result = await vault.Service.CreateContinuityAsync(new(operation, "Diagnostic Canon", "UTC", secret));
            Assert.True(result.Success, result.Message);
            var log = captured.ToString();
            Assert.Contains("\"event\":\"mutation.committed\"", log, StringComparison.Ordinal);
            Assert.Contains(operation.ToLowerInvariant(), log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("continuity.create", log, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
            Assert.DoesNotContain(vault.DatabasePath, log, StringComparison.OrdinalIgnoreCase);
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public async Task DatabasePathPolicyCanonicalizesLocalFilesAndRejectsOtherTargets()
    {
        await using var vault = await TestVault.CreateAsync();
        Assert.Equal(Path.GetFullPath(vault.DatabasePath), VaultProcessHost.ValidatePath(vault.DatabasePath));
        Assert.Throws<InvalidDataException>(() => VaultProcessHost.ValidatePath(Path.Combine(vault.Directory, "vault.mdb")));
        var network = Assert.Throws<InvalidDataException>(() => VaultProcessHost.ValidatePath(@"\\server\share\vault.accdb"));
        Assert.Contains("local filesystem", network.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PhysicalVolumeIdentityIgnoresWindowsMountPointAliases()
    {
        await using var vault = await TestVault.CreateAsync();
        var canonical = LocalPathIdentity.Canonicalize(vault.DatabasePath);
        Assert.StartsWith(@"\\?\Volume{", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(canonical));
        Assert.Equal(VaultProcessHost.Identity(vault.DatabasePath), VaultProcessHost.Identity(canonical));
    }

    [Fact]
    public async Task HealthReportsSchemaQueueBackupAndToolSurfaceSignals()
    {
        await using var vault = await TestVault.CreateAsync();
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var session = new VaultSessionContext();
        var reads = new SemanticVaultReadTools(vault.Service, vault.Schema, vault.Integrity, vault.Coordinator,
            new WritingVaultStorageOptions(vault.StorageRoot), session, references, new VaultMcpResultMapper(references));

        var health = await reads.Health();
        Assert.True(health.Ready);
        Assert.Equal("3.0", health.ToolSurfaceVersion);
        Assert.Equal(0, health.PendingWrites);
        Assert.Empty(health.SchemaIssues);
        Assert.Empty(health.IntegrityIssues);
    }
}
