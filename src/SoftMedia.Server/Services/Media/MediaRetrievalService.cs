using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

public class MediaRetrievalService : IMediaRetrievalService
{
    private readonly IMediaRepository _mediaRepository;

    public MediaRetrievalService(IMediaRepository mediaRepository)
    {
        _mediaRepository = mediaRepository;
    }

    public async Task<IEnumerable<MediaItem>> GetRecentMediaAsync(int limit, LibraryType? type)
    {
        var rawItems = await _mediaRepository.GetRecentMediaAsync(limit, type);

        var distinctItems = new List<MediaItem>();
        var seenSeries = new HashSet<Guid>();
        var seenAlbums = new HashSet<Guid>();

        foreach (var item in rawItems)
        {
            if (distinctItems.Count >= limit) break;

            if ((item.Type == MediaType.Episode || item.Type == MediaType.Season) && item.Series != null)
            {
                if (!seenSeries.Contains(item.Series.Id))
                {
                    // Update Series DateAdded to the most recent activity (this episode/season)
                    // This ensures the frontend 'NEW' badge appears for new activity on old series.
                    item.Series.DateAdded = item.DateAdded;
                    distinctItems.Add(item.Series);
                    seenSeries.Add(item.Series.Id);
                }
            }
            else if (item.Type == MediaType.Audio && item.Album != null)
            {
                if (!seenAlbums.Contains(item.Album.Id))
                {
                    // Promote most recent track/audio addition date to the album
                    item.Album.DateAdded = item.DateAdded;
                    distinctItems.Add(item.Album);
                    seenAlbums.Add(item.Album.Id);
                }
            }
            else if (item.Type == MediaType.Series)
            {
                if (!seenSeries.Contains(item.Id))
                {
                    distinctItems.Add(item);
                    seenSeries.Add(item.Id);
                }
            }
            else if (item.Type == MediaType.Album)
            {
                if (!seenAlbums.Contains(item.Id))
                {
                    distinctItems.Add(item);
                    seenAlbums.Add(item.Id);
                }
            }
            else if (item.Type != MediaType.Episode && item.Type != MediaType.Season && 
                     item.Type != MediaType.Audio)
            {
                // Final fallback: Add item if it's not a grouped type (Movies, Books, etc.)
                distinctItems.Add(item);
            }
        }

        return distinctItems;
    }
}
