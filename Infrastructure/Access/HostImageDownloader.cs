using System.Net;
using System.Net.Sockets;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public interface IHostImageDownloader
{
    bool Ready { get; }
    Task<byte[]> DownloadAsync(V4HostFileInput file, CancellationToken token);
}

public sealed class ImageDownloadException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>Fetches host-authorized files without credentials, proxies, URL logging, or automatic redirects.</summary>
public sealed class HostImageDownloader : IHostImageDownloader
{
    public const string OriginsEnvironmentVariable = "WRITINGVAULT_IMAGE_IMPORT_ORIGINS";
    private readonly HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);
    public bool Ready => true;

    public HostImageDownloader(IEnumerable<string> approvedOrigins)
    {
        foreach (var value in approvedOrigins)
        {
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Image import origins must be HTTPS origins without paths or credentials.");
            origins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
    }

    public static HostImageDownloader FromEnvironment() => new(
        (Environment.GetEnvironmentVariable(OriginsEnvironmentVariable) ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            (origins.Count != 0 && !origins.Contains(uri.GetLeftPart(UriPartial.Authority))) ||
            (IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address)))
            throw new ImageDownloadException("image.url_not_allowed", "The file URL must use a permitted public HTTPS origin without credentials.");
        return uri;
    }

    private static readonly System.Net.IPNetwork[] BlockedV4 =
    [
        System.Net.IPNetwork.Parse("0.0.0.0/8"), System.Net.IPNetwork.Parse("10.0.0.0/8"),
        System.Net.IPNetwork.Parse("100.64.0.0/10"), System.Net.IPNetwork.Parse("127.0.0.0/8"),
        System.Net.IPNetwork.Parse("169.254.0.0/16"), System.Net.IPNetwork.Parse("172.16.0.0/12"),
        System.Net.IPNetwork.Parse("192.0.0.0/24"), System.Net.IPNetwork.Parse("192.0.2.0/24"),
        System.Net.IPNetwork.Parse("192.88.99.0/24"), System.Net.IPNetwork.Parse("192.168.0.0/16"),
        System.Net.IPNetwork.Parse("198.18.0.0/15"), System.Net.IPNetwork.Parse("198.51.100.0/24"),
        System.Net.IPNetwork.Parse("203.0.113.0/24"), System.Net.IPNetwork.Parse("224.0.0.0/3")
    ];

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !BlockedV4.Any(network => network.Contains(address));
        // Only native global unicast; exclude special-purpose, documentation and 6to4 ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 &&
            System.Net.IPNetwork.Parse("2000::/3").Contains(address) &&
            !System.Net.IPNetwork.Parse("2001::/23").Contains(address) &&
            !System.Net.IPNetwork.Parse("2001:db8::/32").Contains(address) &&
            !System.Net.IPNetwork.Parse("2002::/16").Contains(address) &&
            !System.Net.IPNetwork.Parse("3fff::/20").Contains(address);
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ImageDownloadException("image.url_not_allowed", "The file host resolves to a disallowed destination.");
        // Connect directly to a checked IP, retaining the original hostname for TLS validation.
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new ImageDownloadException("image.download_failed", "The file host could not be reached.", true);
    }

    public async Task<byte[]> DownloadAsync(V4HostFileInput file, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectCallback = ConnectAsync,
            MaxResponseHeadersLength = 16
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try { return await FetchAsync(client, file.DownloadUrl, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ImageDownloadException("image.download_timeout", "The file download exceeded 30 seconds.", true); }
        catch (HttpRequestException error)
        {
            for (Exception? cause = error; cause is not null; cause = cause.InnerException)
                if (cause is ImageDownloadException safe) throw safe;
            throw new ImageDownloadException("image.download_failed", "The file download failed.", true);
        }
        catch (IOException) { throw new ImageDownloadException("image.download_failed", "The file transfer was interrupted.", true); }
    }

    internal async Task<byte[]> FetchAsync(HttpClient client, string url, CancellationToken token)
    {
        var uri = ValidateUrl(url);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects == 3 || response.Headers.Location is not { } location)
                    throw new ImageDownloadException("image.download_failed", "The file redirect limit was reached or its destination was missing.");
                uri = ValidateUrl(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            if (status is 401 or 403)
                throw new ImageDownloadException("image.download_denied", $"The file host denied access (HTTP {status}).");
            if (status != 200)
                throw new ImageDownloadException("image.download_failed", $"The file host returned HTTP {status}.", status is 408 or 429 or >= 500);
            if (response.Content.Headers.ContentLength > V4ContractLimits.MaximumImageInputBytes)
                throw new ImageDownloadException("image.too_large", "The image exceeds 20 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + count > V4ContractLimits.MaximumImageInputBytes)
                    throw new ImageDownloadException("image.too_large", "The image exceeds 20 MiB.");
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }
    }
}
