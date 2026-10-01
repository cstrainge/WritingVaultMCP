using System.Data;
using System.Data.OleDb;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WritingVaultMcp.Infrastructure;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed record VaultMutationResult(
    bool Success,
    string Code,
    string? ResourceType = null,
    string? ResourceKey = null,
    int? Version = null,
    bool Replayed = false,
    string? Message = null,
    bool Retryable = false,
    IReadOnlyList<VaultMutationCandidate>? Candidates = null);

public sealed record VaultMutationCandidate(
    string ResourceType, string Reference, string Label, string? Context = null);

internal sealed record VaultMutationOutcome(
    string ResourceType,
    string ResourceKey,
    int? Version,
    string Action,
    object? Change,
    int? VersionBefore = null);

internal sealed class VaultWriteContext(OleDbConnection connection, OleDbTransaction transaction, string operationId)
{
    private readonly List<Action> rollbackActions = [];
    public OleDbConnection Connection { get; } = connection;
    public OleDbTransaction Transaction { get; } = transaction;
    public string OperationId { get; } = operationId;

    public AccessCommand Command(string sql) => new(Connection, sql, Transaction);
    public void OnRollback(Action action) => rollbackActions.Add(action ?? throw new ArgumentNullException(nameof(action)));
    internal void Complete() => rollbackActions.Clear();
    internal void RollbackExternalChanges()
    {
        foreach (var action in rollbackActions.AsEnumerable().Reverse())
        {
            try { action(); } catch { }
        }
        rollbackActions.Clear();
    }
}

public sealed class VaultWriteCoordinator(
    IAccessConnectionFactory connectionFactory,
    Schema.SchemaWriteGate schemaGate,
    ILogger<VaultWriteCoordinator>? logger = null,
    WritingVaultMcp.Mcp.V4.VaultChangeNotifier? changeNotifier = null)
{
    private static readonly SemaphoreSlim WriteQueue = new(1, 1);
    private static readonly AsyncLocal<int> GateDepth = new();
    private static readonly AsyncLocal<VaultWriteContext?> ActiveWrite = new();
    private static int PendingWriteOperations;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Func<VaultWriteContext, VaultMutationOutcome, CancellationToken, Task>? pageSnapshotCapture;

    internal void ConfigurePageSnapshotCapture(
        Func<VaultWriteContext, VaultMutationOutcome, CancellationToken, Task> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (Interlocked.CompareExchange(ref pageSnapshotCapture, capture, null) is not null)
            throw new InvalidOperationException("The page snapshot capture is already configured.");
    }

    internal async Task<VaultMutationResult> ExecuteAsync(
        string operationId,
        string commandType,
        object input,
        string toolName,
        string? clientLabel,
        Func<VaultWriteContext, CancellationToken, Task<VaultMutationOutcome>> action,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(operationId, out var operationGuid) || operationGuid == Guid.Empty)
            return new(false, "validation.operation_id", Message: "operationId must be a GUID.");
        var normalizedOperationId = operationGuid.ToString("D");
        var journalClientLabel = string.IsNullOrWhiteSpace(clientLabel) ? "unspecified" : clientLabel.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(action);
        if (RequestSafetyValidator.Validate(input) is { } validationError)
            return new(false, "validation.failed", Message: validationError);

        for (var schemaAttempt = 0; schemaAttempt < 3; schemaAttempt++)
        {
            try
            {
                await schemaGate.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception exception)
            {
                var failure = AccessErrorClassifier.ToResult(exception, commandType);
                if (!failure.Retryable || schemaAttempt == 2) return failure;
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (schemaAttempt + 1) + Random.Shared.Next(10, 40)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        var inputNode = JsonSerializer.SerializeToNode(input, JsonOptions)
            ?? throw new InvalidOperationException("The request could not be serialized for idempotency.");
        if (inputNode is JsonObject inputObject)
        {
            if (inputObject.ContainsKey("operationId")) inputObject["operationId"] = normalizedOperationId;
            // Attribution is journal metadata, not semantic request input. The
            // same readable request token may safely replay after reconnecting
            // through another client label.
            inputObject.Remove("clientLabel");
        }
        var inputJson = inputNode.ToJsonString(JsonOptions);
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson)));

        Interlocked.Increment(ref PendingWriteOperations);
        try { await WriteQueue.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref PendingWriteOperations); throw; }
        logger?.LogInformation("Vault operation {OperationId} {CommandType} entered the write queue", normalizedOperationId, commandType);
        VaultDiagnostics.Write("mutation.queued", operationId: normalizedOperationId, commandType: commandType,
            pendingWrites: PendingWrites);
        try
        {
            VaultMutationResult? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                last = await AttemptAsync().ConfigureAwait(false);
                if (!last.Retryable || attempt == 2) return last;
                logger?.LogWarning("Retrying vault operation {OperationId} {CommandType}; attempt {Attempt}", normalizedOperationId, commandType, attempt + 2);
                VaultDiagnostics.Write("mutation.retry", "warning", normalizedOperationId, commandType, last.Code, attempt + 2,
                    PendingWrites);
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1) + Random.Shared.Next(10, 40)), cancellationToken)
                    .ConfigureAwait(false);
            }
            return last!;

            async Task<VaultMutationResult> AttemptAsync()
            {
                try
                {
                    await using var connection = connectionFactory.Create();
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    // ACE rejects Serializable for .accdb files. Process-wide write
                    // serialization plus one DML transaction protects application
                    // decisions; direct concurrent writers are outside the support boundary.
                    using var transaction = connection.BeginTransaction();
                    VaultWriteContext? context = null;
                    try
                    {
                        var existing = await FindOperationAsync(connection, transaction, normalizedOperationId, cancellationToken)
                            .ConfigureAwait(false);
                        if (existing is not null)
                        {
                            transaction.Rollback();
                            if (!string.Equals(existing.Value.InputHash, inputHash, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(existing.Value.CommandType, commandType, StringComparison.Ordinal))
                                return new(false, "idempotency.input_mismatch", Message: "The operation ID was already used with different input.");
                            var replay = JsonSerializer.Deserialize<VaultMutationResult>(existing.Value.ResultReference, JsonOptions)
                                ?? new VaultMutationResult(false, "storage.invalid_idempotency_result");
                            VaultDiagnostics.Write("mutation.replayed", operationId: normalizedOperationId,
                                commandType: commandType, code: replay.Code, pendingWrites: PendingWrites);
                            return replay with { Replayed = true };
                        }

                        var now = DateTime.UtcNow;
                        using (var insertOperation = new AccessCommand(connection,
                                   "INSERT INTO [ProcessedOperations] ([OperationId],[CommandType],[InputSha256],[Status],[StartedAtUtc]) VALUES (?,?,?,?,?)", transaction)
                               .Add(OleDbType.VarWChar, normalizedOperationId, 36).Add(OleDbType.VarWChar, commandType, 100)
                               .Add(OleDbType.VarWChar, inputHash, 64).Add(OleDbType.VarWChar, "InProgress", 20).Add(OleDbType.Date, now))
                            await insertOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        context = new VaultWriteContext(connection, transaction, normalizedOperationId);
                        ActiveWrite.Value = context;
                        var outcome = await action(context, cancellationToken).ConfigureAwait(false);
                        if (pageSnapshotCapture is { } capture)
                            await capture(context, outcome, cancellationToken).ConfigureAwait(false);
                        await AccessTransactionInvariantGuard.VerifyAsync(context, cancellationToken).ConfigureAwait(false);
                        var success = new VaultMutationResult(true, "ok", outcome.ResourceType, outcome.ResourceKey, outcome.Version);
                        var resultJson = JsonSerializer.Serialize(success, JsonOptions);

                        using (var journal = new AccessCommand(connection,
                                   "INSERT INTO [ChangeLog] ([OperationId],[ChangedAtUtc],[ClientLabel],[ToolName],[Action],[RecordType],[RecordKey],[VersionBefore],[VersionAfter],[ChangeJson]) VALUES (?,?,?,?,?,?,?,?,?,?)", transaction)
                               .Add(OleDbType.VarWChar, normalizedOperationId, 36).Add(OleDbType.Date, now)
                               .Add(OleDbType.VarWChar, journalClientLabel, 100).Add(OleDbType.VarWChar, toolName, 100)
                               .Add(OleDbType.VarWChar, outcome.Action, 50).Add(OleDbType.VarWChar, outcome.ResourceType, 100)
                               .Add(OleDbType.VarWChar, outcome.ResourceKey, 100).Add(OleDbType.Integer, outcome.VersionBefore)
                               .Add(OleDbType.Integer, outcome.Version).Add(OleDbType.LongVarWChar, VaultJournalPayload.Serialize(outcome.Change, JsonOptions)))
                            await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        using (var complete = new AccessCommand(connection,
                                   "UPDATE [ProcessedOperations] SET [Status]='Completed',[ResultReference]=?,[CompletedAtUtc]=? WHERE [OperationId]=?", transaction)
                               .Add(OleDbType.VarWChar, resultJson, 255).Add(OleDbType.Date, now).Add(OleDbType.VarWChar, normalizedOperationId, 36))
                            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        transaction.Commit();
                        context.Complete();
                        changeNotifier?.Pulse();
                        logger?.LogInformation("Vault operation {OperationId} {CommandType} committed", normalizedOperationId, commandType);
                        VaultDiagnostics.Write("mutation.committed", operationId: normalizedOperationId,
                            commandType: commandType, code: success.Code, pendingWrites: PendingWrites);
                        return success;
                    }
                    catch (Exception exception)
                    {
                        try { transaction.Rollback(); } catch { }
                        if (context is not null) context.RollbackExternalChanges();
                        var failure = AccessErrorClassifier.ToResult(exception, commandType);
                        logger?.LogWarning("Vault operation {OperationId} {CommandType} rolled back with {Code}", normalizedOperationId, commandType, failure.Code);
                        VaultDiagnostics.Write("mutation.rolled_back", "warning", normalizedOperationId,
                            commandType, failure.Code, pendingWrites: PendingWrites);
                        return failure;
                    }
                    finally
                    {
                        if (ReferenceEquals(ActiveWrite.Value, context)) ActiveWrite.Value = null;
                    }
                }
                catch (Exception exception)
                {
                    var failure = AccessErrorClassifier.ToResult(exception, commandType);
                    VaultDiagnostics.Write("mutation.failed", "error", normalizedOperationId,
                        commandType, failure.Code, pendingWrites: PendingWrites);
                    return failure;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref PendingWriteOperations);
            WriteQueue.Release();
        }
    }

    public int PendingWrites => Volatile.Read(ref PendingWriteOperations);

    internal async Task<T> ExecuteConsistentReadAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (GateDepth.Value > 0) return await read().ConfigureAwait(false);
        await WriteQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        GateDepth.Value++;
        try { return await read().ConfigureAwait(false); }
        finally
        {
            GateDepth.Value--;
            WriteQueue.Release();
        }
    }

    // A page snapshot is assembled before the mutation commits. Existing read
    // projections open helper connections, so their commands must instead use
    // the caller's write connection and transaction for this narrowly scoped
    // operation. The write queue is already held by ExecuteAsync.
    internal async Task<T> ReadPendingWriteAsync<T>(
        VaultWriteContext context, Func<Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);
        if (!ReferenceEquals(ActiveWrite.Value, context))
            throw new InvalidOperationException("A pending page can only be read from its active write transaction.");
        using var scope = AccessCommand.UseTransactionForReads(context.Connection, context.Transaction);
        GateDepth.Value++;
        try { return await read().ConfigureAwait(false); }
        finally { GateDepth.Value--; }
    }

    internal async Task ReadPendingWriteAsync(VaultWriteContext context, Func<Task> read)
    {
        await ReadPendingWriteAsync(context, async () =>
        {
            await read().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static async Task<(string CommandType, string InputHash, string ResultReference)?> FindOperationAsync(
        OleDbConnection connection,
        OleDbTransaction transaction,
        string operationId,
        CancellationToken cancellationToken)
    {
        using var command = new AccessCommand(
            connection,
            "SELECT [CommandType],[InputSha256],[ResultReference] FROM [ProcessedOperations] WHERE [OperationId]=?",
            transaction).Add(OleDbType.VarWChar, operationId, 36);
        var rows = await command.QueryAsync(
            reader => (
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
            cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }
}

internal static class AccessErrorClassifier
{
    public static Exception ToClientReadException(Exception exception, string operation)
    {
        if (exception is OperationCanceledException) return exception;
        return new VaultReadException($"The vault could not complete {operation}.");
    }

    public static VaultMutationResult ToResult(Exception exception, string operation)
    {
        if (exception is VaultCommandException command)
            return new(false, command.Code, command.ResourceType, command.ResourceKey, command.ActualVersion, Message: command.Message);
        if (exception is Schema.SchemaNotReadyException)
            return new(false, "schema.not_ready", Message: "The database schema is not ready for writes.");
        if (exception is OperationCanceledException) throw exception;
        if (exception is OleDbException oleDb)
        {
            var messages = oleDb.Errors.Cast<OleDbError>().Select(error => error.Message).ToArray();
            var joined = string.Join(" ", messages);
            if (joined.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || joined.Contains("unique", StringComparison.OrdinalIgnoreCase))
                return new(false, "constraint.duplicate", Message: "A unique value already exists.");
            if (joined.Contains("related record", StringComparison.OrdinalIgnoreCase) || joined.Contains("referential integrity", StringComparison.OrdinalIgnoreCase))
                return new(false, "constraint.reference", Message: "A referenced record is missing or still in use.");
            var retryable = joined.Contains("locked", StringComparison.OrdinalIgnoreCase) || joined.Contains("in use", StringComparison.OrdinalIgnoreCase);
            return new(false, "storage.failure", Message: $"The database could not complete {operation}.", Retryable: retryable);
        }

        return new(false, "storage.failure", Message: $"The database could not complete {operation}.");
    }
}

internal sealed class VaultReadException(string message) : Exception(message);

internal sealed class VaultCommandException(
    string code,
    string message,
    string? resourceType = null,
    string? resourceKey = null,
    int? actualVersion = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? ResourceType { get; } = resourceType;
    public string? ResourceKey { get; } = resourceKey;
    public int? ActualVersion { get; } = actualVersion;
}
