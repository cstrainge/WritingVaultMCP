using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace WritingVaultMcp.Mcp.V4;

public sealed record V4ImageIngressProbeRequest(
    string ClientName,
    string MediaType,
    string? DataBase64 = null,
    string? DataUrl = null);

public sealed record V4ImageIngressProbeResult(
    bool Success,
    string Code,
    string ClientName,
    string? Transport = null,
    string? MediaType = null,
    int? ByteCount = null,
    int? Width = null,
    int? Height = null,
    string? Sha256 = null,
    bool EvidenceRecorded = false,
    string? Message = null);

public sealed record V4ImageIngressProbeOptions(string? EvidenceOutput);

[McpServerToolType]
public sealed class V4ImageIngressProbeTools(V4ImageIngressProbeOptions options)
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly SemaphoreSlim EvidenceLock = new(1, 1);
    internal const int MaximumEncodedCharacters = ((V4ContractLimits.MaximumImageInputBytes + 2) / 3) * 4;

    [McpServerTool(Name = "image_ingress_probe", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
     Description("Debug-only Phase 1 transport probe. Accepts one inline PNG, returns dimensions/hash, records metadata only when configured, discards bytes, and never opens a path, URL, or Vault database.")]
    public async Task<V4ImageIngressProbeResult> Probe(
        string clientName,
        string mediaType,
        string? dataBase64 = null,
        string? dataUrl = null,
        CancellationToken cancellationToken = default)
    {
        var request = new V4ImageIngressProbeRequest(clientName, mediaType, dataBase64, dataUrl);
        var client = NormalizeClient(request.ClientName);
        if (client is null)
            return await Failure("image.client_invalid", request.ClientName, "clientName must be ChatGPT, Claude, or AutomatedTest.", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.MediaType, "image/png", StringComparison.OrdinalIgnoreCase))
            return await Failure("image.unsupported", client, "The Phase 1 probe accepts image/png only.", cancellationToken).ConfigureAwait(false);
        if ((request.DataBase64 is null) == (request.DataUrl is null))
            return await Failure("image.transport_invalid", client, "Supply exactly one of dataBase64 or dataUrl. Server-local paths and remote URLs are not accepted.", cancellationToken).ConfigureAwait(false);

        var transport = request.DataBase64 is not null ? "dataBase64" : "dataUrl";
        var encoded = request.DataBase64;
        if (request.DataUrl is not null)
        {
            const string prefix = "data:image/png;base64,";
            if (!request.DataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return await Failure("image.data_url_invalid", client, "dataUrl must begin with data:image/png;base64,.", cancellationToken, transport).ConfigureAwait(false);
            encoded = request.DataUrl[prefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaximumEncodedCharacters)
            return await Failure("image.too_large", client, $"Encoded PNG input may not exceed {MaximumEncodedCharacters} characters.", cancellationToken, transport).ConfigureAwait(false);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            return await Failure("image.base64_invalid", client, "The supplied PNG is not valid base64.", cancellationToken, transport).ConfigureAwait(false);
        }

        try
        {
            if (bytes.Length > V4ContractLimits.MaximumImageInputBytes)
                return await Failure("image.too_large", client, $"Decoded PNG input may not exceed {V4ContractLimits.MaximumImageInputBytes} bytes.", cancellationToken, transport, bytes.Length).ConfigureAwait(false);
            if (!TryReadPngDimensions(bytes, out var width, out var height))
                return await Failure("image.content_invalid", client, "The decoded bytes are not a valid PNG header and IHDR block.", cancellationToken, transport, bytes.Length).ConfigureAwait(false);
            if (width <= 0 || height <= 0 || width > 100_000 || height > 100_000 || (long)width * height > 100_000_000)
                return await Failure("image.dimensions_unsafe", client, "PNG dimensions exceed the Phase 1 safety ceiling.", cancellationToken, transport, bytes.Length, width, height).ConfigureAwait(false);

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var result = new V4ImageIngressProbeResult(
                true, "ok", client, transport, "image/png", bytes.Length, width, height, hash,
                EvidenceRecorded: !string.IsNullOrWhiteSpace(options.EvidenceOutput),
                Message: "PNG transport succeeded. The probe discarded the decoded bytes.");
            await RecordAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<V4ImageIngressProbeResult> Failure(
        string code, string client, string message, CancellationToken token,
        string? transport = null, int? byteCount = null, int? width = null, int? height = null)
    {
        var result = new V4ImageIngressProbeResult(false, code, string.IsNullOrWhiteSpace(client) ? "unknown" : client.Trim(),
            transport, "image/png", byteCount, width, height,
            EvidenceRecorded: !string.IsNullOrWhiteSpace(options.EvidenceOutput), Message: message);
        await RecordAsync(result, token).ConfigureAwait(false);
        return result;
    }

    private async Task RecordAsync(V4ImageIngressProbeResult result, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(options.EvidenceOutput)) return;
        var path = Path.GetFullPath(options.EvidenceOutput);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Probe evidence path has no directory.");
        Directory.CreateDirectory(directory);
        var evidence = JsonSerializer.Serialize(new
        {
            probeVersion = "1.0",
            receivedAtUtc = DateTime.UtcNow,
            result.Success,
            result.Code,
            result.ClientName,
            result.Transport,
            result.MediaType,
            result.ByteCount,
            result.Width,
            result.Height,
            result.Sha256
        });
        await EvidenceLock.WaitAsync(token).ConfigureAwait(false);
        try { await File.AppendAllTextAsync(path, evidence + Environment.NewLine, token).ConfigureAwait(false); }
        finally { EvidenceLock.Release(); }
    }

    private static string? NormalizeClient(string value)
    {
        if (value.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)) return "ChatGPT";
        if (value.Equals("Claude", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (value.Equals("AutomatedTest", StringComparison.OrdinalIgnoreCase)) return "AutomatedTest";
        return null;
    }

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0; height = 0;
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(PngSignature)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13) return false;
        if (!bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4)));
        height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4)));
        return true;
    }
}

public static class V4ImageIngressProbeHost
{
    public static async Task RunAsync(string? evidenceOutput, CancellationToken token = default)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new V4ImageIngressProbeOptions(evidenceOutput));
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInstructions =
                    "Writing Vault v4 Phase 1 image ingress probe. Call image_ingress_probe with one attached PNG represented as inline base64 or a PNG data URL. " +
                    "This Debug-only server never opens the Vault database, never accepts a local path or remote URL, and discards decoded bytes after hashing and header validation.";
            })
            .WithStdioServerTransport()
            .WithTools<V4ImageIngressProbeTools>();
        await builder.Build().RunAsync(token).ConfigureAwait(false);
    }
}
