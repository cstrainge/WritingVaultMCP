namespace WritingVault.Client;

public interface IVaultReadClient : IAsyncDisposable
{
    Task<VaultHealth> HealthAsync(CancellationToken cancellationToken = default);
    Task<VaultPage<VaultContinuity>> ListContinuitiesAsync(CancellationToken cancellationToken = default);
    Task<VaultPage<VaultContinuity>> ListContinuitiesPageAsync(string? cursor, int limit = 100, CancellationToken cancellationToken = default);
    Task<VaultSession> GetSessionAsync(CancellationToken cancellationToken = default);
    Task<VaultSession> SetSessionAsync(VaultSessionUpdate update, CancellationToken cancellationToken = default);
    Task<VaultPage<VaultReference>> SearchAsync(VaultSearchRequest request, CancellationToken cancellationToken = default);
    Task<VaultRecord> GetAsync(string? reference = null, CancellationToken cancellationToken = default);
    Task<VaultRecord> GetRecordAsync(string reference, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<VaultRecordSnapshot> GetRecordSnapshotAsync(string reference, int snapshotVersion, CancellationToken cancellationToken = default);
    Task<VaultReference> LocateAsync(string reference, CancellationToken cancellationToken = default);
    Task<VaultReference> LocateAsync(string reference, bool includeDeleted, CancellationToken cancellationToken = default);
    Task<VaultPage<VaultReference>> RelatedAsync(VaultRelatedRequest request, CancellationToken cancellationToken = default);
    Task<VaultPage<VaultHistoryEntry>> HistoryAsync(string? reference, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default);
    Task<VaultTimelinePage> TimelineAsync(VaultTimelineRequest request, CancellationToken cancellationToken = default);
    Task<VaultSourceSnapshotTextPage> SourceSnapshotViewAsync(string snapshotRef, string? cursor = null, int limit = 8192, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<VaultPage<VaultImageMetadata>> ImageListAsync(string target, string? cursor = null, int limit = 50, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<VaultPage<VaultImageMetadata>> ImageSearchAsync(string? text = null, string? cursor = null, int limit = 50, bool acrossContinuities = false, CancellationToken cancellationToken = default);
    Task<VaultImageView> ImageViewAsync(string imageRef, string size = "Thumbnail", int? revision = null, CancellationToken cancellationToken = default);
    Task<VaultImageRevisionHistory> ImageRevisionHistoryAsync(string imageRef, int? beforeRevision = null, int limit = 50, CancellationToken cancellationToken = default);
    Task<VaultChanges> ChangesSinceAsync(string? cursor, int waitSeconds, CancellationToken cancellationToken = default);
}

public interface IVaultReadClientFactory
{
    Task<IVaultReadClient> ConnectAsync(string clientLabel, CancellationToken cancellationToken = default);
}
