namespace WritingVaultMcp.Infrastructure.Access;

internal sealed partial class AccessV4PageSnapshotCapture
{
    /// <summary>
    /// Records only the state visible at this migration/startup boundary.
    /// Missing pre-v4 history is never reconstructed from audit summaries.
    /// Batches are repeatable after interruption; no client is accepted until
    /// this method succeeds during backend startup.
    /// </summary>
    internal async Task EnsureBaselineAsync(CancellationToken token = default)
    {
        var records = await references.EnumerateSnapshotRecordsAsync(OverviewTypes, token)
            .ConfigureAwait(false);
        var continuities = records.Where(value => value.Type == "Continuity")
            .Select(value => value.Id).Distinct().ToArray();
        var pages = records.SelectMany(record =>
                record.Type == "Continuity"
                    ? new[] { (record.Type, Key: record.Id, Context: record.Id) }
                    : record.ContinuityId is { } scoped
                        ? new[] { (record.Type, Key: record.Id, Context: scoped) }
                        : continuities.Select(id => (record.Type, Key: record.Id, Context: id)))
            .Distinct().OrderBy(value => value.Context).ThenBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Key).ToArray();

        foreach (var batch in pages.Chunk(40))
        {
            var operationId = Guid.NewGuid().ToString("D");
            var result = await coordinator.ExecuteAsync(operationId, "snapshot.baseline",
                new { pages = batch.Length }, "snapshot_baseline", "local-admin",
                async (context, cancellationToken) =>
                {
                    foreach (var (type, key, continuity) in batch)
                        if (!await store.HasSnapshotAsync(context, type, key, continuity, cancellationToken)
                                .ConfigureAwait(false))
                            await CaptureAsync(context, type, key, continuity, true,
                                cancellationToken).ConfigureAwait(false);
                    return new VaultMutationOutcome("SnapshotBaseline", "0", null,
                        "baseline", new { pages = batch.Length });
                }, token).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException($"Historical-page baseline failed: {result.Code}.");
        }
    }
}
