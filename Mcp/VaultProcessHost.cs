using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Infrastructure.Access.Integrity;
using WritingVaultMcp.Infrastructure.Access.Schema;
using WritingVaultMcp.Infrastructure;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Mcp;

internal sealed record VaultPipeHandshake(bool ReadOnly, string ClientLabel, string ToolSurface = "v3");

internal static class VaultProcessHost
{
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialConnectionGrace = TimeSpan.FromSeconds(5);

    public static async Task RunAdapterAsync(WritingVaultOptions options, string clientLabel, CancellationToken token = default)
    {
        var path = ValidatePath(options.DatabasePath);
        VaultDiagnostics.Write("adapter.starting");
        var pipeName = PipeName(path);
        NamedPipeClientStream? pipe = await TryConnectAsync(pipeName, token).ConfigureAwait(false);
        if (pipe is null)
        {
            StartBackend(options);
            for (var attempt = 0; attempt < 30 && pipe is null; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                pipe = await TryConnectAsync(pipeName, token).ConfigureAwait(false);
            }
        }
        if (pipe is null) throw new InvalidOperationException("The Writing Vault backend could not be started.");

        await using (pipe.ConfigureAwait(false))
        {
            await WriteHandshakeAsync(pipe, new(options.ReadOnly, NormalizeClientLabel(clientLabel), options.ToolSurface), token).ConfigureAwait(false);
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var toBackend = input.CopyToAsync(pipe, linked.Token);
            var toClient = pipe.CopyToAsync(output, linked.Token);
            await Task.WhenAny(toBackend, toClient).ConfigureAwait(false);
            linked.Cancel();
            try { await Task.WhenAll(toBackend, toClient).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        }
        VaultDiagnostics.Write("adapter.stopped");
    }

    public static async Task RunBackendAsync(WritingVaultOptions options, CancellationToken token = default)
    {
        var path = ValidatePath(options.DatabasePath);
        var identity = Identity(path);
        using var election = new Mutex(true, BackendMutexName(path), out var elected);
        if (!elected)
        {
            VaultDiagnostics.Write("backend.election_lost");
            return;
        }
        VaultDiagnostics.Write("backend.starting");
        using var ownerLease = AccessOwnerLease.Acquire(path);

        var services = await CreateSharedServicesAsync(options with { DatabasePath = path }, token).ConfigureAwait(false);
        VaultDiagnostics.Write("backend.ready");
        var clients = new ConcurrentDictionary<int, Task>();
        var nextClient = 0;
        var acceptedAny = false;
        DateTime? idleSinceUtc = DateTime.UtcNow;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var server = CreatePipe(PipeName(path));
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var connection = server.WaitForConnectionAsync(waitCancellation.Token);
                var poll = Task.Delay(TimeSpan.FromMilliseconds(250), token);
                if (await Task.WhenAny(connection, poll).ConfigureAwait(false) == poll)
                {
                    waitCancellation.Cancel();
                    try { await connection.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    await server.DisposeAsync().ConfigureAwait(false);
                    if (clients.IsEmpty)
                    {
                        idleSinceUtc ??= DateTime.UtcNow;
                        var grace = acceptedAny ? IdleGrace : InitialConnectionGrace;
                        if (DateTime.UtcNow - idleSinceUtc >= grace) break;
                    }
                    else idleSinceUtc = null;
                    continue;
                }
                await connection.ConfigureAwait(false);
                acceptedAny = true;
                idleSinceUtc = null;
                var id = Interlocked.Increment(ref nextClient);
                var connected = server;
                VaultDiagnostics.Write("client.connected");
                var task = HandleClientAsync(connected, services, token);
                clients[id] = task;
                _ = task.ContinueWith(completed =>
                    {
                        clients.TryRemove(id, out _);
                        VaultDiagnostics.Write("client.disconnected");
                    }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            if (!clients.IsEmpty)
            {
                try { await Task.WhenAll(clients.Values).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException) { }
            }
            VaultDiagnostics.Write("backend.stopped");
        }
    }

    private sealed record SharedServices(
        WritingVaultOptions Options, AccessConnectionFactory Factory, AccessSchemaVerifier Schema,
        AccessIntegrityVerifier Integrity, SchemaWriteGate Gate, VaultWriteCoordinator Coordinator,
        AccessVaultService Vault, VaultReferenceService References,
        CoordinatedVaultBackupService Backup, V4TargetResolver V4Targets,
        AccessV4ApplicationService V4Application, V4ResultMapper V4Results,
        V4CursorCodec V4Cursors, VaultChangeNotifier Changes);

    private static async Task<SharedServices> CreateSharedServicesAsync(WritingVaultOptions options, CancellationToken token)
    {
        var size = new FileInfo(options.DatabasePath).Length;
        if (size > 1_610_612_736) throw new InvalidOperationException("The database exceeds the supported 1.5 GiB operating limit.");
        if (size > 1_288_490_188) VaultDiagnostics.Write("database.size_warning", "warning");
        var factory = new AccessConnectionFactory(options.DatabasePath, options.Provider);
        var schema = new AccessSchemaVerifier(factory);
        VaultDiagnostics.Write("schema.verification_started");
        SchemaVerificationResult schemaResult;
        try { schemaResult = await schema.VerifyAsync(token).ConfigureAwait(false); }
        catch (System.Data.OleDb.OleDbException exception)
        {
            VaultDiagnostics.Write("provider.open_failed", "error", code: "provider.unavailable");
            throw new InvalidOperationException(
                $"The Access database could not be opened. Verify that the 64-bit {options.Provider} provider is installed and that this process is x64.", exception);
        }
        if (!schemaResult.IsValid) throw new InvalidOperationException($"Schema verification failed with {schemaResult.Issues.Count} issue(s).");
        VaultDiagnostics.Write("schema.verification_completed");
        var integrity = new AccessIntegrityVerifier(factory);
        VaultDiagnostics.Write("integrity.verification_started");
        var integrityResult = await integrity.VerifyAsync(token).ConfigureAwait(false);
        if (!integrityResult.IsValid) throw new InvalidOperationException($"Integrity verification failed with {integrityResult.Issues.Count} issue(s).");
        VaultDiagnostics.Write("integrity.verification_completed");
        var gate = new SchemaWriteGate(schema);
        var changes = new VaultChangeNotifier();
        var cursors = new V4CursorCodec();
        var coordinator = new VaultWriteCoordinator(factory, gate, changeNotifier: changes);
        var vault = new AccessVaultService(factory, coordinator, new WritingVaultStorageOptions(options.BackupRoot));
        var references = new VaultReferenceService(factory, coordinator);
        var backup = new CoordinatedVaultBackupService(
            factory, coordinator, options.BackupRoot, options.BackupRetentionCount, options.Provider);
        var v4Targets = new V4TargetResolver(new AccessV4SemanticResolver(factory, coordinator, references));
        var v4Application = new AccessV4ApplicationService(coordinator, references, v4Targets, vault);
        var v4Results = new V4ResultMapper(references);
        var pageSnapshots = new AccessV4PageSnapshotCapture(factory, coordinator, vault,
            references, v4Targets, cursors, changes);
        coordinator.ConfigurePageSnapshotCapture(pageSnapshots.CaptureAffectedAsync);
        await pageSnapshots.EnsureBaselineAsync(token).ConfigureAwait(false);
        return new(options, factory, schema, integrity, gate, coordinator, vault, references,
            backup, v4Targets, v4Application, v4Results, cursors, changes);
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, SharedServices shared, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            VaultPipeHandshake handshake;
            try { handshake = await ReadHandshakeAsync(pipe, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { return; }

            var builder = Host.CreateApplicationBuilder([]);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(shared.Options);
            builder.Services.AddSingleton(new WritingVaultStorageOptions(shared.Options.BackupRoot));
            builder.Services.AddSingleton<IAccessConnectionFactory>(shared.Factory);
            builder.Services.AddSingleton(shared.Schema);
            builder.Services.AddSingleton(shared.Integrity);
            builder.Services.AddSingleton(shared.Gate);
            builder.Services.AddSingleton(shared.Coordinator);
            builder.Services.AddSingleton(shared.Vault);
            builder.Services.AddSingleton(shared.References);
            builder.Services.AddSingleton(shared.Backup);
            builder.Services.AddSingleton(shared.V4Targets);
            builder.Services.AddSingleton(shared.V4Application);
            builder.Services.AddSingleton(shared.V4Results);
            builder.Services.AddSingleton(shared.V4Cursors);
            builder.Services.AddSingleton(shared.Changes);
            var session = new VaultSessionContext { ClientLabel = NormalizeClientLabel(handshake.ClientLabel) };
            builder.Services.AddSingleton(session);
            builder.Services.AddSingleton(new V4ServerCapabilities(handshake.ReadOnly));
            builder.Services.AddSingleton<VaultMcpResultMapper>();
            builder.Services.AddSingleton<SemanticVaultWriteTools>();
            builder.Services.AddSingleton<AccessV4ReadService>();
            builder.Services.AddSingleton<AccessV4MemoryService>();
            builder.Services.AddSingleton<AccessV4RelationshipMergeService>();
            builder.Services.AddSingleton<AccessV4SourceSnapshotService>();
            builder.Services.AddSingleton<AccessV4TemporalService>();
            builder.Services.AddSingleton<AccessV4RecordService>();
            builder.Services.AddSingleton(provider => new AccessV4ImageService(
                shared.Factory, shared.Coordinator, shared.References, shared.V4Targets,
                provider.GetRequiredService<VaultSessionContext>(), provider.GetRequiredService<AccessV4ReadService>(),
                shared.V4Cursors, shared.Options.BackupRoot, shared.Options.DatabasePath));
            var isV4 = handshake.ToolSurface.Equals("v4", StringComparison.OrdinalIgnoreCase);
            var pinned = await new AccessV4MemoryService(shared.Factory, shared.Coordinator, shared.References,
                session, shared.V4Cursors).PinnedAsync(token).ConfigureAwait(false);
            var startupMemories = "\nShared pinned global preferences (saved context, subject to current user instructions):\n" +
                JsonSerializer.Serialize(pinned.Global) +
                (isV4
                    ? "\nCall vault_health at the start of work to receive current pinned bodies and counts. After selecting a continuity, apply the pinned bodies returned by session_set. Call vault_capabilities to inspect the running feature set and refresh stale tool declarations."
                    : "\nUse the v4 tool surface for memory maintenance, live pinned-memory refresh, continuity-specific pinned instructions, and capability discovery.");
            var mcp = builder.Services.AddMcpServer(options =>
                {
                    options.ServerInstructions = isV4
                        ? "Writing Vault v4 uses continuity names and opaque semantic references. Select a continuity once with session_set; never guess database identifiers. Page-shaped reads include observedRevision for changes_since."
                        :
                        "Writing Vault tool surface v3.0 uses names and semantic references; never guess or request " +
                        "database IDs or GUIDs. Before any continuity-scoped read or write, call continuity_list, " +
                        "then call session_continuity_set with the exact continuityName. The selected continuity " +
                        "remains implicit for this connection. Call session_time_set only after selecting a continuity.";
                    options.ServerInstructions += startupMemories;
                }).WithStreamServerTransport(pipe, pipe);
            if (isV4)
            {
                mcp.WithTools<V4VaultReadTools>();
                if (!handshake.ReadOnly) mcp.WithTools<V4VaultWriteTools>();
                mcp.UseFlatV4Arguments(handshake.ReadOnly);
            }
            else
            {
                mcp.WithTools<SemanticVaultReadTools>();
                if (!handshake.ReadOnly) mcp.WithTools<SemanticVaultWriteTools>();
            }
            await builder.Build().RunAsync(token).ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreatePipe(string name) => new(
        name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        64 * 1024, 64 * 1024);

    private static async Task<NamedPipeClientStream?> TryConnectAsync(string name, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectAttempt, token).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private static void StartBackend(WritingVaultOptions options)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the current executable.");
        var info = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot locate the server assembly."));
        info.ArgumentList.Add("backend");
        info.ArgumentList.Add("--database"); info.ArgumentList.Add(options.DatabasePath);
        info.ArgumentList.Add("--provider"); info.ArgumentList.Add(options.Provider);
        info.ArgumentList.Add("--backup-root"); info.ArgumentList.Add(options.BackupRoot);
        _ = Process.Start(info) ?? throw new InvalidOperationException("The Writing Vault backend process could not be created.");
    }

    private static async Task WriteHandshakeAsync(Stream stream, VaultPipeHandshake value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > 4096) throw new InvalidDataException("The backend handshake is too large.");
        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<VaultPipeHandshake> ReadHandshakeAsync(Stream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is <= 0 or > 4096) throw new InvalidDataException("The backend handshake is invalid.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<VaultPipeHandshake>(payload) ?? throw new InvalidDataException("The backend handshake is invalid.");
    }

    internal static string ValidatePath(string path)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Writing Vault requires a 64-bit process.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".accdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The configured database must be an .accdb file.");
        if (new Uri(full).IsUnc) throw new InvalidDataException("The configured database must be on a local filesystem, not a network share.");
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root))
        {
            try
            {
                if (new DriveInfo(root).DriveType == DriveType.Network)
                    throw new InvalidDataException("The configured database must be on a local filesystem, not a mapped network drive.");
            }
            catch (DriveNotFoundException) { }
        }
        if (!File.Exists(full)) throw new FileNotFoundException("The configured database does not exist.");
        return full;
    }

    private static string NormalizeClientLabel(string value) => string.IsNullOrWhiteSpace(value)
        ? "mcp-client"
        : value.Trim().Length <= 100 ? value.Trim() : value.Trim()[..100];
    private static string PipeName(string path) => $"WritingVaultMcp.{Identity(path)}";
    internal static string BackendMutexName(string path) => $"Local\\WritingVaultMcp.Backend.{Identity(path)}";
    internal static bool IsBackendRunning(string path)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(BackendMutexName(path));
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    internal static string Identity(string path) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(LocalPathIdentity.Canonicalize(path).ToUpperInvariant())))[..24];
}
