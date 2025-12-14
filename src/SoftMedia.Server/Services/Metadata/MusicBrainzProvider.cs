using SoftMedia.Server.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;

namespace SoftMedia.Server.Services.Metadata;

public class MusicBrainzProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusicBrainzProvider> _logger;
    
    // MusicBrainz requires 1 request per second.
    // Using a static semaphore to enforce this across all scoped instances.
    private static readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public LibraryType SupportedType => LibraryType.Music;
    public string ProviderName => "MusicBrainz";

    public MusicBrainzProvider(HttpClient httpClient, ILogger<MusicBrainzProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        // User-Agent is MANDATORY for MusicBrainz
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
             _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
        }
    }

    public async Task<string?> FetchMetadataAsync(MediaItem item)
    {
        // 1. Context Strategy: Prefer Embedded Tags (ID3) over Path Parsing
        // The Aggregator should have already populated embedded tags into item.MetadataJson
        
        string? artist = null;
        string? album = null;
        string trackTitle = item.Title;
        string path = item.Path;

        // Try Embedded Metadata first
        if (!string.IsNullOrEmpty(item.MetadataJson))
        {
            try 
            {
                var tags = JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
                if (tags != null)
                {
                    if (tags.TryGetValue("artist", out var a)) artist = a.ToString();
                    if (tags.TryGetValue("album", out var al)) 
                    {
                        album = al.ToString();
                        // Clean embedded album title (remove (CD X), (Deluxe Edition), year suffixes, etc.)
                        // This aligns embedded tags with MusicBrainz naming conventions
                        if (!string.IsNullOrEmpty(album))
                        {
                            album = Regex.Replace(album, @"\s*\(CDS?\s*\d+\)", "", RegexOptions.IgnoreCase);
                            album = Regex.Replace(album, @"\s*\(Discs?\s*\d+\)", "", RegexOptions.IgnoreCase);
                            album = Regex.Replace(album, @"\s*\(.*(Edition|Version|Remaster|Live).*\)", "", RegexOptions.IgnoreCase);
                        }
                    }
                    if (tags.TryGetValue("title", out var t)) 
                    {
                        var et = t.ToString();
                        if (!string.IsNullOrEmpty(et))
                        {
                            // Clean slash from title (e.g. "Song 1 / Song 2")
                            trackTitle = et.Replace("/", " ").Replace("-", " ");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse existing metadata for context");
            }
        }

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

            // 3. Rate Limit
            await _rateLimitLock.WaitAsync();
            try
            {
                var timeSinceLast = DateTimeOffset.UtcNow - _lastRequestTime;
                if (timeSinceLast.TotalMilliseconds < 1100)
                {
                    await Task.Delay(1100 - (int)timeSinceLast.TotalMilliseconds);
                }
                
                // 4. Execute
                _logger.LogInformation("MusicBrainz Query (UseAlbum={UseAlbum}): {Query}", useAlbum, query);
                var response = await _httpClient.GetAsync(url);
                _lastRequestTime = DateTimeOffset.UtcNow;
                
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
                        var result = new Dictionary<string, object>();
                        // Extract from bestRecording + bestRelease
                        
                        if (bestRecording.TryGetProperty("title", out var t)) result["title"] = t.GetString() ?? trackTitle;
                        if (bestRecording.TryGetProperty("length", out var l) && l.TryGetInt32(out var ms)) result["duration"] = ms / 1000.0;
                        if (bestRecording.TryGetProperty("artist-credit", out var credits) && credits.GetArrayLength() > 0)
                        {
                             if (credits[0].TryGetProperty("name", out var an)) result["artist"] = an.GetString() ?? artist ?? "Unknown";
                        }
                        
                        if (bestRelease.HasValue)
                        {
                            var rel = bestRelease.Value;
                            if (rel.TryGetProperty("title", out var rt)) result["album"] = rt.GetString() ?? album ?? "Unknown";
                            
                            // Try Release Group Image first (Most reliable)
                            string? releaseGroupId = null;
                            if (rel.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("id", out var rgid))
                            {
                                releaseGroupId = rgid.GetString();
                            }

                            if (!string.IsNullOrEmpty(releaseGroupId))
                            {
                                result["poster"] = $"https://coverartarchive.org/release-group/{releaseGroupId}/front";
                            }
                            else if (rel.TryGetProperty("id", out var rid)) // Fallback to Release ID
                            {
                                var rId = rid.GetString();
                                if (!string.IsNullOrEmpty(rId))
                                {
                                    result["poster"] = $"https://coverartarchive.org/release/{rId}/front";
                                }
                            }

                            if (rel.TryGetProperty("date", out var rd)) 
                            {
                                var dateStr = rd.GetString();
                                if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr.Substring(0, Math.Min(4, dateStr.Length)), out var d))
                                    result["year"] = d.Year;
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
                             if (genreList.Count > 0) result["genres"] = genreList.ToArray();
                        }

                        _logger.LogInformation("Selected Match: {Match} for '{Track}'", bestMatchInfo, trackTitle);
                        return JsonSerializer.Serialize(result);
                    }
                }
            }
            finally
            {
                _rateLimitLock.Release();
            }
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
}
