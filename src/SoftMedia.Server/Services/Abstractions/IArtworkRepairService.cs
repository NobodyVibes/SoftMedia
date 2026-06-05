namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Outcome of an artwork-repair sweep. All counts are for a single run.
/// </summary>
/// <param name="ItemsScanned">Media items + persons whose image columns referenced a local <c>/cache/</c> path.</param>
/// <param name="MissingImages">Cached image references whose backing file no longer exists on disk.</param>
/// <param name="ItemsReEnqueued">Distinct top-level items re-queued for metadata enrichment to re-download art.</param>
/// <param name="LockedSkipped">Items skipped because their metadata is admin-locked (Fix Match).</param>
/// <param name="NeedsRescan">Items whose art can only be recovered by re-scanning the media file (e.g. comic-issue covers), not by a metadata refetch.</param>
/// <param name="FailedEnqueue">Items that should have been re-queued but whose enqueue threw.</param>
public record ArtworkRepairResult(
    int ItemsScanned,
    int MissingImages,
    int ItemsReEnqueued,
    int LockedSkipped,
    int NeedsRescan,
    int FailedEnqueue = 0);

/// <summary>
/// Repairs blank artwork left after a database-only restore. Backups intentionally
/// exclude the on-disk image cache (<c>wwwroot/cache</c>), so restored rows point at
/// <c>/cache/...</c> files that no longer exist. This service finds those dangling
/// references and re-queues the owning items for metadata enrichment, which re-fetches
/// the art from the original providers and repopulates the cache.
/// </summary>
public interface IArtworkRepairService
{
    Task<ArtworkRepairResult> RepairAsync(CancellationToken ct);
}
