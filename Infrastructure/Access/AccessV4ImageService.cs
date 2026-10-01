using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed record V4ImageContent(V4ImageViewResult View, byte[] Content);

public sealed partial class AccessV4ImageService(
    IAccessConnectionFactory factory,
    VaultWriteCoordinator coordinator,
    VaultReferenceService references,
    V4TargetResolver targets,
    VaultSessionContext session,
    AccessV4ReadService reads,
    V4CursorCodec cursors,
    string storageRoot,
    string databasePath)
{
    private const long MaxPixels = 50_000_000;
    internal const long DatabaseWarningBytes = 1_288_490_189; // 1.2 GiB
    internal const long DatabaseHardLimitBytes = 1_610_612_736; // 1.5 GiB
    private sealed record EncodedImage(byte[] Original, string MediaType, string Extension, int Width, int Height,
        string Hash, byte[] Display, int DisplayWidth, int DisplayHeight, byte[] Thumbnail, int ThumbWidth, int ThumbHeight,
        bool TransparencyFlattened);

    public async Task<VaultMutationResult> AttachAsync(V4ImageAttachRequest request, CancellationToken token = default)
    {
        EncodedImage encoded;
        try
        {
            ValidateText(request.Title,255,"title");ValidateText(request.Caption,V4ContractLimits.MaximumLongTextLength,"caption");
            ValidateText(request.AltText,V4ContractLimits.MaximumLongTextLength,"altText");ValidateText(request.Role,100,"role");
            ValidateText(request.CanonStatus,50,"canonStatus");encoded = Decode(request.Image, request.BackgroundColor);
        }
        catch (VaultValidationException e) { return new(false, e.Errors[0].Code, Message: e.Message); }
        var continuity = session.RequireContinuityId();
        V4ResolvedTarget owner;
        V4ResolvedTarget? source = null;
        try
        {
            owner = await targets.EntityAsync(request.Entity, continuity, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.Source)) source = await targets.SourceAsync(request.Source, token).ConfigureAwait(false);
        }
        catch (V4ResolutionException e) { return new(false, e.Code, Message: e.Message); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException e) { return new(false, "validation.mutation_token", Message: e.Message); }
        // OriginalRelativePath is relative to the configured assets root.  Keep
        // the assets directory itself out of the persisted value so backup,
        // restore, and image ingestion all resolve the same path contract.
        var relative = Path.Combine("originals", encoded.Hash[..2], encoded.Hash + encoded.Extension);
        var absolute = SafeAssetPath(relative);
        var journalInput = new
        {
            request.MutationToken, request.Entity, imageSha256 = encoded.Hash, request.Image.MediaType,
            request.Title, request.Caption, request.AltText, request.Role, request.CanonStatus,
            request.Source, request.BackgroundColor, request.IsPrimary
        };
        var result = await coordinator.ExecuteAsync(operation, "v4.image.attach", journalInput,
            "image_attach", session.ClientLabel, async (context, ct) =>
            {
                var projectedBytes = new FileInfo(databasePath).Length + encoded.Display.Length + encoded.Thumbnail.Length;
                if (projectedBytes > DatabaseHardLimitBytes)
                    throw new VaultCommandException("capacity.database_limit", "The image renditions would exceed the supported 1.5 GiB database limit.");
                using (var entity = context.Command("SELECT COUNT(*) FROM [CanonEntities] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False")
                           .Add(OleDbType.Integer, owner.StorageKey).Add(OleDbType.Integer, continuity))
                    if (Convert.ToInt32(await entity.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("record.not_found", "The image owner is missing or deleted.");
                if(source is not null)
                {
                    using var sourceCheck=context.Command("SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer,source.StorageKey);
                    if(Convert.ToInt32(await sourceCheck.ExecuteScalarAsync(ct).ConfigureAwait(false))!=1)
                        throw new VaultCommandException("record.not_found","The image source is missing or deleted.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                var createdFile = false;
                if (File.Exists(absolute))
                {
                    var existing = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(encoded.Original)))
                        throw new VaultCommandException("image.hash_collision", "An existing asset has the same content name but different bytes.");
                }
                else
                {
                    var temp = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temp, encoded.Original, ct).ConfigureAwait(false);
                        File.Move(temp, absolute, false); createdFile = true;
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    context.OnRollback(() => { if (File.Exists(absolute)) File.Delete(absolute); });
                }
                var now = DateTime.UtcNow;
                using (var duplicate = context.Command("SELECT COUNT(*) FROM [EntityImages] WHERE [EntityId]=? AND [OriginalSha256]=?")
                           .Add(OleDbType.Integer, owner.StorageKey).Add(OleDbType.VarWChar, encoded.Hash, 64))
                    if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                        throw new VaultCommandException("image.duplicate", "That image is already attached to this entity.");
                using var existingImages = context.Command("SELECT COUNT(*) FROM [EntityImages] WHERE [EntityId]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, owner.StorageKey);
                var effectivePrimary = request.IsPrimary || Convert.ToInt32(await existingImages.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0;
                if (effectivePrimary)
                {
                    using var clear = context.Command("UPDATE [EntityImages] SET [IsPrimary]=False,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [EntityId]=? AND [IsPrimary]=True AND [IsDeleted]=False")
                        .Add(OleDbType.Date, now).Add(OleDbType.Integer, owner.StorageKey);
                    await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                using var insert = context.Command("INSERT INTO [EntityImages] ([EntityId],[SourceId],[Title],[Caption],[AltText],[Role],[CanonStatus],[IsPrimary],[OriginalRelativePath],[OriginalSha256],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, owner.StorageKey).Add(OleDbType.Integer, source?.StorageKey)
                    .Add(OleDbType.VarWChar, request.Title, 255).Add(OleDbType.LongVarWChar, request.Caption)
                    .Add(OleDbType.LongVarWChar, request.AltText).Add(OleDbType.VarWChar, request.Role, 100)
                    .Add(OleDbType.VarWChar, request.CanonStatus, 50).Add(OleDbType.Boolean, effectivePrimary)
                    .Add(OleDbType.VarWChar, relative, 255).Add(OleDbType.VarWChar, encoded.Hash, 64)
                    .Add(OleDbType.VarWChar, encoded.MediaType, 100).Add(OleDbType.Integer, encoded.Width)
                    .Add(OleDbType.Integer, encoded.Height).Add(OleDbType.Integer, encoded.Original.Length)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                var id = await IdentityAsync(context, ct).ConfigureAwait(false);
                await InsertRendition(context, id, "Display", encoded.Display, encoded.DisplayWidth, encoded.DisplayHeight, ct).ConfigureAwait(false);
                await InsertRendition(context, id, "Thumbnail", encoded.Thumbnail, encoded.ThumbWidth, encoded.ThumbHeight, ct).ConfigureAwait(false);
                using (var revision = context.Command("INSERT INTO [ImageContentVersions] ([ImageKind],[ImageId],[CurrentRevision]) VALUES ('EntityImage',?,1)")
                    .Add(OleDbType.Integer, id))
                    await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new VaultMutationOutcome("EntityImage", id.ToString(CultureInfo.InvariantCulture), 1, "attach",
                    new { owner = owner.Reference, isPrimary = effectivePrimary, encoded.Width, encoded.Height, transparencyFlattened = encoded.TransparencyFlattened, createdFile });
            }, token).ConfigureAwait(false);
        return encoded.TransparencyFlattened && result.Success
            ? result with { Message = "Transparency was flattened onto the requested background in the JPEG viewing renditions." }
            : result;
    }

    public async Task<V4Page<V4ImageMetadata>> ListAsync(V4ImageListRequest request, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        var owner = await targets.ImageOwnerAsync(request.Target, continuity,
            request.DeletionState != V4DeletionState.Active, token).ConfigureAwait(false);
        if (owner.ResourceType is "Continuity" or "CharacterRelationship" or "EntityEvent" or "RelationshipEvent")
            return await ListStoryAsync(owner, request, token).ConfigureAwait(false);
        ValidateLimit(request.Limit);
        var scope = $"images:{owner.Reference}:{request.Role}:{request.CanonStatus}:{request.DeletionState}";
        var after = request.Cursor is null ? 0 : checked((int)cursors.Decode(request.Cursor, "images", scope).Position);
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var rows = await ReadMetadataAsync(owner.StorageKey, request.Role, request.CanonStatus, request.DeletionState, after, request.Limit + 1, token).ConfigureAwait(false);
        var items = rows.Take(request.Limit).Select(r => Metadata(r, owner, revision)).ToArray();
        return new(items, rows.Count > request.Limit ? cursors.Encode("images", rows[request.Limit - 1].Id, scope) : null, rows.Count > request.Limit, revision);
    }

    public async Task<VaultMutationResult> UpdateAsync(V4ImageUpdateRequest request,CancellationToken token=default)
    {
        var allowed=new HashSet<string>(["title","caption","altText","role","canonStatus","isPrimary"],StringComparer.OrdinalIgnoreCase);
        try{V4SparseChangeValidator.Validate(request.Changes,allowed);}catch(VaultValidationException e){return new(false,e.Errors[0].Code,Message:e.Message);}
        try{ValidateChangeText(request.Changes,"title",255);ValidateChangeText(request.Changes,"caption",V4ContractLimits.MaximumLongTextLength);ValidateChangeText(request.Changes,"altText",V4ContractLimits.MaximumLongTextLength);ValidateChangeText(request.Changes,"role",100);ValidateChangeText(request.Changes,"canonStatus",50);}
        catch(VaultValidationException e){return new(false,e.Errors[0].Code,Message:e.Message);}
        var continuity=session.RequireContinuityId(); V4ResolvedTarget image;
        try{image=await targets.ImageAsync(request.ImageRef,continuity,false,token).ConfigureAwait(false);}catch(V4ResolutionException e){return new(false,e.Code,Message:e.Message);}
        if (image.ResourceType == "StoryImage")
            return await UpdateStoryAsync(image, request, token).ConfigureAwait(false);
        string operation;try{operation=references.OperationId(request.MutationToken);}catch(ArgumentException e){return new(false,"validation.mutation_token",Message:e.Message);}
        return await coordinator.ExecuteAsync(operation,"v4.image.update",request,"image_update",session.ClientLabel,async(context,ct)=>
        {
            using var find=context.Command("SELECT [EntityId],[Title],[Caption],[AltText],[Role],[CanonStatus],[IsPrimary],[Version] FROM [EntityImages] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer,image.StorageKey);
            var rows=await find.QueryAsync(r=>new{Owner=r.GetInt32(0),Title=r.IsDBNull(1)?null:r.GetString(1),Caption=r.IsDBNull(2)?null:r.GetString(2),Alt=r.IsDBNull(3)?null:r.GetString(3),Role=r.IsDBNull(4)?null:r.GetString(4),Status=r.IsDBNull(5)?null:r.GetString(5),Primary=r.GetBoolean(6),Version=r.GetInt32(7)},ct).ConfigureAwait(false);
            if(rows.Count!=1)throw new VaultCommandException("record.not_found","The image is missing or deleted.");var old=rows[0];if(old.Version!=request.ExpectedVersion)throw new VaultCommandException("concurrency.conflict",$"The current version is {old.Version}.",actualVersion:old.Version);
            var title=TextChange(request.Changes,"title",old.Title);var caption=TextChange(request.Changes,"caption",old.Caption);var alt=TextChange(request.Changes,"altText",old.Alt);var role=TextChange(request.Changes,"role",old.Role);var status=TextChange(request.Changes,"canonStatus",old.Status);var primary=request.Changes.TryGetValue("isPrimary",out var p)?p.GetBoolean():old.Primary;
            var now=DateTime.UtcNow;if(primary&&!old.Primary){using var clear=context.Command("UPDATE [EntityImages] SET [IsPrimary]=False,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [EntityId]=? AND [Id]<>? AND [IsPrimary]=True AND [IsDeleted]=False").Add(OleDbType.Date,now).Add(OleDbType.Integer,old.Owner).Add(OleDbType.Integer,image.StorageKey);await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
            if(old.Primary&&!primary)
            {
                using var successor=context.Command("SELECT TOP 1 [Id] FROM [EntityImages] WHERE [EntityId]=? AND [Id]<>? AND [IsDeleted]=False ORDER BY [Id]").Add(OleDbType.Integer,old.Owner).Add(OleDbType.Integer,image.StorageKey);
                var successorId=await successor.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if(successorId is null or DBNull)primary=true;
                else { using var promote=context.Command("UPDATE [EntityImages] SET [IsPrimary]=True,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=?").Add(OleDbType.Date,now).Add(OleDbType.Integer,Convert.ToInt32(successorId,CultureInfo.InvariantCulture));await promote.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            }
            using var update=context.Command("UPDATE [EntityImages] SET [Title]=?,[Caption]=?,[AltText]=?,[Role]=?,[CanonStatus]=?,[IsPrimary]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.VarWChar,title,255).Add(OleDbType.LongVarWChar,caption).Add(OleDbType.LongVarWChar,alt).Add(OleDbType.VarWChar,role,100).Add(OleDbType.VarWChar,status,50).Add(OleDbType.Boolean,primary).Add(OleDbType.Date,now).Add(OleDbType.Integer,image.StorageKey).Add(OleDbType.Integer,old.Version);
            if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new VaultCommandException("concurrency.conflict","The image changed.");return new VaultMutationOutcome("EntityImage",image.StorageKey.ToString(),old.Version+1,"update",new{title,role,canonStatus=status,isPrimary=primary},old.Version);
        },token).ConfigureAwait(false);
    }

    public Task<V4ImageContent> ViewAsync(V4ImageViewRequest request, CancellationToken token = default) =>
        coordinator.ExecuteConsistentReadAsync(() => ViewCoreAsync(request, token), token);

    private async Task<V4ImageContent> ViewCoreAsync(V4ImageViewRequest request, CancellationToken token)
    {
        var image = await targets.GlobalImageAsync(request.ImageRef, token).ConfigureAwait(false);
        var currentRevision = await CurrentContentRevisionAsync(image, token).ConfigureAwait(false);
        if (request.Revision is <= 0)
            throw new V4ResolutionException("image.revision_invalid", "Image content revisions must be positive.");
        if (request.Revision is { } selectedRevision && selectedRevision != currentRevision)
            return await ViewHistoricalAsync(request.ImageRef, image, request.Size,
                selectedRevision, token).ConfigureAwait(false);
        if (request.Size == V4ImageSize.Original)
            return await ViewOriginalAsync(request.ImageRef, image, token).ConfigureAwait(false);
        if (image.ResourceType == "StoryImage")
        {
            var storyContent = await EnrichViewAsync(image,
                await ViewStoryAsync(image, request.Size, token).ConfigureAwait(false), token)
                .ConfigureAwait(false);
            return storyContent with
            {
                View = storyContent.View with { ContentRevision = currentRevision }
            };
        }
        var ownerContinuity = image.ContinuityKey ??
            throw new V4ResolutionException("record.not_found", "The image has no continuity owner.");
        await using var connection = factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using (var continuityCheck = new AccessCommand(connection,
                   "SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False")
                   .Add(OleDbType.Integer, ownerContinuity))
            if (Convert.ToInt32(await continuityCheck.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                throw new V4ResolutionException("record.deleted", "The image's continuity is deleted.");
        using var command = new AccessCommand(connection, "SELECT i.[EntityId],i.[Title],i.[Caption],i.[AltText],i.[Role],i.[CanonStatus],i.[IsPrimary],i.[OriginalMediaType],i.[OriginalWidth],i.[OriginalHeight],i.[OriginalBytes],i.[Version],i.[IsDeleted],r.[MediaType],r.[Width],r.[Height],r.[ByteSize],r.[Content] FROM [EntityImages] AS i INNER JOIN [ImageRenditions] AS r ON i.[Id]=r.[ImageId] WHERE i.[Id]=? AND r.[RenditionKind]=?")
            .Add(OleDbType.Integer, image.StorageKey).Add(OleDbType.VarWChar, request.Size.ToString(), 20);
        var rows = await command.QueryAsync(r => new { Owner = r.GetInt32(0), Row = ReadImageRow(image.StorageKey, r, 1), Media = r.GetString(13), Width = r.GetInt32(14), Height = r.GetInt32(15), Bytes = r.GetInt32(16), Content = (byte[])r.GetValue(17) }, token).ConfigureAwait(false);
        if (rows.Count != 1) throw new V4ResolutionException("image.rendition_missing", "The requested image rendition is unavailable.");
        var ownerRef = await references.ReferenceAsync("CanonEntity", rows[0].Owner, token).ConfigureAwait(false);
        var owner = await targets.EntityAsync(ownerRef, ownerContinuity, true, token).ConfigureAwait(false);
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var ownerContinuityName = await references.ContinuityNameAsync(ownerContinuity, token).ConfigureAwait(false);
        var metadata = Metadata(rows[0].Row, owner, revision, ownerContinuityName);
        var currentContent = await EnrichViewAsync(image,
            new(new(metadata, request.Size, rows[0].Media, rows[0].Width,
                rows[0].Height, rows[0].Bytes, revision), rows[0].Content), token)
            .ConfigureAwait(false);
        return currentContent with
        {
            View = currentContent.View with { ContentRevision = currentRevision }
        };
    }

    private async Task<V4ImageContent> EnrichViewAsync(V4ResolvedTarget image,
        V4ImageContent content, CancellationToken token)
    {
        var table = image.ResourceType == "StoryImage" ? "StoryImages" : "EntityImages";
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                $"SELECT [SourceId],[CreatedAtUtc],[UpdatedAtUtc] FROM [{table}] WHERE [Id]=?")
            .Add(OleDbType.Integer, image.StorageKey);
        var rows = await command.QueryAsync(reader => new
        {
            Source = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0),
            Created = reader.GetDateTime(1), Updated = reader.GetDateTime(2)
        }, token).ConfigureAwait(false);
        if (rows.Count != 1)
            throw new V4ResolutionException("record.not_found", "The image is unavailable.");
        V4ReferenceSummary? source = null;
        if (rows[0].Source is { } sourceId)
        {
            var reference = await references.ReferenceAsync("Source", sourceId, token)
                .ConfigureAwait(false);
            var resolved = await references.ResolveAsync(reference, null, token, "Source")
                .ConfigureAwait(false);
            source = new(resolved.Reference, V4RecordKind.Source, resolved.Label,
                IsDeleted: resolved.IsDeleted);
        }
        var metadata = content.View.Image with
        {
            Source = source,
            CreatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(rows[0].Created, DateTimeKind.Utc)),
            UpdatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(rows[0].Updated, DateTimeKind.Utc))
        };
        return content with { View = content.View with { Image = metadata } };
    }

    private async Task<int> CurrentContentRevisionAsync(V4ResolvedTarget image,
        CancellationToken token)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                "SELECT [CurrentRevision] FROM [ImageContentVersions] WHERE [ImageKind]=? AND [ImageId]=?")
            .Add(OleDbType.VarWChar, image.ResourceType, 20)
            .Add(OleDbType.Integer, image.StorageKey);
        var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (result is null or DBNull)
            throw new V4ResolutionException("image.revision_missing", "The image content revision is unavailable.");
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private async Task<V4ImageContent> ViewHistoricalAsync(string imageRef,
        V4ResolvedTarget image, V4ImageSize size, int revision, CancellationToken token)
    {
        // The current read enforces the same active owner and continuity policy
        // for every retained revision before historical bytes are accessed.
        var current = await ViewAsync(new(imageRef, V4ImageSize.Display), token)
            .ConfigureAwait(false);
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                "SELECT [OriginalRelativePath],[OriginalSha256],[OriginalMediaType]," +
                "[OriginalWidth],[OriginalHeight],[OriginalBytes],[DisplayMediaType]," +
                "[DisplayWidth],[DisplayHeight],[DisplayBytes],[DisplaySha256],[DisplayContent]," +
                "[ThumbnailMediaType],[ThumbnailWidth],[ThumbnailHeight],[ThumbnailBytes]," +
                "[ThumbnailSha256],[ThumbnailContent] FROM [ImageHistoricalContent] " +
                "WHERE [ImageKind]=? AND [ImageId]=? AND [ContentRevision]=?")
            .Add(OleDbType.VarWChar, image.ResourceType, 20)
            .Add(OleDbType.Integer, image.StorageKey)
            .Add(OleDbType.Integer, revision);
        var rows = await command.QueryAsync(reader => new
        {
            Path = reader.GetString(0), Hash = reader.GetString(1),
            OriginalMedia = reader.GetString(2), OriginalWidth = reader.GetInt32(3),
            OriginalHeight = reader.GetInt32(4), OriginalBytes = reader.GetInt32(5),
            DisplayMedia = reader.GetString(6), DisplayWidth = reader.GetInt32(7),
            DisplayHeight = reader.GetInt32(8), DisplayBytes = reader.GetInt32(9),
            DisplayHash = reader.GetString(10), Display = (byte[])reader.GetValue(11),
            ThumbMedia = reader.GetString(12), ThumbWidth = reader.GetInt32(13),
            ThumbHeight = reader.GetInt32(14), ThumbBytes = reader.GetInt32(15),
            ThumbHash = reader.GetString(16), Thumb = (byte[])reader.GetValue(17)
        }, token).ConfigureAwait(false);
        if (rows.Count != 1)
            throw new V4ResolutionException("image.revision_missing",
                "That image content revision is unavailable.");
        var row = rows[0];
        var (media, width, height, expectedBytes, bytes) = size switch
        {
            V4ImageSize.Thumbnail => (row.ThumbMedia, row.ThumbWidth,
                row.ThumbHeight, row.ThumbBytes, row.Thumb),
            V4ImageSize.Display => (row.DisplayMedia, row.DisplayWidth,
                row.DisplayHeight, row.DisplayBytes, row.Display),
            V4ImageSize.Original => (row.OriginalMedia, row.OriginalWidth,
                row.OriginalHeight, row.OriginalBytes,
                await ReadVerifiedOriginalAsync(row.Path, row.Hash, row.OriginalBytes, token)
                    .ConfigureAwait(false)),
            _ => throw new V4ResolutionException("image.size_invalid", "The image size is unsupported.")
        };
        if (bytes.Length != expectedBytes)
            throw new V4ResolutionException("image.revision_corrupt",
                "The image revision's stored byte count does not match its content.");
        if (size is V4ImageSize.Display or V4ImageSize.Thumbnail)
        {
            var expectedHash = size == V4ImageSize.Display ? row.DisplayHash : row.ThumbHash;
            if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),
                    Convert.FromHexString(expectedHash)))
                throw new V4ResolutionException("image.revision_corrupt",
                    "The image revision failed its stored checksum.");
        }
        return new(new(current.View.Image, size, media, width, height,
            expectedBytes, current.View.ObservedRevision, revision, false), bytes);
    }

    private async Task<V4ImageContent> ViewOriginalAsync(string imageRef,
        V4ResolvedTarget image, CancellationToken token)
    {
        // A display read applies the same active-owner and continuity checks to
        // both image tables before the original asset is exposed.
        var display = await ViewAsync(new(imageRef, V4ImageSize.Display), token)
            .ConfigureAwait(false);
        var table = image.ResourceType == "StoryImage" ? "StoryImages" : "EntityImages";
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                $"SELECT [OriginalRelativePath],[OriginalSha256],[OriginalMediaType]," +
                $"[OriginalWidth],[OriginalHeight],[OriginalBytes] FROM [{table}] WHERE [Id]=?")
            .Add(OleDbType.Integer, image.StorageKey);
        var rows = await command.QueryAsync(reader => new
        {
            Path = reader.GetString(0), Hash = reader.GetString(1),
            MediaType = reader.GetString(2), Width = reader.GetInt32(3),
            Height = reader.GetInt32(4), Bytes = reader.GetInt32(5)
        }, token).ConfigureAwait(false);
        if (rows.Count != 1)
            throw new V4ResolutionException("image.original_missing",
                "The original image is unavailable.");
        var row = rows[0];
        if (row.Bytes is < 1 or > V4ContractLimits.OriginalImageMaximumBytes ||
            row.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new V4ResolutionException("image.original_invalid",
                "The original image metadata is invalid.");
        var bytes = await ReadVerifiedOriginalAsync(row.Path, row.Hash, row.Bytes, token)
            .ConfigureAwait(false);
        var viewed = new V4ImageViewResult(display.View.Image, V4ImageSize.Original,
            row.MediaType, row.Width, row.Height, bytes.Length,
            display.View.ObservedRevision, display.View.ContentRevision, true);
        return new(viewed, bytes);
    }

    private async Task<byte[]> ReadVerifiedOriginalAsync(string relativePath,
        string sha256, int expectedBytes, CancellationToken token)
    {
        if (expectedBytes is < 1 or > V4ContractLimits.OriginalImageMaximumBytes)
            throw new V4ResolutionException("image.original_invalid",
                "The original image metadata is invalid.");
        var path = SafeAssetPath(relativePath);
        var root = Path.GetFullPath(Path.Combine(storageRoot, "assets"));
        for (var part = Path.GetDirectoryName(path); part is not null &&
                 part.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             part = Path.GetDirectoryName(part))
        {
            if (!Directory.Exists(part))
                throw new V4ResolutionException("image.original_missing",
                    "The original image is unavailable.");
            if (File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                throw new V4ResolutionException("image.asset_link",
                    "The original image path contains a linked directory.");
        }
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new V4ResolutionException("image.original_missing",
                "The original image is unavailable.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(sha256); }
        catch (FormatException)
        { throw new V4ResolutionException("image.original_corrupt", "The original image failed its stored checksum."); }
        if (bytes.Length != expectedBytes || expectedHash.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), expectedHash))
            throw new V4ResolutionException("image.original_corrupt",
                "The original image failed its stored checksum.");
        return bytes;
    }

    public async Task<V4Page<V4ImageMetadata>> SearchAsync(V4ImageSearchRequest request, CancellationToken token = default)
    {
        int? continuity = request.AcrossContinuities ? null : session.RequireContinuityId();
        ValidateLimit(request.Limit);
        if(request.Text is {Length:>V4ContractLimits.MaximumSearchTextLength})throw Error("search.text_too_long","text",$"Search text cannot exceed {V4ContractLimits.MaximumSearchTextLength} characters.");
        if (request.Entities is { Count: > 100 } || request.EntityKinds is { Count: > 20 } || request.Roles is { Count: > 100 } || request.CanonStatuses is { Count: > 100 })
            throw Error("image.filter_too_large", "filters", "Image filter lists are too large.");
        var ownerIds = new HashSet<int>();
        if (request.Entities is not null)
            foreach (var value in request.Entities)
                ownerIds.Add((request.AcrossContinuities
                    ? await targets.GlobalEntityAsync(value, token).ConfigureAwait(false)
                    : await targets.EntityAsync(value, continuity!.Value, token).ConfigureAwait(false)).StorageKey);
        var scope = $"image-search:v2:{(continuity is null ? "global" : continuity.Value.ToString(CultureInfo.InvariantCulture))}:{request.Text}:{string.Join(',',request.Entities??[])}:{string.Join(',',request.EntityKinds??[])}:{string.Join(',',request.Roles??[])}:{string.Join(',',request.CanonStatuses??[])}:{request.DeletionState}";
        var afterKey = request.Cursor is null ? 0L : cursors.Decode(request.Cursor,"image-search",scope).Position;
        var after = checked((int)(afterKey / 2));
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var predicates=new List<string>{"c.[IsDeleted]=False","c.[ContinuityId] IN (SELECT [Id] FROM [Continuities] WHERE [IsDeleted]=False)","i.[Id]>?"};
        var parameters=new List<(OleDbType Type,object? Value,int? Size)>{(OleDbType.Integer,after,null)};
        if (continuity is not null) { predicates.Insert(0,"c.[ContinuityId]=?"); parameters.Insert(0,(OleDbType.Integer,continuity.Value,null)); }
        void AddList<T>(string column,IReadOnlyCollection<T>? values,OleDbType type,int size)
        {
            if(values is not {Count:>0})return;predicates.Add($"{column} IN ({string.Join(',',values.Select(_=>"?"))})");
            parameters.AddRange(values.Select(value=>(type,(object?)Convert.ToString(value,CultureInfo.InvariantCulture),(int?)size)));
        }
        if(ownerIds.Count>0){predicates.Add($"i.[EntityId] IN ({string.Join(',',ownerIds.Select(_=>"?"))})");parameters.AddRange(ownerIds.Select(value=>(OleDbType.Integer,(object?)value,(int?)null)));}
        AddList("c.[EntityType]",request.EntityKinds?.Select(value=>value.ToString()).ToArray(),OleDbType.VarWChar,30);
        AddList("i.[Role]",request.Roles,OleDbType.VarWChar,100);AddList("i.[CanonStatus]",request.CanonStatuses,OleDbType.VarWChar,50);
        predicates.Add(request.DeletionState switch{V4DeletionState.Active=>"i.[IsDeleted]=False",V4DeletionState.Deleted=>"i.[IsDeleted]=True",_=>"1=1"});
        if(!string.IsNullOrWhiteSpace(request.Text))
        {
            predicates.Add("(i.[Title] LIKE ? OR i.[Caption] LIKE ? OR i.[AltText] LIKE ?)");var pattern=$"%{EscapeLike(request.Text.Trim())}%";
            parameters.Add((OleDbType.VarWChar,pattern,255));parameters.Add((OleDbType.LongVarWChar,pattern,null));parameters.Add((OleDbType.LongVarWChar,pattern,null));
        }
        var label="IIf(c.[EntityType]='Project',p.[Name],IIf(c.[EntityType]='Location',l.[Name],IIf(c.[EntityType]='Character',IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]),IIf(c.[EntityType]='Organization',o.[Name],IIf(c.[EntityType]='Object',ob.[Name],w.[Title])))))";
        var sql=$"SELECT TOP {request.Limit+1} i.[Id],i.[EntityId],i.[Title],i.[Caption],i.[AltText],i.[Role],i.[CanonStatus],i.[IsPrimary],i.[OriginalMediaType],i.[OriginalWidth],i.[OriginalHeight],i.[OriginalBytes],i.[Version],i.[IsDeleted],c.[EntityType],{label},c.[ContinuityId] FROM ((((((([EntityImages] AS i INNER JOIN [CanonEntities] AS c ON i.[EntityId]=c.[Id]) LEFT JOIN [Projects] AS p ON c.[Id]=p.[EntityId]) LEFT JOIN [Locations] AS l ON c.[Id]=l.[EntityId]) LEFT JOIN [Characters] AS ch ON c.[Id]=ch.[EntityId]) LEFT JOIN [Organizations] AS o ON c.[Id]=o.[EntityId]) LEFT JOIN [Objects] AS ob ON c.[Id]=ob.[EntityId]) LEFT JOIN [WorldEvents] AS w ON c.[Id]=w.[EntityId]) WHERE {string.Join(" AND ",predicates)} ORDER BY i.[Id]";
        await using var connection=factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);using var command=new AccessCommand(connection,sql);
        foreach(var parameter in parameters)command.Add(parameter.Type,parameter.Value,parameter.Size);
        var selected=(await command.QueryAsync(r=>new{Id=r.GetInt32(0),Owner=r.GetInt32(1),Row=ReadImageRow(r.GetInt32(0),r,2),Type=r.GetString(14),Label=r.GetString(15),Continuity=r.GetInt32(16)},token).ConfigureAwait(false)).ToArray();
        var names = new Dictionary<int,string>();
        if (request.AcrossContinuities)
        {
            using var nameQuery = new AccessCommand(connection,"SELECT [Id],[Name] FROM [Continuities] WHERE [IsDeleted]=False");
            foreach (var row in await nameQuery.QueryAsync(r=>(Id:r.GetInt32(0),Name:r.GetString(1)),token).ConfigureAwait(false))
                names[row.Id]=row.Name;
        }
        var entityHits = selected.Select(row => (
            Key: (long)row.Id * 2,
            Image: Metadata(row.Row,
                new V4ResolvedTarget(row.Type, row.Owner,
                    references.ReferenceFromKnownRecord(row.Type, row.Owner, row.Label),
                    row.Label, row.Continuity, false), revision,
                request.AcrossContinuities ? names[row.Continuity] : session.ContinuityName)))
            .ToArray();
        var storyHits = await SearchStoryAsync(request, continuity, afterKey, revision, token)
            .ConfigureAwait(false);
        var combined = entityHits.Concat(storyHits).OrderBy(item => item.Key)
            .Take(request.Limit + 1).ToArray();
        var items = combined.Take(request.Limit).Select(item => item.Image).ToArray();
        return new(items, combined.Length > request.Limit
            ? cursors.Encode("image-search", combined[request.Limit - 1].Key, scope)
            : null, combined.Length > request.Limit, revision);
    }

    public async Task<(long RenditionBytes, string CapacityBand)> CapacityAsync(CancellationToken token = default)
    {
        await using var connection = factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        long bytes = 0;
        foreach (var table in new[] { "ImageRenditions", "StoryImageRenditions" })
        {
            using var command = new AccessCommand(connection, $"SELECT SUM([ByteSize]) FROM [{table}]");
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            bytes += value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        using (var historical = new AccessCommand(connection,
                   "SELECT SUM([DisplayBytes]+[ThumbnailBytes]) FROM [ImageHistoricalContent]"))
        {
            var value = await historical.ExecuteScalarAsync(token).ConfigureAwait(false);
            bytes += value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        var dbBytes = new FileInfo(databasePath).Length;
        var band = dbBytes >= DatabaseHardLimitBytes ? "critical" : dbBytes >= DatabaseWarningBytes ? "warning" : "normal";
        return (bytes, band);
    }

    private sealed record ImageRow(int Id, string? Title, string? Caption, string? Alt, string? Role, string? Status,
        bool Primary, string Media, int Width, int Height, int Bytes, int Version, bool Deleted);
    private async Task<IReadOnlyList<ImageRow>> ReadMetadataAsync(int owner, string? role, string? status, V4DeletionState deletion, int after, int take, CancellationToken token)
    {
        await using var connection = factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        var sql = $"SELECT TOP {take} [Id],[Title],[Caption],[AltText],[Role],[CanonStatus],[IsPrimary],[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes],[Version],[IsDeleted] FROM [EntityImages] WHERE [EntityId]=? AND [Id]>?" +
                  (role is null ? "" : " AND [Role]=?") + (status is null ? "" : " AND [CanonStatus]=?") + deletion switch { V4DeletionState.Active => " AND [IsDeleted]=False", V4DeletionState.Deleted => " AND [IsDeleted]=True", _ => "" } + " ORDER BY [Id]";
        using var command = new AccessCommand(connection, sql).Add(OleDbType.Integer, owner).Add(OleDbType.Integer, after);
        if (role is not null) command.Add(OleDbType.VarWChar, role, 100); if (status is not null) command.Add(OleDbType.VarWChar, status, 50);
        return await command.QueryAsync(r => ReadImageRow(r.GetInt32(0), r, 1), token).ConfigureAwait(false);
    }
    private static ImageRow ReadImageRow(int id, System.Data.Common.DbDataReader r, int o) => new(id,
        r.IsDBNull(o)?null:r.GetString(o), r.IsDBNull(o+1)?null:r.GetString(o+1), r.IsDBNull(o+2)?null:r.GetString(o+2),
        r.IsDBNull(o+3)?null:r.GetString(o+3), r.IsDBNull(o+4)?null:r.GetString(o+4), r.GetBoolean(o+5), r.GetString(o+6),
        r.GetInt32(o+7),r.GetInt32(o+8),r.GetInt32(o+9),r.GetInt32(o+10),r.GetBoolean(o+11));
    private V4ImageMetadata Metadata(ImageRow r, V4ResolvedTarget owner, string revision, string? continuityName = null,
        string imageType = "EntityImage") => new(
        references.ReferenceFromKnownRecord(imageType, r.Id, r.Title ?? "image"),
        new(owner.Reference, AccessV4ReadService.Kind(owner.ResourceType), owner.Label, ContinuityName: continuityName ?? session.ContinuityName, IsDeleted: owner.IsDeleted),
        r.Title,r.Caption,r.Alt,r.Role,r.Status,r.Primary,r.Media,r.Width,r.Height,r.Bytes,r.Version,r.Deleted,revision);

    private EncodedImage Decode(V4InlineImageInput input, string background)
    {
        var bytes = DecodePayload(input);
        if (bytes.LongLength > V4ContractLimits.MaximumImageInputBytes) throw Error("image.too_large", "image", "Image input exceeds 20 MB.");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw Error("image.malformed", "image", "The image cannot be identified safely.");
        var info = codec.Info;
        var detected = codec.EncodedFormat switch { SKEncodedImageFormat.Png => "image/png", SKEncodedImageFormat.Jpeg => "image/jpeg", SKEncodedImageFormat.Webp => "image/webp", _ => null };
        if (detected is not ("image/png" or "image/jpeg" or "image/webp")) throw Error("image.type_unsupported", "image.mediaType", "Only PNG, JPEG, and WebP are supported.");
        if (!string.Equals(input.MediaType, detected, StringComparison.OrdinalIgnoreCase)) throw Error("image.type_mismatch", "image.mediaType", "Declared media type does not match the image bytes.");
        if(info.Width<=0||info.Height<=0||(long)info.Width*info.Height>MaxPixels)throw Error("image.pixel_limit","image","Decoded dimensions must be positive and cannot exceed 50 million pixels.");
        if (codec.FrameCount > 1) throw Error("image.frames", "image", "Animated or multi-frame images are not supported.");
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var decoded = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
            throw Error("image.malformed", "image", "The image could not be decoded safely.");
        using var source = Orient(decoded, codec.EncodedOrigin);
        var flatten = detected is "image/png" or "image/webp" && info.AlphaType != SKAlphaType.Opaque;
        SKColor backgroundColor;
        try { backgroundColor = SKColor.Parse(background); } catch { throw Error("image.background", "backgroundColor", "Background color must be a hex color."); }
        var display = EncodeRendition(source, backgroundColor, V4ContractLimits.DisplayImageMaximumPixels, V4ContractLimits.DisplayImageMaximumBytes);
        var thumb = EncodeRendition(source, backgroundColor, V4ContractLimits.ThumbnailMaximumPixels, V4ContractLimits.ThumbnailMaximumBytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var ext = detected switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        return new(bytes, detected, ext, source.Width, source.Height, hash, display.Bytes, display.Width, display.Height, thumb.Bytes, thumb.Width, thumb.Height, flatten);
    }
    private static (byte[] Bytes,int Width,int Height) EncodeRendition(SKBitmap source, SKColor background, int maxEdge, int maxBytes)
    {
        var scale=Math.Min(1d,Math.Min((double)maxEdge/source.Width,(double)maxEdge/source.Height)); var width=Math.Max(1,(int)Math.Round(source.Width*scale)); var height=Math.Max(1,(int)Math.Round(source.Height*scale));
        using var rendered=new SKBitmap(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Opaque,SKColorSpace.CreateSrgb()));
        using(var canvas=new SKCanvas(rendered)){canvas.Clear(background);using var paint=new SKPaint{IsAntialias=true};canvas.DrawBitmap(source,new SKRect(0,0,width,height),new SKSamplingOptions(SKCubicResampler.Mitchell),paint);canvas.Flush();}
        using var image=SKImage.FromBitmap(rendered);
        for (var quality=95; quality>=40; quality-=5) { using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,quality); var result=encoded.ToArray(); if(result.Length<=maxBytes) return (result,width,height); }
        throw Error("image.rendition_too_large", "image", "A safe JPEG rendition could not fit the configured byte limit.");
    }
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(result.Width,0); canvas.Scale(-1,1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(result.Width,result.Height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0,result.Height); canvas.Scale(1,-1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1,-1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(result.Width,0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(result.Width,result.Height); canvas.RotateDegrees(-90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0,result.Height); canvas.RotateDegrees(-90); canvas.Scale(1,-1); break;
        }
        canvas.DrawBitmap(source,0,0,new SKSamplingOptions(),null); canvas.Flush(); return result;
    }
    private static byte[] DecodePayload(V4InlineImageInput input)
    {
        if ((input.DataBase64 is null) == (input.DataUrl is null)) throw Error("image.payload", "image", "Supply exactly one of dataBase64 or dataUrl.");
        var encoded=input.DataBase64; if(input.DataUrl is not null) { var comma=input.DataUrl.IndexOf(','); if(comma<0 || !input.DataUrl[..comma].Contains(";base64",StringComparison.OrdinalIgnoreCase)) throw Error("image.data_url", "image.dataUrl", "A base64 data URL is required."); encoded=input.DataUrl[(comma+1)..]; }
        if (encoded!.Length > (V4ContractLimits.MaximumImageInputBytes * 4L / 3L) + 16) throw Error("image.too_large", "image", "Image input exceeds 20 MB.");
        try { return Convert.FromBase64String(encoded); } catch(FormatException) { throw Error("image.base64", "image", "Image base64 is malformed."); }
    }
    private static string? TextChange(IReadOnlyDictionary<string,System.Text.Json.JsonElement> changes,string key,string? fallback)=>!changes.TryGetValue(key,out var value)?fallback:value.ValueKind==System.Text.Json.JsonValueKind.Null?null:value.GetString();
    private static void ValidateText(string? value,int maximum,string field){if(value?.Length>maximum)throw Error("validation.text_too_long",field,$"{field} cannot exceed {maximum} characters.");}
    private static void ValidateChangeText(IReadOnlyDictionary<string,System.Text.Json.JsonElement> changes,string key,int maximum){if(changes.TryGetValue(key,out var value)&&value.ValueKind!=System.Text.Json.JsonValueKind.Null)ValidateText(value.GetString(),maximum,key);}
    private static string EscapeLike(string value)=>value.Replace("[","[[]",StringComparison.Ordinal).Replace("%","[%]",StringComparison.Ordinal).Replace("_","[_]",StringComparison.Ordinal);
    private string SafeAssetPath(string relative)
    {
        var root = Path.GetFullPath(Path.Combine(storageRoot, "assets"));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw Error("image.path", "image", "Asset path escaped storage root.");
        for (var directory = Path.GetDirectoryName(full); directory is not null &&
                 directory.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             directory = Path.GetDirectoryName(directory))
            if (Directory.Exists(directory) &&
                File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw Error("image.asset_link", "image",
                    "The asset path contains a linked directory.");
        if (File.Exists(full) &&
            File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw Error("image.asset_link", "image", "The image asset is a linked file.");
        return full;
    }
    private static async Task InsertRendition(VaultWriteContext c,int id,string kind,byte[] bytes,int width,int height,CancellationToken token,bool story=false){var table=story?"StoryImageRenditions":"ImageRenditions";using var q=c.Command($"INSERT INTO [{table}] ([ImageId],[RenditionKind],[MediaType],[Width],[Height],[ByteSize],[Content]) VALUES (?,?,'image/jpeg',?,?,?,?)").Add(OleDbType.Integer,id).Add(OleDbType.VarWChar,kind,20).Add(OleDbType.Integer,width).Add(OleDbType.Integer,height).Add(OleDbType.Integer,bytes.Length).Add(OleDbType.LongVarBinary,bytes,bytes.Length);await q.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
    private static async Task<int> IdentityAsync(VaultWriteContext c,CancellationToken token){using var q=c.Command("SELECT @@IDENTITY");return Convert.ToInt32(await q.ExecuteScalarAsync(token).ConfigureAwait(false));}
    private static void ValidateLimit(int limit){if(limit<1||limit>V4ContractLimits.MaximumPageSize)throw Error("page.limit","limit","Invalid page limit.");}
    private static VaultValidationException Error(string code,string field,string message)=>new([new(code,field,message)]);

}
