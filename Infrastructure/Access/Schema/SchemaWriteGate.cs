namespace WritingVaultMcp.Infrastructure.Access.Schema;

public sealed class SchemaNotReadyException(IReadOnlyList<SchemaIssue> issues)
    : InvalidOperationException("The database schema is not ready for writes.")
{
    public IReadOnlyList<SchemaIssue> Issues { get; } = issues;
}

public sealed class SchemaWriteGate(AccessSchemaVerifier verifier)
{
    private readonly SemaphoreSlim _verification = new(1, 1);
    private volatile bool _ready;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (_ready) return;
        await _verification.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready) return;
            var result = await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsValid) throw new SchemaNotReadyException(result.Issues);
            _ready = true;
        }
        finally { _verification.Release(); }
    }
}
