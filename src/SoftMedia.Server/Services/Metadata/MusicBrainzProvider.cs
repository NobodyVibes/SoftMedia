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

public class MusicBrainzProvider : ISearchableMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzProvider> _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly RateLimiter _caaLimiter;
    private readonly IServiceScopeFactory _scopeFactory;

    public LibraryType SupportedType => LibraryType.Music;
    public string ProviderName => "MusicBrainz";

    private readonly IProviderLookupCache? _lookupCache;

    public MusicBrainzProvider(HttpClient httpClient, ILogger<MusicBrainzProvider> logger, RateLimiterFactory rateLimiterFactory, IServiceScopeFactory scopeFactory,
        IProviderLookupCache? lookupCache = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _lookupCache = lookupCache;
        _rateLimiter = rateLimiterFactory.GetLimiter("MusicBrainz");
        // SM-WI-023: Cover Art Archive is a different service on different infrastructure
        // (Internet Archive) — its HEAD probes must not ride MusicBrainz's 1/s budget
        // uncounted, nor go completely unthrottled as before.
        _caaLimiter = rateLimiterFactory.GetLimiter("CoverArtArchive");
        _scopeFactory = scopeFactory;
        // User-Agent is MANDATORY for MusicBrainz
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
             _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
        }
    }

    /// <summary>
    /// SM-WI-022 — leased GET for MusicBrainz calls. MB's published policy is a strict
    /// 1 request/second per IP with UA-based bans for violators; the Fix-Match search
    /// paths previously bypassed the limiter entirely (a burst of admin searches could
    /// violate policy). One lease per HTTP request, shared with the enrichment paths.
    /// </summary>
    private async Task<string> GetStringLimitedAsync(string url, CancellationToken ct = default)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException($"MusicBrainz rate-limit queue is full; request rejected locally: {url}");
        }
        return await _httpClient.GetStringAsync(url, ct);
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
    /// SM-WI-031: minimum MusicBrainz Lucene score (0–100) for accepting a search hit.
    /// MB ranks by relevance, but result[0] for a short query can be an unrelated
    /// notable entity ("Nirvana" the 60s UK band vs. the grunge band). Below this the
    /// match is a guess, and no metadata beats wrong metadata.
    /// </summary>
    private const int MinSearchScore = 85;

    private static int GetSearchScore(JsonElement candidate)
    {
        if (!candidate.TryGetProperty("score", out var s)) return 0;
        return s.ValueKind switch
        {
            JsonValueKind.Number => s.GetInt32(),
            JsonValueKind.String when int.TryParse(s.GetString(), out var v) => v,
            _ => 0,
        };
    }

    /// <summary>
    /// Shared artist-element parser for the search-hit and direct-by-MBID shapes.
    /// The MBID rides in the result so the aggregator promotes it — every later
    /// refresh then costs one direct request instead of a search.
    /// </summary>
    private static MetadataResult? ParseArtistElement(JsonElement artist, string fallbackName, string? mbid)
    {
        if (artist.ValueKind != JsonValueKind.Object) return null;

        var result = new MetadataResult
        {
            Title = artist.TryGetProperty("name", out var n) ? n.GetString() : fallbackName,
            MusicBrainzId = mbid,
        };

        if (artist.TryGetProperty("disambiguation", out var dis) && dis.ValueKind == JsonValueKind.String)
        {
            result.Description = dis.GetString();
        }

        // SM-WI-044 (Q1): the old Extra["artistType"] was computed then dropped (Extra
        // persists only for photos) — removed rather than persisted: no consumer.

        if (artist.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0)
        {
            result.Genres = tags.EnumerateArray()
                .Where(t => t.TryGetProperty("name", out _))
                .Select(t => t.GetProperty("name").GetString()!)
                .Take(5)
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// Fetches metadata for an Artist-type item. SM-WI-031: MBID-first (a previously
    /// matched artist refreshes with ONE direct request under the strict 1/s budget),
    /// then Lucene search gated by <see cref="MinSearchScore"/>.
    /// </summary>
    private async Task<MetadataResult?> FetchArtistMetadataAsync(MediaItem item)
    {
        var artistName = CleanName(item.Title);

        if (!string.IsNullOrEmpty(item.MusicBrainzId))
        {
            try
            {
                var direct = await GetStringLimitedAsync(
                    $"https://musicbrainz.org/ws/2/artist/{item.MusicBrainzId}?fmt=json&inc=tags");
                using var directDoc = JsonDocument.Parse(direct);
                var parsed = ParseArtistElement(directDoc.RootElement, artistName, item.MusicBrainzId);
                if (parsed != null)
                {
                    _logger.LogInformation("MusicBrainz artist refresh via MBID {Mbid}: '{Name}'", item.MusicBrainzId, parsed.Title);
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MusicBrainz direct artist fetch failed for {Mbid}; falling back to search", item.MusicBrainzId);
            }
        }

        // SM-WI-040: fresh cached miss → skip the search (the MBID path above is exempt).
        var cacheKey = ProviderLookupCacheService.NormalizeKey("artist", artistName);
        if (_lookupCache != null && await _lookupCache.IsFreshMissAsync(ProviderName, cacheKey))
        {
            _logger.LogDebug("MusicBrainz: fresh cached miss for artist '{Artist}'; skipping search", artistName);
            return null;
        }

        _logger.LogInformation("MusicBrainz artist search for: '{Artist}'", artistName);
        var query = $"artist:\"{artistName}\"";
        var url = $"https://musicbrainz.org/ws/2/artist?query={WebUtility.UrlEncode(query)}&fmt=json&limit=5";

        string json;
        try { json = await GetStringLimitedAsync(url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz artist search failed for '{Artist}'", artistName);
            return null; // transient — not cached, the retry ladder owns it
        }
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            var best = artists[0]; // MB orders by score; [0] is the only candidate worth gating
            var score = GetSearchScore(best);
            if (score < MinSearchScore)
            {
                _logger.LogInformation(
                    "MusicBrainz: best artist hit for '{Artist}' scored {Score} < {Min}; leaving unmatched",
                    artistName, score, MinSearchScore);
                if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
                return null;
            }

            var mbid = best.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var result = ParseArtistElement(best, artistName, mbid);
            if (result != null)
            {
                _logger.LogInformation("MusicBrainz artist match: '{Name}' (score {Score})", result.Title, score);
            }
            return result;
        }

        if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
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

        // SM-WI-031: MBID-first — a previously matched album refreshes with one direct
        // request instead of repeating the search under the 1/s budget.
        if (!string.IsNullOrEmpty(item.MusicBrainzId))
        {
            try
            {
                var direct = await GetStringLimitedAsync(
                    $"https://musicbrainz.org/ws/2/release-group/{item.MusicBrainzId}?fmt=json&inc=tags");
                using var directDoc = JsonDocument.Parse(direct);
                var parsed = await ParseReleaseGroupElementAsync(directDoc.RootElement, albumName, item.MusicBrainzId);
                if (parsed != null)
                {
                    _logger.LogInformation("MusicBrainz release-group refresh via MBID {Mbid}: '{Title}'", item.MusicBrainzId, parsed.Title);
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MusicBrainz direct release-group fetch failed for {Mbid}; falling back to search", item.MusicBrainzId);
            }
        }

        // SM-WI-040: fresh cached miss → skip the search (the MBID path above is exempt).
        var cacheKey = ProviderLookupCacheService.NormalizeKey("album", albumName, artistName);
        if (_lookupCache != null && await _lookupCache.IsFreshMissAsync(ProviderName, cacheKey))
        {
            _logger.LogDebug("MusicBrainz: fresh cached miss for album '{Album}'; skipping search", albumName);
            return null;
        }

        _logger.LogInformation("MusicBrainz release-group search for: '{Album}' by '{Artist}'", albumName, artistName);

        var queryParts = new List<string> { $"releasegroup:\"{albumName}\"" };
        if (!string.IsNullOrWhiteSpace(artistName))
            queryParts.Add($"artist:\"{artistName}\"");

        var query = string.Join(" AND ", queryParts);
        var url = $"https://musicbrainz.org/ws/2/release-group?query={WebUtility.UrlEncode(query)}&fmt=json&limit=5";

        string json;
        try { json = await GetStringLimitedAsync(url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz release-group search failed for '{Album}'", albumName);
            return null; // transient — not cached, the retry ladder owns it
        }
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("release-groups", out var groups) && groups.GetArrayLength() > 0)
        {
            // SM-WI-031: candidates are score-ordered; stop at the first sub-threshold
            // score (the rest are worse). When artist context exists, require the
            // candidate's artist-credit to agree — a same-titled album by someone else
            // is exactly the wrong-match this guards against.
            foreach (var candidate in groups.EnumerateArray())
            {
                var score = GetSearchScore(candidate);
                if (score < MinSearchScore)
                {
                    _logger.LogInformation(
                        "MusicBrainz: remaining release-group hits for '{Album}' score {Score} < {Min}; leaving unmatched",
                        albumName, score, MinSearchScore);
                    break;
                }

                if (!ArtistCreditAgrees(candidate, artistName))
                {
                    _logger.LogDebug("MusicBrainz: skipping release-group candidate with mismatched artist for '{Album}'", albumName);
                    continue;
                }

                var mbid = candidate.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var result = await ParseReleaseGroupElementAsync(candidate, albumName, mbid);
                if (result != null)
                {
                    _logger.LogInformation("MusicBrainz release-group match: '{Title}' ({Year}, score {Score})", result.Title, result.Year, score);
                    return result;
                }
            }
        }

        // Definitive miss (empty, all sub-threshold, or all artist-mismatched).
        if (_lookupCache != null) await _lookupCache.RecordMissAsync(ProviderName, cacheKey);
        return null;
    }

    /// <summary>
    /// True when the candidate's artist-credit matches the known artist context (or when
    /// either side lacks the information to disagree). Relaxed compare: equality or
    /// containment, case-insensitive — "The Beatles" vs "Beatles" must not reject.
    /// </summary>
    private static bool ArtistCreditAgrees(JsonElement candidate, string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return true;
        if (!candidate.TryGetProperty("artist-credit", out var ac) ||
            ac.ValueKind != JsonValueKind.Array || ac.GetArrayLength() == 0)
            return true;

        var creditName = ac[0].TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(creditName)) return true;

        return creditName.Equals(artistName, StringComparison.OrdinalIgnoreCase)
            || creditName.Contains(artistName, StringComparison.OrdinalIgnoreCase)
            || artistName.Contains(creditName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shared release-group parser for the search-hit and direct-by-MBID shapes.
    /// Async because cover-art existence is validated against the CAA (own limiter).
    /// </summary>
    private async Task<MetadataResult?> ParseReleaseGroupElementAsync(JsonElement group, string fallbackTitle, string? mbid)
    {
        if (group.ValueKind != JsonValueKind.Object) return null;

        var result = new MetadataResult
        {
            Title = group.TryGetProperty("title", out var t) ? t.GetString() : fallbackTitle,
            MusicBrainzId = mbid,
        };

        if (group.TryGetProperty("first-release-date", out var frd) && frd.ValueKind == JsonValueKind.String)
        {
            var dateStr = frd.GetString();
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && int.TryParse(dateStr.Substring(0, 4), out var year))
            {
                result.Year = year;
            }
        }

        if (!string.IsNullOrEmpty(mbid))
        {
            var coverUrl = $"https://coverartarchive.org/release-group/{mbid}/front";
            if (await ValidateCoverArtUrlAsync(coverUrl))
            {
                result.PosterUrl = coverUrl;
            }
        }

        if (group.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0)
        {
            result.Genres = tags.EnumerateArray()
                .Where(tg => tg.TryGetProperty("name", out _))
                .Select(tg => tg.GetProperty("name").GetString()!)
                .Take(5)
                .ToList();
        }

        return result;
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
    /// SM-WI-023: throttled by the dedicated CoverArtArchive limiter (previously
    /// unlimited), and PESSIMISTIC on 503/errors — the old "assume art exists" answer
    /// stored a PosterUrl whose later download could fail, leaving the item hotlinking
    /// a remote URL forever. "Unknown" now means no URL stored; a later enrichment
    /// pass re-probes.
    /// </summary>
    private async Task<bool> ValidateCoverArtUrlAsync(string coverUrl)
    {
        try
        {
            using var lease = await _caaLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogDebug("CoverArt Archive limiter queue full; treating art as unknown for {Url}", coverUrl);
                return false;
            }

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

            // 503 = CAA's throttle signal; anything else unexpected. Art is UNKNOWN,
            // not present — never store a URL we couldn't confirm.
            _logger.LogDebug("CoverArt Archive returned {Status} for {Url}; treating art as unknown", response.StatusCode, coverUrl);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to validate CoverArt Archive URL: {Url}; treating art as unknown", coverUrl);
            return false;
        }
    }

    // --- ISearchableMetadataProvider (P3-WI-003 Fix Match) ---

    /// <summary>
    /// Free-text release-group search. Uses MusicBrainz's Lucene query syntax via
    /// /ws/2/release-group?query=... (same endpoint pattern used internally for album
    /// disambiguation at line ~390). Returns up to 10 candidates ranked by MB's score.
    /// </summary>
    public async Task<IReadOnlyList<MetadataSearchCandidate>> SearchAsync(string query, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MetadataSearchCandidate>();
        var luceneQuery = year.HasValue
            ? $"{query.Trim()} AND firstreleasedate:{year}*"
            : query.Trim();
        var url = $"https://musicbrainz.org/ws/2/release-group?query={WebUtility.UrlEncode(luceneQuery)}&fmt=json&limit=10";

        string body;
        try { body = await GetStringLimitedAsync(url, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz search failed for '{Query}'", query);
            return Array.Empty<MetadataSearchCandidate>();
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("release-groups", out var groups) ||
            groups.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<MetadataSearchCandidate>();

        var candidates = new List<MetadataSearchCandidate>();
        foreach (var g in groups.EnumerateArray())
        {
            var id = g.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;

            int? releaseYear = null;
            if (g.TryGetProperty("first-release-date", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = d.GetString() ?? "";
                if (s.Length >= 4 && int.TryParse(s.Substring(0, 4), out var y)) releaseYear = y;
            }

            string? artistLine = null;
            if (g.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == System.Text.Json.JsonValueKind.Array && ac.GetArrayLength() > 0)
            {
                var first = ac[0];
                if (first.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                    artistLine = n.GetString();
            }

            // Cover Art Archive URL is constructible from the MBID; the receiver may 404
            // if no cover exists, but the UI can degrade gracefully (broken-image icon).
            var cover = $"https://coverartarchive.org/release-group/{id}/front-250";

            candidates.Add(new MetadataSearchCandidate(
                ProviderName,
                id!,
                g.TryGetProperty("title", out var t) ? (t.GetString() ?? "(untitled)") : "(untitled)",
                releaseYear,
                cover,
                artistLine));

            if (candidates.Count >= 10) break;
        }
        return candidates;
    }

    /// <summary>
    /// Fetches release-group metadata for a candidate MBID. The existing
    /// FetchMetadataAsync path is filename-driven and not directly reusable for a
    /// chosen-by-mbid lookup, so this is a dedicated lightweight fetch.
    /// </summary>
    public async Task<MetadataResult?> FetchByCandidateAsync(string providerItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerItemId)) return null;
        var url = $"https://musicbrainz.org/ws/2/release-group/{providerItemId}?fmt=json&inc=artist-credits";
        try
        {
            var body = await GetStringLimitedAsync(url, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var result = new MetadataResult
            {
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                MusicBrainzId = providerItemId,
                PosterUrl = $"https://coverartarchive.org/release-group/{providerItemId}/front",
            };
            if (root.TryGetProperty("first-release-date", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = d.GetString() ?? "";
                if (s.Length >= 4 && int.TryParse(s.Substring(0, 4), out var y)) result.Year = y;
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz FetchByCandidate failed for {Mbid}", providerItemId);
            return null;
        }
    }
}
