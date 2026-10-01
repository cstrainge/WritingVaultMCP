using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

internal sealed class TestVault : IAsyncDisposable
{
    private TestVault(string directory, string databasePath)
    {
        Directory = directory;
        DatabasePath = databasePath;
        Factory = new AccessConnectionFactory(databasePath);
        Schema = new AccessSchemaVerifier(Factory);
        Integrity = new AccessIntegrityVerifier(Factory);
        StorageRoot = Path.Combine(directory, "backup-data");
        Changes = new VaultChangeNotifier();
        Cursors = new V4CursorCodec();
        Coordinator = new VaultWriteCoordinator(Factory, new SchemaWriteGate(Schema), changeNotifier: Changes);
        Service = new AccessVaultService(
            Factory,
            Coordinator,
            new WritingVaultStorageOptions(StorageRoot));
    }

    public string Directory { get; }
    public string DatabasePath { get; }
    public string StorageRoot { get; }
    public AccessConnectionFactory Factory { get; }
    public AccessSchemaVerifier Schema { get; }
    public AccessIntegrityVerifier Integrity { get; }
    public AccessVaultService Service { get; }
    public VaultWriteCoordinator Coordinator { get; }
    public VaultChangeNotifier Changes { get; }
    public V4CursorCodec Cursors { get; }

    public (AccessV4ReadService Reads, VaultSessionContext Session, VaultReferenceService References) V4(string client = "test")
    {
        var session = new VaultSessionContext { ClientLabel = client };
        var references = new VaultReferenceService(Factory, Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(Factory, Coordinator, references));
        return (new AccessV4ReadService(Factory, Coordinator, Service, references, targets,
            new VaultMcpResultMapper(references), session, Cursors, Changes), session, references);
    }

    public AccessV4PageSnapshotCapture EnableAutomaticPageSnapshots()
    {
        var references = new VaultReferenceService(Factory, Coordinator);
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(Factory, Coordinator, references));
        var capture = new AccessV4PageSnapshotCapture(Factory, Coordinator, Service,
            references, targets, Cursors, Changes);
        Coordinator.ConfigurePageSnapshotCapture(capture.CaptureAffectedAsync);
        return capture;
    }

    public static async Task<TestVault> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WritingVaultMcpTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.accdb");
        AccessDatabaseFileInitializer.Create(path);
        var factory = new AccessConnectionFactory(path);
        var result = await new AccessSchemaMigrator(factory, TimeProvider.System).MigrateAsync(false);
        Assert.True(result.Verification.IsValid);
        return new TestVault(directory, path);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        Assert.False(File.Exists(Path.ChangeExtension(DatabasePath, ".laccdb")));
        Assert.False(File.Exists(Path.ChangeExtension(DatabasePath, ".ldb")));
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}
