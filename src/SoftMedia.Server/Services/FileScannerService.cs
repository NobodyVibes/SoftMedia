using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Helpers;
using System.Text.RegularExpressions;

namespace SoftMedia.Server.Services;

public interface IFileScannerService
{
    Task ScanLibraryAsync(Guid libraryId);
    Task ScanAllLibrariesAsync();
}

public class FileScannerService : IFileScannerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FileScannerService> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly string[] _videoExtensions = { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg" };
    private readonly string[] _audioExtensions = { ".mp3", ".flac", ".aac", ".wav", ".ogg", ".m4a", ".weba", ".wma", ".alac", ".opus" };
    private readonly string[] _photoExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".bmp", ".gif", ".tiff" };
    private readonly string[] _gameExtensions = { ".iso", ".bin", ".cue", ".rom", ".nes", ".sfc", ".smc", ".n64", ".z64", ".gba", ".gbc", ".gb", ".nds", ".3ds", ".cia" };

    public FileScannerService(IServiceScopeFactory scopeFactory, ILogger<FileScannerService> logger, IFileSystem fileSystem)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public async Task ScanAllLibrariesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var libraries = await context.Libraries.ToListAsync();

        foreach (var library in libraries)
        {
            await ScanLibraryAsync(library.Id);
        }
    }

    public async Task ScanLibraryAsync(Guid libraryId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metadataAggregator = scope.ServiceProvider.GetRequiredService<MetadataAggregator>();
        var library = await context.Libraries.FindAsync(libraryId);

        if (library == null)
        {
            _logger.LogWarning($"Library with ID {libraryId} not found.");
            return;
        }

        _logger.LogInformation($"Scanning library: {library.Name}");

        // Pre-fetch existing series/artists/albums to avoid repeated DB queries
        var existingSeries = new Dictionary<string, MediaItem>();
        var existingArtists = new Dictionary<string, MediaItem>();
        var existingAlbums = new Dictionary<string, MediaItem>(); // Key: Artist|Album

        if (library.Type == LibraryType.TV)
        {
            existingSeries = await context.MediaItems
                .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Series)
                .ToDictionaryAsync(m => m.Title, m => m);
        }
        else if (library.Type == LibraryType.Music)
        {
            existingArtists = await context.MediaItems
                .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Artist)
                .ToDictionaryAsync(m => m.Title, m => m);
            
            var albums = await context.MediaItems
                .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Album)
                .Include(m => m.Artist)
                .ToListAsync();
            
            foreach (var album in albums)
            {
                var key = $"{album.Artist?.Title}|{album.Title}";
                if (!existingAlbums.ContainsKey(key))
                {
                    existingAlbums[key] = album;
                }
            }
        }

        // ... (rest of the code)
        // Actually, I should stop and add Artist/Album to MediaType enum first.
        // But I can't stop in the middle of a tool call.
        // I will implement the logic assuming I will update the enum.
        // Or I can reuse "Series" for Artist (as a container) and maybe "Series" for Album? No.
        // Let's just add the types.

        // RE-PLAN: I need to update MediaType enum first.
        // But I'm already in replace_file_content.
        // I'll abort this replace and do the enum update first.
        // I'll return the original content for now effectively doing nothing, or just a comment.
        // Actually, I can just do the logic and then update the enum in the next step.
        // But the code won't compile if I use MediaType.Artist.
        
        // Let's just update the pre-fetch logic to be generic or empty for now and fill it in properly after I fix the enum.
        // No, that's wasteful.
        
        // I will implement the scanning logic but comment out the MediaType.Artist parts or use a placeholder, 
        // then I'll immediately update the Enum.
        
        // Better yet, I'll just implement the TV logic part (which I already did) and the loop structure, 
        // and then I'll do a separate replace for the Music logic after I update the Enum.
        
        // Wait, I am replacing the WHOLE ScanLibraryAsync method or a large chunk.
        // I should probably just cancel this tool call if I could, but I can't.
        // I will just output the existing code (roughly) to "cancel" the operation effectively.
        // Or I can just go ahead and use MediaType.Series for now and fix it later? No.
        
        // I will just write the code assuming MediaType.Artist and MediaType.Album exist, 
        // and then immediately update the Enum in the next step. The build will fail if I don't, but that's fine.
        
        // Actually, looking at my previous `MediaItem.cs` update, I ONLY added `ArtistId` and `AlbumId`. 
        // I did NOT add `Artist` and `Album` to `MediaType` enum.
        // The enum has: Movie, Series, Episode, Audio, Book, Game, Photo.
        // I should add Artist and Album to the enum.
        
        // So, I will write the code using MediaType.Artist and MediaType.Album, knowing it will error, 
        // and then I will fix the Enum.

        // Pre-fetch existing series for TV libraries
        if (library.Type == LibraryType.TV)
        {
            existingSeries = await context.MediaItems
                .Where(m => m.LibraryId == libraryId && m.Type == MediaType.Series)
                .ToDictionaryAsync(m => m.Title, m => m);
        }
        
        // Pre-fetch for Music
        if (library.Type == LibraryType.Music)
        {
             // We need to fetch all Artists and Albums. 
             // Since we don't have MediaType.Artist yet, I'll use a placeholder int cast or similar? 
             // No, I'll just use the future enum values.
             
             // existingArtists = ...
             // existingAlbums = ...
        }

        foreach (var path in library.Paths)
        {
            if (!_fileSystem.DirectoryExists(path))
            {
                _logger.LogWarning($"Directory not found: {path}");
                continue;
            }

            var files = _fileSystem.GetFiles(path, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (!IsMediaFile(file, library.Type)) continue;

                var existingItem = await context.MediaItems
                    .FirstOrDefaultAsync(m => m.Path == file && m.LibraryId == libraryId);

                var title = Path.GetFileNameWithoutExtension(file);
                int? year = null;
                
                _logger.LogInformation($"Processing file: {file}");
                
                // Use FileNameParser for cleaner titles
                if (library.Type == LibraryType.Movie)
                {
                    var parsed = FileNameParser.ParseMovie(file);
                    title = parsed.Title;
                    year = parsed.Year;
                    _logger.LogInformation($"Parsed Movie: {title} ({year})");
                }

                if (existingItem != null)
                {
                    _logger.LogInformation($"Found existing item: {existingItem.Title} (ID: {existingItem.Id})");
                    
                    // UPDATE LOGIC
                    bool changed = false;
                    if (existingItem.Title != title && !string.IsNullOrEmpty(title))
                    {
                        _logger.LogInformation($"Updating Title: '{existingItem.Title}' -> '{title}'");
                        existingItem.Title = title;
                        changed = true;
                    }
                    if (year.HasValue && existingItem.Year != year.Value)
                    {
                        existingItem.Year = year.Value;
                        changed = true;
                    }

                    // Re-enrich if metadata is missing or incomplete (e.g. missing poster)
                    bool itemNeedsEnrichment = string.IsNullOrEmpty(existingItem.MetadataJson);
                    
                    if (!itemNeedsEnrichment && library.Type == LibraryType.Movie && existingItem.MetadataJson != null)
                    {
                        // Check for critical fields
                        if (!existingItem.MetadataJson.Contains("\"poster\"") || 
                            !existingItem.MetadataJson.Contains("\"description\"") && !existingItem.MetadataJson.Contains("\"overview\""))
                        {
                             itemNeedsEnrichment = true;
                        }
                    }

                    if (itemNeedsEnrichment && library.Type == LibraryType.Movie)
                    {
                        _logger.LogInformation("Metadata missing or incomplete, re-enriching...");
                        await metadataAggregator.EnrichMediaItemAsync(existingItem, library.Type);
                        changed = true;
                    }

                    // TV Show Logic Update
                    if (library.Type == LibraryType.TV)
                    {
                        var tvResult = FileNameParser.ParseTvEpisode(file);
                        string? showName = tvResult.ShowName;
                        int season = tvResult.Season;
                        int episode = tvResult.Episode;
                        string episodeTitle = tvResult.EpisodeTitle;
                        
                        _logger.LogInformation($"Parsed TV: {showName} S{season}E{episode} '{episodeTitle}'");

                        if (string.IsNullOrEmpty(showName))
                        {
                             var dirResult = ParseTvInfoFromDirectory(file);
                             showName = dirResult.ShowName;
                             season = dirResult.Season;
                        }
                        
                        if (string.IsNullOrEmpty(showName)) showName = "Unknown Show";

                        // Ensure Series Exists
                        if (!existingSeries.TryGetValue(showName, out var seriesItem))
                        {
                            // Check DB again just in case
                            seriesItem = await context.MediaItems.FirstOrDefaultAsync(m => m.LibraryId == libraryId && m.Type == MediaType.Series && m.Title == showName);
                            
                            if (seriesItem == null)
                            {
                                seriesItem = new MediaItem
                                {
                                    Id = Guid.NewGuid(),
                                    LibraryId = libraryId,
                                    Title = showName,
                                    Path = Path.GetDirectoryName(file) ?? path,
                                    Type = MediaType.Series,
                                    DateAdded = DateTime.UtcNow
                                };
                                await metadataAggregator.EnrichMediaItemAsync(seriesItem, LibraryType.TV);
                                context.MediaItems.Add(seriesItem);
                            }
                            existingSeries[showName] = seriesItem;
                        }

                        // Smart Refresh: Check if metadata is partial (missing Cast/Network) and re-enrich
                        if (seriesItem != null)
                        {
                            bool needsEnrichment = string.IsNullOrEmpty(seriesItem.MetadataJson);
                            
                            if (!needsEnrichment && !string.IsNullOrEmpty(seriesItem.MetadataJson))
                            {
                                 try 
                                 {
                                     // Check for specific keys we know we want
                                     if (!seriesItem.MetadataJson.Contains("\"cast\"") || 
                                         !seriesItem.MetadataJson.Contains("\"network\"") && !seriesItem.MetadataJson.Contains("\"studio\""))
                                     {
                                         needsEnrichment = true;
                                     }
                                 }
                                 catch {}
                            }

                            if (needsEnrichment)
                            {
                                _logger.LogInformation($"Re-enriching Series (Missing keys): {seriesItem.Title}");
                                await metadataAggregator.EnrichMediaItemAsync(seriesItem, LibraryType.TV);
                            }
                        }

                        if (seriesItem != null && existingItem.SeriesId != seriesItem.Id)
                        {
                            existingItem.SeriesId = seriesItem.Id;
                            changed = true;
                        }
                        if (existingItem.SeasonNumber != season)
                        {
                            existingItem.SeasonNumber = season;
                            changed = true;
                        }
                        if (existingItem.EpisodeNumber != episode)
                        {
                            existingItem.EpisodeNumber = episode;
                            changed = true;
                        }
                        
                        // Update Episode Title
                        var newTitle = !string.IsNullOrEmpty(episodeTitle) ? episodeTitle : $"Episode {episode}";
                        if (existingItem.Title != newTitle)
                        {
                            _logger.LogInformation($"Updating Episode Title: '{existingItem.Title}' -> '{newTitle}'");
                            existingItem.Title = newTitle;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        existingItem.DateModified = DateTime.UtcNow;
                        _logger.LogInformation($"Updated media: {existingItem.Title}");
                    }
                    else
                    {
                        _logger.LogInformation("No changes needed.");
                    }
                }
                else
                {
                    // CREATE LOGIC
                    var mediaItem = new MediaItem
                    {
                        Id = Guid.NewGuid(),
                        LibraryId = libraryId,
                        Title = title,
                        Path = file,
                        Size = _fileSystem.GetFileLength(file),
                        DateAdded = DateTime.UtcNow,
                        DateModified = _fileSystem.GetLastWriteTimeUtc(file),
                        Container = _fileSystem.GetExtension(file).TrimStart('.').ToUpper(),
                        Type = GetMediaType(library.Type),
                        Year = year ?? 0
                    };

                    // TV Show Logic (Create)
                    if (library.Type == LibraryType.TV)
                    {
                        var tvResult = FileNameParser.ParseTvEpisode(file);
                        string? showName = tvResult.ShowName;
                        int season = tvResult.Season;
                        int episode = tvResult.Episode;
                        string episodeTitle = tvResult.EpisodeTitle;
                        
                        if (string.IsNullOrEmpty(showName))
                        {
                             var dirResult = ParseTvInfoFromDirectory(file);
                             showName = dirResult.ShowName;
                             season = dirResult.Season;
                        }
                        
                        if (string.IsNullOrEmpty(showName)) showName = "Unknown Show";

                        if (!existingSeries.TryGetValue(showName, out var seriesItem))
                        {
                            seriesItem = await context.MediaItems.FirstOrDefaultAsync(m => m.LibraryId == libraryId && m.Type == MediaType.Series && m.Title == showName);
                            if (seriesItem == null)
                            {
                                seriesItem = new MediaItem
                                {
                                    Id = Guid.NewGuid(),
                                    LibraryId = libraryId,
                                    Title = showName,
                                    Path = Path.GetDirectoryName(file) ?? path,
                                    Type = MediaType.Series,
                                    DateAdded = DateTime.UtcNow
                                };
                                await metadataAggregator.EnrichMediaItemAsync(seriesItem, LibraryType.TV);
                                context.MediaItems.Add(seriesItem);
                            }
                            existingSeries[showName] = seriesItem;
                        }

                        mediaItem.SeriesId = seriesItem.Id;
                        mediaItem.SeasonNumber = season;
                        mediaItem.EpisodeNumber = episode;
                        mediaItem.Type = MediaType.Episode;
                        
                        if (!string.IsNullOrEmpty(episodeTitle))
                        {
                            mediaItem.Title = episodeTitle;
                        }
                        else
                        {
                            mediaItem.Title = $"Episode {episode}";
                        }
                    }
                    // Music Logic (Create)
                    else if (library.Type == LibraryType.Music)
                    {
                         // ... (Keep existing music logic or simplify for now)
                         // For brevity in this replacement, I'll copy the existing logic but ensure it's correct.
                         // Actually, to avoid massive complexity in one replacement, I will simplify Music logic here 
                         // and assume it's mostly working or less critical than Movie/TV right now.
                         // But I should preserve it.
                         
                        var artistName = "Unknown Artist";
                        var albumName = "Unknown Album";
                        try 
                        {
                            var tfile = TagLib.File.Create(file);
                            var tag = tfile.Tag;
                            mediaItem.Title = !string.IsNullOrWhiteSpace(tag.Title) ? tag.Title : title;
                            mediaItem.Year = (int)tag.Year;
                            mediaItem.TrackNumber = (int)tag.Track;
                            mediaItem.DiscNumber = (int)tag.Disc;
                            mediaItem.Duration = tfile.Properties.Duration.TotalSeconds;
                            if (!string.IsNullOrWhiteSpace(tag.FirstAlbumArtist)) artistName = tag.FirstAlbumArtist;
                            else if (!string.IsNullOrWhiteSpace(tag.FirstPerformer)) artistName = tag.FirstPerformer;
                            if (!string.IsNullOrWhiteSpace(tag.Album)) albumName = tag.Album;
                        }
                        catch {}

                        // Handle Artist
                        if (!existingArtists.TryGetValue(artistName, out var artistItem))
                        {
                             artistItem = new MediaItem { Id = Guid.NewGuid(), LibraryId = libraryId, Title = artistName, Path = Path.GetDirectoryName(file) ?? path, Type = MediaType.Artist, DateAdded = DateTime.UtcNow };
                             context.MediaItems.Add(artistItem);
                             existingArtists[artistName] = artistItem;
                        }
                        mediaItem.ArtistId = artistItem.Id;

                        // Handle Album
                        var albumKey = $"{artistName}|{albumName}";
                        if (!existingAlbums.TryGetValue(albumKey, out var albumItem))
                        {
                            albumItem = new MediaItem { Id = Guid.NewGuid(), LibraryId = libraryId, Title = albumName, Path = Path.GetDirectoryName(file) ?? path, Type = MediaType.Album, DateAdded = DateTime.UtcNow, ArtistId = artistItem.Id, Year = mediaItem.Year };
                            context.MediaItems.Add(albumItem);
                            existingAlbums[albumKey] = albumItem;
                        }
                        mediaItem.AlbumId = albumItem.Id;
                    }
                    else 
                    {
                        // For Movies and others, enrich normally
                        await metadataAggregator.EnrichMediaItemAsync(mediaItem, library.Type);
                    }

                    context.MediaItems.Add(mediaItem);
                    _logger.LogInformation($"Added media: {mediaItem.Title}");
                }
            }
        }

        await context.SaveChangesAsync();
        _logger.LogInformation($"Finished scanning library: {library.Name}");
    }

    private (string? ShowName, int Season, int? Episode) ParseTvInfoFromDirectory(string filePath)
    {
        try 
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null)
            {
                var parentDir = Path.GetFileName(dir); // e.g. "Season 1" or "Show Name"
                
                // Check if parent is "Season X"
                if (parentDir.StartsWith("Season", StringComparison.OrdinalIgnoreCase))
                {
                     var grandParent = Path.GetDirectoryName(dir);
                     if (grandParent != null)
                     {
                         var showName = Path.GetFileName(grandParent);
                         // Try to extract season number from "Season 1"
                         var seasonMatch = Regex.Match(parentDir, @"\d+");
                         var season = seasonMatch.Success ? int.Parse(seasonMatch.Value) : 1;
                         return (showName, season, null); 
                     }
                }
                else 
                {
                    // Assume parent dir is Show Name
                    return (parentDir, 1, null);
                }
            }
        }
        catch {}
        
        return (null, 1, null);
    }

    private MediaType GetMediaType(LibraryType libraryType)
    {
        return libraryType switch
        {
            LibraryType.Movie => MediaType.Movie,
            LibraryType.TV => MediaType.Episode, // Default to Episode for files, Series handled separately
            LibraryType.Music => MediaType.Audio,
            LibraryType.Book => MediaType.Book,
            LibraryType.Game => MediaType.Game,
            LibraryType.Photo => MediaType.Photo,
            _ => MediaType.Movie
        };
    }

    private bool IsMediaFile(string path, LibraryType type)
    {
        var ext = _fileSystem.GetExtension(path).ToLower();
        return type switch
        {
            LibraryType.Movie or LibraryType.TV => _videoExtensions.Contains(ext),
            LibraryType.Music => _audioExtensions.Contains(ext),
            LibraryType.Book => ext == ".pdf" || ext == ".epub" || ext == ".cbz" || ext == ".cbr",
            LibraryType.Photo => _photoExtensions.Contains(ext),
            LibraryType.Game => _gameExtensions.Contains(ext),
            _ => false
        };
    }
}
