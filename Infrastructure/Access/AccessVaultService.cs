using System.Data.Common;
using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed partial class AccessVaultService(
    IAccessConnectionFactory connectionFactory,
    VaultWriteCoordinator writes,
    WritingVaultStorageOptions? storageOptions = null)
{
    private readonly IAccessConnectionFactory _connectionFactory = connectionFactory;
    private readonly WritingVaultStorageOptions _storage = storageOptions ?? new(Path.Combine(Path.GetTempPath(), "WritingVaultMcpCache"));
    private static readonly SemaphoreSlim SnapshotCacheGate = new(1, 1);
    public Task<VaultMutationResult> CreateContinuityAsync(
        CreateContinuityRequest request,
        CancellationToken cancellationToken = default)
    {
        string name;
        try
        {
            name = TextNormalization.Required(request.Name, 255, nameof(request.Name));
            _ = TimeZoneInfo.FindSystemTimeZoneById(request.DefaultTimeZoneId);
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: exception.Message));
        }

        return writes.ExecuteAsync(
            request.OperationId, "continuity.create", request, "continuity_create", request.ClientLabel,
            async (context, token) =>
            {
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                        "INSERT INTO [Continuities] ([Name],[NormalizedName],[Description],[DefaultTimeZoneId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                    .Add(OleDbType.VarWChar, name, 255)
                    .Add(OleDbType.VarWChar, TextNormalization.CanonicalKey(name), 255)
                    .Add(OleDbType.LongVarWChar, request.Description)
                    .Add(OleDbType.VarWChar, request.DefaultTimeZoneId, 100)
                    .Add(OleDbType.Date, now)
                    .Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                using var clock = context.Command(
                        "INSERT INTO [ContinuityClocks] ([ContinuityId],[CurrentInstantUtc],[ReferenceTimeZoneId],[UpdatedAtUtc]) VALUES (?,?,?,?)")
                    .Add(OleDbType.Integer, id)
                    .Add(OleDbType.Date, null)
                    .Add(OleDbType.VarWChar, request.DefaultTimeZoneId, 100)
                    .Add(OleDbType.Date, now);
                await clock.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return new VaultMutationOutcome("Continuity", id.ToString(), 1, "create", new { name, request.DefaultTimeZoneId });
            }, cancellationToken);
    }

    public Task<VaultMutationResult> CreateEntityAsync(
        CreateCanonEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        string name;
        try
        {
            name = TextNormalization.Required(request.Name, request.EntityType == CanonEntityType.Character ? 100 : 255, nameof(request.Name));
            if (request.EntityType != CanonEntityType.Character && new[]
                {
                    request.MiddleNames, request.FamilyName, request.PreferredName, request.Gender, request.Pronouns,
                    request.Species, request.Occupation, request.Nationality, request.PhysicalDescription, request.PersonalitySummary
                }.Any(value => value is not null))
                throw new ArgumentException("Character-specific fields require EntityType Character.");
            if (request.Species is not null && request.SecondaryType is not null &&
                !string.Equals(request.Species, request.SecondaryType, StringComparison.Ordinal))
                throw new ArgumentException("Species and SecondaryType cannot specify different values.");
            if (request.PersonalitySummary is not null && request.Description is not null &&
                !string.Equals(request.PersonalitySummary, request.Description, StringComparison.Ordinal))
                throw new ArgumentException("PersonalitySummary and Description cannot specify different values.");
            ValidateStoryDate(request.Birth);
            ValidateStoryDate(request.Death);
            ValidateStoryDate(request.Occurred);
            if (request.NarrativeOrder is { } narrativeOrder && !double.IsFinite(narrativeOrder))
                throw new ArgumentException("NarrativeOrder must be a finite number.");
            if (request.TimeZoneId is not null) _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Task.FromResult(new VaultMutationResult(false, "validation.failed", Message: exception.Message));
        }

        return writes.ExecuteAsync(
            request.OperationId, $"{request.EntityType}.create", request, "entity_create", request.ClientLabel,
            async (context, token) =>
            {
                await RequireContinuityAsync(context, request.ContinuityId, token).ConfigureAwait(false);
                if (request.VariantGroupId is { } variantGroupId)
                    await RequireVariantGroupAsync(context, variantGroupId, request.ContinuityId, request.EntityType, token).ConfigureAwait(false);
                if (request.BirthLocationId is { } birthLocation)
                    await RequireEntityAsync(context, birthLocation, CanonEntityType.Location, request.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var canon = context.Command(
                        "INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[VariantGroupId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ContinuityId)
                    .Add(OleDbType.VarWChar, request.EntityType.ToString(), 30)
                    .Add(OleDbType.Integer, request.VariantGroupId)
                    .Add(OleDbType.Date, now)
                    .Add(OleDbType.Date, now);
                await canon.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                await InsertSubtypeAsync(context, id, name, request, token).ConfigureAwait(false);
                return new VaultMutationOutcome(request.EntityType.ToString(), id.ToString(), 1, "create", new { request.ContinuityId, name });
            }, cancellationToken);
    }

    public Task<VaultMutationResult> CreateVariantGroupAsync(
        CreateVariantGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Name is { Length: > 255 })
            return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: "Name cannot exceed 255 characters."));
        return writes.ExecuteAsync(
            request.OperationId, "variant_group.create", request, "variant_group_create", request.ClientLabel,
            async (context, token) =>
            {
                await RequireContinuityAsync(context, request.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [VariantGroups] ([ContinuityId],[EntityType],[Name],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.ContinuityId).Add(OleDbType.VarWChar, request.EntityType.ToString(), 30)
                    .Add(OleDbType.VarWChar, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), 255)
                    .Add(OleDbType.LongVarWChar, request.Notes).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("VariantGroup", id.ToString(), 1, "create", new { request.ContinuityId, request.EntityType, request.Name });
            }, cancellationToken);
    }

    public Task<VaultMutationResult> DuplicateEntityAsync(
        DuplicateEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: "Name cannot be blank when specified."));
        return writes.ExecuteAsync(
            request.OperationId, "entity.duplicate_to_continuity", request, "entity_duplicate_to_continuity", request.ClientLabel,
            async (context, token) =>
            {
                var source = await RequireEntityAsync(context, request.SourceEntityId, null, null, token).ConfigureAwait(false);
                await RequireContinuityAsync(context, request.TargetContinuityId, token).ConfigureAwait(false);
                var (sourceTable, sourceNameColumn, nameLimit) = source.Type switch
                {
                    CanonEntityType.Project => ("Projects", "Name", 255),
                    CanonEntityType.Location => ("Locations", "Name", 255),
                    CanonEntityType.Character => ("Characters", "GivenName", 100),
                    CanonEntityType.Organization => ("Organizations", "Name", 255),
                    CanonEntityType.Object => ("Objects", "Name", 255),
                    CanonEntityType.WorldEvent => ("WorldEvents", "Title", 255),
                    _ => throw new ArgumentOutOfRangeException()
                };
                using var sourceNameCommand = context.Command($"SELECT [{sourceNameColumn}] FROM [{sourceTable}] WHERE [EntityId]=?")
                    .Add(OleDbType.Integer, request.SourceEntityId);
                var sourceNameValue = await sourceNameCommand.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (sourceNameValue is null or DBNull)
                    throw new VaultCommandException("integrity.missing_subtype", "The source entity is missing its subtype record.");
                var name = TextNormalization.Required(request.Name ?? Convert.ToString(sourceNameValue)!, nameLimit, nameof(request.Name));
                var now = DateTime.UtcNow;
                using var canon = context.Command("INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[VariantGroupId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.TargetContinuityId).Add(OleDbType.VarWChar, source.Type.ToString(), 30)
                    .Add(OleDbType.Integer, null).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await canon.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                await DuplicateSubtypeAsync(context, source.Type, request.SourceEntityId, id, name, token).ConfigureAwait(false);
                return new VaultMutationOutcome(source.Type.ToString(), id.ToString(), 1, "duplicate", new
                {
                    request.SourceEntityId,
                    request.TargetContinuityId,
                    name,
                    copied = "core-fields-only"
                });
            }, cancellationToken);
    }

    public Task<VaultMutationResult> SetVariantGroupAsync(
        SetVariantGroupRequest request,
        CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(
            request.OperationId, "entity.variant_group.set", request, "entity_variant_group_set", request.ClientLabel,
            async (context, token) =>
            {
                var entity = await RequireEntityAsync(context, request.EntityId, null, null, token).ConfigureAwait(false);
                if (request.VariantGroupId is { } groupId)
                    await RequireVariantGroupAsync(context, groupId, entity.ContinuityId, entity.Type, token).ConfigureAwait(false);
                using var update = context.Command("UPDATE [CanonEntities] SET [VariantGroupId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                    .Add(OleDbType.Integer, request.VariantGroupId).Add(OleDbType.Date, DateTime.UtcNow)
                    .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, entity.Type.ToString(), request.EntityId, "CanonEntities", "Id", token).ConfigureAwait(false);
                return new VaultMutationOutcome(entity.Type.ToString(), request.EntityId.ToString(), request.ExpectedVersion + 1, "set-variant-group", new { request.VariantGroupId }, request.ExpectedVersion);
            }, cancellationToken);

    public Task<VaultMutationResult> SetClockAsync(SetContinuityClockRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.ReferenceTimeZoneId is not null)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(request.ReferenceTimeZoneId);
                if (request.CurrentInstant is { } instant && zone.GetUtcOffset(instant.UtcDateTime) != instant.Offset)
                    throw new ArgumentException("CurrentInstant offset does not match ReferenceTimeZoneId at that instant.");
            }
            if (request.CurrentInstant is { } supportedInstant && supportedInstant.UtcDateTime < StoryDate.AccessMinimum)
                throw new ArgumentException("CurrentInstant is outside Access's supported date range.");
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Task.FromResult(new VaultMutationResult(false, "validation.timezone", Message: exception.Message));
        }

        return writes.ExecuteAsync(
            request.OperationId, "continuity.clock.set", request, "continuity_clock_set", request.ClientLabel,
            async (context, token) =>
            {
                await RequireContinuityAsync(context, request.ContinuityId, token).ConfigureAwait(false);
                using var update = context.Command(
                        "UPDATE [ContinuityClocks] SET [CurrentInstantUtc]=?,[ReferenceTimeZoneId]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [ContinuityId]=? AND [Version]=?")
                    .Add(OleDbType.Date, request.CurrentInstant?.UtcDateTime)
                    .Add(OleDbType.VarWChar, request.ReferenceTimeZoneId, 100)
                    .Add(OleDbType.Date, DateTime.UtcNow)
                    .Add(OleDbType.Integer, request.ContinuityId)
                    .Add(OleDbType.Integer, request.ExpectedVersion);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    await ThrowVersionOrNotFoundAsync(context, "ContinuityClock", request.ContinuityId, "ContinuityClocks", "ContinuityId", token).ConfigureAwait(false);
                return new VaultMutationOutcome("ContinuityClock", request.ContinuityId.ToString(), request.ExpectedVersion + 1, "set-clock", new { request.CurrentInstant, request.ReferenceTimeZoneId }, request.ExpectedVersion);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddNoteAsync(AddNoteRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Task.FromResult(new VaultMutationResult(false, "validation.body", Message: "Body is required."));
        return writes.ExecuteAsync(
            request.OperationId, "entity.note.add", request, "entity_note_add", request.ClientLabel,
            async (context, token) =>
            {
                await RequireEntityAsync(context, request.EntityId, null, null, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                        "INSERT INTO [EntityNotes] ([EntityId],[Title],[Body],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.EntityId)
                    .Add(OleDbType.VarWChar, request.Title, 255)
                    .Add(OleDbType.LongVarWChar, request.Body)
                    .Add(OleDbType.Date, now)
                    .Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("EntityNote", id.ToString(), 1, "add", new { request.EntityId, request.Title });
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddEntityEventAsync(AddEntityEventRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateStoryDate(request.Occurred);
            if (request.NarrativeOrder is { } narrativeOrder && !double.IsFinite(narrativeOrder))
                throw new ArgumentException("NarrativeOrder must be a finite number.");
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.event_date", Message: exception.Message)); }
        if (string.IsNullOrWhiteSpace(request.Title)) return Task.FromResult(new VaultMutationResult(false, "validation.title", Message: "Title is required."));
        return writes.ExecuteAsync(request.OperationId, "entity.event.add", request, "entity_event_add", request.ClientLabel,
            async (context, token) =>
            {
                var entity = await RequireEntityAsync(context, request.EntityId, null, null, token).ConfigureAwait(false);
                if (entity.Type is CanonEntityType.Project or CanonEntityType.WorldEvent)
                    throw new VaultCommandException("event.unsupported_owner", "Entity-specific events are supported for characters, locations, organizations, and objects.");
                if (request.WorldEventId is { } worldEventId)
                    await RequireEntityAsync(context, worldEventId, CanonEntityType.WorldEvent, entity.ContinuityId, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [EntityEvents] ([EntityId],[WorldEventId],[Title],[Description],[NarrativeOrder],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.WorldEventId)
                    .Add(OleDbType.VarWChar, request.Title.Trim(), 255).Add(OleDbType.LongVarWChar, request.Description)
                    .Add(OleDbType.Double, request.NarrativeOrder);
                AddStoryDate(insert, request.Occurred); insert.Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("EntityEvent", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> AddAliasAsync(AddAliasRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Alias)) return Task.FromResult(new VaultMutationResult(false, "validation.alias", Message: "Alias is required."));
        return writes.ExecuteAsync(request.OperationId, "entity.alias.add", request, "entity_alias_add", request.ClientLabel,
            async (context, token) =>
            {
                var entity = await RequireEntityAsync(context, request.EntityId, null, null, token).ConfigureAwait(false);
                var (table, ownerColumn) = entity.Type switch
                {
                    CanonEntityType.Character => ("CharacterAliases", "CharacterId"),
                    CanonEntityType.Organization => ("OrganizationAliases", "OrganizationId"),
                    _ => throw new VaultCommandException("alias.unsupported", "Aliases are supported for characters and organizations.")
                };
                var now = DateTime.UtcNow;
                using var insert = context.Command($"INSERT INTO [{table}] ([{ownerColumn}],[Alias],[NormalizedAlias],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.VarWChar, request.Alias.Trim(), 255)
                    .Add(OleDbType.VarWChar, TextNormalization.CanonicalKey(request.Alias), 255)
                    .Add(OleDbType.LongVarWChar, request.Notes).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome(entity.Type + "Alias", id.ToString(), 1, "add", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> LinkNoteSourceAsync(LinkNoteSourceRequest request, CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(request.OperationId, "note.source.link", request, "note_source_link", request.ClientLabel,
            async (context, token) =>
            {
                using (var note = context.Command("SELECT COUNT(*) FROM [EntityNotes] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, request.NoteId))
                    if (Convert.ToInt32(await note.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("entity.not_found", "Note was not found.", "EntityNote", request.NoteId.ToString());
                using (var source = context.Command("SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, request.SourceId))
                    if (Convert.ToInt32(await source.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("entity.not_found", "Source was not found.", "Source", request.SourceId.ToString());
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [NoteSources] ([NoteId],[SourceId],[Locator],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, request.NoteId).Add(OleDbType.Integer, request.SourceId)
                    .Add(OleDbType.VarWChar, request.Locator, 255).Add(OleDbType.LongVarWChar, request.Notes)
                    .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("NoteSource", id.ToString(), 1, "link", request);
            }, cancellationToken);

    public Task<VaultMutationResult> CreateTagAsync(CreateTagRequest request, CancellationToken cancellationToken = default)
    {
        string name;
        try { name = TextNormalization.Required(request.Name, 100, nameof(request.Name)); }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.name", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "tag.create", request, "tag_create", request.ClientLabel,
            async (context, token) =>
            {
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                        "INSERT INTO [Tags] ([Name],[NormalizedName],[Description],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?)")
                    .Add(OleDbType.VarWChar, name, 100)
                    .Add(OleDbType.VarWChar, TextNormalization.CanonicalKey(name), 100)
                    .Add(OleDbType.LongVarWChar, request.Description)
                    .Add(OleDbType.Date, now)
                    .Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("Tag", id.ToString(), 1, "create", new { name });
            }, cancellationToken);
    }

    public Task<VaultMutationResult> CreateSourceAsync(CreateSourceRequest request, CancellationToken cancellationToken = default)
    {
        string title;
        try
        {
            title = TextNormalization.Required(request.Title, 255, nameof(request.Title));
            ValidateWebUrl(request.CanonicalUrl, nameof(request.CanonicalUrl));
            ValidateWebUrl(request.ArchiveUrl, nameof(request.ArchiveUrl));
            if (request.SourceType is { Length: > 100 }) throw new ArgumentException("SourceType cannot exceed 100 characters.");
            if (request.AuthorPublisher is { Length: > 255 }) throw new ArgumentException("AuthorPublisher cannot exceed 255 characters.");
        }
        catch (ArgumentException exception) { return Task.FromResult(new VaultMutationResult(false, "validation.source", Message: exception.Message)); }
        return writes.ExecuteAsync(
            request.OperationId, "source.create", request, "source_create", request.ClientLabel,
            async (context, token) =>
            {
                var now = DateTime.UtcNow;
                using var insert = context.Command(
                        "INSERT INTO [Sources] ([Title],[SourceType],[AuthorPublisher],[CanonicalUrl],[ArchiveUrl],[Citation],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.VarWChar, title, 255)
                    .Add(OleDbType.VarWChar, request.SourceType, 100)
                    .Add(OleDbType.VarWChar, request.AuthorPublisher, 255)
                    .Add(OleDbType.LongVarWChar, request.CanonicalUrl)
                    .Add(OleDbType.LongVarWChar, request.ArchiveUrl)
                    .Add(OleDbType.LongVarWChar, request.Citation)
                    .Add(OleDbType.LongVarWChar, request.Notes)
                    .Add(OleDbType.Date, now)
                    .Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                var id = await IdentityAsync(context, token).ConfigureAwait(false);
                return new VaultMutationOutcome("Source", id.ToString(), 1, "create", new { title, request.CanonicalUrl });
            }, cancellationToken);
    }

    private static void ValidateWebUrl(string? value, string name)
    {
        if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException($"{name} must be an absolute HTTP or HTTPS URL.");
    }

    public Task<VaultMutationResult> LinkEntityAsync(
        string kind,
        LinkEntityRequest request,
        CancellationToken cancellationToken = default)
    {
        var mapping = kind.ToLowerInvariant() switch
        {
            "tag" => (Table: "EntityTags", RelatedTable: "Tags", RelatedColumn: "TagId"),
            "source" => (Table: "EntitySources", RelatedTable: "Sources", RelatedColumn: "SourceId"),
            "project" => (Table: "ProjectEntities", RelatedTable: "Projects", RelatedColumn: "ProjectId"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Supported link kinds are tag, source, and project.")
        };
        return writes.ExecuteAsync(
            request.OperationId, $"entity.{kind}.link", request, $"entity_{kind}_link", request.ClientLabel,
            async (context, token) =>
            {
                var entity = await RequireEntityAsync(context, request.EntityId, null, null, token).ConfigureAwait(false);
                if (kind.Equals("project", StringComparison.OrdinalIgnoreCase))
                {
                    var project = await RequireEntityAsync(context, request.RelatedId, CanonEntityType.Project, null, token).ConfigureAwait(false);
                    if (entity.ContinuityId != project.ContinuityId) throw new VaultCommandException("continuity.mismatch", "Project and entity must share a continuity.");
                    if (entity.Type == CanonEntityType.Project) throw new VaultCommandException("project.hierarchy_unsupported", "Project-to-project hierarchy is intentionally not modeled in this version.");
                    var now = DateTime.UtcNow;
                    using var insert = context.Command("INSERT INTO [ProjectEntities] ([ProjectId],[MemberEntityId],[Role],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                        .Add(OleDbType.Integer, request.RelatedId).Add(OleDbType.Integer, request.EntityId)
                        .Add(OleDbType.VarWChar, request.Role, 100).Add(OleDbType.LongVarWChar, request.Notes)
                        .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                else
                {
                    using var exists = context.Command($"SELECT COUNT(*) FROM [{mapping.RelatedTable}] WHERE [Id]=? AND [IsDeleted]=False")
                        .Add(OleDbType.Integer, request.RelatedId);
                    if (Convert.ToInt32(await exists.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                        throw new VaultCommandException("entity.not_found", $"{kind} was not found.");
                    using var insert = context.Command($"INSERT INTO [{mapping.Table}] ([EntityId],[{mapping.RelatedColumn}]) VALUES (?,?)")
                        .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.RelatedId);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                return new VaultMutationOutcome("EntityLink", $"{request.EntityId}:{kind}:{request.RelatedId}", null, "link", request);
            }, cancellationToken);
    }

    public Task<VaultMutationResult> LinkSourceTagAsync(LinkEntityRequest request, CancellationToken cancellationToken = default) =>
        writes.ExecuteAsync(request.OperationId, "source.tag.link", request, "source_tag_link", request.ClientLabel,
            async (context, token) =>
            {
                using var source = context.Command("SELECT COUNT(*) FROM [Sources] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, request.EntityId);
                using var tag = context.Command("SELECT COUNT(*) FROM [Tags] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, request.RelatedId);
                if (Convert.ToInt32(await source.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1 ||
                    Convert.ToInt32(await tag.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
                    throw new VaultCommandException("entity.not_found", "Source or tag was not found.");
                using var insert = context.Command("INSERT INTO [SourceTags] ([SourceId],[TagId]) VALUES (?,?)")
                    .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.RelatedId);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return new VaultMutationOutcome("SourceTag", $"{request.EntityId}:{request.RelatedId}", null, "link", request);
            }, cancellationToken);

    public Task<VaultMutationResult> UnlinkEntityAsync(string kind, UnlinkEntityRequest request, CancellationToken cancellationToken = default)
    {
        var mapping = kind.ToLowerInvariant() switch
        {
            "tag" => (Table: "EntityTags", OwnerColumn: "EntityId", RelatedColumn: "TagId", Resource: "EntityTag"),
            "source" => (Table: "EntitySources", OwnerColumn: "EntityId", RelatedColumn: "SourceId", Resource: "EntitySource"),
            "source-tag" => (Table: "SourceTags", OwnerColumn: "SourceId", RelatedColumn: "TagId", Resource: "SourceTag"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Supported unlink kinds are tag, source, and source-tag.")
        };
        return writes.ExecuteAsync(
            request.OperationId, $"{kind}.unlink", request, kind.Replace('-', '_') + "_unlink", request.ClientLabel,
            async (context, token) =>
            {
                using var delete = context.Command($"DELETE FROM [{mapping.Table}] WHERE [{mapping.OwnerColumn}]=? AND [{mapping.RelatedColumn}]=?")
                    .Add(OleDbType.Integer, request.EntityId).Add(OleDbType.Integer, request.RelatedId);
                if (await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    throw new VaultCommandException("link.not_found", "The requested link was not found.");
                return new VaultMutationOutcome(mapping.Resource, $"{request.EntityId}:{request.RelatedId}", null, "unlink", request);
            }, cancellationToken);
    }

    private static async Task InsertSubtypeAsync(VaultWriteContext context, int id, string name, CreateCanonEntityRequest request, CancellationToken token)
    {
        switch (request.EntityType)
        {
            case CanonEntityType.Project:
                using (var command = context.Command("INSERT INTO [Projects] ([EntityId],[Name],[Description]) VALUES (?,?,?)")
                           .Add(OleDbType.Integer, id).Add(OleDbType.VarWChar, name, 255).Add(OleDbType.LongVarWChar, request.Description))
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                break;
            case CanonEntityType.Location:
                using (var command = context.Command("INSERT INTO [Locations] ([EntityId],[Name],[LocationType],[TimeZoneId],[Description]) VALUES (?,?,?,?,?)")
                           .Add(OleDbType.Integer, id).Add(OleDbType.VarWChar, name, 255).Add(OleDbType.VarWChar, request.SecondaryType, 100)
                           .Add(OleDbType.VarWChar, request.TimeZoneId, 100).Add(OleDbType.LongVarWChar, request.Description))
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                break;
            case CanonEntityType.Character:
                var birth = request.Birth ?? StoryDate.Unknown();
                var death = request.Death ?? StoryDate.Unknown();
                using (var command = context.Command("INSERT INTO [Characters] ([EntityId],[GivenName],[MiddleNames],[FamilyName],[PreferredName],[BirthKind],[BirthLowerBound],[BirthUpperBound],[BirthLowerInclusive],[BirthUpperInclusive],[BirthOriginalText],[BirthCalendarId],[DeathKind],[DeathLowerBound],[DeathUpperBound],[DeathLowerInclusive],[DeathUpperInclusive],[DeathOriginalText],[DeathCalendarId],[BirthLocationId],[BirthLocationDetail],[Gender],[Pronouns],[Species],[Occupation],[Nationality],[PhysicalDescription],[PersonalitySummary]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                           .Add(OleDbType.Integer, id).Add(OleDbType.VarWChar, name, 100)
                           .Add(OleDbType.VarWChar, request.MiddleNames, 255).Add(OleDbType.VarWChar, request.FamilyName, 100)
                           .Add(OleDbType.VarWChar, request.PreferredName, 100))
                {
                    AddStoryDate(command, birth); AddStoryDate(command, death);
                    command.Add(OleDbType.Integer, request.BirthLocationId).Add(OleDbType.VarWChar, request.BirthLocationDetail, 255)
                        .Add(OleDbType.VarWChar, request.Gender, 100).Add(OleDbType.VarWChar, request.Pronouns, 100)
                        .Add(OleDbType.VarWChar, request.Species ?? request.SecondaryType, 100)
                        .Add(OleDbType.VarWChar, request.Occupation, 255).Add(OleDbType.VarWChar, request.Nationality, 100)
                        .Add(OleDbType.LongVarWChar, request.PhysicalDescription)
                        .Add(OleDbType.LongVarWChar, request.PersonalitySummary ?? request.Description);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                break;
            case CanonEntityType.Organization:
                using (var command = context.Command("INSERT INTO [Organizations] ([EntityId],[Name],[OrganizationType],[Description]) VALUES (?,?,?,?)")
                           .Add(OleDbType.Integer, id).Add(OleDbType.VarWChar, name, 255).Add(OleDbType.VarWChar, request.SecondaryType, 100).Add(OleDbType.LongVarWChar, request.Description))
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                break;
            case CanonEntityType.Object:
                using (var command = context.Command("INSERT INTO [Objects] ([EntityId],[Name],[ObjectType],[Description]) VALUES (?,?,?,?)")
                           .Add(OleDbType.Integer, id).Add(OleDbType.VarWChar, name, 255).Add(OleDbType.VarWChar, request.SecondaryType, 100).Add(OleDbType.LongVarWChar, request.Description))
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                break;
            case CanonEntityType.WorldEvent:
                var occurred = request.Occurred ?? StoryDate.Unknown();
                using (var command = context.Command("INSERT INTO [WorldEvents] ([EntityId],[Title],[Description],[NarrativeOrder],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId]) VALUES (?,?,?,?,?,?,?,?,?,?,?)")
                           .Add(OleDbType.Integer, id).Add(OleDbType.VarWChar, name, 255).Add(OleDbType.LongVarWChar, request.Description)
                           .Add(OleDbType.Double, request.NarrativeOrder))
                {
                    AddStoryDate(command, occurred);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(request.EntityType));
        }
    }

    private static async Task DuplicateSubtypeAsync(
        VaultWriteContext context,
        CanonEntityType type,
        int sourceId,
        int targetId,
        string name,
        CancellationToken token)
    {
        var sql = type switch
        {
            CanonEntityType.Project =>
                "INSERT INTO [Projects] ([EntityId],[Name],[Description]) SELECT ?,?,[Description] FROM [Projects] WHERE [EntityId]=?",
            CanonEntityType.Location =>
                "INSERT INTO [Locations] ([EntityId],[Name],[LocationType],[ParentLocationId],[TimeZoneId],[Description]) SELECT ?,?,[LocationType],Null,[TimeZoneId],[Description] FROM [Locations] WHERE [EntityId]=?",
            CanonEntityType.Character =>
                "INSERT INTO [Characters] ([EntityId],[GivenName],[MiddleNames],[FamilyName],[PreferredName]," +
                "[BirthKind],[BirthLowerBound],[BirthUpperBound],[BirthLowerInclusive],[BirthUpperInclusive],[BirthOriginalText],[BirthCalendarId]," +
                "[DeathKind],[DeathLowerBound],[DeathUpperBound],[DeathLowerInclusive],[DeathUpperInclusive],[DeathOriginalText],[DeathCalendarId]," +
                "[BirthLocationId],[BirthLocationDetail],[Gender],[Pronouns],[Species],[Occupation],[Nationality],[PhysicalDescription],[PersonalitySummary]) " +
                "SELECT ?,?,[MiddleNames],[FamilyName],[PreferredName]," +
                "[BirthKind],[BirthLowerBound],[BirthUpperBound],[BirthLowerInclusive],[BirthUpperInclusive],[BirthOriginalText],[BirthCalendarId]," +
                "[DeathKind],[DeathLowerBound],[DeathUpperBound],[DeathLowerInclusive],[DeathUpperInclusive],[DeathOriginalText],[DeathCalendarId]," +
                "Null,[BirthLocationDetail],[Gender],[Pronouns],[Species],[Occupation],[Nationality],[PhysicalDescription],[PersonalitySummary] FROM [Characters] WHERE [EntityId]=?",
            CanonEntityType.Organization =>
                "INSERT INTO [Organizations] ([EntityId],[Name],[OrganizationType],[Description]) SELECT ?,?,[OrganizationType],[Description] FROM [Organizations] WHERE [EntityId]=?",
            CanonEntityType.Object =>
                "INSERT INTO [Objects] ([EntityId],[Name],[ObjectType],[Description]) SELECT ?,?,[ObjectType],[Description] FROM [Objects] WHERE [EntityId]=?",
            CanonEntityType.WorldEvent =>
                "INSERT INTO [WorldEvents] ([EntityId],[Title],[Description],[NarrativeOrder],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId]) " +
                "SELECT ?,?,[Description],[NarrativeOrder],[EventKind],[EventLowerBound],[EventUpperBound],[EventLowerInclusive],[EventUpperInclusive],[EventOriginalText],[EventCalendarId] FROM [WorldEvents] WHERE [EntityId]=?",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        using var insert = context.Command(sql)
            .Add(OleDbType.Integer, targetId)
            .Add(OleDbType.VarWChar, name, type == CanonEntityType.Character ? 100 : 255)
            .Add(OleDbType.Integer, sourceId);
        if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new VaultCommandException("integrity.missing_subtype", "The source entity is missing its subtype record.");
    }

    private static void AddStoryDate(AccessCommand command, StoryDate value) => command
        .Add(OleDbType.VarWChar, value.Kind.ToString(), 30)
        .Add(OleDbType.Date, value.LowerBound)
        .Add(OleDbType.Date, value.UpperBound)
        .Add(OleDbType.Boolean, value.LowerInclusive)
        .Add(OleDbType.Boolean, value.UpperInclusive)
        .Add(OleDbType.LongVarWChar, value.OriginalText)
        .Add(OleDbType.VarWChar, value.CalendarId, 50);

    private static void ValidateStoryDate(StoryDate? value)
    {
        if (value is null) return;
        var issues = value.Validate();
        if (issues.Count > 0) throw new ArgumentException(string.Join(" ", issues));
    }

    private static async Task<int> IdentityAsync(VaultWriteContext context, CancellationToken token)
    {
        using var identity = context.Command("SELECT @@IDENTITY");
        return Convert.ToInt32(await identity.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static async Task RequireContinuityAsync(VaultWriteContext context, int id, CancellationToken token)
    {
        using var command = context.Command("SELECT COUNT(*) FROM [Continuities] WHERE [Id]=? AND [IsDeleted]=False").Add(OleDbType.Integer, id);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("entity.not_found", "Continuity was not found.", "Continuity", id.ToString());
    }

    private static async Task RequireVariantGroupAsync(VaultWriteContext context, int id, int continuityId, CanonEntityType entityType, CancellationToken token)
    {
        using var command = context.Command("SELECT COUNT(*) FROM [VariantGroups] WHERE [Id]=? AND [ContinuityId]=? AND [EntityType]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, id).Add(OleDbType.Integer, continuityId).Add(OleDbType.VarWChar, entityType.ToString(), 30);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1)
            throw new VaultCommandException("variant_group.mismatch", "Variant group was not found for this entity type and continuity.");
    }

    private static async Task<(int ContinuityId, CanonEntityType Type, int Version)> RequireEntityAsync(
        VaultWriteContext context, int id, CanonEntityType? type, int? continuityId, CancellationToken token)
    {
        using var command = context.Command("SELECT [ContinuityId],[EntityType],[Version] FROM [CanonEntities] WHERE [Id]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, id);
        var rows = await command.QueryAsync(reader => (reader.GetInt32(0), Enum.Parse<CanonEntityType>(reader.GetString(1)), reader.GetInt32(2)), token).ConfigureAwait(false);
        if (rows.Count != 1) throw new VaultCommandException("entity.not_found", "Canon entity was not found.", type?.ToString() ?? "CanonEntity", id.ToString());
        var row = rows[0];
        if (type is not null && row.Item2 != type) throw new VaultCommandException("entity.type_mismatch", $"Entity {id} is not a {type}.");
        if (continuityId is not null && row.Item1 != continuityId) throw new VaultCommandException("continuity.mismatch", "Linked entities must share a continuity.");
        return row;
    }

    private static async Task ThrowVersionOrNotFoundAsync(
        VaultWriteContext context, string resourceType, int id, string table, string keyColumn, CancellationToken token)
    {
        using var command = context.Command($"SELECT [Version] FROM [{table}] WHERE [{keyColumn}]=?").Add(OleDbType.Integer, id);
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (value is null or DBNull) throw new VaultCommandException("entity.not_found", $"{resourceType} was not found.", resourceType, id.ToString());
        throw new VaultCommandException("concurrency.conflict", $"{resourceType} changed after it was read.", resourceType, id.ToString(), Convert.ToInt32(value));
    }
}
