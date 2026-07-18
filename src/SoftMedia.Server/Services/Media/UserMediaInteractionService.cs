using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Media;

public class UserMediaInteractionService : IUserMediaInteractionService
{
    private readonly AppDbContext _context;
    private readonly ILogger<UserMediaInteractionService> _logger;

    public UserMediaInteractionService(AppDbContext context, ILogger<UserMediaInteractionService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task RateMediaAsync(Guid userId, Guid mediaId, int? rating)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            if (rating == null) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.Rating = rating;
        
        if (interaction.Rating == null && interaction.IsFavorite == false && interaction.IsWatched == false && (interaction.PlaybackPosition ?? 0) <= 0)
        {
            _context.UserMediaInteractions.Remove(interaction);
        }

        await _context.SaveChangesAsync();

        // Recalculate average rating for the media item
        await UpdateMediaInternalRatingAsync(mediaId);
    }

    private async Task UpdateMediaInternalRatingAsync(Guid mediaId)
    {
        var ratings = await _context.UserMediaInteractions
            .Where(i => i.MediaItemId == mediaId && i.Rating != null)
            .Select(i => i.Rating!.Value)
            .ToListAsync();

        var mediaItem = await _context.MediaItems.FindAsync(mediaId);
        if (mediaItem != null)
        {
            if (ratings.Any())
            {
                mediaItem.InternalRating = ratings.Average();
                mediaItem.InternalRatingCount = ratings.Count;
            }
            else
            {
                mediaItem.InternalRating = null;
                mediaItem.InternalRatingCount = 0;
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Updated internal rating for media {MediaId}: {Rating} ({Count} votes)", 
                mediaId, mediaItem.InternalRating, mediaItem.InternalRatingCount);
        }
    }

    public async Task ToggleFavoriteAsync(Guid userId, Guid mediaId, bool isFavorite)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            if (!isFavorite) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsFavorite = isFavorite;
        await _context.SaveChangesAsync();
    }

    public async Task MarkWatchedAsync(Guid userId, Guid mediaId, bool watched)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            if (!watched) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsWatched = watched;
        if (watched)
        {
            interaction.LastPlayed = DateTime.UtcNow;
            interaction.PlaybackPosition = 0;

            // R-WI-013: an explicit "watched" (next-episode overlay, detail page) closes the
            // current open play — the user finished it even if the 95% beat never landed.
            // Privacy review: this write must honour the recording toggle too — with it OFF, the
            // diary is frozen entirely (an open row from before the opt-out keeps its last
            // recorded state and ages out of the window; no post-opt-out timestamps land).
            var recordHistory = await _context.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (bool?)u.RecordPlaybackHistory)
                .FirstOrDefaultAsync();
            if (recordHistory == true)
            {
                var openPlay = await _context.PlaybackHistory
                    .Where(h => h.UserId == userId && h.MediaItemId == mediaId && !h.Completed)
                    .OrderByDescending(h => h.LastBeatAt)
                    .FirstOrDefaultAsync();
                if (openPlay != null && DateTime.UtcNow - openPlay.LastBeatAt <= PlaySessionWindow)
                {
                    openPlay.Completed = true;
                    openPlay.LastBeatAt = DateTime.UtcNow;
                }
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task UpdateProgressAsync(Guid userId, Guid mediaId, double position, string? bookLocation = null)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.PlaybackPosition = position;
        if (bookLocation != null)
        {
            interaction.BookLocation = bookLocation;
        }
        interaction.LastPlayed = DateTime.UtcNow;

        await RecordPlaybackHistoryAsync(userId, mediaId, position);

        await _context.SaveChangesAsync();
    }

    // ---- R-WI-013: per-play history, recorded inside the progress-beat flow ----

    /// <summary>A play only counts once the position crosses min(this, half the runtime).</summary>
    public const double VideoPlayThresholdSeconds = 240;
    public const double AudioPlayThresholdSeconds = 60;

    /// <summary>Beats within this window continue the same play row; later beats open a new
    /// one (pause/resume is one play; tomorrow's rewatch is another).</summary>
    public static readonly TimeSpan PlaySessionWindow = TimeSpan.FromHours(6);

    /// <summary>After a play completes, a beat whose position is below this fraction of the play's
    /// high-water mark is treated as a rewatch-from-the-top (opens a new play); at/above it is the
    /// post-credits tail or a near-end scrub of the same viewing (keeps the same row).</summary>
    public const double RewatchRestartFraction = 0.5;

    /// <summary>
    /// The play-threshold decision, kept pure for tests. §7 Q5 proposed default: a play counts
    /// when the position first crosses min(240s video / 60s audio, 50% of the runtime) — short
    /// clips and songs still count, a quick peek at a long movie doesn't. Unknown duration
    /// falls back to the absolute threshold alone.
    /// </summary>
    public static bool CrossesPlayThreshold(MediaType type, double position, double duration)
    {
        var absolute = type == MediaType.Audio ? AudioPlayThresholdSeconds : VideoPlayThresholdSeconds;
        var threshold = duration > 0 ? Math.Min(absolute, duration * 0.5) : absolute;
        return position >= threshold;
    }

    private async Task RecordPlaybackHistoryAsync(Guid userId, Guid mediaId, double position)
    {
        // Guards: only playable AV types get history (the book reader posts the same endpoint
        // per page-turn and has ReadingSession), and position<=0 beats are the player's
        // next-episode reset, not playback.
        if (position <= 0) return;

        var item = await _context.MediaItems.AsNoTracking()
            .Where(m => m.Id == mediaId)
            .Select(m => new { m.Type, m.Duration, m.CreditsStart })
            .FirstOrDefaultAsync();
        if (item == null) return;
        if (item.Type != MediaType.Movie && item.Type != MediaType.Episode && item.Type != MediaType.Audio) return;

        // History-privacy toggle (user-owned): when off, this user's plays are never written
        // to history and never bump the aggregate counters — invisible everywhere. Resume
        // state was already updated above; only the diary stops.
        var recordHistory = await _context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.RecordPlaybackHistory)
            .FirstOrDefaultAsync();
        if (recordHistory != true) return;

        var now = DateTime.UtcNow;
        // Accepted limitation (review, low): this read-then-add is not serialized, so two beats
        // for the same (user,item) arriving within one ~10s window on separate scoped DbContexts
        // (e.g. the same title on two devices, or an unmount-save racing the interval-save) can
        // each open a row / bump PlayCount. A unique index won't help — abandoned plays stay
        // non-completed, so two legitimate open rows for one (user,item) are valid — and beats are
        // fire-and-forget, so a SQLITE_BUSY is harmless. Worst case is one cosmetic extra row.
        var latest = await _context.PlaybackHistory
            .Where(h => h.UserId == userId && h.MediaItemId == mediaId)
            .OrderByDescending(h => h.LastBeatAt)
            .FirstOrDefaultAsync();

        // Decide whether this beat CONTINUES the latest play or starts a new one.
        // A beat within the recency window continues the latest row when:
        //   - the row is still open (not completed): any beat continues it, so scrubbing around
        //     an active viewing never spawns a second play; OR
        //   - the row is completed but this beat is still in the "already-watched" region (the
        //     post-95%/credits TAIL, or a scrub near the end). Crucially this stops the tail
        //     beats that keep arriving every ~10s after completion from each opening a brand-new
        //     completed row (the double-counting the review caught). Only a genuine RESTART —
        //     position dropped back below RewatchRestartFraction of how far this play reached —
        //     is treated as a rewatch and opens a new play.
        var withinWindow = latest != null && now - latest.LastBeatAt <= PlaySessionWindow;
        var continuesLatest = withinWindow &&
            (!latest!.Completed || position >= latest.MaxPosition * RewatchRestartFraction);

        if (continuesLatest)
        {
            latest!.LastBeatAt = now;
            latest.MaxPosition = Math.Max(latest.MaxPosition, position);
            latest.Completed = latest.Completed || Helpers.MediaCompletionHelper.IsComplete(
                latest.MaxPosition, item.Duration, item.CreditsStart, isWatched: false);
            return; // caller saves
        }

        // Starting a new play (first watch, a return after the window, or a rewatch from the top):
        // only a beat past the threshold opens one.
        if (!CrossesPlayThreshold(item.Type, position, item.Duration)) return;

        _context.PlaybackHistory.Add(new PlaybackHistory
        {
            UserId = userId,
            MediaItemId = mediaId,
            MediaType = item.Type,
            StartedAt = now,
            LastBeatAt = now,
            MaxPosition = position,
            Completed = Helpers.MediaCompletionHelper.IsComplete(position, item.Duration, item.CreditsStart, isWatched: false),
        });

        // Make the previously-dead MediaItem columns real: plays = history rows.
        var tracked = await _context.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaId);
        if (tracked != null)
        {
            tracked.PlayCount++;
            tracked.LastPlayed = now;
        }

        _logger.LogDebug("Play recorded: user {UserId} item {MediaId} at position {Position:F0}s", userId, mediaId, position);
    }

    /// <summary>R-WI-013 privacy — whether this user's plays are recorded to history.</summary>
    public async Task<bool> GetRecordHistoryAsync(Guid userId)
    {
        return await _context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.RecordPlaybackHistory)
            .FirstOrDefaultAsync(); // unknown user → false (never records anyway)
    }

    /// <summary>R-WI-013 privacy — user-owned toggle; false stops the diary going forward.</summary>
    public async Task SetRecordHistoryAsync(Guid userId, bool record)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return;
        user.RecordPlaybackHistory = record;
        await _context.SaveChangesAsync();
        // Debug — a user's privacy choice must not land in shipped (Information-level) logs.
        _logger.LogDebug("User {UserId} set playback-history recording to {Record}", userId, record);
    }

    /// <summary>
    /// R-WI-013 privacy — erase the caller's entire play history. The aggregate
    /// MediaItem.PlayCount/LastPlayed are RECOMPUTED from the remaining rows (all users), so
    /// the documented invariant "PlayCount == history rows for the item" holds after a clear
    /// and no user-linked residue survives. Returns the number of plays erased.
    /// </summary>
    public async Task<int> ClearHistoryAsync(Guid userId)
    {
        var mine = await _context.PlaybackHistory
            .Where(h => h.UserId == userId)
            .ToListAsync();
        if (mine.Count == 0) return 0;

        var affectedItemIds = mine.Select(h => h.MediaItemId).Distinct().ToList();

        // Compute the survivors by EXCLUDING the caller's rows up front, so the delete and the
        // aggregate recompute commit in ONE SaveChanges (atomic under EF's implicit transaction —
        // a crash can't leave rows deleted but aggregates stale). A concurrent beat racing this
        // with a stale in-memory PlayCount remains possible and accepted, like the beat-vs-beat
        // race above: a cosmetic one-off drift on a home server, self-limited in practice.
        var remaining = await _context.PlaybackHistory
            .Where(h => affectedItemIds.Contains(h.MediaItemId) && h.UserId != userId)
            .GroupBy(h => h.MediaItemId)
            .Select(g => new { MediaItemId = g.Key, Count = g.Count(), Last = g.Max(h => h.StartedAt) })
            .ToListAsync();
        var byItem = remaining.ToDictionary(r => r.MediaItemId);

        _context.PlaybackHistory.RemoveRange(mine);

        var items = await _context.MediaItems.Where(m => affectedItemIds.Contains(m.Id)).ToListAsync();
        foreach (var item in items)
        {
            if (byItem.TryGetValue(item.Id, out var agg))
            {
                item.PlayCount = agg.Count;
                item.LastPlayed = agg.Last;
            }
            else
            {
                item.PlayCount = 0;
                item.LastPlayed = null;
            }
        }
        await _context.SaveChangesAsync();

        // Debug, not Information: privacy actions must not leave user-linked residue in shipped
        // logs (the production default level is Information) — matches the Debug convention for
        // per-user playback data above.
        _logger.LogDebug("User {UserId} cleared {Count} play-history row(s)", userId, mine.Count);
        return mine.Count;
    }

    /// <summary>
    /// R-WI-013 — self-scoped history page, newest first. Only surfaces plays whose item the
    /// user can CURRENTLY access: a revoked library grant or a lowered content-rating ceiling
    /// hides the title from history too (matches WatchlistController's read gate — history must
    /// not leak titles of media the user can no longer see). Admin/unrestricted short-circuits
    /// inside the filters. Filtering before paging keeps the returned count honest.
    /// </summary>
    public async Task<IReadOnlyList<PlaybackHistory>> GetHistoryAsync(
        Guid userId, int page, int pageSize, LibraryAccess access, UserRatingCeilings ceilings)
    {
        var allowedItemIds = _context.MediaItems
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .Select(m => m.Id);

        return await _context.PlaybackHistory
            .AsNoTracking()
            .Include(h => h.MediaItem)
            .Where(h => h.UserId == userId && allowedItemIds.Contains(h.MediaItemId))
            .OrderByDescending(h => h.LastBeatAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<UserMediaInteraction?> GetInteractionAsync(Guid userId, Guid mediaId)
    {
        return await _context.UserMediaInteractions
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);
    }

    public async Task<IEnumerable<UserMediaInteraction>> GetInteractionsAsync(Guid userId, IEnumerable<Guid> mediaIds)
    {
        return await _context.UserMediaInteractions
            .AsNoTracking()
            .Where(i => i.UserId == userId && mediaIds.Contains(i.MediaItemId))
            .ToListAsync();
    }

    public async Task ToggleWatchlistAsync(Guid userId, Guid mediaId, bool isWatchlisted)
    {
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == mediaId);

        if (interaction == null)
        {
            // No-op when removing from watchlist for an item the user never watchlisted.
            if (!isWatchlisted) return;
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId,
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsWatchlisted = isWatchlisted;
        // Stamp on add (used for sort); clear on remove so re-adds get a fresh timestamp.
        interaction.WatchlistedAt = isWatchlisted ? DateTime.UtcNow : null;

        // GC empty interaction rows so the table doesn't accumulate dead state.
        if (!interaction.IsFavorite
            && !interaction.IsWatched
            && !interaction.IsWatchlisted
            && interaction.Rating == null
            && (interaction.PlaybackPosition ?? 0) <= 0
            && interaction.BookLocation == null)
        {
            _context.UserMediaInteractions.Remove(interaction);
        }

        await _context.SaveChangesAsync();
    }
}
