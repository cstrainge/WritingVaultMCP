using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WritingVaultMcp.Mcp.V4;

public sealed record V4CursorState(string Purpose, long Position, string Scope, DateTime ExpiresAtUtc);
public sealed record V4TextCursorState(string Purpose, string Position, string Scope, DateTime ExpiresAtUtc);

/// <summary>Process-local authenticated cursors. Payload fields never appear in the public token.</summary>
public sealed class V4CursorCodec
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] revisionNonceKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// A stable cursor for one observed database position. Ordinary paging cursors remain
    /// randomized; page revisions must compare equal across independent reads of the same state.
    /// </summary>
    public string EncodeRevision(string purpose, long position, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var state = new V4CursorState(purpose, position, scope, DateTime.UtcNow.Date.AddDays(2));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        var nonce = HMACSHA256.HashData(revisionNonceKey, plaintext)[..12];
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var bytes = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(bytes, 0);
        tag.CopyTo(bytes, nonce.Length);
        ciphertext.CopyTo(bytes, nonce.Length + tag.Length);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public string Encode(string purpose, long position, string scope, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var state = new V4CursorState(purpose, position, scope,
            DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(24)));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var bytes = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(bytes, 0);
        tag.CopyTo(bytes, nonce.Length);
        ciphertext.CopyTo(bytes, nonce.Length + tag.Length);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public V4CursorState Decode(string cursor, string purpose, string scope)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > V4ContractLimits.MaximumCursorLength)
            throw new V4CursorException("cursor.invalid", "The cursor is missing or malformed.");
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length < 29) throw new FormatException();
            var plaintext = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
            var state = JsonSerializer.Deserialize<V4CursorState>(plaintext) ?? throw new FormatException();
            if (!string.Equals(state.Purpose, purpose, StringComparison.Ordinal) ||
                !string.Equals(state.Scope, scope, StringComparison.Ordinal))
                throw new V4CursorException("cursor.invalid", "The cursor cannot be used for this query.");
            if (state.ExpiresAtUtc <= DateTime.UtcNow)
                throw new V4CursorException("cursor.expired", "The cursor expired; refresh the visible data and restart from its observed revision.");
            return state;
        }
        catch (V4CursorException) { throw; }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException)
        {
            throw new V4CursorException("cursor.invalid", "The cursor is invalid or belongs to another server process.");
        }
    }

    public string EncodeText(string purpose, string position, string scope, TimeSpan? lifetime = null) =>
        Encrypt(new V4TextCursorState(purpose, position, scope,
            DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(24))));

    public V4TextCursorState DecodeText(string cursor, string purpose, string scope)
    {
        var state = Decrypt<V4TextCursorState>(cursor);
        Validate(state.Purpose, state.Scope, state.ExpiresAtUtc, purpose, scope);
        return state;
    }

    private string Encrypt<T>(T state)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var bytes = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(bytes, 0);
        tag.CopyTo(bytes, nonce.Length);
        ciphertext.CopyTo(bytes, nonce.Length + tag.Length);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private T Decrypt<T>(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > V4ContractLimits.MaximumCursorLength)
            throw new V4CursorException("cursor.invalid", "The cursor is missing or malformed.");
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length < 29) throw new FormatException();
            var plaintext = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
            return JsonSerializer.Deserialize<T>(plaintext) ?? throw new FormatException();
        }
        catch (V4CursorException) { throw; }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException)
        {
            throw new V4CursorException("cursor.invalid", "The cursor is invalid or belongs to another server process.");
        }
    }

    private static void Validate(string actualPurpose, string actualScope, DateTime expiresAtUtc, string purpose, string scope)
    {
        if (!string.Equals(actualPurpose, purpose, StringComparison.Ordinal) ||
            !string.Equals(actualScope, scope, StringComparison.Ordinal))
            throw new V4CursorException("cursor.invalid", "The cursor cannot be used for this query.");
        if (expiresAtUtc <= DateTime.UtcNow)
            throw new V4CursorException("cursor.expired", "The cursor expired; refresh the visible data and restart from its observed revision.");
    }
}

public sealed class V4CursorException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class VaultChangeNotifier
{
    private readonly object sync = new();
    private TaskCompletionSource<long> next = NewSignal();
    private long version;

    public long Version => Interlocked.Read(ref version);

    public void Pulse()
    {
        TaskCompletionSource<long> signal;
        long value;
        lock (sync)
        {
            value = Interlocked.Increment(ref version);
            signal = next;
            next = NewSignal();
        }
        signal.TrySetResult(value);
    }

    public async Task WaitForChangeAfterAsync(long observedVersion, TimeSpan timeout, CancellationToken token)
    {
        Task signal;
        lock (sync)
        {
            if (Version != observedVersion) return;
            signal = next.Task;
        }
        if (timeout <= TimeSpan.Zero) return;
        try { await signal.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static TaskCompletionSource<long> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
