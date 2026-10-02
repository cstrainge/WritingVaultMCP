using System.Net;
using Microsoft.AspNetCore.Http.Json;
using WritingVault.Client;
using WritingVault.Web;

ViewerOptions options;
try { options = ViewerOptions.Parse(args); }
catch (Exception)
{
    Console.Error.WriteLine("Writing Vault viewer configuration failed. Check the fixed address and configured server, database, and backup locations.");
    return 64;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(settings =>
{
    settings.SingleLine = true;
    settings.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});
builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(server =>
{
    server.AddServerHeader = false;
    server.Limits.MaxRequestBodySize = 64 * 1024;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    server.Listen(IPAddress.Loopback, ViewerOptions.Port);
});
builder.Services.Configure<JsonOptions>(json => json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<IVaultReadClientFactory>(new McpVaultReadClientFactory(new(
    options.ServerAssemblyPath, options.DatabasePath, options.BackupRoot, options.Provider)));
builder.Services.AddSingleton<ViewerSessionStore>();
builder.Services.AddHostedService<ViewerSessionReaper>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'none'; img-src 'self' data: blob:; font-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    context.Response.Headers["X-WritingVault-Build"] = ViewerOptions.Configuration;
    context.Response.Headers.CacheControl = context.Request.Path.StartsWithSegments("/api") ? "no-store" : "no-cache";

    if (!string.Equals(context.Request.Host.Value, $"127.0.0.1:{ViewerOptions.Port}", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { code = "host.invalid", message = $"Use {ViewerOptions.Address}." });
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!context.Request.Headers.TryGetValue("X-WritingVault-Session", out var session) ||
            !context.Request.Headers.TryGetValue("X-WritingVault-Request", out var marker) || marker != "1")
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "request.invalid", message = "Required same-origin viewer headers are missing." });
            return;
        }
        if (context.Request.Headers.TryGetValue("Origin", out var origin) && !origin.ToString().Equals(ViewerOptions.Address, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = "origin.invalid", message = "Cross-origin access is forbidden." });
            return;
        }
        context.Items["viewer-session"] = session.ToString();
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = false });

static async Task<IResult> Guard(Func<Task<IResult>> action)
{
    try { return await action().ConfigureAwait(false); }
    catch (ViewerRequestException exception) { return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode); }
    catch (VaultReadException exception) { return Results.Json(new { code = exception.Code ?? "vault.read_failed", message = exception.Message }, statusCode: 503); }
    catch (OperationCanceledException) { return Results.Json(new { code = "request.cancelled", message = "The request was cancelled." }, statusCode: 499); }
}

static string SessionId(HttpContext context) => (string)context.Items["viewer-session"]!;

app.MapGet("/api/bootstrap", (HttpContext context, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try
    {
        var continuities = await session.Interactive.ListContinuitiesAsync(token);
        var current = await session.Interactive.GetSessionAsync(token);
        var health = await session.Interactive.HealthAsync(token);
        return Results.Ok(new { continuities, session = current, health });
    }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/continuities", (HttpContext context, string? cursor, int? limit, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.ListContinuitiesPageAsync(cursor, Math.Clamp(limit ?? 100, 1, 100), token)); }
    finally { session.Reads.Release(); }
}));

app.MapPost("/api/session", (HttpContext context, ViewerSessionUpdateRequest request, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var id = SessionId(context);
    var update = request.ToVaultUpdate();
    try
    {
        var session = await store.GetAsync(id, token);
        var selected = await session.SelectAsync(update, token);
        return Results.Ok(new { session = selected.Session, overview = selected.Overview });
    }
    catch
    {
        await store.InvalidateAsync(id);
        throw;
    }
}));

app.MapGet("/api/search", (HttpContext context, string? text, string? kinds, string? cursor, string? deletionState, bool? includeContent, string? tags, string? project, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    var requestedKinds = string.IsNullOrWhiteSpace(kinds) ? null : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var requestedTags = string.IsNullOrWhiteSpace(tags) ? null : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.SearchAsync(new(text, requestedKinds, cursor, 60, deletionState ?? "Active", includeContent ?? false, requestedTags, project), token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/overview", (HttpContext context, string? reference, bool? includeDeleted, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(reference is null
        ? await session.Interactive.GetAsync(null, token)
        : await session.Interactive.GetRecordAsync(reference, includeDeleted ?? false, token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/record-locate", (HttpContext context, string reference, bool? includeDeleted, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.LocateAsync(reference, includeDeleted ?? false, token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/record-snapshot", (HttpContext context, string reference, int version,
    ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.GetRecordSnapshotAsync(reference, version, token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/related", (HttpContext context, string? reference, string relation, string? cursor, string? deletionState, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.RelatedAsync(new(reference, relation, deletionState ?? "Active", cursor), token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/history", (HttpContext context, string? reference, string? cursor, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.HistoryAsync(reference, cursor, cancellationToken: token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/timeline", (HttpContext context, string? from, string? to, string? lanes, string? kinds, string? entityEventKinds,
    string? focusRefs, string? highlightRef, string? projects, string? locations, string? tags, string? text,
    string? mode, string? resolution, string? cursor, bool? undatedOnly, bool? includeUndated, bool? expandRanges, bool? expandRecurrences,
    int? limit, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    static string[]? Values(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try
    {
        var request = new VaultTimelineRequest(
            From: from, To: to, Lanes: Values(lanes), Kinds: Values(kinds), EntityEventKinds: Values(entityEventKinds), FocusRefs: Values(focusRefs),
            Projects: Values(projects), Locations: Values(locations), Tags: Values(tags), Text: text,
            IncludeUndated: includeUndated ?? true, Mode: mode ?? "Calendar", Resolution: resolution ?? "Detail",
            Cursor: cursor, Limit: Math.Clamp(limit ?? 100, 1, 500), UndatedOnly: undatedOnly ?? false,
            HighlightRef: highlightRef, ExpandRanges: expandRanges ?? false, ExpandRecurrences: expandRecurrences ?? true);
        return Results.Ok(await session.Interactive.TimelineAsync(request, token));
    }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/source-text", (HttpContext context, string snapshotRef, string? cursor, bool? includeDeleted, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.SourceSnapshotViewAsync(snapshotRef, cursor, includeDeleted: includeDeleted ?? false, cancellationToken: token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/images", (HttpContext context, string target, string? cursor, bool? includeDeleted, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.ImageListAsync(target, cursor, includeDeleted: includeDeleted ?? false, cancellationToken: token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/image-search", (HttpContext context, string? text, string? cursor,
    bool? acrossContinuities, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.ImageSearchAsync(text, cursor, 20,
        acrossContinuities ?? false, token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/image", (HttpContext context, string imageRef, string? size, int? revision, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try
    {
        var image = await session.Interactive.ImageViewAsync(imageRef, size ?? "Thumbnail", revision, token);
        if (image.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ViewerRequestException(415, "image.media_type", "The image format is not supported by this viewer.");
        return Results.File(image.Bytes, image.MediaType, enableRangeProcessing: false);
    }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/image-info", (HttpContext context, string imageRef, int? revision, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try
    {
        // image_view resolves a semantic image reference globally and applies
        // the same owner/deletion checks as the byte endpoint.
        var image = await session.Interactive.ImageViewAsync(imageRef, "Thumbnail", revision, token);
        return Results.Ok(new { metadata = image.Image, image.ContentRevision,
            image.IsCurrentContent, image.MediaType, image.Width, image.Height,
            image.ByteCount, image.ObservedRevision });
    }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/image-revisions", (HttpContext context, string imageRef,
    int? beforeRevision,
    ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var session = await store.GetAsync(SessionId(context), token);
    await session.Reads.WaitAsync(token);
    try { return Results.Ok(await session.Interactive.ImageRevisionHistoryAsync(
        imageRef, beforeRevision, cancellationToken: token)); }
    finally { session.Reads.Release(); }
}));

app.MapGet("/api/watch", (HttpContext context, string? cursor, ViewerSessionStore store, CancellationToken token) => Guard(async () =>
{
    var id=SessionId(context);
    try
    {
        var session = await store.GetAsync(id, token);
        if (!await session.Watches.WaitAsync(0, token))
            throw new ViewerRequestException(409, "watch.concurrent", "This browser tab already has an active change watcher.");
        try { return Results.Ok(await session.Watcher.ChangesSinceAsync(cursor, 20, token)); }
        finally { session.Watches.Release(); }
    }
    catch (VaultReadException)
    {
        await store.InvalidateAsync(id);
        throw;
    }
}));

app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], () =>
    Results.Json(new { code = "route.not_found", message = "The requested viewer API route does not exist." }, statusCode: 404));

app.MapFallbackToFile("index.html");

try
{
    await app.StartAsync();
    app.Logger.LogInformation("Writing Vault viewer ready at {Address}", ViewerOptions.Address);
    await app.WaitForShutdownAsync();
}
catch (IOException exception) when (exception.Message.Contains("address", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Writing Vault viewer could not start because {ViewerOptions.Address} is already in use.");
    return 20;
}
return 0;
