using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using System.Text.Json;

namespace SoftMedia.Server.Services.Media;

public class RecommendationService : IRecommendationService
{
    private readonly IMediaRepository _mediaRepository;
    private readonly IUserMediaInteractionRepository _interactionRepository;
    private readonly AppDbContext _context;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;
    private readonly IUserContentRatingProvider _contentRatingProvider;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        IMediaRepository mediaRepository,
        IUserMediaInteractionRepository interactionRepository,
        AppDbContext context,
        IUserLibraryAccessProvider libraryAccessProvider,
        IUserContentRatingProvider contentRatingProvider,
        ILogger<RecommendationService> logger)
    {
        _mediaRepository = mediaRepository;
        _interactionRepository = interactionRepository;
        _context = context;
        _libraryAccessProvider = libraryAccessProvider;
        _contentRatingProvider = contentRatingProvider;
        _logger = logger;
    }

    public async Task<NextEpisodeResponse?> GetNextEpisodeAsync(Guid userId, Guid seriesId)
    {
        // Debug, not Information: the Continue Watching row calls this for every candidate series
        // on each home-page load — at Information the shipped config would record every user's
        // actively-watched series in the server log on every visit.
        _logger.LogDebug("[SmartContinue] GetNextEpisode called for series {SeriesId}", seriesId);

        // Get all episodes for this series ordered by season/episode
        var episodes = (await _mediaRepository.GetEpisodesAsync(seriesId)).ToList();

        _logger.LogDebug("[SmartContinue] Found {Count} episodes for series {SeriesId}", episodes.Count, seriesId);

        if (episodes.Count == 0)
        {
            return null;
        }

        // Get user interactions for all episodes
        var episodeIds = episodes.Select(e => e.Id).ToList();
        var interactionList = (await _interactionRepository.GetManyAsync(userId, episodeIds)).ToList();

        _logger.LogDebug("[SmartContinue] Found {Count} user interactions", interactionList.Count);

        // Find the most recently watched episode (by LastPlayed)
        var lastWatched = interactionList
            .Where(i => i.LastPlayed.HasValue)
            .OrderByDescending(i => i.LastPlayed)
            .FirstOrDefault();

        if (lastWatched != null)
        {
            var episode = episodes.First(e => e.Id == lastWatched.MediaItemId);
            
            // Check if this episode is complete
            bool isComplete = IsEpisodeComplete(episode, lastWatched);

            if (!isComplete)
            {
                // Resume this incomplete episode
                return new NextEpisodeResponse
                {
                    EpisodeId = episode.Id,
                    SeriesId = seriesId,
                    SeasonNumber = episode.SeasonNumber ?? 1,
                    EpisodeNumber = episode.EpisodeNumber ?? 1,
                    Title = episode.Title,
                    ResumePosition = lastWatched.PlaybackPosition ?? 0,
                    DebugDuration = episode.Duration,
                    DebugThreshold = episode.Duration * 0.95,
                    DebugIsComplete = isComplete
                };
            }
            else
            {
                // The most recently played episode is finished. Offer the first UNFINISHED
                // episode: scan forward from it in season/episode order, then wrap to the start.
                // The wrap matters — a finished EPISODE must never read as a finished SERIES:
                //   - rewatching a mid-series episode of a completed show must not resurrect the
                //     show with an already-watched "next" episode (forward scan skips finished);
                //   - watching only the finale of an otherwise-unwatched show must not mark the
                //     whole series complete (wrap finds the earliest unfinished episode).
                // IsSeriesComplete is reported ONLY when every episode is finished, which is what
                // the Continue Watching row relies on to drop a series.
                var currentIndex = episodes.FindIndex(e => e.Id == episode.Id);
                for (var step = 1; step < episodes.Count; step++)
                {
                    var candidate = episodes[(currentIndex + step) % episodes.Count];
                    var candidateInteraction = interactionList.FirstOrDefault(i => i.MediaItemId == candidate.Id);
                    if (candidateInteraction != null && IsEpisodeComplete(candidate, candidateInteraction))
                    {
                        continue;
                    }
                    return new NextEpisodeResponse
                    {
                        EpisodeId = candidate.Id,
                        SeriesId = seriesId,
                        SeasonNumber = candidate.SeasonNumber ?? 1,
                        EpisodeNumber = candidate.EpisodeNumber ?? 1,
                        Title = candidate.Title,
                        ResumePosition = candidateInteraction?.PlaybackPosition ?? 0
                    };
                }

                // Every episode is finished — the series is genuinely complete. Return the
                // first episode as the rewatch entry point (existing UI contract).
                var firstEp = episodes[0];
                return new NextEpisodeResponse
                {
                    EpisodeId = firstEp.Id,
                    SeriesId = seriesId,
                    SeasonNumber = firstEp.SeasonNumber ?? 1,
                    EpisodeNumber = firstEp.EpisodeNumber ?? 1,
                    Title = firstEp.Title,
                    ResumePosition = 0,
                    IsSeriesComplete = true
                };
            }
        }
        else
        {
            // No watch history - start from first episode
            var firstEp = episodes[0];
            var firstInteraction = interactionList.FirstOrDefault(i => i.MediaItemId == firstEp.Id);
            return new NextEpisodeResponse
            {
                EpisodeId = firstEp.Id,
                SeriesId = seriesId,
                SeasonNumber = firstEp.SeasonNumber ?? 1,
                EpisodeNumber = firstEp.EpisodeNumber ?? 1,
                Title = firstEp.Title,
                ResumePosition = firstInteraction?.PlaybackPosition ?? 0
            };
        }
    }

    public async Task<NextEpisodeResponse?> GetNextEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId)
    {
        _logger.LogInformation("[PlayNext] GetNextEpisodeFromCurrent called for episode {EpisodeId}", currentEpisodeId);

        // Get the current episode
        var currentEpisode = await _mediaRepository.GetByIdAsync(currentEpisodeId);

        if (currentEpisode == null || currentEpisode.SeriesId == null || currentEpisode.Type != MediaType.Episode)
        {
            return null;
        }

        // Get all episodes for this series ordered by season/episode
        var episodes = (await _mediaRepository.GetEpisodesAsync(currentEpisode.SeriesId.Value)).ToList();

        // Find current episode index and get next
        var currentIndex = episodes.FindIndex(e => e.Id == currentEpisodeId);
        if (currentIndex < 0 || currentIndex >= episodes.Count - 1)
        {
            // No next episode - at end of series
            return new NextEpisodeResponse
            {
                EpisodeId = Guid.Empty,
                SeriesId = currentEpisode.SeriesId.Value,
                IsSeriesComplete = true
            };
        }

        var nextEpisode = episodes[currentIndex + 1];
        
        // Get user interaction for resume position
        var interaction = await _interactionRepository.GetAsync(userId, nextEpisode.Id);

        // Extract poster/backdrop
        var (posterPath, backdropPath) = await ExtractImagesAsync(nextEpisode, currentEpisode.SeriesId);

        _logger.LogInformation("[PlayNext] Next episode: S{Season}E{Episode} - {Title}, PosterPath: {Poster}",
            nextEpisode.SeasonNumber, nextEpisode.EpisodeNumber, nextEpisode.Title, posterPath ?? "none");

        return new NextEpisodeResponse
        {
            EpisodeId = nextEpisode.Id,
            SeriesId = currentEpisode.SeriesId.Value,
            SeasonNumber = nextEpisode.SeasonNumber ?? 1,
            EpisodeNumber = nextEpisode.EpisodeNumber ?? 1,
            Title = nextEpisode.Title,
            ResumePosition = interaction?.PlaybackPosition ?? 0,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            IsSeriesComplete = false
        };
    }

    public async Task<NextEpisodeResponse?> GetPreviousEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId)
    {
        _logger.LogInformation("[EpisodeNav] GetPreviousEpisodeFromCurrent called for episode {EpisodeId}", currentEpisodeId);

        // Get the current episode
        var currentEpisode = await _mediaRepository.GetByIdAsync(currentEpisodeId);

        if (currentEpisode == null || currentEpisode.SeriesId == null || currentEpisode.Type != MediaType.Episode)
        {
            return null;
        }

        // Get all episodes for this series ordered by season/episode
        var episodes = (await _mediaRepository.GetEpisodesAsync(currentEpisode.SeriesId.Value)).ToList();

        // Find current episode index and get previous
        var currentIndex = episodes.FindIndex(e => e.Id == currentEpisodeId);
        if (currentIndex <= 0)
        {
            // No previous episode - at start of series
            return new NextEpisodeResponse
            {
                EpisodeId = Guid.Empty,
                SeriesId = currentEpisode.SeriesId.Value,
                IsSeriesComplete = false 
            };
        }

        var previousEpisode = episodes[currentIndex - 1];
        
        // Get user interaction for resume position
        var interaction = await _interactionRepository.GetAsync(userId, previousEpisode.Id);

        // Extract poster/backdrop
        var (posterPath, backdropPath) = await ExtractImagesAsync(previousEpisode, currentEpisode.SeriesId);

        _logger.LogInformation("[EpisodeNav] Previous episode: S{Season}E{Episode} - {Title}",
            previousEpisode.SeasonNumber, previousEpisode.EpisodeNumber, previousEpisode.Title);

        return new NextEpisodeResponse
        {
            EpisodeId = previousEpisode.Id,
            SeriesId = currentEpisode.SeriesId.Value,
            SeasonNumber = previousEpisode.SeasonNumber ?? 1,
            EpisodeNumber = previousEpisode.EpisodeNumber ?? 1,
            Title = previousEpisode.Title,
            ResumePosition = interaction?.PlaybackPosition ?? 0,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            IsSeriesComplete = false
        };
    }

    // Completion logic lives in MediaCompletionHelper so the next-episode resolver and the
    // Continue Watching row share one definition of "finished" (IsWatched, else credits timecode,
    // else 95% of runtime). See Helpers/MediaCompletionHelper.cs.
    private static bool IsEpisodeComplete(MediaItem episode, UserMediaInteraction interaction)
        => MediaCompletionHelper.IsComplete(
            interaction.PlaybackPosition, episode.Duration, episode.CreditsStart, interaction.IsWatched);

    private async Task<(string? Poster, string? Backdrop)> ExtractImagesAsync(MediaItem episode, Guid? seriesId)
    {
        // Use promoted columns for image URLs
        string? posterPath = episode.PosterUrl;
        string? backdropPath = episode.BackdropUrl;
        
        // Fallback to series poster/backdrop if no episode-specific image found
        if ((string.IsNullOrEmpty(posterPath) || string.IsNullOrEmpty(backdropPath)) && seriesId.HasValue)
        {
            var series = await _mediaRepository.GetByIdAsync(seriesId.Value);
            if (series != null)
            {
                if (string.IsNullOrEmpty(posterPath))
                    posterPath = series.PosterUrl;
                if (string.IsNullOrEmpty(backdropPath))
                    backdropPath = series.BackdropUrl;
            }
        }

        return (posterPath, backdropPath);
    }

    public async Task UpdateHeroCacheAsync()
    {
        _logger.LogInformation("Updating Hero Section Cache...");
        var heroItems = new List<MediaItem>();

        // 1. Get Top 2 Highest Externally Rated (CommunityRating)
        // Valid types: Movie, Series.
        var top2 = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.CommunityRating.HasValue && (m.Type == MediaType.Movie || m.Type == MediaType.Series))
            .OrderByDescending(m => m.CommunityRating)
            .Take(2)
            .ToListAsync();

        heroItems.AddRange(top2);
        var excludedIds = top2.Select(x => x.Id).ToHashSet();

        // 2. Get 10 Random items from other types
        var types = new[] { MediaType.Movie, MediaType.Series, MediaType.Album, MediaType.Book, MediaType.Game };
        
        var candidatesPerType = new Dictionary<MediaType, List<MediaItem>>();
        foreach (var type in types)
        {
            var sample = await _context.MediaItems
                .AsNoTracking()
                .Where(m => m.Type == type && !excludedIds.Contains(m.Id))
                .OrderBy(x => EF.Functions.Random()) 
                .Take(10) 
                .ToListAsync();
            
            candidatesPerType[type] = sample;
        }

        while (heroItems.Count < 12)
        {
            bool addedAny = false;
            foreach (var type in types)
            {
                if (heroItems.Count >= 12) break;
                if (candidatesPerType[type].Count > 0)
                {
                    var item = candidatesPerType[type][0];
                    candidatesPerType[type].RemoveAt(0);
                    heroItems.Add(item);
                    addedAny = true;
                }
            }
            if (!addedAny) break; 
        }

        // Shuffle the final list so top-rated items don't always appear first
        var random = new Random();
        heroItems = heroItems.OrderBy(x => random.Next()).ToList();

        // Convert to DTOs
        var dtos = heroItems.Select(m => MediaItemDto.FromMediaItem(m, SoftMedia.Server.Constants.MediaConstants.Routes.ImageProxy)).ToList();
        var json = JsonSerializer.Serialize(dtos);

        // Save to Cache
        var cache = await _context.HeroCaches.FindAsync(1);
        if (cache == null)
        {
            cache = new HeroCache { Id = 1, CachedJson = json, LastUpdated = DateTime.UtcNow };
            _context.HeroCaches.Add(cache);
        }
        else
        {
            cache.CachedJson = json;
            cache.LastUpdated = DateTime.UtcNow;
            _context.Entry(cache).State = EntityState.Modified;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Hero Section Cache updated with {Count} items.", heroItems.Count);
    }

    public async Task<PostPlayResponse?> GetMoviePostPlayAsync(Guid userId, Guid movieId, int limit = 8)
    {
        limit = Math.Clamp(limit, 1, 24);

        // Same visibility gates as the Continue Watching row: candidates are filtered at the
        // query so a blocked item can never appear, and the SOURCE movie itself must be visible
        // to the caller — an ACL/rating-blocked id answers exactly like a nonexistent one
        // (anti-probe, matching CollectionsController.GetByMovie).
        var access = await _libraryAccessProvider.GetCurrentAsync();
        var ceilings = await _contentRatingProvider.GetCurrentAsync();
        var visible = _context.MediaItems.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings);

        var source = await visible
            .Where(m => m.Id == movieId && m.Type == MediaType.Movie)
            .Select(m => new { m.CollectionId, m.ReleaseDate, m.Year })
            .FirstOrDefaultAsync();
        if (source == null) return null;

        var response = new PostPlayResponse();

        // 1) Collection siblings — the marathon path. Unfinished films from the same collection,
        //    release order, rotated so the first film released AFTER the finished one leads
        //    (finish Fellowship -> Two Towers first; earlier unwatched films trail).
        if (source.CollectionId != null)
        {
            var collectionName = await _context.Collections.AsNoTracking()
                .Where(c => c.Id == source.CollectionId.Value)
                .Select(c => c.Name)
                .FirstOrDefaultAsync();

            var siblings = await visible
                .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
                .Where(m => m.CollectionId == source.CollectionId.Value
                            && m.Type == MediaType.Movie && m.Id != movieId)
                .OrderBy(m => m.ReleaseDate).ThenBy(m => m.Year).ThenBy(m => m.Title)
                .ToListAsync();

            var unfinished = await DropFinishedAsync(userId, siblings);

            var sourceDate = source.ReleaseDate
                ?? (source.Year is int y ? new DateTime(y, 1, 1) : (DateTime?)null);
            if (sourceDate != null)
            {
                var later = unfinished.Where(m => YearDate(m) > sourceDate).ToList();
                var earlier = unfinished.Except(later).ToList();
                unfinished = later.Concat(earlier).ToList();
            }

            if (unfinished.Count > 0)
            {
                response.CollectionName = collectionName;
                response.CollectionItems = unfinished
                    .Take(limit)
                    .Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy))
                    .ToList();
            }
        }

        // 2) Genre-similar movies fill the remaining slots: most shared genres first, then
        //    community rating; collection members are excluded (they're already section 1).
        var remaining = limit - response.CollectionItems.Count;
        if (remaining > 0)
        {
            var genreIds = await _context.MediaItemGenres.AsNoTracking()
                .Where(g => g.MediaItemId == movieId)
                .Select(g => g.GenreId)
                .ToListAsync();

            if (genreIds.Count > 0)
            {
                var ranked = await visible
                    .Where(m => m.Type == MediaType.Movie && m.Id != movieId
                                && (source.CollectionId == null || m.CollectionId != source.CollectionId)
                                && m.MediaItemGenres.Any(g => genreIds.Contains(g.GenreId)))
                    .Select(m => new
                    {
                        m.Id,
                        Shared = m.MediaItemGenres.Count(g => genreIds.Contains(g.GenreId)),
                        m.CommunityRating,
                    })
                    .OrderByDescending(x => x.Shared)
                    .ThenByDescending(x => x.CommunityRating)
                    .ThenBy(x => x.Id) // deterministic tiebreak
                    .Take(remaining * 3) // headroom — finished ones are dropped below
                    .ToListAsync();

                var rankedIds = ranked.Select(r => r.Id).ToList();
                var items = (await _context.MediaItems.AsNoTracking()
                        .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
                        .Where(m => rankedIds.Contains(m.Id))
                        .ToListAsync())
                    .ToDictionary(m => m.Id);

                var ordered = rankedIds
                    .Where(items.ContainsKey)
                    .Select(id => items[id])
                    .ToList();

                response.SimilarItems = (await DropFinishedAsync(userId, ordered))
                    .Take(remaining)
                    .Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy))
                    .ToList();
            }
        }

        return response;
    }

    /// <summary>
    /// R-WI-020 — personalized home rows. Ship-simple genre/collection affinity per
    /// the spec: the play history (R-WI-013; episodes roll up to their series) seeds
    /// the taste signal, candidate queries carry the same ACL + rating gates as the
    /// Continue Watching row, watched/seed items are excluded, and thin rows
    /// (&lt;4 items) self-suppress. Rows never repeat an item.
    /// </summary>
    public async Task<IReadOnlyList<HomeRowDto>> GetHomeRowsAsync(Guid userId, int itemsPerRow = 15)
    {
        itemsPerRow = Math.Clamp(itemsPerRow, 4, 30);
        const int MinRowItems = 4;

        var access = await _libraryAccessProvider.GetCurrentAsync();
        var ceilings = await _contentRatingProvider.GetCurrentAsync();
        var visible = _context.MediaItems.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings);

        // Taste signal: recent VIDEO plays, newest first; episodes roll up to their
        // series. Music history is excluded (review MED: a heavy listener's window
        // filled with tracks — emptying the rows or steering them with music genres
        // while candidates are movies/series only).
        var history = await _context.PlaybackHistory.AsNoTracking()
            .Where(h => h.UserId == userId
                        && (h.MediaType == MediaType.Movie || h.MediaType == MediaType.Episode))
            .OrderByDescending(h => h.LastBeatAt)
            .Take(60)
            .Select(h => new
            {
                h.MediaItemId,
                h.MediaType,
                SeriesId = h.MediaItem != null ? h.MediaItem.SeriesId : (Guid?)null,
            })
            .ToListAsync();
        // NOTE: no early-return on empty history — "Your most watched" below counts
        // the FULL history independently of this recent-window taste signal, and the
        // visibleSeedIds guard already ends the genre rows when there are no seeds.

        var seedIds = history
            .Select(h => h.MediaType == MediaType.Episode && h.SeriesId != null ? h.SeriesId.Value : h.MediaItemId)
            .Distinct()
            .ToList();

        var rows = new List<HomeRowDto>();
        var used = new HashSet<Guid>(seedIds);

        // Row 0 — "Your most watched": FULL-history play counts (not the recent-60
        // taste window), episodes rolled up to their series so a binged show is one
        // card rather than twenty. Watched items belong here by definition, so unlike
        // the recommendation rows there is no watched-exclusion — but the ACL +
        // rating-ceiling gate applies unchanged, and the row self-suppresses below
        // MinRowItems (a "most watched" of one or two titles is noise, and users who
        // disabled history recording simply have no rows to count).
        var playCounts = await _context.PlaybackHistory.AsNoTracking()
            .Where(h => h.UserId == userId
                        && (h.MediaType == MediaType.Movie || h.MediaType == MediaType.Episode))
            .Select(h => new
            {
                // LEFT-JOIN semantics: a deleted item's SeriesId is null → falls back
                // to the (dangling) media id, which the visibility filter then drops.
                RolledId = (Guid?)h.MediaItem!.SeriesId ?? h.MediaItemId,
                h.LastBeatAt,
            })
            .GroupBy(x => x.RolledId)
            .Select(g => new { Id = g.Key, Plays = g.Count(), Last = g.Max(x => x.LastBeatAt) })
            .OrderByDescending(x => x.Plays)
            .ThenByDescending(x => x.Last)
            .Take(itemsPerRow * 2) // headroom — visibility filtering thins the list
            .ToListAsync();
        if (playCounts.Count >= MinRowItems)
        {
            var mostWatchedIds = playCounts.Select(p => p.Id).ToList();
            var mostWatchedItems = await visible
                .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
                .Where(m => mostWatchedIds.Contains(m.Id)
                            && (m.Type == MediaType.Movie || m.Type == MediaType.Series))
                .ToListAsync();
            var byId = mostWatchedItems.ToDictionary(m => m.Id);
            var ordered = playCounts
                .Where(p => byId.ContainsKey(p.Id))
                .Select(p => byId[p.Id])
                .Take(itemsPerRow)
                .ToList();
            if (ordered.Count >= MinRowItems)
            {
                foreach (var m in ordered) used.Add(m.Id); // rows never repeat an item
                rows.Add(new HomeRowDto
                {
                    Title = "Your most watched",
                    Items = ordered.Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy)).ToList(),
                });
            }
        }

        // Only VISIBLE seeds may steer the rows: a lowered ceiling must not keep
        // pulling recommendations (or row headings) toward content the caller can
        // no longer see (review).
        var visibleSeedIds = await visible
            .Where(m => seedIds.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync();
        if (visibleSeedIds.Count == 0) return rows;

        // Genre affinity: distinct seed count per genre across the history window
        // (a 40-episode binge counts its series' genres once — breadth over volume).
        var seedGenres = await _context.MediaItemGenres.AsNoTracking()
            .Where(mg => visibleSeedIds.Contains(mg.MediaItemId) && mg.Genre != null)
            .Select(mg => new { mg.MediaItemId, mg.GenreId, GenreName = mg.Genre!.Name })
            .ToListAsync();
        if (seedGenres.Count == 0) return rows;

        var affinity = seedGenres
            .GroupBy(g => new { g.GenreId, g.GenreName })
            .Select(g => new { g.Key.GenreId, g.Key.GenreName, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.GenreName)
            .Take(3)
            .ToList();

        // Shared candidate shape: visible, browsable top-level types, not a seed, not
        // finished (EXISTS subquery — the watched set can be large), not already used.
        async Task<List<MediaItem>> CandidatesAsync(List<int> genreIds, int take)
        {
            var pool = await visible
                .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
                .Where(m => (m.Type == MediaType.Movie || m.Type == MediaType.Series)
                            && !seedIds.Contains(m.Id)
                            && m.MediaItemGenres.Any(mg => genreIds.Contains(mg.GenreId))
                            && !_context.UserMediaInteractions.Any(i =>
                                i.UserId == userId && i.MediaItemId == m.Id
                                && (i.IsWatched
                                    || (m.Duration > 0 && i.PlaybackPosition >= m.Duration * 0.95))))
                .Select(m => new
                {
                    Item = m,
                    Shared = m.MediaItemGenres.Count(mg => genreIds.Contains(mg.GenreId)),
                })
                .OrderByDescending(x => x.Shared)
                .ThenByDescending(x => x.Item.CommunityRating)
                .ThenByDescending(x => x.Item.DateAdded)
                .ThenBy(x => x.Item.Id) // deterministic tiebreak
                .Take(take * 3) // headroom — the used-filter below thins the pool
                .ToListAsync();

            return pool.Select(p => p.Item).Where(m => !used.Contains(m.Id)).Take(take).ToList();
        }

        // Row 1 — "Because you watched {most recent VISIBLE seed that has genres}"
        // (falls back to the next visible seed instead of dropping the row when the
        // latest one is ceiling-hidden).
        var recentSeedId = history
            .Select(h => h.MediaType == MediaType.Episode && h.SeriesId != null ? h.SeriesId.Value : h.MediaItemId)
            .FirstOrDefault(id => seedGenres.Any(g => g.MediaItemId == id));
        if (recentSeedId != Guid.Empty)
        {
            var seedTitle = await visible
                .Where(m => m.Id == recentSeedId)
                .Select(m => m.Title)
                .FirstOrDefaultAsync();
            if (seedTitle != null)
            {
                var seedGenreIds = seedGenres.Where(g => g.MediaItemId == recentSeedId).Select(g => g.GenreId).ToList();
                var items = await CandidatesAsync(seedGenreIds, itemsPerRow);
                if (items.Count >= MinRowItems)
                {
                    foreach (var m in items) used.Add(m.Id);
                    rows.Add(new HomeRowDto
                    {
                        Title = $"Because you watched {seedTitle}",
                        Items = items.Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy)).ToList(),
                    });
                }
            }
        }

        // Row 2 — "Top picks for you": the caller's top affinity genres together.
        var topGenreIds = affinity.Select(a => a.GenreId).ToList();
        var topPicks = await CandidatesAsync(topGenreIds, itemsPerRow);
        if (topPicks.Count >= MinRowItems)
        {
            foreach (var m in topPicks) used.Add(m.Id);
            rows.Add(new HomeRowDto
            {
                Title = "Top picks for you",
                Items = topPicks.Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy)).ToList(),
            });
        }

        // Row 3 — "More {top genre}" (skipped when rows 1/2 drained the pool).
        var topGenre = affinity.FirstOrDefault();
        if (topGenre != null)
        {
            var more = await CandidatesAsync(new List<int> { topGenre.GenreId }, itemsPerRow);
            if (more.Count >= MinRowItems)
            {
                foreach (var m in more) used.Add(m.Id);
                rows.Add(new HomeRowDto
                {
                    Title = $"More {topGenre.GenreName}",
                    Items = more.Select(m => MediaItemDto.FromMediaItem(m, Constants.MediaConstants.Routes.ImageProxy)).ToList(),
                });
            }
        }

        return rows;
    }

    /// <summary>
    /// Removes movies the user has already finished, by the same rule the Continue Watching row
    /// uses (explicit watched flag > credits timecode > 95%). In-progress movies stay — resuming
    /// one is a perfectly good post-play choice.
    /// </summary>
    private async Task<List<MediaItem>> DropFinishedAsync(Guid userId, List<MediaItem> movies)
    {
        if (movies.Count == 0) return movies;

        var interactions = (await _interactionRepository.GetManyAsync(userId, movies.Select(m => m.Id).ToList()))
            .ToDictionary(i => i.MediaItemId);

        return movies.Where(m =>
                !interactions.TryGetValue(m.Id, out var i)
                || !MediaCompletionHelper.IsComplete(i.PlaybackPosition ?? 0, m.Duration, m.CreditsStart, i.IsWatched))
            .ToList();
    }

    private static DateTime? YearDate(MediaItem m)
        => m.ReleaseDate ?? (m.Year is int y ? new DateTime(y, 1, 1) : (DateTime?)null);

    public async Task<IEnumerable<MediaItemDto>> GetHeroItemsAsync()
    {
        var cache = await _context.HeroCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1);
        List<MediaItemDto>? items = null;

        if (cache != null)
        {
            try
            {
                items = JsonSerializer.Deserialize<List<MediaItemDto>>(cache.CachedJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize Hero cache.");
            }
        }

        if (items == null)
        {
            // Fallback: Trigger update and return it (or wait)
            await UpdateHeroCacheAsync();

            // Re-fetch
            cache = await _context.HeroCaches.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1);
            items = cache != null
                ? JsonSerializer.Deserialize<List<MediaItemDto>>(cache.CachedJson) ?? new List<MediaItemDto>()
                : new List<MediaItemDto>();
        }

        // Wave C — apply per-user ACL at READ time, not at cache-build time.
        // The hero cache is server-wide (one row, every user reads from it),
        // so filtering is done per-request after deserialization.
        var access = await _libraryAccessProvider.GetCurrentAsync();
        if (!access.IsUnrestricted)
        {
            var allowed = access.AllowedLibraryIds;
            items = items.Where(i => allowed.Contains(i.LibraryId)).ToList();
        }

        // Re-hydrate live average ratings from DB — and apply the CONTENT-RATING
        // ceiling in the same round trip (backlog B-19: the shared cache is built
        // unfiltered and only the ACL was applied at read time, so a ceiling-
        // restricted user got over-ceiling titles in the hero rotation, unlike
        // every browse path). Items filtered out by the ceiling drop from the
        // dictionary and are removed below.
        if (items.Any())
        {
            var ceilings = await _contentRatingProvider.GetCurrentAsync();
            var itemIds = items.Select(i => i.Id).ToList();
            var liveRatings = await _context.MediaItems
                .AsNoTracking()
                .ApplyContentRatingFilter(ceilings)
                .Where(m => itemIds.Contains(m.Id))
                .Select(m => new { m.Id, m.InternalRating })
                .ToDictionaryAsync(x => x.Id, x => x.InternalRating);

            items = items.Where(i => liveRatings.ContainsKey(i.Id)).ToList();
            foreach (var item in items)
            {
                if (liveRatings.TryGetValue(item.Id, out var rating))
                {
                    item.UserRating = rating;
                }
            }
        }

        return items;
    }
}
