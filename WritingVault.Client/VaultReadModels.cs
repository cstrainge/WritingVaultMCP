using System.Text.Json;

namespace WritingVault.Client;

public sealed record VaultReadClientOptions(
    string ServerAssemblyPath,
    string DatabasePath,
    string BackupRoot,
    string Provider = "Microsoft.ACE.OLEDB.12.0",
    string ClientLabel = "Writing Vault Web");

public sealed record VaultClock(
    string Status,
    DateTimeOffset? CurrentTime,
    string? ReferenceTimeZoneId,
    string Source,
    string? LocalDisplay = null,
    DateOnly? CurrentDate = null);

public sealed record VaultSession(string? ContinuityName, VaultClock Clock, string SurfaceVersion);

public sealed record VaultContinuity(
    string Name,
    string? Description,
    string DefaultTimeZoneId,
    VaultClock Clock,
    int Version,
    bool IsDeleted,
    string ObservedRevision);

public sealed record VaultReference(
    string Ref,
    string Kind,
    string Label,
    string? Context = null,
    string? ContinuityName = null,
    int? Version = null,
    bool IsDeleted = false,
    string? AssociationRole = null,
    string? AssociationNotes = null);

public sealed record VaultPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore, string ObservedRevision);

public sealed record VaultSection<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

public sealed record VaultRecord(
    VaultReference Summary,
    IReadOnlyDictionary<string, JsonElement> Fields,
    IReadOnlyDictionary<string, VaultSection<VaultReference>> Sections,
    string ObservedRevision);

public sealed record VaultRecordSnapshot(
    VaultRecord Overview,
    IReadOnlyDictionary<string, VaultRecord> Notes,
    int SnapshotVersion,
    DateTime SavedAtUtc,
    bool IsBaseline,
    string LatestRef);

public sealed record VaultIssue(string Code, string Message, bool Retryable = false);

public sealed record VaultHealth(
    bool Ready,
    string ToolSurfaceVersion,
    string SchemaMigration,
    int PendingWrites,
    string DatabaseCapacityBand,
    long ImageRenditionBytes,
    string? LastSuccessfulBackupUtc,
    IReadOnlyList<VaultIssue> Issues,
    string ObservedRevision);

public sealed record VaultChange(
    string Scope,
    string Kind,
    string? Ref,
    string Action,
    bool TimelineAffected,
    bool FullVisibleRefreshRequired = false);

public sealed record VaultChanges(
    string Cursor,
    IReadOnlyList<VaultChange> Changes,
    bool Heartbeat,
    bool CursorExpired,
    string ObservedRevision);

public sealed record VaultSessionUpdate(
    string? ContinuityName = null,
    string TimeAction = "Keep",
    DateTimeOffset? CurrentTime = null,
    string? ReferenceTimeZoneId = null,
    DateOnly? CurrentDate = null);

public sealed record VaultSearchRequest(
    string? Text = null,
    IReadOnlyList<string>? Kinds = null,
    string? Cursor = null,
    int Limit = 50,
    string DeletionState = "Active",
    bool IncludeContent = false,
    IReadOnlyList<string>? Tags = null,
    string? Project = null);

public sealed record VaultRelatedRequest(
    string? Ref, string Relation, string DeletionState = "Active",
    string? Cursor = null, int Limit = 50);

public sealed record VaultHistoryEntry(
    string ChangedAtUtc, string ClientLabel, string Action, string RecordKind,
    string? RecordRef, int? VersionBefore, int? VersionAfter,
    IReadOnlyDictionary<string, JsonElement>? Changes = null);

public sealed record VaultStoryDate(
    string Kind, string Display, string? Lower, string? Upper,
    bool LowerInclusive, bool UpperInclusive, string CalendarId, string? OriginalText = null);

public sealed record VaultTimelineItem(
    string Ref, string Kind, string Title, string? Summary, string Lane,
    VaultStoryDate Occurred, IReadOnlyList<VaultReference> Related,
    double? NarrativeOrder = null, bool IsDeleted = false,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<VaultReference>? Projects = null,
    bool ContainsHighlight = false,
    string? Boundary = null, VaultStoryDate? BoundaryDate = null);

public sealed record VaultTimelinePage(
    IReadOnlyList<VaultTimelineItem> Items, string? NextCursor, bool HasMore,
    IReadOnlyList<VaultReference> Undated, VaultClock Clock,
    string ObservedRevision, bool Aggregated = false);

public sealed record VaultTimelineRequest(
    string? From = null, string? To = null,
    IReadOnlyList<string>? Lanes = null, IReadOnlyList<string>? FocusRefs = null,
    IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? Projects = null,
    IReadOnlyList<string>? Locations = null, string? Text = null,
    bool IncludeUndated = true, string Mode = "Calendar", string Resolution = "Detail",
    string? Cursor = null, int Limit = 100,
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyList<string>? EntityEventKinds = null,
    bool UndatedOnly = false,
    string? HighlightRef = null,
    bool ExpandRanges = false);

public sealed record VaultSourceSnapshotTextPage(
    string SnapshotRef, string Text, string? NextCursor, bool HasMore,
    string? MediaType, string? RetrievedAtUtc, int ByteSize, string ObservedRevision);

public sealed record VaultImageMetadata(
    string Ref, VaultReference Owner, string? Title, string? Caption,
    string? AltText, string? Role, string? CanonStatus, bool IsPrimary,
    string MediaType, int Width, int Height, long ByteCount,
    int Version, bool IsDeleted, string ObservedRevision,
    VaultReference? Source = null, string? CreatedAtUtc = null,
    string? UpdatedAtUtc = null);

public sealed record VaultImageView(VaultImageMetadata Image, string Size, string MediaType,
    int Width, int Height, long ByteCount, string ObservedRevision, byte[] Bytes,
    int ContentRevision = 1, bool IsCurrentContent = true);

public sealed record VaultImageContentRevision(int Revision, bool IsCurrent,
    string MediaType, int Width, int Height, long ByteCount, string? ArchivedAtUtc);

public sealed record VaultImageRevisionHistory(string ImageRef, int CurrentRevision,
    IReadOnlyList<VaultImageContentRevision> Revisions, bool HasMore,
    int? NextBeforeRevision,
    string ObservedRevision);

public sealed class VaultReadException(string message, string? code = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string? Code { get; } = code;
}
