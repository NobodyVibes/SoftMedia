using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Security.LibraryAccess;
using System.Text.Json;

namespace SoftMedia.Server.Services.Media;

public class RecommendationService : IRecommendationService
{
    private readonly IMediaRepository _mediaRepository;
    private readonly IUserMediaInteractionRepository _interactionRepository;
    private readonly AppDbContext _context;
    private readonly IUserLibraryAccessProvider _libraryAccessProvider;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        IMediaRepository mediaRepository,
        IUserMediaInteractionRepository interactionRepository,
        AppDbContext context,
        IUserLibraryAccessProvider libraryAccessProvider,
        ILogger<RecommendationService> logger)
    {
        _mediaRepository = mediaRepository;
        _interactionRepository = interactionRepository;
        _context = context;
        _libraryAccessProvider = libraryAccessProvider;
        _logger = logger;
    }

    public async Task<NextEpisodeResponse?> GetNextEpisodeAsync(Guid userId, Guid seriesId)
    {
        _logger.LogInformation("[SmartContinue] GetNextEpisode called for series {SeriesId}", seriesId);

        // Get all episodes for this series ordered by season/episode
        var episodes = (await _mediaRepository.GetEpisodesAsync(seriesId)).ToList();

        _logger.LogInformation("[SmartContinue] Found {Count} episodes for series {SeriesId}", episodes.Count, seriesId);

        if (episodes.Count == 0)
        {
            return null;
        }

        // Get user interactions for all episodes
        var episodeIds = episodes.Select(e => e.Id).ToList();
        var interactionList = (await _interactionRepository.GetManyAsync(userId, episodeIds)).ToList();

        _logger.LogInformation("[SmartContinue] Found {Count} user interactions", interactionList.Count);

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
                // Find the next episode in sequence
                var currentIndex = episodes.FindIndex(e => e.Id == episode.Id);
                if (currentIndex < episodes.Count - 1)
                {
                    var nextEp = episodes[currentIndex + 1];
                    var nextInteraction = interactionList.FirstOrDefault(i => i.MediaItemId == nextEp.Id);
                    return new NextEpisodeResponse
                    {
                        EpisodeId = nextEp.Id,
                        SeriesId = seriesId,
                        SeasonNumber = nextEp.SeasonNumber ?? 1,
                        EpisodeNumber = nextEp.EpisodeNumber ?? 1,
                        Title = nextEp.Title,
                        ResumePosition = nextInteraction?.PlaybackPosition ?? 0
                    };
                }
                else
                {
                    // Series complete - return first episode for rewatch
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

    private bool IsEpisodeComplete(MediaItem episode, UserMediaInteraction interaction)
    {
        if (interaction.IsWatched)
        {
            _logger.LogInformation("[SmartContinue] Episode {Title} marked IsWatched=true", episode.Title);
            return true;
        }
        if (episode.Duration <= 0)
        {
            _logger.LogWarning("[SmartContinue] Episode {Title} has no duration (Duration={Duration})", episode.Title, episode.Duration);
            return false;
        }

        var position = interaction.PlaybackPosition ?? 0;
        var threshold = episode.Duration * 0.95;
        var isComplete = position >= threshold;
        
        // Try to use credits timecode from promoted column
        if (episode.CreditsStart.HasValue && episode.CreditsStart.Value > 0)
        {
            return position >= episode.CreditsStart.Value;
        }

        return isComplete;
    }

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

        // Re-hydrate live average ratings from DB
        if (items.Any())
        {
            var itemIds = items.Select(i => i.Id).ToList();
            var liveRatings = await _context.MediaItems
                .AsNoTracking()
                .Where(m => itemIds.Contains(m.Id))
                .Select(m => new { m.Id, m.InternalRating })
                .ToDictionaryAsync(x => x.Id, x => x.InternalRating);

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
