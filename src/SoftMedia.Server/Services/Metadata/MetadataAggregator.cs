using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataAggregator
{
    Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true);
}

public class MetadataAggregator : IMetadataAggregator
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IMetadataRouter _metadataRouter;
    private readonly ISettingsService _settingsService;
    private readonly IImageUrlExtractorService _imageUrlExtractor;
    private readonly ITvMetadataEnricher _tvMetadataEnricher;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService, 
        IImageUrlExtractorService imageUrlExtractor,
        ITvMetadataEnricher tvMetadataEnricher,
        AppDbContext dbContext,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageUrlExtractor = imageUrlExtractor;
        _tvMetadataEnricher = tvMetadataEnricher;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true)
    {
        try
        {
            // Use MetadataRouter to get metadata from the user's preferred provider
            var result = await _metadataRouter.FetchMetadataAsync(item, type);
            if (result == null) return;

            await ProcessMetadataResultAsync(item, result, deferImageCaching, refreshImages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching media item {Title}", item.Title);
        }
    }

    private async Task ProcessMetadataResultAsync(MediaItem item, MetadataResult metadata, bool deferImageCaching = false, bool refreshImages = true)
    {
        // Populate standard fields
        if (metadata.Year.HasValue) item.Year = metadata.Year.Value;
        if (!string.IsNullOrEmpty(metadata.Description)) item.Overview = metadata.Description;
        if (!string.IsNullOrEmpty(metadata.ContentRating)) item.ContentRating = metadata.ContentRating;
        
        // Promote Ratings
        if (metadata.ImdbRating.HasValue) item.CommunityRating = metadata.ImdbRating.Value;
        else if (metadata.Rating.HasValue) item.CommunityRating = metadata.Rating.Value;
        
        if (metadata.ReleaseDate.HasValue) item.ReleaseDate = metadata.ReleaseDate.Value;

        // Populate External IDs
        if (!string.IsNullOrEmpty(metadata.ImdbId)) item.ImdbId = metadata.ImdbId;
        if (metadata.TvMazeId.HasValue) item.TvMazeId = metadata.TvMazeId.Value;
        if (!string.IsNullOrEmpty(metadata.MusicBrainzId)) item.MusicBrainzId = metadata.MusicBrainzId;

        // Promote queryable fields
        if (!string.IsNullOrEmpty(metadata.Studio)) item.Studio = metadata.Studio;
        if (!string.IsNullOrEmpty(metadata.Director)) item.Director = metadata.Director;

        // Persist normalized genres and cast to relational tables
        if (metadata.Genres != null && metadata.Genres.Count > 0)
            await PersistGenresAsync(item, metadata.Genres);

        if (metadata.Cast != null && metadata.Cast.Count > 0)
            await PersistCastAsync(item, metadata.Cast);

        // Filtering: Only keep metadata for Seasons/Episodes that exist locally
        if (item.Type == MediaType.Series)
        {
            await _tvMetadataEnricher.FilterToLocalEpisodesAsync(item, metadata);
            await _tvMetadataEnricher.PropagateEpisodeMetadataAsync(item, metadata);
        }

        if (deferImageCaching || !refreshImages)
        {
            return;
        }

        // Capture remote URLs to promoted columns BEFORE image extraction strips them.
        // These columns store the original remote URLs as the source of truth.
        if (!string.IsNullOrEmpty(metadata.PosterUrl))
            item.PosterUrl = metadata.PosterUrl;

        // Process images (ExtractAndQueueAsync nulls out remote URLs to prevent hotlinking)
        bool imagesEnqueued = await _imageUrlExtractor.ExtractAndQueueAsync(item, metadata);

        // Re-serialize the MetadataResult (with image URLs now stripped) and MERGE
        // into the existing MetadataJson to preserve scanner-injected keys (e.g. "author"
        // from BookScanner, "hasEmbeddedArt" from IMediaAnalysisService).
        var jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var updatedResultJson = JsonSerializer.Serialize(metadata, jsonOptions);

        try
        {
            item.MetadataJson = MetadataJsonMerger.MergeJson(item.MetadataJson, updatedResultJson);
        }
        catch
        {
            item.MetadataJson = updatedResultJson;
        }
    }

    /// <summary>
    /// Persist genre data to the normalized Genre/MediaItemGenre tables.
    /// Uses batch loading and diff-based association updates to avoid N+1 writes.
    /// </summary>
    private async Task PersistGenresAsync(MediaItem item, List<string> genreNames)
    {
        try
        {
            var trimmedNames = genreNames
                .Select(g => g.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (trimmedNames.Count == 0) return;

            // Batch-load all matching Genre entities in a single query
            var existingGenres = await _dbContext.Genres
                .Where(g => trimmedNames.Contains(g.Name))
                .ToDictionaryAsync(g => g.Name, StringComparer.OrdinalIgnoreCase);

            // Batch-create any missing Genre entities
            var newGenres = new List<Genre>();
            foreach (var name in trimmedNames)
            {
                if (!existingGenres.ContainsKey(name))
                {
                    var genre = new Genre { Name = name };
                    newGenres.Add(genre);
                    existingGenres[name] = genre;
                }
            }

            if (newGenres.Count > 0)
            {
                _dbContext.Genres.AddRange(newGenres);
                await _dbContext.SaveChangesAsync();
            }

            // Diff-based association update: compute delta instead of delete-and-rebuild
            var existingAssociations = await _dbContext.MediaItemGenres
                .Where(mg => mg.MediaItemId == item.Id)
                .ToListAsync();

            var existingGenreIds = existingAssociations.Select(a => a.GenreId).ToHashSet();
            var desiredGenreIds = trimmedNames
                .Select(name => existingGenres[name].Id)
                .ToHashSet();

            // Remove stale associations
            var toRemove = existingAssociations.Where(a => !desiredGenreIds.Contains(a.GenreId)).ToList();
            if (toRemove.Count > 0)
                _dbContext.MediaItemGenres.RemoveRange(toRemove);

            // Add missing associations
            var toAdd = desiredGenreIds.Except(existingGenreIds)
                .Select(genreId => new MediaItemGenre { MediaItemId = item.Id, GenreId = genreId })
                .ToList();
            if (toAdd.Count > 0)
                _dbContext.MediaItemGenres.AddRange(toAdd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist genres for {Title}", item.Title);
        }
    }

    /// <summary>
    /// Persist cast data to the normalized Person/MediaItemCast tables.
    /// Uses batch loading, external ID dedup, and diff-based association updates.
    /// </summary>
    private async Task PersistCastAsync(MediaItem item, List<CastMember> castMembers)
    {
        try
        {
            var validMembers = castMembers
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .ToList();

            if (validMembers.Count == 0) return;

            // Batch-load all potentially matching Person entities
            var memberNames = validMembers.Select(m => m.Name).Distinct().ToList();
            var memberExternalIds = validMembers
                .Where(m => m.Id.HasValue && m.Id.Value > 0)
                .Select(m => m.Id!.Value)
                .Distinct()
                .ToList();

            var personsByExternalId = memberExternalIds.Count > 0
                ? await _dbContext.Persons
                    .Where(p => p.ExternalId.HasValue && memberExternalIds.Contains(p.ExternalId.Value))
                    .ToDictionaryAsync(p => p.ExternalId!.Value)
                : new Dictionary<int, Person>();

            var personsByName = await _dbContext.Persons
                .Where(p => memberNames.Contains(p.Name))
                .ToDictionaryAsync(p => p.Name);

            // Resolve or create Person entities in batch
            var newPersons = new List<Person>();
            var resolvedPersons = new List<(Person Person, CastMember Member, int Order)>();

            for (int i = 0; i < validMembers.Count; i++)
            {
                var member = validMembers[i];
                Person? person = null;

                // Prefer ExternalId dedup, fall back to name
                if (member.Id.HasValue && member.Id.Value > 0)
                    personsByExternalId.TryGetValue(member.Id.Value, out person);

                person ??= personsByName.GetValueOrDefault(member.Name);

                if (person == null)
                {
                    person = new Person
                    {
                        Name = member.Name,
                        ExternalId = member.Id,
                        ImagePath = member.ImageUrl
                    };
                    newPersons.Add(person);
                    // Register in lookups to avoid creating duplicates within the same batch
                    personsByName[member.Name] = person;
                    if (member.Id.HasValue && member.Id.Value > 0)
                        personsByExternalId[member.Id.Value] = person;
                }
                else if (member.Id.HasValue && !person.ExternalId.HasValue)
                {
                    person.ExternalId = member.Id;
                }

                resolvedPersons.Add((person, member, i));
            }

            if (newPersons.Count > 0)
            {
                _dbContext.Persons.AddRange(newPersons);
                await _dbContext.SaveChangesAsync();
            }

            // Diff-based cast association update
            var existingAssociations = await _dbContext.MediaItemCasts
                .Where(mc => mc.MediaItemId == item.Id)
                .ToListAsync();

            // For cast, order and character matter — rebuild associations
            _dbContext.MediaItemCasts.RemoveRange(existingAssociations);

            var castEntries = resolvedPersons.Select(rp => new MediaItemCast
            {
                MediaItemId = item.Id,
                PersonId = rp.Person.Id,
                Character = string.IsNullOrEmpty(rp.Member.Character) ? string.Empty : rp.Member.Character,
                Order = rp.Order
            }).ToList();

            _dbContext.MediaItemCasts.AddRange(castEntries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cast for {Title}", item.Title);
        }
    }
}
