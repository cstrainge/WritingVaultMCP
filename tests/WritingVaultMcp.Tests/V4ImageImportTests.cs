using System.Data.OleDb;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4ImageImportTests
{
    [Theory]
    [InlineData("entity")]
    [InlineData("story")]
    [InlineData("replace")]
    public async Task HostFileImportsReplayWithoutRefetchAndPreserveOriginalBytes(string operation)
    {
        await using var vault = await TestVault.CreateAsync();
        var (images, downloader, continuity) = await Setup(vault);
        var imageRef = "";
        if (operation == "replace")
        {
            Assert.True((await images.AttachAsync(new("seed", "Portrait owner", Inline(SKColors.Red)))).Success);
            imageRef = Assert.Single((await images.ListAsync(new("Portrait owner"))).Items).Ref;
        }
        var file = new V4HostFileInput("https://host.example/image?secret=first", "file-one");
        Task<VaultMutationResult> Import(V4HostFileInput input, string title = "Portrait") => operation switch
        {
            "story" => images.AttachStoryAsync(new("host-import", "Import world", Title: title, File: input)),
            "replace" => images.ReplaceAsync(new("host-import", imageRef, 1, File: input)),
            _ => images.AttachAsync(new("host-import", "Portrait owner", Title: title, File: input))
        };
        var result = await Import(file);
        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(1, downloader.Calls);
        var listed = Assert.Single((await images.ListAsync(new(operation == "story" ? "Import world" : "Portrait owner"))).Items);
        Assert.Equal(downloader.Bytes, (await images.ViewAsync(new(listed.Ref, V4ImageSize.Original))).Content);
        if (operation == "replace")
            Assert.Equal(Png(SKColors.Red), (await images.ViewAsync(new(listed.Ref, V4ImageSize.Original, Revision: 1))).Content);

        downloader.BeforeReturn = () => throw new Xunit.Sdk.XunitException("Replay fetched an expired URL.");
        var replay = await Import(file with { DownloadUrl = "https://host.example/expired", FileName = "changed-name.png" });
        Assert.True(replay.Success, replay.Code);
        Assert.True(replay.Replayed);
        Assert.Equal(result.ResourceKey, replay.ResourceKey);
        Assert.Equal(result.Version, replay.Version);
        Assert.Equal(1, downloader.Calls);
        Assert.Equal("idempotency.input_mismatch", (await Import(file with { FileId = "different-file" })).Code);
        if (operation != "replace") Assert.Equal("idempotency.input_mismatch", (await Import(file, "Different title")).Code);

        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var journal = new AccessCommand(connection, "SELECT [ChangeJson] FROM [ChangeLog] WHERE [Action]=?")
            .Add(OleDbType.VarWChar, operation == "replace" ? "replace" : "attach", 50);
        var entries = await journal.QueryAsync(reader => reader.GetString(0), default);
        var receipt = Assert.Single(entries, entry => entry.Contains("file-one", StringComparison.Ordinal));
        Assert.Contains(Convert.ToHexString(SHA256.HashData(downloader.Bytes)), receipt);
        Assert.DoesNotContain("secret=", receipt);
        Assert.DoesNotContain("download_url", receipt);
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task FailedDownloadCanRetryWithFreshUrlAndSameToken()
    {
        await using var vault = await TestVault.CreateAsync();
        var (images, downloader, _) = await Setup(vault);
        var request = new V4ImageAttachRequest("retry-download", "Portrait owner",
            File: new("https://host.example/expired", "stable-file"));
        downloader.BeforeReturn = () => throw new ImageDownloadException("image.download_denied", "HTTP 403.");
        Assert.Equal("image.download_denied", (await images.AttachAsync(request)).Code);
        Assert.Empty((await images.ListAsync(new("Portrait owner"))).Items);
        downloader.BeforeReturn = null;
        var retried = await images.AttachAsync(request with { File = request.File! with { DownloadUrl = "https://host.example/fresh" } });
        Assert.True(retried.Success, retried.Code);
        Assert.False(retried.Replayed);
        Assert.Equal(2, downloader.Calls);
        Assert.Single((await images.ListAsync(new("Portrait owner"))).Items);
    }

    [Fact]
    public async Task InvalidInputOwnerAndStaleVersionAreRejectedBeforeFetching()
    {
        await using var vault = await TestVault.CreateAsync();
        var (images, downloader, _) = await Setup(vault);
        var file = new V4HostFileInput("https://host.example/image", "file-one");
        Assert.Equal("image.input", (await images.AttachAsync(new("neither", "Portrait owner"))).Code);
        Assert.Equal("image.input", (await images.AttachAsync(new("both", "Portrait owner", Inline(SKColors.Red), File: file))).Code);
        Assert.Equal("image.input", (await images.AttachStoryAsync(new("story-neither", "Import world"))).Code);
        Assert.Equal("image.input", (await images.ReplaceAsync(new("replace-neither", "bad-ref", 1))).Code);
        Assert.False((await images.AttachAsync(new("missing-owner", "Missing", File: file))).Success);
        Assert.Equal("image.background", (await images.AttachAsync(new("bad-bg", "Portrait owner", BackgroundColor: "not-a-colour", File: file))).Code);
        Assert.True((await images.AttachAsync(new("seed", "Portrait owner", Inline(SKColors.Red)))).Success);
        var image = Assert.Single((await images.ListAsync(new("Portrait owner"))).Items);
        Assert.Equal("concurrency.conflict", (await images.ReplaceAsync(new("stale", image.Ref, 999, File: file))).Code);
        Assert.Equal(0, downloader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRechecksVersionAfterDownloadWithoutHoldingWriteQueue(bool story)
    {
        await using var vault = await TestVault.CreateAsync();
        var (images, downloader, _) = await Setup(vault);
        var seed = story
            ? await images.AttachStoryAsync(new("seed-story", "Import world", Inline(SKColors.Red)))
            : await images.AttachAsync(new("seed", "Portrait owner", Inline(SKColors.Red)));
        Assert.True(seed.Success, seed.Code);
        var image = Assert.Single((await images.ListAsync(new(story ? "Import world" : "Portrait owner"))).Items);
        downloader.BeforeReturn = async () =>
        {
            var update = await images.UpdateAsync(new("concurrent-edit", image.Ref, image.Version,
                new Dictionary<string, JsonElement> { ["title"] = JsonSerializer.SerializeToElement("Concurrent title") }));
            Assert.True(update.Success, update.Code);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var replaced = await images.ReplaceAsync(new("racing-replace", image.Ref, image.Version,
            File: new("https://host.example/new", "file-new")), timeout.Token);
        Assert.Equal("concurrency.conflict", replaced.Code);
        Assert.Equal(Png(SKColors.Red), (await images.ViewAsync(new(image.Ref, V4ImageSize.Original))).Content);
        Assert.Equal(1, (await images.ViewAsync(new(image.Ref))).View.ContentRevision);
        Assert.Single(Directory.GetFiles(Path.Combine(vault.StorageRoot, "assets", "originals"), "*", SearchOption.AllDirectories));
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task ConcurrentRetriesCommitOnlyOneAttachment()
    {
        await using var vault = await TestVault.CreateAsync();
        var (images, downloader, _) = await Setup(vault);
        var entered = 0;
        var bothDownloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloader.BeforeReturn = async () =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothDownloading.SetResult();
            await bothDownloading.Task;
        };
        var request = new V4ImageAttachRequest("concurrent-retry", "Portrait owner",
            File: new("https://host.example/file", "same-file"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var results = await Task.WhenAll(images.AttachAsync(request, timeout.Token),
            images.AttachAsync(request with { File = request.File! with { DownloadUrl = "https://host.example/refreshed" } }, timeout.Token));
        Assert.All(results, result => Assert.True(result.Success, result.Code));
        Assert.Single(results, result => result.Replayed);
        Assert.Equal(results[0].ResourceKey, results[1].ResourceKey);
        Assert.Single((await images.ListAsync(new("Portrait owner"))).Items);
    }

    [Fact]
    public async Task MalformedDownloadedImageLeavesNoAssetsOrReceipt()
    {
        await using var vault = await TestVault.CreateAsync();
        var (images, downloader, _) = await Setup(vault);
        var request = new V4StoryImageAttachRequest("bad-image", "Import world", File: new("https://host.example/file", "file-one"));
        downloader.Bytes = "not an image"u8.ToArray();
        Assert.Equal("image.invalid", (await images.AttachStoryAsync(request)).Code);
        Assert.Empty((await images.ListAsync(new("Import world"))).Items);
        downloader.Bytes = Png(SKColors.Blue);
        Assert.True((await images.AttachStoryAsync(request)).Success);
    }

    private static async Task<(AccessV4ImageService Images, FakeDownloader Downloader, int Continuity)> Setup(TestVault vault)
    {
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Import world", "UTC"))).ResourceKey!);
        Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Portrait owner"))).Success);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Import world");
        var downloader = new FakeDownloader();
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath, downloader);
        return (images, downloader, continuity);
    }

    private sealed class FakeDownloader : IHostImageDownloader
    {
        public bool Ready => true;
        public int Calls { get; private set; }
        public byte[] Bytes { get; set; } = Png(SKColors.Blue);
        public Func<Task>? BeforeReturn { get; set; }
        public async Task<byte[]> DownloadAsync(V4HostFileInput file, CancellationToken token)
        {
            Calls++;
            if (BeforeReturn is not null) await BeforeReturn().WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return Bytes;
        }
    }

    private static V4InlineImageInput Inline(SKColor color) => new("image/png", Convert.ToBase64String(Png(color)));
    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(3, 3);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
