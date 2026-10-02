using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ImageService
{
    public Task<V4ImageRevisionHistoryResult> RevisionHistoryAsync(
        V4ImageRevisionHistoryRequest request, CancellationToken token = default) =>
        coordinator.ExecuteConsistentReadAsync(() => RevisionHistoryCoreAsync(request, token), token);

    private async Task<V4ImageRevisionHistoryResult> RevisionHistoryCoreAsync(
        V4ImageRevisionHistoryRequest request, CancellationToken token)
    {
        // A normal current read enforces the complete active owner chain.
        var current = await ViewAsync(new(request.ImageRef, V4ImageSize.Thumbnail), token)
            .ConfigureAwait(false);
        if (request.Limit is < 1 or > 100)
            throw new V4ResolutionException("image.history_limit",
                "Image history limit must be between 1 and 100.");
        if (request.BeforeRevision is < 1 ||
            request.BeforeRevision > current.View.ContentRevision)
            throw new V4ResolutionException("image.revision_invalid",
                "The requested image revision boundary is invalid.");
        var includeCurrent = request.BeforeRevision is null;
        var before = request.BeforeRevision ?? current.View.ContentRevision;
        var historicalLimit = request.Limit - (includeCurrent ? 1 : 0);
        var image = await targets.GlobalImageAsync(request.ImageRef, token)
            .ConfigureAwait(false);
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                $"SELECT TOP {historicalLimit + 1} [ContentRevision],[OriginalMediaType]," +
                "[OriginalWidth],[OriginalHeight],[OriginalBytes],[ArchivedAtUtc] " +
                "FROM [ImageHistoricalContent] WHERE [ImageKind]=? AND [ImageId]=? " +
                "AND [ContentRevision]<? " +
                "ORDER BY [ContentRevision] DESC")
            .Add(OleDbType.VarWChar, image.ResourceType, 20)
            .Add(OleDbType.Integer, image.StorageKey)
            .Add(OleDbType.Integer, before);
        var rows = await command.QueryAsync(reader =>
            new V4ImageContentRevision(reader.GetInt32(0), false, reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5),
                    DateTimeKind.Utc))), token).ConfigureAwait(false);
        var revisions = new List<V4ImageContentRevision>();
        if (includeCurrent)
            revisions.Add(new(current.View.ContentRevision, true, current.View.Image.MediaType,
                current.View.Image.Width, current.View.Image.Height,
                current.View.Image.ByteCount, null));
        revisions.AddRange(rows.Take(historicalLimit));
        var hasMore = rows.Count > historicalLimit;
        return new(request.ImageRef, current.View.ContentRevision, revisions,
            hasMore, hasMore ? revisions[^1].Revision : null,
            current.View.ObservedRevision);
    }

    public async Task<VaultMutationResult> ReplaceAsync(V4ImageReplaceRequest request,
        CancellationToken token = default)
    {
        EncodedImage? encoded = null;
        try
        {
            ValidateImageInput(request.Image, request.File, request.BackgroundColor);
            if (request.Image is not null) encoded = Decode(request.Image, request.BackgroundColor);
        }
        catch (VaultValidationException error)
        { return new(false, error.Errors[0].Code, Message: error.Message); }
        var continuity = session.RequireContinuityId();
        V4ResolvedTarget image;
        try
        {
            // Resolve deleted images so the journal can replay an earlier
            // success after a later lifecycle change. The transaction below
            // enforces the active image and owner chain for new operations.
            image = await targets.ImageAsync(request.ImageRef, continuity, true, token)
                .ConfigureAwait(false);
        }
        catch (V4ResolutionException error)
        { return new(false, error.Code, Message: error.Message); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException error)
        { return new(false, "validation.mutation_token", Message: error.Message); }
        var kind = image.ResourceType;
        var table = kind == "StoryImage" ? "StoryImages" : "EntityImages";
        var renditionTable = kind == "StoryImage" ? "StoryImageRenditions" : "ImageRenditions";
        object journalInput;
        if (request.File is { } file)
        {
            journalInput = HostImportInput(request with { File = null, Image = null }, file, image.Reference, continuity);
            if (await coordinator.TryReplayAsync(operation, "v4.image.replace", journalInput, token).ConfigureAwait(false) is { } replay)
                return replay;
            if (await PreflightImportAsync(image, continuity, null, request.ExpectedVersion, token).ConfigureAwait(false) is { } failure)
                return failure;
            var download = await DownloadImageAsync(file, request.BackgroundColor, token).ConfigureAwait(false);
            if (download.Failure is not null) return download.Failure;
            encoded = download.Image!;
        }
        else journalInput = new
        {
            request.MutationToken, request.ImageRef, request.ExpectedVersion,
            imageSha256 = encoded!.Hash, request.Image!.MediaType,
            request.BackgroundColor
        };
        var relative = Path.Combine("originals", encoded!.Hash[..2], encoded.Hash + encoded.Extension);
        var absolute = SafeAssetPath(relative);
        return await coordinator.ExecuteAsync(operation, "v4.image.replace", journalInput,
            "image_replace", session.ClientLabel, async (context, ct) =>
        {
            using var find = context.Command(
                    $"SELECT [Version],[OriginalRelativePath],[OriginalSha256]," +
                    $"[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes] " +
                    $"FROM [{table}] WHERE [Id]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, image.StorageKey);
            var rows = await find.QueryAsync(reader => new
            {
                Version = reader.GetInt32(0), Path = reader.GetString(1),
                Hash = reader.GetString(2), Media = reader.GetString(3),
                Width = reader.GetInt32(4), Height = reader.GetInt32(5),
                Bytes = reader.GetInt32(6)
            }, ct).ConfigureAwait(false);
            if (rows.Count != 1)
                throw new VaultCommandException("record.not_found", "The image is missing or deleted.");
            var old = rows[0];
            if (kind == "StoryImage")
            {
                using var owner = context.Command(
                        "SELECT [OwnerKind],[ContinuityId],[RelationshipId]," +
                        "[EntityEventId],[RelationshipEventId] FROM [StoryImages] WHERE [Id]=?")
                    .Add(OleDbType.Integer, image.StorageKey);
                var owners = await owner.QueryAsync(reader => new
                {
                    Kind = reader.GetString(0), Continuity = reader.GetInt32(1),
                    OwnerId = reader.GetString(0) switch
                    {
                        "Continuity" => reader.GetInt32(1),
                        "Relationship" => reader.GetInt32(2),
                        "EntityEvent" => reader.GetInt32(3),
                        "RelationshipEvent" => reader.GetInt32(4),
                        _ => 0
                    }
                }, ct).ConfigureAwait(false);
                if (owners.Count != 1)
                    throw new VaultCommandException("record.not_found", "The image owner is missing.");
                using (var continuity = context.Command(
                        "SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, owners[0].Continuity))
                    if (Convert.ToInt32(await continuity.ExecuteScalarAsync(ct)
                            .ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("record.not_found",
                            "The image continuity is missing or deleted.");
                await RequireActiveStoryOwnerAsync(context, owners[0].Kind,
                    owners[0].OwnerId, owners[0].Continuity, ct).ConfigureAwait(false);
            }
            else
            {
                using var owner = context.Command(
                        "SELECT COUNT(*) FROM ([EntityImages] AS i INNER JOIN " +
                        "[CanonEntities] AS e ON i.[EntityId]=e.[Id]) INNER JOIN " +
                        "[Continuities] AS c ON e.[ContinuityId]=c.[Id] " +
                        "WHERE i.[Id]=? AND e.[IsDeleted]=False AND c.[IsDeleted]=False")
                    .Add(OleDbType.Integer, image.StorageKey);
                if (Convert.ToInt32(await owner.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                    throw new VaultCommandException("record.not_found", "The image owner is missing or deleted.");
            }
            if (old.Version != request.ExpectedVersion)
                throw new VaultCommandException("concurrency.conflict",
                    $"The current version is {old.Version}.", actualVersion: old.Version);
            using var version = context.Command(
                    "SELECT [CurrentRevision] FROM [ImageContentVersions] " +
                    "WHERE [ImageKind]=? AND [ImageId]=?")
                .Add(OleDbType.VarWChar, kind, 20)
                .Add(OleDbType.Integer, image.StorageKey);
            var value = await version.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (value is null or DBNull)
                throw new VaultCommandException("image.revision_missing",
                    "The current image revision is missing.");
            var currentRevision = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            var nextRevision = checked(currentRevision + 1);
            using var readRenditions = context.Command(
                    $"SELECT [RenditionKind],[MediaType],[Width],[Height],[ByteSize],[Content] " +
                    $"FROM [{renditionTable}] WHERE [ImageId]=?")
                .Add(OleDbType.Integer, image.StorageKey);
            var renditions = await readRenditions.QueryAsync(reader => new
            {
                Kind = reader.GetString(0), Media = reader.GetString(1),
                Width = reader.GetInt32(2), Height = reader.GetInt32(3),
                Bytes = reader.GetInt32(4), Content = (byte[])reader.GetValue(5)
            }, ct).ConfigureAwait(false);
            if (renditions.Count != 2 || renditions.Any(row => row.Content.Length != row.Bytes))
                throw new VaultCommandException("image.rendition_missing",
                    "The current image renditions are incomplete.");
            var display = renditions.SingleOrDefault(row => row.Kind == "Display");
            var thumb = renditions.SingleOrDefault(row => row.Kind == "Thumbnail");
            if (display is null || thumb is null)
                throw new VaultCommandException("image.rendition_missing",
                    "The current image renditions are incomplete.");
            if (old.Hash.Equals(encoded.Hash, StringComparison.OrdinalIgnoreCase) &&
                display.Content.AsSpan().SequenceEqual(encoded.Display) &&
                thumb.Content.AsSpan().SequenceEqual(encoded.Thumbnail))
                throw new VaultCommandException("image.same_content",
                    "The replacement has the same original and rendered content as the current image.");
            var projected = new FileInfo(databasePath).Length +
                display.Bytes + thumb.Bytes + encoded.Display.Length +
                encoded.Thumbnail.Length + 8192;
            if (projected > DatabaseHardLimitBytes)
                throw new VaultCommandException("capacity.database_limit",
                    "Retaining the prior image rendition would exceed the supported database limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            if (File.Exists(absolute))
            {
                var existing = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(existing), SHA256.HashData(encoded.Original)))
                    throw new VaultCommandException("image.hash_collision",
                        "An existing asset has the same content name but different bytes.");
            }
            else
            {
                var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, encoded.Original, ct)
                        .ConfigureAwait(false);
                    File.Move(temporary, absolute, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                context.OnRollback(() => { if (File.Exists(absolute)) File.Delete(absolute); });
            }
            var now = DateTime.UtcNow;
            using (var archive = context.Command(
                "INSERT INTO [ImageHistoricalContent] ([ImageKind],[ImageId]," +
                "[ContentRevision],[OriginalRelativePath],[OriginalSha256]," +
                "[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes]," +
                "[DisplayMediaType],[DisplayWidth],[DisplayHeight],[DisplayBytes]," +
                "[DisplaySha256],[DisplayContent],[ThumbnailMediaType],[ThumbnailWidth],[ThumbnailHeight]," +
                "[ThumbnailBytes],[ThumbnailSha256],[ThumbnailContent],[ArchivedAtUtc]) " +
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                .Add(OleDbType.VarWChar, kind, 20)
                .Add(OleDbType.Integer, image.StorageKey)
                .Add(OleDbType.Integer, currentRevision)
                .Add(OleDbType.VarWChar, old.Path, 255)
                .Add(OleDbType.VarWChar, old.Hash, 64)
                .Add(OleDbType.VarWChar, old.Media, 100)
                .Add(OleDbType.Integer, old.Width)
                .Add(OleDbType.Integer, old.Height)
                .Add(OleDbType.Integer, old.Bytes)
                .Add(OleDbType.VarWChar, display.Media, 100)
                .Add(OleDbType.Integer, display.Width)
                .Add(OleDbType.Integer, display.Height)
                .Add(OleDbType.Integer, display.Bytes)
                .Add(OleDbType.VarWChar, Convert.ToHexString(SHA256.HashData(display.Content)), 64)
                .Add(OleDbType.LongVarBinary, display.Content, display.Bytes)
                .Add(OleDbType.VarWChar, thumb.Media, 100)
                .Add(OleDbType.Integer, thumb.Width)
                .Add(OleDbType.Integer, thumb.Height)
                .Add(OleDbType.Integer, thumb.Bytes)
                .Add(OleDbType.VarWChar, Convert.ToHexString(SHA256.HashData(thumb.Content)), 64)
                .Add(OleDbType.LongVarBinary, thumb.Content, thumb.Bytes)
                .Add(OleDbType.Date, now))
                await archive.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            using (var update = context.Command(
                    $"UPDATE [{table}] SET [OriginalRelativePath]=?," +
                    "[OriginalSha256]=?,[OriginalMediaType]=?,[OriginalWidth]=?," +
                    "[OriginalHeight]=?,[OriginalBytes]=?,[UpdatedAtUtc]=?," +
                    "[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.VarWChar, relative, 255)
                .Add(OleDbType.VarWChar, encoded.Hash, 64)
                .Add(OleDbType.VarWChar, encoded.MediaType, 100)
                .Add(OleDbType.Integer, encoded.Width)
                .Add(OleDbType.Integer, encoded.Height)
                .Add(OleDbType.Integer, encoded.Original.Length)
                .Add(OleDbType.Date, now)
                .Add(OleDbType.Integer, image.StorageKey)
                .Add(OleDbType.Integer, old.Version))
                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new VaultCommandException("concurrency.conflict", "The image changed.");
            foreach (var (renditionKind, content, width, height) in new[]
            {
                ("Display", encoded.Display, encoded.DisplayWidth, encoded.DisplayHeight),
                ("Thumbnail", encoded.Thumbnail, encoded.ThumbWidth, encoded.ThumbHeight)
            })
            {
                using var update = context.Command(
                        $"UPDATE [{renditionTable}] SET [MediaType]='image/jpeg'," +
                        "[Width]=?,[Height]=?,[ByteSize]=?,[Content]=? " +
                        "WHERE [ImageId]=? AND [RenditionKind]=?")
                    .Add(OleDbType.Integer, width).Add(OleDbType.Integer, height)
                    .Add(OleDbType.Integer, content.Length)
                    .Add(OleDbType.LongVarBinary, content, content.Length)
                    .Add(OleDbType.Integer, image.StorageKey)
                    .Add(OleDbType.VarWChar, renditionKind, 20);
                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new VaultCommandException("image.rendition_missing",
                        "The image rendition is missing.");
            }
            using (var updateVersion = context.Command(
                    "UPDATE [ImageContentVersions] SET [CurrentRevision]=? " +
                    "WHERE [ImageKind]=? AND [ImageId]=? AND [CurrentRevision]=?")
                .Add(OleDbType.Integer, nextRevision)
                .Add(OleDbType.VarWChar, kind, 20)
                .Add(OleDbType.Integer, image.StorageKey)
                .Add(OleDbType.Integer, currentRevision))
                if (await updateVersion.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new VaultCommandException("concurrency.conflict",
                        "The image content revision changed.");
            return new VaultMutationOutcome(kind,
                image.StorageKey.ToString(CultureInfo.InvariantCulture), old.Version + 1,
                "replace", new { contentRevision = nextRevision,
                    encoded.Width, encoded.Height, imageSha256 = encoded.Hash, hostFileId = request.File?.FileId }, old.Version);
        }, token).ConfigureAwait(false);
    }
}
