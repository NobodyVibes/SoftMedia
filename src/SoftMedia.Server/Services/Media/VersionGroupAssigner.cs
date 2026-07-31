using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// DV-WI-010 — assigns <see cref="MediaItem.VersionGroupId"/>. All assignment is
/// FILL-ONLY (a non-null group id is never rewritten) with two deliberate exceptions:
/// the TV scanner recomputes when a file's parsed (Season, Episode) identity MOVED, and
/// <see cref="GroupMoviesAsync"/> splits groups whose members turn out to carry
/// conflicting provider IDs. Fill-only is what makes an admin split (fresh random id)
/// survive every later scan and backfill.
///
/// Static by design: scanners call it with their own scoped context mid-scan, the boot
/// backfill with its own — no shared state, no DI surface.
/// </summary>
public static class VersionGroupAssigner
{
    /// <summary>
    /// Groups a single (typically new) movie with an existing same-identity row, if one
    /// is already in the DB. Covers the watcher single-file path and incremental scans;
    /// two copies first seen in the SAME parallel scan can miss each other here (their
    /// contexts don't see each other's unsaved rows) — the post-scan
    /// <see cref="GroupMoviesAsync"/> pass converges them.
    /// </summary>
    public static async Task AssignMovieGroupAsync(AppDbContext context, MediaItem movie, CancellationToken ct = default)
    {
        if (movie.VersionGroupId != null || movie.Type != MediaType.Movie) return;

        // Year-scoped fetch (or provider-id match, which bypasses year drift); the
        // normalized-title comparison is not translatable and runs in memory.
        var candidates = await context.MediaItems
            .Where(m => m.LibraryId == movie.LibraryId && m.Type == MediaType.Movie && m.Id != movie.Id)
            .Where(m => (movie.Year == null ? m.Year == null : m.Year == movie.Year)
                     || (movie.ImdbId != null && m.ImdbId == movie.ImdbId))
            .ToListAsync(ct);

        var sibling = candidates
            .Where(c => VersionGroupHelper.AreSameMovie(movie, c))
            .OrderBy(c => c.Id)
            .FirstOrDefault();
        if (sibling == null) return;

        sibling.VersionGroupId ??= Guid.NewGuid();
        movie.VersionGroupId = sibling.VersionGroupId;
    }

    /// <summary>
    /// Library-wide (or, with null, instance-wide) movie grouping sweep. Idempotent:
    /// a converged database produces zero changes. Two passes —
    ///  1. provider-conflict split: a group whose members carry ≥2 distinct non-null
    ///     ImdbIds was a title/year collision that enrichment later disambiguated; every
    ///     ImdbId cluster after the first moves to a fresh shared id.
    ///  2. fill: ungrouped movies join their identity-mates (existing group id if any
    ///     member has one, else a fresh id). Grouped members are never moved — fill-only.
    /// Caller saves. Returns the number of rows whose group changed.
    /// </summary>
    public static async Task<int> GroupMoviesAsync(AppDbContext db, Guid? libraryId, CancellationToken ct = default)
    {
        var movies = await db.MediaItems
            .Where(m => m.Type == MediaType.Movie && (libraryId == null || m.LibraryId == libraryId))
            .ToListAsync(ct);
        var changed = 0;

        // Pass 1 — provider-conflict split.
        foreach (var group in movies.Where(m => m.VersionGroupId != null).GroupBy(m => m.VersionGroupId))
        {
            var idClusters = group
                .Where(m => !string.IsNullOrEmpty(m.ImdbId))
                .GroupBy(m => m.ImdbId!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Min(m => m.Id)) // stable choice of which cluster keeps the group
                .ToList();
            if (idClusters.Count <= 1) continue;

            foreach (var cluster in idClusters.Skip(1))
            {
                var fresh = Guid.NewGuid();
                foreach (var m in cluster)
                {
                    m.VersionGroupId = fresh;
                    changed++;
                }
            }
        }

        // Pass 2 — fill, per library so cross-library copies never merge (ACLs differ).
        foreach (var byLibrary in movies.GroupBy(m => m.LibraryId))
        {
            var buckets = byLibrary.GroupBy(
                m => (Key: VersionGroupHelper.NormalizeTitleKey(m.Title), m.Year));
            foreach (var bucket in buckets)
            {
                var members = bucket.OrderBy(m => m.Id).ToList();
                if (members.Count < 2) continue;

                // Provider veto inside the bucket: with ≥2 distinct non-null ImdbIds the
                // title+year collided across genuinely different films — cluster per id
                // and leave id-less members alone (ambiguous, admin can merge).
                var distinctIds = members
                    .Where(m => !string.IsNullOrEmpty(m.ImdbId))
                    .Select(m => m.ImdbId!.ToLowerInvariant())
                    .Distinct()
                    .Count();

                var clusters = distinctIds >= 2
                    ? members.Where(m => !string.IsNullOrEmpty(m.ImdbId))
                        .GroupBy(m => m.ImdbId!, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.ToList())
                    : new[] { members }.AsEnumerable();

                foreach (var cluster in clusters)
                {
                    if (cluster.Count < 2 && cluster.All(m => m.VersionGroupId == null)) continue;
                    var target = cluster.FirstOrDefault(m => m.VersionGroupId != null)?.VersionGroupId
                                 ?? (cluster.Count > 1 ? Guid.NewGuid() : (Guid?)null);
                    if (target == null) continue;
                    foreach (var m in cluster.Where(m => m.VersionGroupId == null))
                    {
                        m.VersionGroupId = target;
                        changed++;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// DV-WI-014 (plan §5 invariant) — "all sibling copies agree on IsWatched": for every
    /// multi-member group, a user with ANY watched copy gets all their EXISTING rows in
    /// that group stamped watched (any-watched wins; missing rows aren't minted — read
    /// paths aggregate and the write fan-out keeps new state consistent). Runs after the
    /// boot grouping pass and after an admin merge. Caller saves; returns rows changed.
    /// </summary>
    public static async Task<int> ReconcileGroupWatchedAsync(
        AppDbContext db, IReadOnlyCollection<Guid>? onlyGroupIds = null, CancellationToken ct = default)
    {
        var groupsQuery = db.MediaItems.Where(m => m.VersionGroupId != null);
        if (onlyGroupIds is { Count: > 0 })
        {
            var wanted = onlyGroupIds.Cast<Guid?>().ToList();
            groupsQuery = groupsQuery.Where(m => wanted.Contains(m.VersionGroupId));
        }
        var duplicateGroupIds = await groupsQuery
            .GroupBy(m => m.VersionGroupId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        var changed = 0;
        // Chunked — SQLite caps parameters at 999 and a big library can hold many groups.
        foreach (var groupChunk in duplicateGroupIds.Chunk(200))
        {
            var members = await db.MediaItems
                .Where(m => groupChunk.Contains(m.VersionGroupId))
                .Select(m => new { m.Id, m.VersionGroupId })
                .ToListAsync(ct);
            var groupOf = members.ToDictionary(m => m.Id, m => m.VersionGroupId!.Value);
            var memberIds = members.Select(m => m.Id).ToList();

            var interactions = await db.UserMediaInteractions
                .Where(i => memberIds.Contains(i.MediaItemId))
                .ToListAsync(ct);

            foreach (var bucket in interactions.GroupBy(i => (i.UserId, GroupId: groupOf[i.MediaItemId])))
            {
                if (!bucket.Any(i => i.IsWatched)) continue;
                foreach (var row in bucket.Where(i => !i.IsWatched))
                {
                    row.IsWatched = true;
                    row.PlaybackPosition = 0;
                    changed++;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Backfill for legacy episode rows (pre-DV-WI-010 scans): stamps the deterministic
    /// (SeriesId, Season, Episode) group id wherever it is missing. Unparseable rows
    /// (null/0 episode number) stay ungrouped. Caller saves; returns rows changed.
    /// </summary>
    public static async Task<int> AssignEpisodeGroupsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.MediaItems
            .Where(m => m.Type == MediaType.Episode && m.VersionGroupId == null
                     && m.SeriesId != null && m.EpisodeNumber > 0 && m.SeasonNumber != null)
            .ToListAsync(ct);

        foreach (var episode in rows)
        {
            episode.VersionGroupId = VersionGroupHelper.ComputeEpisodeGroupId(
                episode.SeriesId!.Value, episode.SeasonNumber!.Value, episode.EpisodeNumber!.Value);
        }
        return rows.Count;
    }
}
