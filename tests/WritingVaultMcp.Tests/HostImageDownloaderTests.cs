using System.Net;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class HostImageDownloaderTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.2.3.4", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.100.200", false)]
    [InlineData("192.168.2.2", false)]
    [InlineData("198.18.1.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2002:7f00:1::", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void RejectsNonPublicDestinations(string address, bool expected) =>
        Assert.Equal(expected, HostImageDownloader.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://host.example/image")]
    [InlineData("https://user:password@host.example/image")]
    [InlineData("https://127.0.0.1/image")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("file:///C:/secret")]
    public void RejectsUnsafeUrls(string url) => Assert.Equal("image.url_not_allowed",
        Assert.Throws<ImageDownloadException>(() => new HostImageDownloader([]).ValidateUrl(url)).Code);

    [Fact]
    public async Task DnsLoopbackIsRejectedByActualConnectionHandler()
    {
        var error = await Assert.ThrowsAsync<ImageDownloadException>(() => new HostImageDownloader([])
            .DownloadAsync(new("https://localhost/file", "file-test"), default));
        Assert.Equal("image.url_not_allowed", error.Code);
    }

    [Fact]
    public async Task RedirectsAreCheckedAndBounded()
    {
        var downloader = new HostImageDownloader(["https://host.example"]);
        using var handler = new Handler(_ => new(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://other.example/image") } });
        using var client = new HttpClient(handler);
        Assert.Equal("image.url_not_allowed", (await Assert.ThrowsAsync<ImageDownloadException>(() =>
            downloader.FetchAsync(client, "https://host.example/image", default))).Code);
        Assert.Equal(1, handler.Calls);
        handler.Respond = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/again", UriKind.Relative) } };
        Assert.Equal("image.download_failed", (await Assert.ThrowsAsync<ImageDownloadException>(() =>
            downloader.FetchAsync(client, "https://host.example/image", default))).Code);
        Assert.Equal(5, handler.Calls);
    }

    [Theory]
    [InlineData(401, "image.download_denied", false)]
    [InlineData(403, "image.download_denied", false)]
    [InlineData(404, "image.download_failed", false)]
    [InlineData(429, "image.download_failed", true)]
    [InlineData(503, "image.download_failed", true)]
    public async Task HttpErrorsAreSafeAndDoNotMislabelExpiry(int status, string code, bool retryable)
    {
        using var handler = new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("secret upstream details") });
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ImageDownloadException>(() => new HostImageDownloader([])
            .FetchAsync(client, "https://host.example/file?secret=token", default));
        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnforcesLimitWithAndWithoutContentLength(bool lengthKnown)
    {
        var bytes = new byte[V4ContractLimits.MaximumImageInputBytes + 1];
        using var handler = new Handler(_ => new(HttpStatusCode.OK)
        { Content = lengthKnown ? new ByteArrayContent(bytes) : new ChunkedContent(bytes) });
        using var client = new HttpClient(handler);
        Assert.Equal("image.too_large", (await Assert.ThrowsAsync<ImageDownloadException>(() => new HostImageDownloader([])
            .FetchAsync(client, "https://host.example/image", default))).Code);
    }

    [Fact]
    public async Task AcceptsHostSuppliedPublicUrlWithoutOriginConfiguration()
    {
        using var handler = new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return new(HttpStatusCode.OK) { Content = new ChunkedContent([1, 2, 3]) };
        });
        using var client = new HttpClient(handler);
        var downloader = new HostImageDownloader([]);
        Assert.True(downloader.Ready);
        Assert.Equal(new byte[] { 1, 2, 3 }, await downloader.FetchAsync(client, "https://host.example/image", default));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Respond(request)); }
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
