using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;

namespace SoftMedia.Server.Services.Metadata;

public class MusicBrainzProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly IServiceScopeFactory _scopeFactory;

    public LibraryType SupportedType => LibraryType.Music;
    public string ProviderName => "MusicBrainz";

    public MusicBrainzProvider(HttpClient httpClient, ILogger<MusicBrainzProvider> logger, RateLimiterFactory rateLimiterFactory, IServiceScopeFactory scopeFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("MusicBrainz");
        _scopeFactory = scopeFactory;
        // User-Agent is MANDATORY for MusicBrainz
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
             _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
        }
    }

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        // Type-aware search branching: use the appropriate MusicBrainz endpoint
        // based on the media item type to improve search accuracy.
        if (item.Type == Models.MediaType.Artist)
        {
            return await FetchArtistMetadataAsync(item);
        }
        if (item.Type == Models.MediaType.Album)
        {
            return await FetchReleaseGroupMetadataAsync(item);
        }

        // Default: Track/Audio — search recordings (existing behavior)

        // 1. Context Strategy: Prefer Embedded Tags (ID3) over Path Parsing
        // The Aggregator should have already populated embedded tags into item.MetadataJson
        
        string? artist = null;
        string? album = null;
        string trackTitle = item.Title;
        string path = item.Path;

        // Use direct properties from MediaItem instead of parsing MetadataJson.
        // The MusicScanner already populates ArtistId/AlbumId/Title during scan.
        if (item.Artist != null) artist = item.Artist.Title;
        if (item.Album != null) album = item.Album.Title;

        // Fallback: Path Parsing (folder structure)
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(album))
        {
            try 
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null)
                {
                    var albumDir = Path.GetFileName(dir); // Potential Album or CD folder
                    var parentDir = Path.GetDirectoryName(dir);
                    var artistDir = parentDir != null ? Path.GetFileName(parentDir) : null;
                    
                    // Check if albumDir is actually a Disc identifier (e.g., "CD1", "Disc 2", "CD1 - Album")
                    if (!string.IsNullOrEmpty(albumDir) && Regex.IsMatch(albumDir, @"^(CD|Disc)\s*\d+([\s\.\-_].*)?$", RegexOptions.IgnoreCase))
                    {
                        // Shift up one level
                        // The "Artist" dir we found is actually the Album dir
                        albumDir = artistDir;
                        
                        // The real Artist dir is the parent of the parent
                        var grandParentDir = parentDir != null ? Path.GetDirectoryName(parentDir) : null;
                        artistDir = grandParentDir != null ? Path.GetFileName(grandParentDir) : null;
                    }

                    // Only overwrite if still empty
                    if (string.IsNullOrEmpty(album) && !string.IsNullOrEmpty(albumDir)) album = CleanName(albumDir);
                    if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(artistDir)) artist = CleanName(artistDir);
                }
            }
            catch {}
        }
            
        // Clean track title (remove "01 ", extension)
        // title passed from FileScanner is typically filename without extension
        trackTitle = Regex.Replace(trackTitle, @"^\d+([\s\.\-_]+)?", ""); // Remove leading numbers
        trackTitle = Regex.Replace(trackTitle, @"\s*\(.*?\)", "").Trim(); // Remove (Europe cover) etc
        trackTitle = trackTitle.Replace("-", " ").Trim();

        _logger.LogInformation("Searching MusicBrainz for Track: '{Track}', Artist: '{Artist}', Album: '{Album}'", trackTitle, artist, album);

        // 2. Execution Loop (Exact -> Broad)
        // Attempt 1: Strict (Title + Artist + Album)
        // Attempt 2: Broad (Title + Artist) - "Release" field in MB is strict, so we drop it if strict fails.
        
        var attempts = new List<bool> { true, false }; // true = useAlbum, false = skipAlbum

        foreach (var useAlbum in attempts)
        {
            if (useAlbum && string.IsNullOrWhiteSpace(album)) continue; // Skip strict if no album to search for

            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(trackTitle)) queryParts.Add($"recording:\"{trackTitle}\"");
            if (!string.IsNullOrWhiteSpace(artist)) queryParts.Add($"artist:\"{artist}\"");
            if (useAlbum && !string.IsNullOrWhiteSpace(album)) queryParts.Add($"release:\"{album}\"");
            
            if (queryParts.Count == 0) continue;

            var query = string.Join(" AND ", queryParts);
            var url = $"https://musicbrainz.org/ws/2/recording?query={WebUtility.UrlEncode(query)}&fmt=json&limit=5";

            // Acquire rate limit lease before making API call
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("MusicBrainz rate limit exceeded for '{Track}', request was queued too long", trackTitle);
                continue;
            }
            
            _logger.LogInformation("MusicBrainz Query (UseAlbum={UseAlbum}): {Query}", useAlbum, query);
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz returned {Status}", response.StatusCode);
                continue; 
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("recordings", out var recordingsArray) && recordingsArray.GetArrayLength() > 0)
            {
                // Find BEST match across all recordings and their releases
                JsonElement? bestRelease = null;
                JsonElement bestRecording = recordingsArray[0];
                int bestScore = -1;
                string bestMatchInfo = "First Result"; 

                foreach (var rec in recordingsArray.EnumerateArray())
                {
                    if (rec.TryGetProperty("releases", out var releases))
                    {
                        foreach (var rel in releases.EnumerateArray())
                        {
                            int score = 0;
                            string relTitle = rel.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
                            
                            // Score 1: Album Name Match
                            if (!string.IsNullOrEmpty(album))
                            {
                                if (relTitle.Equals(album, StringComparison.OrdinalIgnoreCase)) 
                                {
                                    score += 100;
                                }
                                else if (relTitle.Contains(album, StringComparison.OrdinalIgnoreCase) || 
                                         album.Contains(relTitle, StringComparison.OrdinalIgnoreCase)) 
                                {
                                    score += 50;
                                }
                            }

                            // Score 2: Release Group Type (Prefer Album over Single)
                            // We can't easily check Type here without parsing "secondary-types" or "primary-type" which might be in release-group
                            // But simply matching name is usually enough.
                            
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestRelease = rel;
                                bestRecording = rec;
                                bestMatchInfo = $"{relTitle} (Score: {score})";
                            }
                        }
                    }
                    else
                    {
                        // Recording without release info. Score low.
                        if (bestScore == -1) 
                        { 
                            bestScore = 0; 
                            bestRecording = rec; 
                        }
                    }
                }

                if (bestScore > -1)
                {
                    var result = new MetadataResult();
                    // Extract from bestRecording + bestRelease
                    
                    if (bestRecording.TryGetProperty("title", out var t)) result.Title = t.GetString() ?? trackTitle;
                    if (bestRecording.TryGetProperty("length", out var l) && l.TryGetInt32(out var ms)) result.Duration = ms / 1000.0;
                    if (bestRecording.TryGetProperty("artist-credit", out var credits) && credits.GetArrayLength() > 0)
                    {
                         if (credits[0].TryGetProperty("name", out var an)) result.Artist = an.GetString() ?? artist ?? "Unknown";
                    }
                    if (bestRecording.TryGetProperty("id", out var recId))
                    {
                        result.MusicBrainzId = recId.GetString();
                    }
                    
                    if (bestRelease.HasValue)
                    {
                        var rel = bestRelease.Value;
                        if (rel.TryGetProperty("title", out var rt)) result.Album = rt.GetString() ?? album ?? "Unknown";
                        
                        // Try Release Group Image first (Most reliable)
                        string? releaseGroupId = null;
                        if (rel.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("id", out var rgid))
                        {
                            releaseGroupId = rgid.GetString();
                        }

                        // Validate CoverArt Archive URL via HEAD request before storing.
                        // Per CAA docs: 307 = art exists, 404 = no art available.
                        if (!string.IsNullOrEmpty(releaseGroupId))
                        {
                            var coverUrl = $"https://coverartarchive.org/release-group/{releaseGroupId}/front";
                            if (await ValidateCoverArtUrlAsync(coverUrl))
                            {
                                result.PosterUrl = coverUrl;
                            }
                        }
                        
                        if (string.IsNullOrEmpty(result.PosterUrl) && rel.TryGetProperty("id", out var rid))
                        {
                            var rId = rid.GetString();
                            if (!string.IsNullOrEmpty(rId))
                            {
                                var coverUrl = $"https://coverartarchive.org/release/{rId}/front";
                                if (await ValidateCoverArtUrlAsync(coverUrl))
                                {
                                    result.PosterUrl = coverUrl;
                                }
                            }
                        }

                        if (rel.TryGetProperty("date", out var rd)) 
                        {
                            var dateStr = rd.GetString();
                            if (!string.IsNullOrEmpty(dateStr))
                            {
                                var yearPart = dateStr.Length >= 4 ? dateStr.Substring(0, 4) : dateStr;
                                if (int.TryParse(yearPart, out var parsedYear))
                                    result.Year = parsedYear;
                            }
                        }
                    }
                    
                    // Tags from recording
                    if (bestRecording.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0)
                    {
                         var genreList = new List<string>();
                         foreach(var tag in tags.EnumerateArray())
                         {
                             if (tag.TryGetProperty("name", out var tn)) genreList.Add(tn.GetString()!);
                         }
                         if (genreList.Count > 0) result.Genres = genreList;
                    }

                    _logger.LogInformation("Selected Match: {Match} for '{Track}'", bestMatchInfo, trackTitle);
                    return result;
                }
            }
        }


        return null;
    }

    /// <summary>
    /// Fetches metadata for an Artist-type item using /ws/2/artist endpoint.
    /// </summary>
    private async Task<MetadataResult?> FetchArtistMetadataAsync(MediaItem item)
    {
        var artistName = CleanName(item.Title);
        _logger.LogInformation("MusicBrainz artist search for: '{Artist}'", artistName);

        using var lease = await _rateLimiter.AcquireAsync(1);
        if (!lease.IsAcquired)
        {
            _logger.LogWarning("MusicBrainz rate limit exceeded for artist '{Artist}'", artistName);
            return null;
        }

        var query = $"artist:\"{artistName}\"";
        var url = $"https://musicbrainz.org/ws/2/artist?query={WebUtility.UrlEncode(query)}&fmt=json&limit=5";
        
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        
        if (doc.RootElement.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            var best = artists[0];
            var result = new MetadataResult
            {
                Title = best.TryGetProperty("name", out var n) ? n.GetString() : artistName
            };

            if (best.TryGetProperty("disambiguation", out var dis) && dis.ValueKind == JsonValueKind.String)
            {
                result.Description = dis.GetString();
            }

            if (best.TryGetProperty("type", out var typeVal) && typeVal.ValueKind == JsonValueKind.String)
            {
                result.Extra ??= new Dictionary<string, JsonElement>();
                result.Extra["artistType"] = JsonSerializer.SerializeToElement(typeVal.GetString());
            }

            if (best.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0)
            {
                result.Genres = tags.EnumerateArray()
                    .Where(t => t.TryGetProperty("name", out _))
                    .Select(t => t.GetProperty("name").GetString()!)
                    .Take(5)
                    .ToList();
            }

            _logger.LogInformation("MusicBrainz artist match: '{Name}'", result.Title);
            return result;
        }

        return null;
    }

    /// <summary>
    /// Fetches metadata for an Album-type item using /ws/2/release-group endpoint.
    /// </summary>
    private async Task<MetadataResult?> FetchReleaseGroupMetadataAsync(MediaItem item)
    {
        var albumName = CleanName(item.Title);
        string? artistName = null;

        // Resolve artist context from ArtistId FK
        if (item.ArtistId.HasValue)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var parentArtist = await db.MediaItems
                    .AsNoTracking()
                    .Where(m => m.Id == item.ArtistId.Value && m.Type == MediaType.Artist)
                    .Select(m => m.Title)
                    .FirstOrDefaultAsync();
                if (!string.IsNullOrEmpty(parentArtist))
                {
                    artistName = parentArtist;
                    _logger.LogDebug("Resolved artist '{Artist}' from ArtistId FK for album '{Album}'", artistName, albumName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve ArtistId FK for album '{Album}'", albumName);
            }
        }

        _logger.LogInformation("MusicBrainz release-group search for: '{Album}' by '{Artist}'", albumName, artistName);

        using var lease = await _rateLimiter.AcquireAsync(1);
        if (!lease.IsAcquired)
        {
            _logger.LogWarning("MusicBrainz rate limit exceeded for album '{Album}'", albumName);
            return null;
        }

        var queryParts = new List<string> { $"releasegroup:\"{albumName}\"" };
        if (!string.IsNullOrWhiteSpace(artistName))
            queryParts.Add($"artist:\"{artistName}\"");

        var query = string.Join(" AND ", queryParts);
        var url = $"https://musicbrainz.org/ws/2/release-group?query={WebUtility.UrlEncode(query)}&fmt=json&limit=5";

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("release-groups", out var groups) && groups.GetArrayLength() > 0)
        {
            var best = groups[0];
            var result = new MetadataResult
            {
                Title = best.TryGetProperty("title", out var t) ? t.GetString() : albumName
            };

            // Year from first-release-date
            if (best.TryGetProperty("first-release-date", out var frd) && frd.ValueKind == JsonValueKind.String)
            {
                var dateStr = frd.GetString();
                if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var year))
                {
                    result.Year = year;
                }
            }

            // Cover art from release-group ID
            if (best.TryGetProperty("id", out var rgId))
            {
                var coverUrl = $"https://coverartarchive.org/release-group/{rgId.GetString()}/front";
                if (await ValidateCoverArtUrlAsync(coverUrl))
                {
                    result.PosterUrl = coverUrl;
                }
            }

            // Tags/genres
            if (best.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0)
            {
                result.Genres = tags.EnumerateArray()
                    .Where(tg => tg.TryGetProperty("name", out _))
                    .Select(tg => tg.GetProperty("name").GetString()!)
                    .Take(5)
                    .ToList();
            }

            _logger.LogInformation("MusicBrainz release-group match: '{Title}' ({Year})", result.Title, result.Year);
            return result;
        }

        return null;
    }

    private string CleanName(string name)
    {
        // Remove "Discography (YYYY-YYYY)" or "YYYY - " prefix
        // Example: "Arch Enemy - Discography (1996 - 2022)" -> "Arch Enemy"
        // Example: "2022 - Deceivers" -> "Deceivers"
        
        var n = name;
        
        // Remove year prefix "2022 - "
        n = Regex.Replace(n, @"^\d{4}\s*-\s*", "");
        
        // Remove " - Discography..."
        n = Regex.Replace(n, @"\s*-\s*Discography.*$", "", RegexOptions.IgnoreCase);
        
        // Remove parentheticals like "(Limited Edition)"
        n = Regex.Replace(n, @"\s*\(.*?\)", "");
        
        return n.Trim();
    }

    /// <summary>
    /// Validates a CoverArt Archive URL via HEAD request.
    /// Per CAA API docs: 307 = cover art exists (redirect to image),
    /// 404 = no cover art available, 503 = rate limited.
    /// </summary>
    private async Task<bool> ValidateCoverArtUrlAsync(string coverUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, coverUrl);
            // Don't follow redirects — we just need to know if art exists
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (response.IsSuccessStatusCode || 
                response.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect ||
                response.StatusCode == System.Net.HttpStatusCode.Redirect)
            {
                return true;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("CoverArt Archive returned 404 for {Url}", coverUrl);
                return false;
            }

            // 503 (rate limit) or other errors — assume art MAY exist, don't block
            _logger.LogDebug("CoverArt Archive returned {Status} for {Url}", response.StatusCode, coverUrl);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to validate CoverArt Archive URL: {Url}", coverUrl);
            // Network error — don't block, assume URL might be valid
            return true;
        }
    }
}
