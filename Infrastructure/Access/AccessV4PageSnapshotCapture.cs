using WritingVaultMcp.Application.V4;
using System.Text.Json;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

/// <summary>
/// Builds a complete, paged record overview and its Markdown notes using the
/// same Access transaction as the mutation, then stores one immutable page.
/// Discovery of every page affected by a mutation is deliberately separate.
/// </summary>
internal sealed partial class AccessV4PageSnapshotCapture(
    IAccessConnectionFactory factory,
    VaultWriteCoordinator coordinator,
    AccessVaultService vault,
    VaultReferenceService references,
    V4TargetResolver targets,
    V4CursorCodec cursors,
    VaultChangeNotifier changes)
{
    private readonly AccessV4PageSnapshotStore store = new(factory);

    internal Task<int> CaptureAsync(VaultWriteContext context, string recordType,
        int recordKey, int contextContinuityId, bool baseline,
        CancellationToken token = default) =>
        coordinator.ReadPendingWriteAsync(context, async () =>
        {
            var continuityName = await references.ContinuityNameAsync(contextContinuityId, token)
                .ConfigureAwait(false);
            var session = new VaultSessionContext { ClientLabel = "page-snapshot" };
            session.SelectContinuity(contextContinuityId, continuityName);
            var reads = new AccessV4ReadService(factory, coordinator, vault, references,
                targets, new VaultMcpResultMapper(references), session, cursors, changes);
            var reference = recordType.Equals("Continuity", StringComparison.OrdinalIgnoreCase)
                ? null : await references.ReferenceAsync(recordType, recordKey, token).ConfigureAwait(false);
            var overview = await reads.GetAsync(new V4GetRequest(reference, IncludeDeleted: true), token)
                .ConfigureAwait(false);
            if (reference is not null && !overview.Summary.IsDeleted && overview.Summary.Kind is
                    V4RecordKind.Project or V4RecordKind.Location or V4RecordKind.Character or
                    V4RecordKind.Organization or V4RecordKind.Object or V4RecordKind.WorldEvent)
            {
                var fields = new Dictionary<string, JsonElement>(overview.Fields,
                    StringComparer.OrdinalIgnoreCase);
                fields["currentTemporalState"] = await reads.CurrentTemporalStateAsync(reference, token)
                    .ConfigureAwait(false);
                if (overview.Summary.Kind == V4RecordKind.Character)
                {
                    var temporal = new AccessV4TemporalService(factory, coordinator, references,
                        targets, session, reads);
                    fields["age"] = JsonSerializer.SerializeToElement(
                        await temporal.AgeAsync(new(reference), token).ConfigureAwait(false),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    fields["temporalProfile"] = await temporal.ProfileAsync(reference, token)
                        .ConfigureAwait(false);
                }
                overview = overview with { Fields = fields };
            }
            var sections = new Dictionary<string, V4Section<V4ReferenceSummary>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (relation, first) in overview.Sections)
            {
                var items = new List<V4ReferenceSummary>(first.Items);
                var cursor = first.NextCursor;
                var hasMore = first.HasMore;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (hasMore && cursor is not null)
                {
                    if (!seen.Add(cursor) || items.Count > 10_000)
                        throw new VaultCommandException("snapshot.relation_too_large",
                            "The change would make a historical page association exceed its limit.");
                    var next = await reads.RelatedAsync(new V4ListRelatedRequest(
                        reference, relation, V4DeletionState.All, cursor, 500), token)
                        .ConfigureAwait(false);
                    items.AddRange(next.Items);
                    hasMore = next.HasMore;
                    cursor = next.NextCursor;
                }
                if (hasMore)
                    throw new VaultCommandException("snapshot.relation_incomplete",
                        "A historical page association could not be captured completely.");
                sections[relation] = new(items, null, false);
            }
            overview = overview with { Sections = sections };
            var notes = new Dictionary<string, V4RecordOverview>(StringComparer.Ordinal);
            if (sections.TryGetValue("notes", out var noteSection))
            {
                foreach (var note in noteSection.Items)
                    notes[note.Ref] = await reads.GetAsync(new V4GetRequest(note.Ref,
                        IncludeDeleted: true), token).ConfigureAwait(false);
            }
            return await store.CaptureAsync(context, recordType, recordKey,
                contextContinuityId, new(overview, notes), baseline, token)
                .ConfigureAwait(false);
        });
}
