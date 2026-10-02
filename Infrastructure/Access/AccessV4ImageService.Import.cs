using System.Data.OleDb;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ImageService
{
    private static void ValidateImageInput(V4InlineImageInput? image, V4HostFileInput? file, string background)
    {
        if ((image is null) == (file is null))
            throw Error("image.input", "file", "Provide exactly one non-null file or inline image.");
        try { _ = SKColor.Parse(background); }
        catch { throw Error("image.background", "backgroundColor", "Background color must be a hex color."); }
        if (file is null) return;
        if (string.IsNullOrWhiteSpace(file.FileId) || file.FileId.Length > 512 ||
            string.IsNullOrWhiteSpace(file.DownloadUrl) || file.DownloadUrl.Length > 16384 ||
            file.MimeType?.Length > 100 || file.FileName?.Length > 1024)
            throw Error("image.input", "file", "The host file requires a bounded file_id and download_url; optional metadata must also be bounded.");
    }

    // Only a sanitized request may reach the coordinator. URLs and host filenames
    // are transport details; file_id and business metadata identify a logical import.
    private static JsonObject HostImportInput(object sanitizedRequest, V4HostFileInput file,
        string targetReference, int continuity)
    {
        var node = JsonSerializer.SerializeToNode(sanitizedRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node.Remove("file");
        node.Remove("image");
        node["hostFileId"] = file.FileId;
        node["targetReference"] = targetReference;
        node["continuity"] = continuity;
        return node;
    }

    private async Task<(EncodedImage? Image, VaultMutationResult? Failure)> DownloadImageAsync(
        V4HostFileInput file, string background, CancellationToken token)
    {
        try
        {
            var bytes = await downloader.DownloadAsync(file, token).ConfigureAwait(false);
            return (Decode(bytes, file.MimeType, background), null);
        }
        catch (ImageDownloadException error)
        { return (null, new(false, error.Code, Message: error.Message, Retryable: error.Retryable)); }
        catch (VaultValidationException error)
        {
            return (null, new(false, error.Errors[0].Code == "image.too_large" ? "image.too_large" : "image.invalid",
                Message: error.Message));
        }
    }

    private Task<VaultMutationResult?> PreflightImportAsync(V4ResolvedTarget target, int continuity,
        V4ResolvedTarget? source, int? expectedVersion, CancellationToken token) =>
        coordinator.ExecuteConsistentReadAsync<VaultMutationResult?>(async () =>
        {
            try
            {
                await using var connection = factory.Create();
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var transaction = connection.BeginTransaction();
                var context = new VaultWriteContext(connection, transaction, "image-import-preflight");
                await RequireActiveContinuityAsync(context, continuity, token).ConfigureAwait(false);
                if (target.ResourceType is "EntityImage" or "StoryImage")
                {
                    var table = target.ResourceType == "StoryImage" ? "StoryImages" : "EntityImages";
                    using var version = context.Command($"SELECT [Version] FROM [{table}] WHERE [Id]=? AND [IsDeleted]=False")
                        .Add(OleDbType.Integer, target.StorageKey);
                    var value = await version.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (value is null or DBNull)
                        throw new VaultCommandException("record.not_found", "The image is missing or deleted.");
                    if (Convert.ToInt32(value) != expectedVersion)
                        throw new VaultCommandException("concurrency.conflict", $"The current version is {Convert.ToInt32(value)}.",
                            actualVersion: Convert.ToInt32(value));
                    if (target.ResourceType == "EntityImage")
                    {
                        using var owner = context.Command("SELECT [EntityId] FROM [EntityImages] WHERE [Id]=?")
                            .Add(OleDbType.Integer, target.StorageKey);
                        await RequireActiveEntityOwnerAsync(context,
                            Convert.ToInt32(await owner.ExecuteScalarAsync(token).ConfigureAwait(false)), continuity, token).ConfigureAwait(false);
                    }
                    else
                    {
                        using var owner = context.Command("SELECT [OwnerKind],[ContinuityId],[RelationshipId],[EntityEventId],[RelationshipEventId] FROM [StoryImages] WHERE [Id]=?")
                            .Add(OleDbType.Integer, target.StorageKey);
                        var owners = await owner.QueryAsync(reader => new
                        {
                            Kind = reader.GetString(0), Continuity = reader.GetInt32(1),
                            OwnerId = reader.GetString(0) switch
                            {
                                "Continuity" => reader.GetInt32(1), "Relationship" => reader.GetInt32(2),
                                "EntityEvent" => reader.GetInt32(3), "RelationshipEvent" => reader.GetInt32(4), _ => 0
                            }
                        }, token).ConfigureAwait(false);
                        if (owners.Count != 1 || owners[0].Continuity != continuity)
                            throw new VaultCommandException("record.not_found", "The image owner is unavailable in this continuity.");
                        await RequireActiveStoryOwnerAsync(context, owners[0].Kind, owners[0].OwnerId, continuity, token).ConfigureAwait(false);
                    }
                }
                else if (target.ResourceType is "Continuity" or "CharacterRelationship" or "EntityEvent" or "RelationshipEvent")
                {
                    var kind = target.ResourceType == "CharacterRelationship" ? "Relationship" : target.ResourceType;
                    await RequireActiveStoryOwnerAsync(context, kind, target.StorageKey, continuity, token).ConfigureAwait(false);
                }
                else await RequireActiveEntityOwnerAsync(context, target.StorageKey, continuity, token).ConfigureAwait(false);
                if (source is not null)
                {
                    using var check = context.Command("SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False")
                        .Add(OleDbType.Integer, source.StorageKey);
                    if (Convert.ToInt32(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("record.not_found", "The image source is missing or deleted.");
                }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { return AccessErrorClassifier.ToResult(error, "image-import-preflight"); }
        }, token);

    private static async Task RequireActiveContinuityAsync(VaultWriteContext context, int continuity, CancellationToken token)
    {
        using var check = context.Command("SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, continuity);
        if (Convert.ToInt32(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("record.not_found", "The image continuity is missing or deleted.");
    }

    private static async Task RequireActiveEntityOwnerAsync(VaultWriteContext context, int owner, int continuity, CancellationToken token)
    {
        using var check = context.Command("SELECT COUNT(*) FROM [CanonEntities] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, owner).Add(OleDbType.Integer, continuity);
        if (Convert.ToInt32(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("record.not_found", "The image owner is missing or deleted.");
    }
}
