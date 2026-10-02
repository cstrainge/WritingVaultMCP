using System.Data.OleDb;
using System.Globalization;
using System.Security.Cryptography;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessV4ImageService
{
    private async Task<IReadOnlyList<(long Key, V4ImageMetadata Image)>> SearchStoryAsync(
        V4ImageSearchRequest request, int? continuity, long afterKey,
        string revision, CancellationToken token)
    {
        // The legacy filters explicitly name canon entities and their kinds.
        // They do not match continuity, relationship, or event owners.
        if (request.Entities is { Count: > 0 } || request.EntityKinds is { Count: > 0 })
            return [];
        var after = checked((int)(afterKey / 2)) - (afterKey % 2 == 0 ? 1 : 0);
        var predicates = new List<string>
        {
            "c.[IsDeleted]=False", "i.[Id]>?",
            "(i.[OwnerKind]='Continuity' OR " +
            "(i.[OwnerKind]='Relationship' AND EXISTS (SELECT * FROM [CharacterRelationships] AS r WHERE r.[Id]=i.[RelationshipId] AND r.[ContinuityId]=i.[ContinuityId] AND r.[IsDeleted]=False)) OR " +
            "(i.[OwnerKind]='EntityEvent' AND EXISTS (SELECT * FROM [EntityEvents] AS e INNER JOIN [CanonEntities] AS ce ON e.[EntityId]=ce.[Id] WHERE e.[Id]=i.[EntityEventId] AND ce.[ContinuityId]=i.[ContinuityId] AND e.[IsDeleted]=False AND ce.[IsDeleted]=False)) OR " +
            "(i.[OwnerKind]='RelationshipEvent' AND EXISTS (SELECT * FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] WHERE e.[Id]=i.[RelationshipEventId] AND r.[ContinuityId]=i.[ContinuityId] AND e.[IsDeleted]=False AND r.[IsDeleted]=False)))"
        };
        var parameters = new List<(OleDbType Type, object? Value, int? Size)>
        {
            (OleDbType.Integer, after, null)
        };
        if (continuity is not null)
        {
            predicates.Insert(0, "i.[ContinuityId]=?");
            parameters.Insert(0, (OleDbType.Integer, continuity.Value, null));
        }
        void AddList(string column, IReadOnlyCollection<string>? values, int size)
        {
            if (values is not { Count: > 0 }) return;
            predicates.Add($"{column} IN ({string.Join(',', values.Select(_ => "?"))})");
            parameters.AddRange(values.Select(value =>
                (OleDbType.VarWChar, (object?)value, (int?)size)));
        }
        AddList("i.[Role]", request.Roles, 100);
        AddList("i.[CanonStatus]", request.CanonStatuses, 50);
        predicates.Add(request.DeletionState switch
        {
            V4DeletionState.Active => "i.[IsDeleted]=False",
            V4DeletionState.Deleted => "i.[IsDeleted]=True",
            _ => "1=1"
        });
        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            predicates.Add("(i.[Title] LIKE ? OR i.[Caption] LIKE ? OR i.[AltText] LIKE ?)");
            var pattern = $"%{EscapeLike(request.Text.Trim())}%";
            parameters.Add((OleDbType.VarWChar, pattern, 255));
            parameters.Add((OleDbType.LongVarWChar, pattern, null));
            parameters.Add((OleDbType.LongVarWChar, pattern, null));
        }
        var sql = $"SELECT TOP {request.Limit + 1} i.[Id],i.[OwnerKind]," +
                  "i.[RelationshipId],i.[EntityEventId],i.[RelationshipEventId]," +
                  "i.[ContinuityId],i.[Title],i.[Caption],i.[AltText],i.[Role]," +
                  "i.[CanonStatus],i.[IsPrimary],i.[OriginalMediaType]," +
                  "i.[OriginalWidth],i.[OriginalHeight],i.[OriginalBytes]," +
                  "i.[Version],i.[IsDeleted],c.[Name] FROM [StoryImages] AS i " +
                  "INNER JOIN [Continuities] AS c ON i.[ContinuityId]=c.[Id] " +
                  $"WHERE {string.Join(" AND ", predicates)} ORDER BY i.[Id]";
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, sql);
        foreach (var parameter in parameters)
            command.Add(parameter.Type, parameter.Value, parameter.Size);
        var rows = await command.QueryAsync(reader => new
        {
            Id = reader.GetInt32(0), Kind = reader.GetString(1),
            Relationship = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
            EntityEvent = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
            RelationshipEvent = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
            Continuity = reader.GetInt32(5), Row = ReadImageRow(reader.GetInt32(0), reader, 6),
            ContinuityName = reader.GetString(18)
        }, token).ConfigureAwait(false);
        var result = new List<(long Key, V4ImageMetadata Image)>();
        foreach (var row in rows)
        {
            var (type, ownerId) = row.Kind switch
            {
                "Continuity" => ("Continuity", row.Continuity),
                "Relationship" => ("CharacterRelationship", row.Relationship ?? 0),
                "EntityEvent" => ("EntityEvent", row.EntityEvent ?? 0),
                "RelationshipEvent" => ("RelationshipEvent", row.RelationshipEvent ?? 0),
                _ => throw new V4ResolutionException("image.owner_invalid", "The image owner is invalid.")
            };
            var ownerRef = await references.ReferenceAsync(type, ownerId, token)
                .ConfigureAwait(false);
            var owner = await targets.StoryImageOwnerAsync(ownerRef, row.Continuity, token)
                .ConfigureAwait(false);
            result.Add(((long)row.Id * 2 + 1,
                Metadata(row.Row, owner, revision, row.ContinuityName, "StoryImage")));
        }
        return result;
    }

    private async Task<VaultMutationResult> UpdateStoryAsync(V4ResolvedTarget image,
        V4ImageUpdateRequest request, CancellationToken token)
    {
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException error)
        { return new(false, "validation.mutation_token", Message: error.Message); }
        var continuity = session.RequireContinuityId();
        return await coordinator.ExecuteAsync(operation, "v4.story.image.update", request,
            "image_update", session.ClientLabel, async (context, ct) =>
        {
            using var find = context.Command(
                    "SELECT [ContinuityId],[OwnerKind],[RelationshipId],[EntityEventId]," +
                    "[RelationshipEventId],[Title],[Caption],[AltText],[Role],[CanonStatus]," +
                    "[IsPrimary],[Version] FROM [StoryImages] WHERE [Id]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer, image.StorageKey);
            var rows = await find.QueryAsync(reader => new
            {
                Continuity = reader.GetInt32(0), Kind = reader.GetString(1),
                Relationship = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                EntityEvent = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                RelationshipEvent = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                Title = reader.IsDBNull(5) ? null : reader.GetString(5),
                Caption = reader.IsDBNull(6) ? null : reader.GetString(6),
                Alt = reader.IsDBNull(7) ? null : reader.GetString(7),
                Role = reader.IsDBNull(8) ? null : reader.GetString(8),
                Status = reader.IsDBNull(9) ? null : reader.GetString(9),
                Primary = reader.GetBoolean(10), Version = reader.GetInt32(11)
            }, ct).ConfigureAwait(false);
            if (rows.Count != 1 || rows[0].Continuity != continuity)
                throw new VaultCommandException("record.not_found", "The image is missing or deleted.");
            var old = rows[0];
            if (old.Version != request.ExpectedVersion)
                throw new VaultCommandException("concurrency.conflict",
                    $"The current version is {old.Version}.", actualVersion: old.Version);
            var ownerId = old.Kind switch
            {
                "Continuity" => continuity,
                "Relationship" => old.Relationship ?? 0,
                "EntityEvent" => old.EntityEvent ?? 0,
                "RelationshipEvent" => old.RelationshipEvent ?? 0,
                _ => 0
            };
            await RequireActiveStoryOwnerAsync(context, old.Kind, ownerId, continuity, ct)
                .ConfigureAwait(false);
            var ownerColumn = old.Kind switch
            {
                "Relationship" => "RelationshipId",
                "EntityEvent" => "EntityEventId",
                "RelationshipEvent" => "RelationshipEventId",
                _ => null
            };
            var ownerPredicate = "[ContinuityId]=? AND [OwnerKind]=?" +
                (ownerColumn is null ? "" : $" AND [{ownerColumn}]=?");
            AccessCommand ScopeCommand(string sql)
            {
                var command = context.Command(sql).Add(OleDbType.Integer, continuity)
                    .Add(OleDbType.VarWChar, old.Kind, 30);
                if (ownerColumn is not null) command.Add(OleDbType.Integer, ownerId);
                return command;
            }
            var title = TextChange(request.Changes, "title", old.Title);
            var caption = TextChange(request.Changes, "caption", old.Caption);
            var alt = TextChange(request.Changes, "altText", old.Alt);
            var role = TextChange(request.Changes, "role", old.Role);
            var status = TextChange(request.Changes, "canonStatus", old.Status);
            var primary = request.Changes.TryGetValue("isPrimary", out var value)
                ? value.GetBoolean() : old.Primary;
            var now = DateTime.UtcNow;
            if (primary && !old.Primary)
            {
                using var clear = context.Command(
                        "UPDATE [StoryImages] SET [IsPrimary]=False,[UpdatedAtUtc]=?," +
                        $"[Version]=[Version]+1 WHERE {ownerPredicate} AND [Id]<>? " +
                        "AND [IsPrimary]=True AND [IsDeleted]=False")
                    .Add(OleDbType.Date, now).Add(OleDbType.Integer, continuity)
                    .Add(OleDbType.VarWChar, old.Kind, 30);
                if (ownerColumn is not null) clear.Add(OleDbType.Integer, ownerId);
                clear.Add(OleDbType.Integer, image.StorageKey);
                await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            if (old.Primary && !primary)
            {
                using var successor = ScopeCommand(
                    $"SELECT TOP 1 [Id] FROM [StoryImages] WHERE {ownerPredicate} " +
                    "AND [Id]<>? AND [IsDeleted]=False ORDER BY [Id]");
                successor.Add(OleDbType.Integer, image.StorageKey);
                var next = await successor.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (next is null or DBNull) primary = true;
                else
                {
                    using var promote = context.Command(
                            "UPDATE [StoryImages] SET [IsPrimary]=True,[UpdatedAtUtc]=?," +
                            "[Version]=[Version]+1 WHERE [Id]=?")
                        .Add(OleDbType.Date, now)
                        .Add(OleDbType.Integer, Convert.ToInt32(next, CultureInfo.InvariantCulture));
                    await promote.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            using var update = context.Command(
                    "UPDATE [StoryImages] SET [Title]=?,[Caption]=?,[AltText]=?,[Role]=?," +
                    "[CanonStatus]=?,[IsPrimary]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 " +
                    "WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.VarWChar, title, 255).Add(OleDbType.LongVarWChar, caption)
                .Add(OleDbType.LongVarWChar, alt).Add(OleDbType.VarWChar, role, 100)
                .Add(OleDbType.VarWChar, status, 50).Add(OleDbType.Boolean, primary)
                .Add(OleDbType.Date, now).Add(OleDbType.Integer, image.StorageKey)
                .Add(OleDbType.Integer, old.Version);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new VaultCommandException("concurrency.conflict", "The image changed.");
            return new VaultMutationOutcome("StoryImage",
                image.StorageKey.ToString(CultureInfo.InvariantCulture), old.Version + 1,
                "update", new { title, role, canonStatus = status, isPrimary = primary }, old.Version);
        }, token).ConfigureAwait(false);
    }

    private async Task<V4Page<V4ImageMetadata>> ListStoryAsync(V4ResolvedTarget owner,
        V4ImageListRequest request, CancellationToken token)
    {
        ValidateLimit(request.Limit);
        var (kind, column) = owner.ResourceType switch
        {
            "Continuity" => ("Continuity", (string?)null),
            "CharacterRelationship" => ("Relationship", "RelationshipId"),
            "EntityEvent" => ("EntityEvent", "EntityEventId"),
            "RelationshipEvent" => ("RelationshipEvent", "RelationshipEventId"),
            _ => throw new InvalidOperationException("Unsupported story image owner.")
        };
        var continuity = session.RequireContinuityId();
        var scope = $"images:{owner.Reference}:{request.Role}:{request.CanonStatus}:{request.DeletionState}";
        var after = request.Cursor is null ? 0 :
            checked((int)cursors.Decode(request.Cursor, "images", scope).Position);
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var predicate = "[ContinuityId]=? AND [OwnerKind]=? AND [Id]>?" +
            (column is null ? "" : $" AND [{column}]=?");
        var sql = $"SELECT TOP {request.Limit + 1} [Id],[Title],[Caption],[AltText],[Role]," +
                  "[CanonStatus],[IsPrimary],[OriginalMediaType],[OriginalWidth]," +
                  "[OriginalHeight],[OriginalBytes],[Version],[IsDeleted] FROM [StoryImages] " +
                  $"WHERE {predicate}" +
                  (request.Role is null ? "" : " AND [Role]=?") +
                  (request.CanonStatus is null ? "" : " AND [CanonStatus]=?") +
                  (request.DeletionState switch
                  {
                      V4DeletionState.Active => " AND [IsDeleted]=False",
                      V4DeletionState.Deleted => " AND [IsDeleted]=True",
                      _ => ""
                  }) + " ORDER BY [Id]";
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, sql)
            .Add(OleDbType.Integer, continuity).Add(OleDbType.VarWChar, kind, 30)
            .Add(OleDbType.Integer, after);
        if (column is not null) command.Add(OleDbType.Integer, owner.StorageKey);
        if (request.Role is not null) command.Add(OleDbType.VarWChar, request.Role, 100);
        if (request.CanonStatus is not null)
            command.Add(OleDbType.VarWChar, request.CanonStatus, 50);
        var rows = await command.QueryAsync(reader => ReadImageRow(reader.GetInt32(0), reader, 1),
            token).ConfigureAwait(false);
        var items = rows.Take(request.Limit)
            .Select(row => Metadata(row, owner, revision, imageType: "StoryImage"))
            .ToArray();
        return new(items, rows.Count > request.Limit
            ? cursors.Encode("images", rows[request.Limit - 1].Id, scope) : null,
            rows.Count > request.Limit, revision);
    }

    private async Task<V4ImageContent> ViewStoryAsync(V4ResolvedTarget image,
        V4ImageSize size, CancellationToken token)
    {
        var ownerContinuity = image.ContinuityKey ??
            throw new V4ResolutionException("record.not_found", "The image has no continuity owner.");
        await using var connection = factory.Create();
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                "SELECT i.[OwnerKind],i.[RelationshipId],i.[EntityEventId],i.[RelationshipEventId]," +
                "i.[Title],i.[Caption],i.[AltText],i.[Role],i.[CanonStatus],i.[IsPrimary]," +
                "i.[OriginalMediaType],i.[OriginalWidth],i.[OriginalHeight],i.[OriginalBytes]," +
                "i.[Version],i.[IsDeleted],r.[MediaType],r.[Width],r.[Height],r.[ByteSize],r.[Content] " +
                "FROM [StoryImages] AS i INNER JOIN [StoryImageRenditions] AS r " +
                "ON i.[Id]=r.[ImageId] WHERE i.[Id]=? AND r.[RenditionKind]=?")
            .Add(OleDbType.Integer, image.StorageKey)
            .Add(OleDbType.VarWChar, size.ToString(), 20);
        var rows = await command.QueryAsync(reader => new
        {
            OwnerKind = reader.GetString(0),
            Relationship = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
            EntityEvent = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
            RelationshipEvent = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
            Row = ReadImageRow(image.StorageKey, reader, 4),
            Media = reader.GetString(16), Width = reader.GetInt32(17),
            Height = reader.GetInt32(18), Bytes = reader.GetInt32(19),
            Content = (byte[])reader.GetValue(20)
        }, token).ConfigureAwait(false);
        if (rows.Count != 1)
            throw new V4ResolutionException("image.rendition_missing",
                "The requested image rendition is unavailable.");
        var found = rows[0];
        var (ownerType, ownerId) = found.OwnerKind switch
        {
            "Continuity" => ("Continuity", ownerContinuity),
            "Relationship" => ("CharacterRelationship", found.Relationship ?? 0),
            "EntityEvent" => ("EntityEvent", found.EntityEvent ?? 0),
            "RelationshipEvent" => ("RelationshipEvent", found.RelationshipEvent ?? 0),
            _ => throw new V4ResolutionException("image.owner_invalid", "The image owner is invalid.")
        };
        var ownerRef = await references.ReferenceAsync(ownerType, ownerId, token).ConfigureAwait(false);
        var owner = await targets.StoryImageOwnerAsync(ownerRef, ownerContinuity, token)
            .ConfigureAwait(false);
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var continuityName = await references.ContinuityNameAsync(ownerContinuity, token)
            .ConfigureAwait(false);
        var metadata = Metadata(found.Row, owner, revision, continuityName, "StoryImage");
        return new(new(metadata, size, found.Media, found.Width, found.Height,
            found.Bytes, revision), found.Content);
    }

    public async Task<VaultMutationResult> AttachStoryAsync(
        V4StoryImageAttachRequest request, CancellationToken token = default)
    {
        EncodedImage? encoded = null;
        try
        {
            ValidateText(request.Title, 255, "title");
            ValidateText(request.Caption, V4ContractLimits.MaximumLongTextLength, "caption");
            ValidateText(request.AltText, V4ContractLimits.MaximumLongTextLength, "altText");
            ValidateText(request.Role, 100, "role");
            ValidateText(request.CanonStatus, 50, "canonStatus");
            ValidateImageInput(request.Image, request.File, request.BackgroundColor);
            if (request.Image is not null) encoded = Decode(request.Image, request.BackgroundColor);
        }
        catch (VaultValidationException error)
        {
            return new(false, error.Errors[0].Code, Message: error.Message);
        }

        var continuity = session.RequireContinuityId();
        V4ResolvedTarget owner;
        V4ResolvedTarget? source = null;
        try
        {
            owner = await targets.StoryImageOwnerAsync(request.Owner, continuity, request.File is not null, token).ConfigureAwait(false);
            if (request.File is null && !string.IsNullOrWhiteSpace(request.Source))
                source = await targets.SourceAsync(request.Source, token).ConfigureAwait(false);
        }
        catch (V4ResolutionException error)
        {
            return new(false, error.Code, Message: error.Message);
        }
        var (ownerKind, ownerColumn) = owner.ResourceType switch
        {
            "Continuity" => ("Continuity", (string?)null),
            "CharacterRelationship" => ("Relationship", "RelationshipId"),
            "EntityEvent" => ("EntityEvent", "EntityEventId"),
            "RelationshipEvent" => ("RelationshipEvent", "RelationshipEventId"),
            _ => throw new InvalidOperationException("The resolved record is not an image owner.")
        };
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException error)
        { return new(false, "validation.mutation_token", Message: error.Message); }

        object journalInput;
        if (request.File is { } file)
        {
            journalInput = HostImportInput(request with { File = null, Image = null }, file, owner.Reference, continuity);
            if (await coordinator.TryReplayAsync(operation, "v4.story.image.attach", journalInput, token).ConfigureAwait(false) is { } replay)
                return replay;
            try
            {
                if (!string.IsNullOrWhiteSpace(request.Source)) source = await targets.SourceAsync(request.Source, token).ConfigureAwait(false);
            }
            catch (V4ResolutionException error) { return new(false, error.Code, Message: error.Message); }
            if (await PreflightImportAsync(owner, continuity, source, null, token).ConfigureAwait(false) is { } failure)
                return failure;
            var download = await DownloadImageAsync(file, request.BackgroundColor, token).ConfigureAwait(false);
            if (download.Failure is not null) return download.Failure;
            encoded = download.Image!;
        }
        else journalInput = new
        {
            request.MutationToken, owner = owner.Reference, imageSha256 = encoded!.Hash,
            request.Image!.MediaType, request.Title, request.Caption, request.AltText,
            request.Role, request.CanonStatus, request.Source, request.BackgroundColor,
            request.IsPrimary
        };
        var relative = Path.Combine("originals", encoded!.Hash[..2], encoded.Hash + encoded.Extension);
        var absolute = SafeAssetPath(relative);
        return await coordinator.ExecuteAsync(operation, "v4.story.image.attach", journalInput,
            "story_image_attach", session.ClientLabel, async (context, ct) =>
        {
            await RequireActiveContinuityAsync(context, continuity, ct).ConfigureAwait(false);
            var projectedBytes = new FileInfo(databasePath).Length + encoded.Display.Length + encoded.Thumbnail.Length;
            if (projectedBytes > DatabaseHardLimitBytes)
                throw new VaultCommandException("capacity.database_limit",
                    "The image renditions would exceed the supported 1.5 GiB database limit.");
            await RequireActiveStoryOwnerAsync(context, ownerKind, owner.StorageKey, continuity, ct)
                .ConfigureAwait(false);
            if (source is not null)
            {
                using var sourceCheck = context.Command(
                        "SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False")
                    .Add(OleDbType.Integer, source.StorageKey);
                if (Convert.ToInt32(await sourceCheck.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                    throw new VaultCommandException("record.not_found", "The image source is missing or deleted.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            if (File.Exists(absolute))
            {
                var existing = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing),
                        SHA256.HashData(encoded.Original)))
                    throw new VaultCommandException("image.hash_collision",
                        "An existing asset has the same content name but different bytes.");
            }
            else
            {
                var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, encoded.Original, ct).ConfigureAwait(false);
                    File.Move(temporary, absolute, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                context.OnRollback(() => { if (File.Exists(absolute)) File.Delete(absolute); });
            }

            var scope = ownerColumn is null
                ? "[OwnerKind]='Continuity' AND [ContinuityId]=?"
                : $"[OwnerKind]='{ownerKind}' AND [ContinuityId]=? AND [{ownerColumn}]=?";
            AccessCommand ScopeCommand(string sql)
            {
                var command = context.Command(sql).Add(OleDbType.Integer, continuity);
                if (ownerColumn is not null) command.Add(OleDbType.Integer, owner.StorageKey);
                return command;
            }
            using (var duplicate = ScopeCommand(
                       $"SELECT COUNT(*) FROM [StoryImages] WHERE {scope} AND [OriginalSha256]=?"))
            {
                duplicate.Add(OleDbType.VarWChar, encoded.Hash, 64);
                if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                    throw new VaultCommandException("image.duplicate", "That image is already attached to this owner.");
            }
            using var existingImages = ScopeCommand(
                $"SELECT COUNT(*) FROM [StoryImages] WHERE {scope} AND [IsDeleted]=False");
            var effectivePrimary = request.IsPrimary ||
                Convert.ToInt32(await existingImages.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0;
            var now = DateTime.UtcNow;
            if (effectivePrimary)
            {
                using var update = context.Command(
                    $"UPDATE [StoryImages] SET [IsPrimary]=False,[UpdatedAtUtc]=?," +
                    $"[Version]=[Version]+1 WHERE {scope} AND [IsPrimary]=True AND [IsDeleted]=False")
                    .Add(OleDbType.Date, now).Add(OleDbType.Integer, continuity);
                if (ownerColumn is not null) update.Add(OleDbType.Integer, owner.StorageKey);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using var insert = context.Command(
                    "INSERT INTO [StoryImages] ([ContinuityId],[OwnerKind],[RelationshipId]," +
                    "[EntityEventId],[RelationshipEventId],[SourceId],[Title],[Caption],[AltText]," +
                    "[Role],[CanonStatus],[IsPrimary],[OriginalRelativePath],[OriginalSha256]," +
                    "[OriginalMediaType],[OriginalWidth],[OriginalHeight],[OriginalBytes]," +
                    "[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                .Add(OleDbType.Integer, continuity)
                .Add(OleDbType.VarWChar, ownerKind, 30)
                .Add(OleDbType.Integer, ownerKind == "Relationship" ? owner.StorageKey : null)
                .Add(OleDbType.Integer, ownerKind == "EntityEvent" ? owner.StorageKey : null)
                .Add(OleDbType.Integer, ownerKind == "RelationshipEvent" ? owner.StorageKey : null)
                .Add(OleDbType.Integer, source?.StorageKey)
                .Add(OleDbType.VarWChar, request.Title, 255)
                .Add(OleDbType.LongVarWChar, request.Caption)
                .Add(OleDbType.LongVarWChar, request.AltText)
                .Add(OleDbType.VarWChar, request.Role, 100)
                .Add(OleDbType.VarWChar, request.CanonStatus, 50)
                .Add(OleDbType.Boolean, effectivePrimary)
                .Add(OleDbType.VarWChar, relative, 255)
                .Add(OleDbType.VarWChar, encoded.Hash, 64)
                .Add(OleDbType.VarWChar, encoded.MediaType, 100)
                .Add(OleDbType.Integer, encoded.Width)
                .Add(OleDbType.Integer, encoded.Height)
                .Add(OleDbType.Integer, encoded.Original.Length)
                .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var id = await IdentityAsync(context, ct).ConfigureAwait(false);
            await InsertRendition(context, id, "Display", encoded.Display,
                encoded.DisplayWidth, encoded.DisplayHeight, ct, story: true).ConfigureAwait(false);
            await InsertRendition(context, id, "Thumbnail", encoded.Thumbnail,
                encoded.ThumbWidth, encoded.ThumbHeight, ct, story: true).ConfigureAwait(false);
            using (var revision = context.Command("INSERT INTO [ImageContentVersions] ([ImageKind],[ImageId],[CurrentRevision]) VALUES ('StoryImage',?,1)")
                .Add(OleDbType.Integer, id))
                await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new VaultMutationOutcome("StoryImage", id.ToString(CultureInfo.InvariantCulture),
                1, "attach", new { owner = owner.Reference, isPrimary = effectivePrimary,
                    encoded.Width, encoded.Height, imageSha256 = encoded.Hash, hostFileId = request.File?.FileId }, null);
        }, token).ConfigureAwait(false);
    }

    private static async Task RequireActiveStoryOwnerAsync(VaultWriteContext context,
        string kind, int ownerId, int continuity, CancellationToken token)
    {
        var sql = kind switch
        {
            "Continuity" => "SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False",
            "Relationship" => "SELECT COUNT(*) FROM [CharacterRelationships] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False",
            "EntityEvent" => "SELECT COUNT(*) FROM [EntityEvents] AS e INNER JOIN [CanonEntities] AS c ON e.[EntityId]=c.[Id] WHERE e.[Id]=? AND c.[ContinuityId]=? AND e.[IsDeleted]=False AND c.[IsDeleted]=False",
            "RelationshipEvent" => "SELECT COUNT(*) FROM [RelationshipEvents] AS e INNER JOIN [CharacterRelationships] AS r ON e.[RelationshipId]=r.[Id] WHERE e.[Id]=? AND r.[ContinuityId]=? AND e.[IsDeleted]=False AND r.[IsDeleted]=False",
            _ => throw new InvalidOperationException("Unsupported story image owner.")
        };
        using var command = context.Command(sql).Add(OleDbType.Integer, ownerId);
        if (kind != "Continuity") command.Add(OleDbType.Integer, continuity);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("record.not_found", "The image owner is missing or deleted.");
    }
}
