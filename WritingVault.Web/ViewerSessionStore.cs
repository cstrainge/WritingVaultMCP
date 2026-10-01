using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WritingVault.Client;

namespace WritingVault.Web;

internal sealed partial class ViewerSessionStore(IVaultReadClientFactory factory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ViewerSession>>> sessions = new(StringComparer.Ordinal);

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();

    public Task<ViewerSession> GetAsync(string id, CancellationToken token)
    {
        if (!SessionPattern().IsMatch(id)) throw new ViewerRequestException(400, "session.invalid", "The browser session token is invalid.");
        var lazy = sessions.GetOrAdd(id, key => new(() => CreateAsync(key, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAsync(id, lazy, token);
    }

    public async Task InvalidateAsync(string id)
    {
        if (!sessions.TryRemove(id, out var lazy) || !lazy.IsValueCreated) return;
        try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    public async Task RemoveExpiredAsync(DateTime cutoffUtc)
    {
        foreach (var entry in sessions.ToArray())
        {
            if (!entry.Value.IsValueCreated || !entry.Value.Value.IsCompletedSuccessfully) continue;
            var session = await entry.Value.Value.ConfigureAwait(false);
            if (session.LastAccessUtc >= cutoffUtc || !sessions.TryRemove(entry)) continue;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ViewerSession> CreateAsync(string id, CancellationToken token)
    {
        var suffix = id[..8];
        var interactive = await factory.ConnectAsync($"Writing Vault Web {suffix}", token).ConfigureAwait(false);
        try
        {
            var watcher = await factory.ConnectAsync($"Writing Vault Web watcher {suffix}", token).ConfigureAwait(false);
            return new(interactive, watcher);
        }
        catch
        {
            await interactive.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ViewerSession> AwaitAsync(string id, Lazy<Task<ViewerSession>> lazy, CancellationToken token)
    {
        try
        {
            var session = await lazy.Value.WaitAsync(token).ConfigureAwait(false);
            session.Touch();
            return session;
        }
        catch
        {
            sessions.TryRemove(new KeyValuePair<string, Lazy<Task<ViewerSession>>>(id, lazy));
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var values = sessions.Values.ToArray();
        sessions.Clear();
        foreach (var lazy in values)
        {
            if (!lazy.IsValueCreated) continue;
            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }
}

internal sealed class ViewerSessionReaper(ViewerSessionStore sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await sessions.RemoveExpiredAsync(DateTime.UtcNow.AddMinutes(-30)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

internal sealed class ViewerSession : IAsyncDisposable
{
    public ViewerSession(IVaultReadClient interactive, IVaultReadClient watcher)
    {
        Interactive = interactive;
        Watcher = watcher;
    }

    public IVaultReadClient Interactive { get; }
    public IVaultReadClient Watcher { get; }
    public SemaphoreSlim Reads { get; } = new(1, 1);
    public SemaphoreSlim Watches { get; } = new(1, 1);
    public DateTime LastAccessUtc { get; private set; } = DateTime.UtcNow;
    public void Touch() => LastAccessUtc = DateTime.UtcNow;

    public async Task<(VaultSession Session, VaultRecord Overview)> SelectAsync(VaultSessionUpdate update, CancellationToken token)
    {
        await Reads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Watches.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var selected = await Interactive.SetSessionAsync(update, token).ConfigureAwait(false);
                await Watcher.SetSessionAsync(update, token).ConfigureAwait(false);
                var overview = await Interactive.GetAsync(cancellationToken: token).ConfigureAwait(false);
                return (selected, overview);
            }
            finally { Watches.Release(); }
        }
        finally { Reads.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await Watcher.DisposeAsync().ConfigureAwait(false);
        await Interactive.DisposeAsync().ConfigureAwait(false);
        Reads.Dispose();
        Watches.Dispose();
    }
}

internal sealed class ViewerRequestException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
