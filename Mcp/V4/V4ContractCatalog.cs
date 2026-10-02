using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WritingVaultMcp.Mcp.V4;

public enum V4ToolAccess { Read, Write }

public sealed record V4ToolDefinition(
    string Name, string Description, V4ToolAccess Access,
    bool Destructive, Type RequestType, Type ResponseType);

public static class V4ContractCatalog
{
    public const string SurfaceVersion = "4.0";
    private const string MarkdownLinkHint = "The viewer renders [text](vault-record:<ref>) links and " +
        "![alt](vault-image:<image-ref>) embeds with optional {width=300px} or {width=50%}. " +
        "Use ?v=<positive-version> to pin an immutable image-content revision or record-page snapshot; " +
        "record_snapshot_get reads saved fields, notes, and associations. See docs/V4_CONTRACT.md.";

    public static IReadOnlyList<V4ToolDefinition> Tools { get; } =
    [
        R<V4EmptyRequest, V4HealthResult>("vault_health", "Reports v4 readiness, schema and integrity issues, pending writes, backup recency, and storage capacity without exposing paths."),
        R<V4ContinuityListRequest, V4Page<V4ContinuitySummary>>("continuity_list", "Lists continuity names and clock summaries with bounded keyset paging."),
        R<V4SessionSetRequest, V4SessionView>("session_set", "Selects continuity by exact name and optionally sets an exact currentTime or date-only currentDate for this connection, or clears the override."),
        R<V4EmptyRequest, V4SessionView>("session_get", "Returns this connection's selected continuity and effective clock provenance."),
        R<V4SearchRequest, V4Page<V4ReferenceSummary>>("search", "Searches across selected record kinds, aliases, tags, projects, and optional content without guessing identity."),
        R<V4GetRequest, V4RecordOverview>("get", "Returns a bounded page-shaped overview; omit ref to read the selected continuity."),
        R<V4RecordSnapshotGetRequest, V4RecordSnapshotResult>("record_snapshot_get", "Reads an immutable pinned page version with saved fields, notes, and associations."),
        R<V4RecordSnapshotListRequest, V4RecordSnapshotListResult>("record_snapshot_list", "Lists retained record-page snapshot versions newest first so a pinned link can use an actual version."),
        R<V4RecordRefRequest, V4ReferenceSummary>("record_locate", "Locates an explicit semantic record reference across active continuities for internal links without changing the session."),
        R<V4SourceSnapshotViewRequest, V4SourceSnapshotViewResult>("source_snapshot_view", "Pages verified cached source text without exposing a local file path."),
        R<V4ListRelatedRequest, V4Page<V4ReferenceSummary>>("list_related", "Pages one named relation from a continuity or semantic record reference."),
        R<V4TimelineRequest, V4TimelineResult>("timeline_get", "Returns calendar or narrative timeline items, including project spans and repeated occurrences inside the requested viewport. Without a viewport, only stored dates establish the range; repeat stop dates never extend it. Set expandRecurrences=false when fitting an automatic viewport, then query that viewport with repeats enabled. Historical data does not require a clock."),
        R<V4TagTargetsRequest, V4Page<V4ReferenceSummary>>("tag_targets", "Pages all selected-continuity and vault-global targets carrying one tag."),
        R<V4HistoryRequest, V4Page<V4HistoryEntry>>("history_get", "Reads real-UTC change history by semantic reference or public mutation token."),
        R<V4ChangesSinceRequest, V4ChangesResult>("changes_since", "Waits for or lists committed changes after an opaque revision cursor for viewer freshness."),
        R<V4CharacterAgeRequest, V4CharacterAgeResult>("character_age", "Returns calendar, legal, biological, and experienced age at session time or a hypothetical at value; a date-only session returns day-wide bounds."),
        R<V4TemporalEffectPreviewRequest, V4TemporalPreviewResult>("temporal_effect_preview", "Validates one hypothetical temporal effect and previews ages without writing."),
        R<V4EntityLocalTimeRequest, V4LocalTimeResult>("entity_local_time", "Resolves local story time and timezone provenance for one semantic record."),
        R<V4RecordRefRequest, V4DeletePreview>("record_delete_preview", "Reports blockers and consequences before soft deletion of a supported semantic record."),
        R<V4RelationshipMergePreviewRequest, V4RelationshipMergePreview>("relationship_merge_preview", "Reviews a legacy relationship merge, including both periods, events, notes, claims and histories, and returns a stale-safe token."),
        R<V4ImageListRequest, V4Page<V4ImageMetadata>>("image_list", "Pages image metadata for one canon entity, continuity, relationship, or event without returning image bytes."),
        R<V4ImageSearchRequest, V4Page<V4ImageMetadata>>("image_search", "Searches image metadata without bytes; acrossContinuities finds images throughout the active Vault without changing the selected continuity."),
        R<V4ImageViewRequest, V4ImageViewResult>("image_view", "Returns an explicit image reference from any active continuity as metadata plus a bounded thumbnail, display, or original MCP image content block."),
        R<V4ImageRevisionHistoryRequest, V4ImageRevisionHistoryResult>("image_revision_history", "Lists retained image content revisions without returning bytes."),

        W<V4ContinuityCreateRequest>("continuity_create", "Creates a continuity and its initially unset shared clock."),
        W<V4BackupCreateRequest>("vault_backup_create", "Creates and verifies an idempotent database and companion-asset backup in the configured backup root without accepting or exposing a filesystem path."),
        W<V4ContinuityUpdateRequest>("continuity_update", "Applies sparse versioned changes to continuity metadata.", true),
        W<V4ContinuityClockSetRequest>("continuity_clock_set", "Sets or clears the selected continuity's shared artificial clock.", true),
        W<V4VariantGroupCreateRequest>("variant_group_create", "Creates a selected-continuity variant group for one canon entity kind."),
        W<V4VariantGroupUpdateRequest>("variant_group_update", "Applies sparse versioned changes to a variant group.", true),
        W<V4EntityVariantGroupSetRequest>("entity_variant_group_set", "Assigns or clears an entity's same-kind selected-continuity variant group.", true),
        W<V4EntityCreateRequest>("entity_create", "Creates one of the six canon entity kinds inside the selected continuity."),
        W<V4EntityUpdateRequest>("entity_update", "Applies an allowlisted sparse versioned patch to one canon entity. For a character with an exact Gregorian birth date, birthdayRecurring enables annual birthdays through death; changing birth or death automatically updates the schedule.", true),
        W<V4EntityDuplicateRequest>("entity_duplicate_to_continuity", "Creates an independent shallow duplicate in a target continuity."),
        W<V4TagCreateRequest>("tag_create", "Creates a normalized vault-global tag."),
        W<V4TagUpdateRequest>("tag_update", "Applies sparse versioned changes to a vault-global tag.", true),
        W<V4TagApplyRequest>("tag_apply", "Atomically adds or removes one tag across several mixed semantic targets.", true),
        W<V4SourceCreateRequest>("source_create", "Creates a vault-global source with revisitable URLs and local citation data."),
        W<V4SourceUpdateRequest>("source_update", "Applies sparse versioned changes to a source.", true),
        W<V4SourceSnapshotAddRequest>("source_snapshot_add", "Stores a bounded content-addressed local source snapshot."),
        W<V4ClaimCreateRequest>("claim_create", "Creates a continuity-scoped claim with targets and source evidence atomically."),
        W<V4ClaimUpdateRequest>("claim_update", "Applies sparse versioned changes to claim text and editorial status.", true),
        W<V4NoteAddRequest>("note_add", "Adds a Markdown note to a canon entity or named continuity. " + MarkdownLinkHint),
        W<V4NoteUpdateRequest>("note_update", "Applies sparse versioned changes to a Markdown note. " + MarkdownLinkHint, true),
        W<V4EventRecordRequest>("event_record", "Creates one world event with participants, locations, and zero or more same-continuity project associations atomically."),
        W<V4EventUpdateRequest>("event_update", "Updates an existing world, entity, or relationship event with an expected version. Set recurrence to mark an exact single-day event as repeating daily, weekly, monthly, or yearly; until is an inclusive YYYY-MM-DD stop date. Missing calendar days are skipped. Clear flags remove optional fields. Project boundary edits immediately change the displayed book span.", true),
        W<V4EntityEventAddRequest>("entity_event_add", "Adds a character, location, organization, object, or project story event. Exact single-day events can recur daily, weekly, monthly, or yearly through an inclusive until date; missing calendar days are skipped. Projects may designate one StoryBegins and one StoryEnds boundary to drive their timeline span. Other events omit projectBoundary. Optionally link to a world event and same-continuity projects."),
        W<V4EventProjectApplyRequest>("event_project_apply", "Atomically adds or removes project associations for a world, entity, or relationship event.", true),
        W<V4AliasAddRequest>("entity_alias_add", "Adds a normalized alias to a character or organization."),
        W<V4NoteSourceLinkRequest>("note_source_link", "Links a source to a note with locator and commentary."),
        W<V4EntitySourceLinkRequest>("entity_source_link", "Links a vault-global source to one canon entity."),
        W<V4EntitySourceLinkRequest>("entity_source_unlink", "Removes one exact entity-source link.", true),
        W<V4ProjectEntityLinkRequest>("project_entity_link", "Assigns an entity to a selected-continuity project with role and notes."),
        W<V4LocationMoveRequest>("location_move", "Moves a location under another selected-continuity location after cycle checks.", true),
        W<V4ResidenceAddRequest>("character_residence_add", "Adds a residence period while enforcing primary overlap rules."),
        W<V4ResidenceTransitionRequest>("character_residence_transition", "Atomically closes one residence and opens its replacement.", true),
        W<V4MembershipAddRequest>("organization_membership_add", "Adds an organization membership using one period or independent joined/left dates, with optional descriptions for each transition."),
        W<V4MembershipTransitionRequest>("organization_membership_transition", "Atomically closes a membership and optionally opens its replacement; accepts descriptions for the exit and replacement entry.", true),
        W<V4OrganizationMembershipTransitionsSetRequest>("organization_membership_transitions_set", "Sets independently precise or fuzzy join and leave dates and optional descriptions for one organization membership.", true),
        W<V4OrganizationLocationAddRequest>("organization_location_add", "Adds an organization location period with primary-location validation."),
        W<V4RelationshipTypeCreateRequest>("relationship_type_create", "Creates a directed or undirected reciprocal character relationship type."),
        W<V4CharacterRelationshipCreateRequest>("character_relationship_create", "Creates one canonical temporal character relationship visible from both endpoints."),
        W<V4RelationshipCreateRequest>("relationship_create", "Creates one shared relationship with 2 to 100 characters and optional initial membership periods."),
        W<V4RelationshipParticipantAddRequest>("relationship_participant_add", "Adds a character to an undirected relationship using its expected version."),
        W<V4RelationshipMembershipPeriodAddRequest>("relationship_membership_period_add", "Atomically adds a repeatable membership period using either one period date or independently fuzzy joined and left dates, each with an optional description."),
        W<V4RelationshipMembershipPeriodUpdateRequest>("relationship_membership_period_update", "Corrects one versioned membership period and rechecks overlap.", true),
        W<V4RelationshipMembershipTransitionsSetRequest>("relationship_membership_transitions_set", "Sets independently precise or fuzzy join and leave dates and optional descriptions for one membership period; clear flags restore generic timeline wording.", true),
        W<V4RelationshipEventAddRequest>("relationship_event_add", "Records one relationship-owned event, optionally linked to a world event and projects."),
        W<V4RelationshipNotesSetRequest>("relationship_notes_set", "Sets or clears Markdown notes on a versioned relationship record. " + MarkdownLinkHint, true),
        W<V4RelationshipMergeApplyRequest>("relationship_merge_apply", "Consolidates a reviewed legacy pair into a surviving relationship while preserving old references, notes and history.", true),
        W<V4OwnershipPrincipalCreateRequest>("ownership_principal_create", "Creates a character, organization, or external ownership principal."),
        W<V4ObjectOwnershipAddRequest>("object_ownership_add", "Adds a non-overlapping owned, unknown, or unowned period."),
        W<V4ObjectOwnershipReplaceOwnersRequest>("object_ownership_replace_owners", "Corrects the complete co-owner group for one versioned period.", true),
        W<V4ObjectOwnershipTransferRequest>("object_ownership_transfer", "Atomically closes an ownership period and opens its replacement.", true),
        W<V4ObjectLocationAddRequest>("object_location_add", "Adds a non-overlapping physical-location period for an object."),
        W<V4ObjectCustodyAddRequest>("object_custody_add", "Adds a non-overlapping custody period independent of ownership and location."),
        W<V4WorldEventParticipantAddRequest>("world_event_participant_add", "Adds a same-continuity participant with role, impact, and outcome."),
        W<V4WorldEventLocationAddRequest>("world_event_location_add", "Adds a world-event location while enforcing one active primary location."),
        W<V4TemporalProfileSetRequest>("character_temporal_profile_set", "Creates or updates a character's opt-in temporal-age profile.", true),
        W<V4TemporalEffectCreateRequest>("character_temporal_effect_create", "Adds a non-overlapping personal temporal-age effect."),
        W<V4TemporalEffectUpdateRequest>("character_temporal_effect_update", "Applies versioned changes to a temporal-age effect and revalidates overlap.", true),
        W<V4VersionedRecordRequest>("record_soft_delete", "Soft-deletes an allowlisted semantic record after version and blocker checks.", true),
        W<V4VersionedRecordRequest>("record_restore", "Restores an allowlisted semantic record and rechecks its invariants.", true),
        W<V4ImageAttachRequest>("image_attach", "Attaches one PNG, JPEG, or WebP to a canon entity. Supply exactly one host-provided file or legacy inline image (20 MiB maximum). Keep mutationToken and file_id stable across retries; never invent file URLs."),
        W<V4StoryImageAttachRequest>("story_image_attach", "Attaches one PNG, JPEG, or WebP to a continuity, relationship, or event. Supply exactly one host-provided file or legacy inline image (20 MiB maximum). Keep mutationToken and file_id stable across retries; never invent file URLs."),
        W<V4ImageReplaceRequest>("image_replace", "Replaces image content while retaining prior revisions. Supply exactly one host-provided file or legacy inline image (PNG/JPEG/WebP, 20 MiB maximum), plus expectedVersion. Keep mutationToken and file_id stable across retries; never invent file URLs."),
        W<V4ImageUpdateRequest>("image_update", "Updates versioned image metadata or primary designation.", true)
    ];

    public static JsonObject ExportSchemas()
    {
        var tools = new JsonArray();
        foreach (var tool in Tools.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            var inputSchema = ExportSchema(tool.RequestType);
            if (tool.Name is "relationship_membership_period_add" or "organization_membership_add" &&
                inputSchema is JsonObject membership)
            {
                if (membership["properties"] is JsonObject fields)
                {
                    fields["period"] = StoryDateInputSchema();
                    fields["joined"] = StoryDateInputSchema();
                    fields["left"] = StoryDateInputSchema();
                }
                membership["oneOf"] = new JsonArray(
                    new JsonObject
                    {
                        ["required"] = new JsonArray("period"),
                        ["not"] = new JsonObject { ["anyOf"] = new JsonArray(
                            new JsonObject { ["required"] = new JsonArray("joined") },
                            new JsonObject { ["required"] = new JsonArray("left") }) }
                    },
                    new JsonObject
                    {
                        ["required"] = new JsonArray("joined", "left"),
                        ["not"] = new JsonObject { ["required"] = new JsonArray("period") }
                    });
            }
            var descriptor = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["access"] = tool.Access.ToString(),
                ["destructive"] = tool.Destructive,
                ["inputSchema"] = inputSchema,
                ["outputSchema"] = ExportOutputSchema(tool.ResponseType)
            };
            if (tool.Name is "image_attach" or "story_image_attach" or "image_replace")
                descriptor["_meta"] = new JsonObject { ["openai/fileParams"] = new JsonArray("file") };
            tools.Add(descriptor);
        }

        return new JsonObject
        {
            ["surfaceVersion"] = SurfaceVersion,
            ["toolCount"] = Tools.Count,
            ["tools"] = tools,
            ["limits"] = JsonSerializer.SerializeToNode(new
            {
                defaultPageSize = V4ContractLimits.DefaultPageSize,
                maximumPageSize = V4ContractLimits.MaximumPageSize,
                defaultRelationSectionSize = V4ContractLimits.DefaultRelationSectionSize,
                maximumRelationSectionSize = V4ContractLimits.MaximumRelationSectionSize,
                defaultTimelinePageSize = V4ContractLimits.DefaultTimelinePageSize,
                maximumTimelinePageSize = V4ContractLimits.MaximumTimelinePageSize,
                maximumIncludeSections = V4ContractLimits.MaximumIncludeSections,
                maximumAmbiguityCandidates = V4ContractLimits.MaximumAmbiguityCandidates,
                maximumMutationTokenLength = V4ContractLimits.MaximumMutationTokenLength,
                maximumCursorLength = V4ContractLimits.MaximumCursorLength,
                maximumSearchTextLength = V4ContractLimits.MaximumSearchTextLength,
                maximumNameLength = V4ContractLimits.MaximumNameLength,
                maximumShortTextLength = V4ContractLimits.MaximumShortTextLength,
                maximumLongTextLength = V4ContractLimits.MaximumLongTextLength,
                maximumSnapshotUtf8Bytes = V4ContractLimits.MaximumSnapshotUtf8Bytes,
                maximumImageInputBytes = V4ContractLimits.MaximumImageInputBytes,
                maximumChangeWaitSeconds = V4ContractLimits.MaximumChangeWaitSeconds,
                displayImageMaximumPixels = V4ContractLimits.DisplayImageMaximumPixels,
                displayImageMaximumBytes = V4ContractLimits.DisplayImageMaximumBytes,
                thumbnailMaximumPixels = V4ContractLimits.ThumbnailMaximumPixels,
                thumbnailMaximumBytes = V4ContractLimits.ThumbnailMaximumBytes
            }, SerializerOptions)
        };
    }

    public static JsonObject ExportCommonSchemas()
    {
        var types = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["referenceSummary"] = typeof(V4ReferenceSummary),
            ["page"] = typeof(V4Page<V4ReferenceSummary>),
            ["section"] = typeof(V4Section<V4ReferenceSummary>),
            ["storyDateInput"] = typeof(V4StoryDateInput),
            ["storyDateView"] = typeof(V4StoryDateView),
            ["error"] = typeof(V4Error),
            ["mutationResult"] = typeof(V4MutationResult),
            ["clock"] = typeof(V4ClockView),
            ["timelineItem"] = typeof(V4TimelineItem),
            ["recordOverview"] = typeof(V4RecordOverview),
            ["changesResult"] = typeof(V4ChangesResult)
        };
        var schemas = new JsonObject();
        foreach (var (name, type) in types) schemas[name] = ExportOutputSchema(type);
        return new JsonObject { ["surfaceVersion"] = SurfaceVersion, ["schemas"] = schemas };
    }

    public static JsonObject ExportExamples()
    {
        var tools = new JsonArray();
        foreach (var tool in Tools.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            var request = ExampleFor(ExportSchema(tool.RequestType), tool.Name, true);
            var response = ExampleFor(ExportSchema(tool.ResponseType), tool.Name, false);
            if (tool.Name is ("image_attach" or "story_image_attach" or "image_replace") && request is JsonObject imageRequest)
            {
                imageRequest.Remove("image");
                imageRequest["file"] = new JsonObject
                {
                    ["download_url"] = "https://host-provided.example/temporary-authorized-file",
                    ["file_id"] = "host-provided-file-id",
                    ["mime_type"] = "image/png",
                    ["file_name"] = "portrait.png"
                };
            }
            if (tool.Name == "event_update" && request is JsonObject eventRequest)
            {
                eventRequest["event"] = "worldevent:arrival-day~ABCDEFGHJK";
                eventRequest["recurrence"] = new JsonObject { ["frequency"] = "Yearly", ["interval"] = 1, ["until"] = "2030-09-23" };
            }
            if (tool.Name == "relationship_membership_period_add" && request is JsonObject membershipRequest)
                membershipRequest["period"] = new JsonObject
                {
                    ["kind"] = "ExactDate", ["value"] = "2026-09-28"
                };
            tools.Add(new JsonObject { ["name"] = tool.Name, ["request"] = request, ["response"] = response });
        }
        return new JsonObject { ["surfaceVersion"] = SurfaceVersion, ["tools"] = tools };
    }

    public static void WriteSnapshots(string outputDirectory)
    {
        var full = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(full);
        Write(Path.Combine(full, "tool-schemas.json"), ExportSchemas());
        Write(Path.Combine(full, "common-schemas.json"), ExportCommonSchemas());
        Write(Path.Combine(full, "examples.json"), ExportExamples());
    }

    private static V4ToolDefinition R<TRequest, TResponse>(string name, string description) =>
        new(name, description, V4ToolAccess.Read, false, typeof(TRequest), typeof(TResponse));

    private static V4ToolDefinition W<TRequest>(string name, string description, bool destructive = false) =>
        new(name, description, V4ToolAccess.Write, destructive, typeof(TRequest), typeof(V4MutationResult));

    private static JsonNode ExportSchema(Type type)
    {
        var options = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) => TransformSchema(context, node)
        };
        return JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, type, options);
    }

    private static JsonNode ExportOutputSchema(Type type)
    {
        var schema = ExportSchema(type);
        AllowOmittedNullOutputProperties(schema);
        return schema;
    }

    private static void AllowOmittedNullOutputProperties(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) AllowOmittedNullOutputProperties(item);
            return;
        }
        if (node is not JsonObject schema) return;

        // The v4 tools omit null values from serialized results. A nullable
        // constructor parameter can therefore be absent on the wire even when
        // JsonSchemaExporter marks it required. Keep the published output
        // contract aligned with the actual MCP structured content.
        if (schema["properties"] is JsonObject properties && schema["required"] is JsonArray required)
        {
            for (var index = required.Count - 1; index >= 0; index--)
            {
                var name = required[index]?.GetValue<string>();
                if (name is not null && properties[name] is JsonObject property && AllowsNull(property))
                    required.RemoveAt(index);
            }
            if (required.Count == 0) schema.Remove("required");
        }
        foreach (var child in schema.ToArray()) AllowOmittedNullOutputProperties(child.Value);
    }

    private static bool AllowsNull(JsonObject schema) =>
        schema["type"] is JsonArray types && types.Any(type => type?.ToJsonString() == "\"null\"") ||
        schema["type"]?.ToJsonString() == "\"null\"" ||
        schema["anyOf"] is JsonArray anyOf && anyOf.OfType<JsonObject>().Any(AllowsNull) ||
        schema["oneOf"] is JsonArray oneOf && oneOf.OfType<JsonObject>().Any(AllowsNull);

    private static JsonNode TransformSchema(JsonSchemaExporterContext context, JsonNode node)
    {
        if (context.TypeInfo.Type == typeof(V4StoryDateInput)) return StoryDateInputSchema();
        if (context.TypeInfo.Type == typeof(V4InlineImageInput)) return InlineImageSchema();
        if (context.TypeInfo.Type == typeof(V4HostFileInput)) return HostFileSchema();
        if (node is not JsonObject value) return node;

        if (context.TypeInfo.Type == typeof(V4TimelineRequest) && context.PropertyInfo is null &&
            value["properties"] is JsonObject timelineProperties &&
            timelineProperties["limit"] is JsonObject timelineLimit)
        {
            timelineLimit["minimum"] = 1;
            timelineLimit["maximum"] = V4ContractLimits.MaximumTimelinePageSize;
        }
        if (context.TypeInfo.Type.IsGenericType &&
            context.TypeInfo.Type.GetGenericTypeDefinition() == typeof(V4Page<>) &&
            value["properties"]?["items"] is JsonObject pageItems)
            pageItems["maxItems"] = V4ContractLimits.MaximumPageSize;
        if (context.TypeInfo.Type.IsGenericType &&
            context.TypeInfo.Type.GetGenericTypeDefinition() == typeof(V4Section<>) &&
            value["properties"]?["items"] is JsonObject sectionItems)
            sectionItems["maxItems"] = V4ContractLimits.MaximumRelationSectionSize;
        if (context.TypeInfo.Type == typeof(V4TimelineResult) && value["properties"]?["items"] is JsonObject timelineItems)
            timelineItems["maxItems"] = V4ContractLimits.MaximumTimelinePageSize;
        if (context.TypeInfo.Type == typeof(V4Error) && value["properties"]?["candidates"] is JsonObject candidates)
            candidates["maxItems"] = V4ContractLimits.MaximumAmbiguityCandidates;
        if (context.TypeInfo.Type == typeof(V4RecordOverview) && value["properties"]?["sections"] is JsonObject sections)
            sections["maxProperties"] = V4ContractLimits.MaximumIncludeSections;

        var name = context.PropertyInfo?.Name;
        if (name is null) return value;
        if (name == "changes" && IsSparseUpdate(context.PropertyInfo?.DeclaringType))
            return SparseChangesSchema(context.PropertyInfo!.DeclaringType);
        switch (name)
        {
            case "limit":
                value["minimum"] = 1;
                value["maximum"] = context.BaseTypeInfo?.Type == typeof(V4TimelineRequest)
                    ? V4ContractLimits.MaximumTimelinePageSize
                    : V4ContractLimits.MaximumPageSize;
                break;
            case "waitSeconds": value["minimum"] = 0; value["maximum"] = V4ContractLimits.MaximumChangeWaitSeconds; break;
            case "cursor": value["maxLength"] = V4ContractLimits.MaximumCursorLength; break;
            case "mutationToken":
                value["minLength"] = 1; value["maxLength"] = V4ContractLimits.MaximumMutationTokenLength;
                value["pattern"] = "^[A-Za-z0-9][A-Za-z0-9._:-]*$"; break;
            case "text": value["maxLength"] = V4ContractLimits.MaximumSearchTextLength; break;
            case "name": case "title": case "label": case "alias":
                value["maxLength"] = V4ContractLimits.MaximumNameLength; break;
            case "purpose": value["maxLength"] = V4ContractLimits.MaximumNameLength; break;
            case "body": case "description": case "joinDescription": case "leaveDescription":
            case "replacementJoinDescription": case "notes": case "commentary": case "citation":
                value["maxLength"] = V4ContractLimits.MaximumLongTextLength; break;
            case "content": value["maxLength"] = V4ContractLimits.MaximumSnapshotUtf8Bytes; break;
            case "include": value["maxItems"] = V4ContractLimits.MaximumIncludeSections; break;
            case "characters": value["minItems"] = 2; value["maxItems"] = 100; break;
            case "limits": value["maxProperties"] = V4ContractLimits.MaximumIncludeSections; break;
            case "projects": case "targets": case "participants": case "locations":
            case "entities": case "kinds": case "tags": case "roles": case "canonStatuses":
            case "entityKinds": case "focusRefs": case "lanes": case "owners":
                value["maxItems"] = V4ContractLimits.MaximumPageSize; break;
            case "interval": value["minimum"] = 1; value["maximum"] = 10000; break;
            case "until": value["pattern"] = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$"; break;
            case "expectedVersion": value["minimum"] = 1; break;
            case "confidence": value["minimum"] = 0; value["maximum"] = 1; break;
            case "biologicalRate": case "experiencedRate": value["minimum"] = 0; break;
            case "sharePartsPerMillion": value["minimum"] = 0; value["maximum"] = 1_000_000; break;
        }
        return value;
    }

    private static bool IsSparseUpdate(Type? type) => type == typeof(V4ContinuityUpdateRequest) ||
        type == typeof(V4VariantGroupUpdateRequest) || type == typeof(V4EntityUpdateRequest) ||
        type == typeof(V4TagUpdateRequest) || type == typeof(V4SourceUpdateRequest) ||
        type == typeof(V4ClaimUpdateRequest) || type == typeof(V4NoteUpdateRequest) ||
        type == typeof(V4TemporalEffectUpdateRequest) || type == typeof(V4ImageUpdateRequest);

    private static JsonObject SparseChangesSchema(Type? requestType)
    {
        static JsonObject Text(int maximum, bool nullable = true) => new()
        {
            ["type"] = nullable ? new JsonArray("string", "null") : "string",
            ["maxLength"] = maximum
        };
        static JsonObject Number(bool nullable = false) => new()
        {
            ["type"] = nullable ? new JsonArray("number", "null") : "number"
        };
        static JsonObject Boolean() => new() { ["type"] = "boolean" };
        static JsonObject NullableDate()
        {
            return new JsonObject
            {
                ["oneOf"] = new JsonArray(StoryDateInputSchema(), new JsonObject { ["type"] = "null" })
            };
        }

        var properties = new JsonObject();
        if (requestType == typeof(V4ContinuityUpdateRequest))
        {
            properties["name"] = Text(V4ContractLimits.MaximumNameLength, false);
            properties["description"] = Text(V4ContractLimits.MaximumLongTextLength);
            properties["defaultTimeZoneId"] = Text(100, false);
        }
        else if (requestType == typeof(V4VariantGroupUpdateRequest))
        {
            properties["name"] = Text(V4ContractLimits.MaximumNameLength);
            properties["notes"] = Text(V4ContractLimits.MaximumLongTextLength);
        }
        else if (requestType == typeof(V4EntityUpdateRequest))
        {
            properties["name"] = Text(V4ContractLimits.MaximumNameLength, false);
            foreach (var field in new[]
                     {
                         "description", "physicalDescription", "personalitySummary"
                     }) properties[field] = Text(V4ContractLimits.MaximumLongTextLength);
            foreach (var field in new[]
                     {
                         "secondaryType", "timeZoneId", "birthLocation", "birthLocationDetail", "middleNames",
                         "familyName", "preferredName", "gender", "pronouns", "species", "occupation", "nationality"
                     }) properties[field] = Text(V4ContractLimits.MaximumNameLength);
            properties["birthdayRecurring"] = new JsonObject { ["type"] = "boolean", ["description"] = "Repeat exact Gregorian birthdays annually through the character death date without extending the timeline bounds." };
            properties["birth"] = NullableDate();
            properties["death"] = NullableDate();
            properties["occurred"] = NullableDate();
            properties["narrativeOrder"] = Number(true);
        }
        else if (requestType == typeof(V4TagUpdateRequest))
        {
            properties["name"] = Text(100, false);
            properties["description"] = Text(V4ContractLimits.MaximumLongTextLength);
        }
        else if (requestType == typeof(V4SourceUpdateRequest))
        {
            properties["title"] = Text(V4ContractLimits.MaximumNameLength, false);
            foreach (var field in new[] { "canonicalUrl", "archiveUrl", "citation", "notes" })
                properties[field] = Text(V4ContractLimits.MaximumLongTextLength);
            properties["sourceType"] = Text(100);
            properties["authorPublisher"] = Text(V4ContractLimits.MaximumNameLength);
        }
        else if (requestType == typeof(V4ClaimUpdateRequest))
        {
            properties["claimText"] = Text(V4ContractLimits.MaximumLongTextLength, false);
            properties["status"] = Text(50, false);
            properties["confidence"] = Number(true);
            properties["targetField"] = Text(V4ContractLimits.MaximumNameLength);
            properties["commentary"] = Text(V4ContractLimits.MaximumLongTextLength);
        }
        else if (requestType == typeof(V4NoteUpdateRequest))
        {
            properties["title"] = Text(V4ContractLimits.MaximumNameLength);
            properties["body"] = Text(V4ContractLimits.MaximumLongTextLength, false);
        }
        else if (requestType == typeof(V4TemporalEffectUpdateRequest))
        {
            properties["name"] = Text(V4ContractLimits.MaximumNameLength, false);
            properties["period"] = StoryDateInputSchema();
            properties["biologicalRate"] = Number();
            properties["experiencedRate"] = Number();
            properties["causeWorldEvent"] = Text(V4ContractLimits.MaximumNameLength);
            properties["notes"] = Text(V4ContractLimits.MaximumLongTextLength);
        }
        else if (requestType == typeof(V4ImageUpdateRequest))
        {
            properties["title"] = Text(V4ContractLimits.MaximumNameLength);
            foreach (var field in new[] { "caption", "altText" }) properties[field] = Text(V4ContractLimits.MaximumLongTextLength);
            properties["role"] = Text(100);
            properties["canonStatus"] = Text(50);
            properties["source"] = Text(V4ContractLimits.MaximumNameLength);
            properties["isPrimary"] = Boolean();
        }
        else
        {
            throw new InvalidOperationException($"No sparse-change schema is registered for {requestType?.Name ?? "unknown request"}.");
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Sparse changes. Omitted fields are preserved; explicit null clears fields whose schema permits null.",
            ["additionalProperties"] = false,
            ["minProperties"] = 1,
            ["properties"] = properties
        };
    }

    private static JsonObject StoryDateInputSchema() => new()
    {
        ["description"] = "A compact exact date string, compact year object, or full timezone-free fuzzy story-date object. Circa may use value as a nominal calendar day with no invented uncertainty window, or lower/upper for an authored uncertainty interval.",
        ["oneOf"] = new JsonArray
        {
            new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" },
            new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["const"] = "Year" },
                    ["value"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9]{4}$" },
                    ["calendarId"] = new JsonObject { ["type"] = "string", ["default"] = "Gregorian" }
                },
                ["required"] = new JsonArray("kind", "value")
            },
            new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Unknown", "ExactInstant", "ExactDate", "Month", "Year", "Circa", "KnownRange", "UncertainRange", "Before", "After") },
                    ["value"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                    ["lower"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                    ["upper"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                    ["lowerInclusive"] = new JsonObject { ["type"] = new JsonArray("boolean", "null") },
                    ["upperInclusive"] = new JsonObject { ["type"] = new JsonArray("boolean", "null") },
                    ["originalText"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = V4ContractLimits.MaximumShortTextLength },
                    ["calendarId"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["default"] = "Gregorian" }
                },
                ["required"] = new JsonArray("kind")
            }
        }
    };

    private static JsonObject HostFileSchema() => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["description"] = "Host-authorized file. Use the file object supplied by the MCP client; do not manufacture its URL or ID. Supply file or image, never both.",
        ["properties"] = new JsonObject
        {
            ["download_url"] = new JsonObject { ["type"] = "string" },
            ["file_id"] = new JsonObject { ["type"] = "string" },
            ["mime_type"] = new JsonObject { ["type"] = "string" },
            ["file_name"] = new JsonObject { ["type"] = "string" }
        },
        ["required"] = new JsonArray("download_url", "file_id")
    };

    private static JsonObject InlineImageSchema() => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["description"] = "Legacy inline image bytes. Supply exactly one of dataBase64 or dataUrl, and omit the host file input.",
        ["properties"] = new JsonObject
        {
            ["mediaType"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("image/png", "image/jpeg", "image/webp") },
            ["dataBase64"] = new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64", ["minLength"] = 1, ["maxLength"] = ((V4ContractLimits.MaximumImageInputBytes + 2) / 3) * 4 },
            ["dataUrl"] = new JsonObject { ["type"] = "string", ["pattern"] = "^data:image/(png|jpeg|webp);base64,", ["maxLength"] = ((V4ContractLimits.MaximumImageInputBytes + 2) / 3) * 4 + 64 }
        },
        ["required"] = new JsonArray("mediaType"),
        ["oneOf"] = new JsonArray
        {
            new JsonObject { ["required"] = new JsonArray("dataBase64") },
            new JsonObject { ["required"] = new JsonArray("dataUrl") }
        }
    };

    private static JsonNode? ExampleFor(JsonNode? schema, string toolName, bool request)
    {
        if (schema is null) return null;
        if (schema is JsonObject obj)
        {
            if (obj["oneOf"] is JsonArray oneOf)
            {
                var selected = oneOf.OfType<JsonObject>().FirstOrDefault(candidate => !IsNullOnly(candidate));
                return ExampleFor(selected, toolName, request);
            }
            if (obj["anyOf"] is JsonArray anyOf)
            {
                var selected = anyOf.OfType<JsonObject>().FirstOrDefault(candidate => !IsNullOnly(candidate));
                return ExampleFor(selected, toolName, request);
            }
            if (obj["enum"] is JsonArray values && values.Count > 0) return values[0]?.DeepClone();
            if (obj["const"] is { } constant) return constant.DeepClone();

            var type = TypeName(obj);
            if (type == "object" || obj["properties"] is JsonObject)
            {
                var result = new JsonObject();
                var required = obj["required"] is JsonArray requiredValues
                    ? requiredValues.Select(value => value?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                if (obj["properties"] is JsonObject properties)
                {
                    foreach (var (name, property) in properties)
                    {
                        if (!required.Contains(name)) continue;
                        result[name] = ExampleFor(property, name, request);
                    }
                    if (result.Count == 0 && obj["minProperties"]?.GetValue<int>() is > 0 &&
                        properties.FirstOrDefault() is var first && first.Key is not null)
                        result[first.Key] = ExampleFor(first.Value, first.Key, request);
                }
                return result;
            }
            if (type == "array") return new JsonArray(ExampleFor(obj["items"], toolName, request));
            if (type == "boolean") return false;
            if (type == "integer") return IntegerMinimum(obj["minimum"]);
            if (type == "number") return NumberMinimum(obj["minimum"]);
            if (type == "string") return StringExample(toolName);
        }
        return null;
    }

    private static string StringExample(string name) => name switch
    {
        "mutationToken" => "example-20260928-001",
        "continuityName" or "targetContinuityName" => "Lostville",
        "referenceTimeZoneId" or "timeZoneId" => "America/Vancouver",
        "calendarId" => "Gregorian",
        "ref" or "target" or "entity" or "character" or "source" or "note" or "project" or "location" or "organization" or "object" or "worldEvent" or "participant" => "character:aurora~ABCDEFGHJK",
        "cursor" or "observedRevision" => "cursor~ABCDEFGHJK",
        "mediaType" => "image/png",
        "currentTime" or "changedAtUtc" or "retrievedAtUtc" => "2026-09-28T12:00:00-07:00",
        "effectiveAt" or "lower" or "upper" or "value" => "2026-09-28",
        "title" => "Example title",
        "name" => "Example name",
        "body" => "## Example\n\nMarkdown note.",
        "content" => "Cached source text.",
        _ => "example"
    };

    private static string? TypeName(JsonObject value)
    {
        if (value["type"] is JsonValue single && single.TryGetValue<string>(out var name)) return name;
        if (value["type"] is JsonArray many)
            return many.Select(node => node?.GetValue<string>()).FirstOrDefault(name => name is not null and not "null");
        return null;
    }

    private static bool IsNullOnly(JsonObject value) => TypeName(value) is null && value["type"]?.ToJsonString() == "\"null\"";

    private static int IntegerMinimum(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var wide)) return checked((int)wide);
            if (value.TryGetValue<double>(out var number)) return checked((int)number);
        }
        return 1;
    }

    private static double NumberMinimum(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var wide)) return wide;
        }
        return 1.0;
    }

    private static void Write(string path, JsonNode value) =>
        File.WriteAllText(path, value.ToJsonString(IndentedOptions) + Environment.NewLine);

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static JsonSerializerOptions IndentedOptions { get; } = new() { WriteIndented = true };
}
