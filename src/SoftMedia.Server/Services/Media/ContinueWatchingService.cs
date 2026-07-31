using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Services.Media;

public interface IContinueWatchingService
{
    /// <summary>
    /// The calling user's in-progress Movies and TV shows, newest-first. TV shows collapse to a
    /// single show card; finished movies and fully-watched series are excluded. ACL- and
    /// rating-filtered. Each DTO carries Progress/PlaybackPosition so the card can render a resume bar.
    /// </summary>
    Task<List<MediaItemDto>> GetContinueWatchingAsync(Guid userId, int limit);
}

public class ContinueWatchingService : IContinueWatchingService
{
    private readonly AppDbContext _db;
    private readonly IRecommendationService _recommendations;
    private readonly IUserLibraryAccessProvider _libraryAccess;
    private readonly IUserContentRatingProvider _ratings;

    public ContinueWatchingService(
        AppDbContext db,
        IRecommendationService recommendations,
        IUserLibraryAccessProvider libraryAccess,
        IUserContentRatingProvider ratings)
    {
        _db = db;
        _recommendations = recommendations;
        _libraryAccess = libraryAccess;
        _ratings = ratings;
    }

    /// <summary>Candidate rows fetched per page while assembling the row.</summary>
    private const int CandidatePageSize = 300;

    /// <summary>
    /// Hard ceiling on total candidate rows scanned per request. Paging (rather than a single
    /// fixed window) means a long tail of watched-episode rows cannot push an older in-progress
    /// item out of reach, while this cap keeps a pathological interaction history bounded.
    /// </summary>
    private const int MaxCandidateScan = 3000;

    /// <summary>
    /// Ceiling on next-episode resolver calls per request. Each distinct candidate series costs a
    /// resolver call (two indexed queries); realistic accounts have a handful, but a user with
    /// hundreds of distinct watched series must not turn one home-page load into hundreds of
    /// per-series queries. Once spent, remaining series candidates are skipped (movies still scan).
    /// </summary>
    private const int SeriesResolveBudget = 60;

    public async Task<List<MediaItemDto>> GetContinueWatchingAsync(Guid userId, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);

        // Per-user gates fetched up front so candidates are filtered AT THE SQL JOIN: an item the
        // user cannot see must never consume a row slot (WatchlistController applies its limit
        // after the ACL filter for the same reason). GetEpisodesAsync applies these same filters,
        // so the resolver and this query agree on which episodes exist for this user.
        var access = await _libraryAccess.GetCurrentAsync();
        var ceilings = await _ratings.GetCurrentAsync();
        var visibleItems = _db.MediaItems.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing();

        // One entry per movie / series, in most-recently-played order. Episodes collapse into a
        // single show card (deduped by series); finished movies and fully-finished series drop out.
        var entries = new List<Entry>();
        var seenSeries = new HashSet<Guid>();
        // DV-WI-004/016: duplicate copies of one movie must occupy ONE slot, like
        // episodes collapse into one show card. VersionGroupId is the identity; the
        // (library, normalized title, year) key remains as fallback for rows the boot
        // backfill hasn't grouped yet.
        var seenMovies = new HashSet<string>();
        var resolveBudget = SeriesResolveBudget;

        for (var offset = 0; entries.Count < limit && offset < MaxCandidateScan; offset += CandidatePageSize)
        {
            // Candidates, newest-played first. The qualifying rule differs by type ON PURPOSE:
            //   - Movies: started and NOT explicitly watched. A finished movie leaves the row.
            //   - Episodes: started OR explicitly watched. A just-FINISHED episode (auto-marked at
            //     the credits) must still surface its SERIES so the row can offer the next episode —
            //     otherwise finishing an episode would wrongly behave like finishing the whole show.
            //     Whether the series is actually done is decided by the next-episode resolver below.
            // The "almost finished" 95%/credits rule is applied in-code afterwards — it can't be a
            // SQL predicate because it compares per-row CreditsStart against per-row Duration.
            // MediaItemId is a deterministic tiebreaker so Skip/Take paging is stable.
            var page = await (
                from i in _db.UserMediaInteractions.AsNoTracking()
                where i.UserId == userId && i.LastPlayed != null
                join m in visibleItems on i.MediaItemId equals m.Id
                where (m.Type == MediaType.Movie
                           && !i.IsWatched && i.PlaybackPosition != null && i.PlaybackPosition > 0)
                      || (m.Type == MediaType.Episode
                           && (i.IsWatched || (i.PlaybackPosition != null && i.PlaybackPosition > 0)))
                orderby i.LastPlayed descending, i.MediaItemId
                select new CandidateRow
                {
                    ItemId = m.Id,
                    Type = m.Type,
                    Duration = m.Duration,
                    CreditsStart = m.CreditsStart,
                    SeriesId = m.SeriesId,
                    LibraryId = m.LibraryId,
                    Title = m.Title,
                    Year = m.Year,
                    VersionGroupId = m.VersionGroupId,
                    Position = i.PlaybackPosition ?? 0,
                    IsWatched = i.IsWatched,
                })
                .Skip(offset)
                .Take(CandidatePageSize)
                .ToListAsync();

            if (page.Count == 0) break;

            foreach (var c in page)
            {
                if (entries.Count >= limit) break;

                if (c.Type == MediaType.Episode && c.SeriesId.HasValue)
                {
                    var seriesId = c.SeriesId.Value;
                    // First (newest-played) episode of a series fixes the show's place in the row;
                    // later episodes of the same show are ignored so episodes are never listed
                    // individually.
                    if (!seenSeries.Add(seriesId)) continue;

                    if (resolveBudget <= 0) continue;
                    resolveBudget--;

                    // Reuse the canonical "where should the user resume this series" resolver: it
                    // resumes an in-progress episode, else offers the first UNFINISHED episode
                    // (skipping watched ones, wrapping across seasons), and reports
                    // IsSeriesComplete ONLY when every episode is finished — so a finished
                    // EPISODE keeps the show in the row while a finished SERIES removes it.
                    var next = await _recommendations.GetNextEpisodeAsync(userId, seriesId);
                    if (next == null || next.IsSeriesComplete || next.EpisodeId == Guid.Empty) continue;

                    entries.Add(new Entry
                    {
                        CardId = seriesId,            // the SHOW is the card the user sees
                        ResumeId = next.EpisodeId,    // progress % is measured against this episode
                        Position = next.ResumePosition,
                    });
                }
                else
                {
                    // DV-WI-004/016: the NEWEST-played copy decides for the whole duplicate
                    // group — claim the identity BEFORE the completion check, so a finished
                    // newer copy also suppresses an older copy's stale half-progress (finishing
                    // the 4K file must not resurrect the movie via the abandoned 1080p file).
                    if (c.Type == MediaType.Movie && !seenMovies.Add(
                            c.VersionGroupId?.ToString()
                            ?? $"{c.LibraryId}|{NormalizeTitleKey(c.Title)}|{c.Year}"))
                        continue;

                    // Movie, or an orphan episode with no parent series — represented by the item
                    // itself. The interaction's real IsWatched flag matters here: the candidate
                    // query admits watched EPISODES (for series resolution above), so a watched
                    // orphan episode must be dropped by the explicit-flag rule.
                    if (MediaCompletionHelper.IsComplete(c.Position, c.Duration, c.CreditsStart, c.IsWatched))
                        continue;

                    entries.Add(new Entry
                    {
                        CardId = c.ItemId,
                        ResumeId = c.ItemId,
                        Position = c.Position,
                    });
                }
            }

            if (page.Count < CandidatePageSize) break; // final page — nothing further to scan
        }

        if (entries.Count == 0) return new List<MediaItemDto>();

        // Load the card items (movies + series). Candidates were already gated at the join, but a
        // series CARD is a different row than its episodes — re-apply the filters here as
        // defense-in-depth for the rare case where the show item itself is blocked (e.g. a rating
        // on the series row) while its episodes pass. Such a card is dropped without backfill,
        // which can shorten the row by at most those mismatched entries.
        var cardIds = entries.Select(e => e.CardId).ToList();
        var cards = (await _db.MediaItems
            .AsNoTracking()
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m => cardIds.Contains(m.Id))
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .ToListAsync())
            .ToDictionary(m => m.Id);

        // Progress % is measured against the RESUME item's runtime (for a series that's the resume
        // episode, which can differ from the show card). Batch-load those durations.
        var resumeIds = entries.Select(e => e.ResumeId).Distinct().ToList();
        var resumeDurations = await _db.MediaItems
            .AsNoTracking()
            .ExcludeMissing()
            .Where(m => resumeIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Duration })
            .ToDictionaryAsync(x => x.Id, x => x.Duration);

        var result = new List<MediaItemDto>(entries.Count);
        foreach (var e in entries)
        {
            if (!cards.TryGetValue(e.CardId, out var card)) continue; // stripped by ACL / rating

            var dto = MediaItemDto.FromMediaItem(card, Constants.MediaConstants.Routes.ImageProxy);
            // No UserMediaInteraction is passed to FromMediaItem, so Watched/IsFavorite/
            // IsWatchlisted/PersonalRating stay at their defaults — consistent with the other
            // home-page row surfaces. Resume position/progress are what this row adds:
            dto.PlaybackPosition = e.Position;
            var duration = resumeDurations.TryGetValue(e.ResumeId, out var d) ? d : 0;
            dto.Progress = duration > 0 ? Math.Clamp(e.Position / duration * 100.0, 0, 100) : (double?)null;

            result.Add(dto);
        }

        return result;
    }

    private sealed class CandidateRow
    {
        public Guid ItemId { get; set; }
        public MediaType Type { get; set; }
        public double Duration { get; set; }
        public double? CreditsStart { get; set; }
        public Guid? SeriesId { get; set; }
        public Guid LibraryId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int? Year { get; set; }
        public Guid? VersionGroupId { get; set; }
        public double Position { get; set; }
        public bool IsWatched { get; set; }
    }

    /// <summary>
    /// DV-WI-004 interim duplicate-movie identity, shared with the version-group
    /// assigner so the two rules cannot drift. Superseded by VersionGroupId reads in
    /// Layer 2 (plan DV-WI-016).
    /// </summary>
    private static string NormalizeTitleKey(string title)
        => VersionGroupHelper.NormalizeTitleKey(title);

    private sealed class Entry
    {
        public Guid CardId { get; set; }
        public Guid ResumeId { get; set; }
        public double Position { get; set; }
    }
}
