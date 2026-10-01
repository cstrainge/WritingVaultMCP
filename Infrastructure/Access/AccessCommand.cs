using System.Data;
using System.Data.Common;
using System.Data.OleDb;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>
/// Builds OleDb commands without exposing its positional-parameter trap to repositories.
/// Each call to Add appends a value for the next '?' placeholder in SQL.
/// </summary>
internal sealed class AccessCommand : IDisposable
{
    internal const int DefaultTimeoutSeconds = 30;
    private static readonly AsyncLocal<(OleDbConnection Connection, OleDbTransaction Transaction)?> AmbientRead = new();

    private readonly OleDbCommand _command;

    public AccessCommand(OleDbConnection connection, string sql, OleDbTransaction? transaction = null)
    {
        // Snapshot materialization can reuse existing read projections while a
        // writer's changes are still uncommitted. Only an explicit opt-in scope
        // redirects their commands to the writer's connection and transaction.
        if (transaction is null && AmbientRead.Value is { } ambient)
        {
            connection = ambient.Connection;
            transaction = ambient.Transaction;
        }
        _command = connection.CreateCommand();
        _command.CommandText = sql;
        _command.Transaction = transaction;
        _command.CommandTimeout = DefaultTimeoutSeconds;
    }

    internal static IDisposable UseTransactionForReads(OleDbConnection connection, OleDbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var previous = AmbientRead.Value;
        AmbientRead.Value = (connection, transaction);
        return new ReadScope(previous);
    }

    private sealed class ReadScope((OleDbConnection Connection, OleDbTransaction Transaction)? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            AmbientRead.Value = previous;
            disposed = true;
        }
    }

    public AccessCommand Add(OleDbType type, object? value, int? size = null)
    {
        var parameter = _command.Parameters.Add($"p{_command.Parameters.Count}", type);
        if (size is not null)
        {
            parameter.Size = size.Value;
        }

        parameter.Value = value ?? DBNull.Value;
        return this;
    }

    public Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) =>
        _command.ExecuteNonQueryAsync(cancellationToken);

    public Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default) =>
        _command.ExecuteScalarAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        var results = new List<T>();
        await using var reader = await _command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<T>> QueryPageAsync<T>(
        int offset,
        int limit,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentNullException.ThrowIfNull(map);

        var results = new List<T>(limit);
        await using var reader = await _command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var skipped = 0;
        while (skipped < offset && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            skipped++;
        }

        while (results.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(map(reader));
        }

        return results;
    }

    public void Dispose() => _command.Dispose();
}
