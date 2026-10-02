using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WritingVault.Client;

public sealed class McpVaultReadClientFactory(VaultReadClientOptions options) : IVaultReadClientFactory
{
    public async Task<IVaultReadClient> ConnectAsync(string clientLabel, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var label = string.IsNullOrWhiteSpace(clientLabel) ? options.ClientLabel : clientLabel.Trim();
        if (label.Length > 100) label = label[..100];
        var arguments = new List<string>
        {
            Path.GetFullPath(options.ServerAssemblyPath), "serve",
            "--database", Path.GetFullPath(options.DatabasePath),
            "--backup-root", Path.GetFullPath(options.BackupRoot),
            "--provider", options.Provider,
            "--client-label", label,
            "--tool-surface", "v4",
            "--read-only"
        };
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = label,
                Command = "dotnet",
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ServerAssemblyPath))!,
                ShutdownTimeout = TimeSpan.FromSeconds(10)
            });
            try
            {
                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (client.ServerInstructions?.Contains("v4", StringComparison.OrdinalIgnoreCase) != true)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw new VaultReadException("The backend did not negotiate the required v4 tool surface.", "surface.unsupported");
                }
                return new McpVaultReadClient(client);
            }
            catch (VaultReadException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                lastError = exception;
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new VaultReadException("The read-only Vault connection could not be established after three attempts.", "connection.failed", lastError);
    }

    private static void ValidateOptions(VaultReadClientOptions value)
    {
        if (!File.Exists(value.ServerAssemblyPath))
            throw new VaultReadException("The configured Writing Vault server assembly is unavailable.", "configuration.invalid");
        if (!File.Exists(value.DatabasePath))
            throw new VaultReadException("The configured Writing Vault database is unavailable.", "configuration.invalid");
        if (!Path.GetExtension(value.DatabasePath).Equals(".accdb", StringComparison.OrdinalIgnoreCase))
            throw new VaultReadException("The Writing Vault database must be an .accdb file.", "configuration.invalid");
        if (!Directory.Exists(value.BackupRoot))
            throw new VaultReadException("The configured Writing Vault backup root is unavailable.", "configuration.invalid");
    }
}

internal sealed class McpVaultReadClient(McpClient client) : IVaultReadClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private int disposeStarted;

    public Task<VaultHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        CallAsync<VaultHealth>("vault_health", new(), cancellationToken);

    public Task<VaultPage<VaultContinuity>> ListContinuitiesAsync(CancellationToken cancellationToken = default) =>
        ListContinuitiesPageAsync(null, 100, cancellationToken);

    public Task<VaultPage<VaultContinuity>> ListContinuitiesPageAsync(string? cursor, int limit = 100, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["limit"] = Math.Clamp(limit, 1, 100) };
        if (cursor is not null) arguments["cursor"] = cursor;
        return CallAsync<VaultPage<VaultContinuity>>("continuity_list", arguments, cancellationToken);
    }

    public Task<VaultSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
        CallAsync<VaultSession>("session_get", new(), cancellationToken);

    public Task<VaultSession> SetSessionAsync(VaultSessionUpdate update, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["timeAction"] = update.TimeAction };
        if (update.ContinuityName is not null) arguments["continuityName"] = update.ContinuityName;
        if (update.CurrentTime is not null) arguments["currentTime"] = update.CurrentTime;
        if (update.CurrentDate is not null) arguments["currentDate"] = update.CurrentDate;
        if (update.ReferenceTimeZoneId is not null) arguments["referenceTimeZoneId"] = update.ReferenceTimeZoneId;
        return CallAsync<VaultSession>("session_set", arguments, cancellationToken);
    }

    public Task<VaultPage<VaultReference>> SearchAsync(VaultSearchRequest request, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["limit"] = Math.Clamp(request.Limit, 1, 100) };
        if (!string.IsNullOrWhiteSpace(request.Text)) arguments["text"] = request.Text.Trim();
        if (request.Kinds is { Count: > 0 }) arguments["kinds"] = request.Kinds;
        if (request.Cursor is not null) arguments["cursor"] = request.Cursor;
        if (request.DeletionState != "Active") arguments["deletionState"] = request.DeletionState;
        if (request.IncludeContent) arguments["includeContent"] = true;
        if (request.Tags is { Count: > 0 }) arguments["tags"] = request.Tags;
        if (request.Project is not null) arguments["project"] = request.Project;
        return CallAsync<VaultPage<VaultReference>>("search", arguments, cancellationToken);
    }

    public Task<VaultRecord> GetAsync(string? reference = null, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(reference)) arguments["ref"] = reference;
        return CallAsync<VaultRecord>("get", arguments, cancellationToken);
    }

    public Task<VaultRecord> GetRecordAsync(string reference, bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        CallAsync<VaultRecord>("get", new() { ["ref"] = reference, ["includeDeleted"] = includeDeleted }, cancellationToken);

    public Task<VaultRecordSnapshot> GetRecordSnapshotAsync(string reference, int snapshotVersion,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultRecordSnapshot>("record_snapshot_get",
            new() { ["ref"] = reference, ["snapshotVersion"] = snapshotVersion }, cancellationToken);

    public Task<VaultReference> LocateAsync(string reference, CancellationToken cancellationToken = default) =>
        LocateAsync(reference, false, cancellationToken);

    public Task<VaultReference> LocateAsync(string reference, bool includeDeleted,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultReference>("record_locate",
            new() { ["ref"] = reference, ["includeDeleted"] = includeDeleted }, cancellationToken);

    public Task<VaultPage<VaultReference>> RelatedAsync(VaultRelatedRequest request, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["ref"] = request.Ref, ["relation"] = request.Relation,
            ["deletionState"] = request.DeletionState, ["limit"] = Math.Clamp(request.Limit, 1, 100)
        };
        if (request.Cursor is not null) arguments["cursor"] = request.Cursor;
        return CallAsync<VaultPage<VaultReference>>("list_related", arguments, cancellationToken);
    }

    public Task<VaultPage<VaultHistoryEntry>> HistoryAsync(string? reference, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["limit"] = Math.Clamp(limit, 1, 100) };
        if (reference is not null) arguments["ref"] = reference;
        if (cursor is not null) arguments["cursor"] = cursor;
        return CallAsync<VaultPage<VaultHistoryEntry>>("history_get", arguments, cancellationToken);
    }

    public Task<VaultTimelinePage> TimelineAsync(VaultTimelineRequest request, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["limit"] = Math.Clamp(request.Limit, 1, 500), ["includeUndated"] = request.IncludeUndated,
            ["mode"] = request.Mode, ["resolution"] = request.Resolution,
            ["undatedOnly"] = request.UndatedOnly, ["expandRanges"] = request.ExpandRanges, ["expandRecurrences"] = request.ExpandRecurrences
        };
        if (request.From is not null) arguments["from"] = request.From;
        if (request.To is not null) arguments["to"] = request.To;
        if (request.Lanes is { Count: > 0 }) arguments["lanes"] = request.Lanes;
        if (request.Kinds is { Count: > 0 }) arguments["kinds"] = request.Kinds;
        if (request.EntityEventKinds is { Count: > 0 }) arguments["entityEventKinds"] = request.EntityEventKinds;
        if (request.FocusRefs is { Count: > 0 }) arguments["focusRefs"] = request.FocusRefs;
        if (request.HighlightRef is not null) arguments["highlightRef"] = request.HighlightRef;
        if (request.Tags is { Count: > 0 }) arguments["tags"] = request.Tags;
        if (request.Projects is { Count: > 0 }) arguments["projects"] = request.Projects;
        if (request.Locations is { Count: > 0 }) arguments["locations"] = request.Locations;
        if (!string.IsNullOrWhiteSpace(request.Text)) arguments["text"] = request.Text.Trim();
        if (request.Cursor is not null) arguments["cursor"] = request.Cursor;
        return CallAsync<VaultTimelinePage>("timeline_get", arguments, cancellationToken);
    }

    public Task<VaultSourceSnapshotTextPage> SourceSnapshotViewAsync(string snapshotRef, string? cursor = null, int limit = 8192, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["snapshotRef"] = snapshotRef, ["limit"] = Math.Clamp(limit, 2, 16_384) };
        if (cursor is not null) arguments["cursor"] = cursor;
        if (includeDeleted) arguments["includeDeleted"] = true;
        return CallAsync<VaultSourceSnapshotTextPage>("source_snapshot_view", arguments, cancellationToken);
    }

    public Task<VaultPage<VaultImageMetadata>> ImageListAsync(string target, string? cursor = null, int limit = 50, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["target"] = target, ["limit"] = Math.Clamp(limit, 1, 100) };
        if (cursor is not null) arguments["cursor"] = cursor;
        if (includeDeleted) arguments["deletionState"] = "All";
        return CallAsync<VaultPage<VaultImageMetadata>>("image_list", arguments, cancellationToken);
    }

    public Task<VaultPage<VaultImageMetadata>> ImageSearchAsync(string? text = null,
        string? cursor = null, int limit = 50, bool acrossContinuities = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["limit"] = Math.Clamp(limit, 1, 100),
            ["acrossContinuities"] = acrossContinuities
        };
        if (!string.IsNullOrWhiteSpace(text)) arguments["text"] = text.Trim();
        if (cursor is not null) arguments["cursor"] = cursor;
        return CallAsync<VaultPage<VaultImageMetadata>>("image_search", arguments,
            cancellationToken);
    }

    public async Task<VaultImageView> ImageViewAsync(string imageRef, string size = "Thumbnail", int? revision = null, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["imageRef"] = imageRef, ["size"] = size };
        if (revision is not null) arguments["revision"] = revision;
        var result = await CallResultAsync("image_view", arguments, cancellationToken);
        var metadata = Deserialize<VaultImageViewMetadata>(result, "image_view");
        var block = result.Content.OfType<ImageContentBlock>().SingleOrDefault()
            ?? throw new VaultReadException("Vault image_view returned no image block.", "response.invalid");
        if (!string.Equals(block.MimeType, metadata.MediaType, StringComparison.OrdinalIgnoreCase))
            throw new VaultReadException("Vault image_view returned mismatched image metadata.", "response.invalid");
        var bytes = block.DecodedData.ToArray();
        if (bytes.LongLength != metadata.ByteCount)
            throw new VaultReadException("Vault image_view returned the wrong byte count.", "response.invalid");
        return new(metadata.Image, metadata.Size, metadata.MediaType, metadata.Width, metadata.Height,
            metadata.ByteCount, metadata.ObservedRevision, bytes,
            metadata.ContentRevision, metadata.IsCurrentContent);
    }

    public Task<VaultImageRevisionHistory> ImageRevisionHistoryAsync(string imageRef,
        int? beforeRevision = null, int limit = 50,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultImageRevisionHistory>("image_revision_history",
            new() { ["imageRef"] = imageRef, ["beforeRevision"] = beforeRevision,
                ["limit"] = limit }, cancellationToken);

    public Task<VaultChanges> ChangesSinceAsync(string? cursor, int waitSeconds, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["waitSeconds"] = Math.Clamp(waitSeconds, 0, 30) };
        if (cursor is not null) arguments["cursor"] = cursor;
        return CallAsync<VaultChanges>("changes_since", arguments, cancellationToken);
    }

    private async Task<T> CallAsync<T>(string tool, Dictionary<string, object?> arguments, CancellationToken cancellationToken) =>
        Deserialize<T>(await CallResultAsync(tool, arguments, cancellationToken), tool);

    private static T Deserialize<T>(CallToolResult result, string tool)
    {
        var payload = result.StructuredContent?.ToString();
        if (string.IsNullOrWhiteSpace(payload)) throw new VaultReadException($"Vault operation '{tool}' returned no structured data.", "response.invalid");
        return JsonSerializer.Deserialize<T>(payload, Json)
            ?? throw new VaultReadException($"Vault operation '{tool}' returned an empty response.", "response.invalid");
    }

    private async Task<CallToolResult> CallResultAsync(string tool, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: linked.Token).ConfigureAwait(false);
            if (result.IsError == true)
            {
                var message = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                throw new VaultReadException(string.IsNullOrWhiteSpace(message) ? $"Vault operation '{tool}' failed." : message, ParseCode(message));
            }
            return result;
        }
        catch (VaultReadException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new VaultReadException($"Vault operation '{tool}' failed.", "connection.failed", exception);
        }
        finally { gate.Release(); }
    }

    private sealed record VaultImageViewMetadata(VaultImageMetadata Image, string Size, string MediaType,
        int Width, int Height, long ByteCount, string ObservedRevision,
        int ContentRevision = 1, bool IsCurrentContent = true);

    private static string? ParseCode(string message)
    {
        var separator = message.IndexOf(':');
        return separator is > 0 and < 80 ? message[..separator].Trim() : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        lifetime.Cancel();
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            disposed = true;
            await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
            lifetime.Dispose();
        }
    }
}
