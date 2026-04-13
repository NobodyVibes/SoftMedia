using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace SoftMedia.Server.Services.Metadata;

public class OpenLibraryProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenLibraryProvider> _logger;
    private readonly RateLimiter _rateLimiter;

    public LibraryType SupportedType => LibraryType.Book;
    public string ProviderName => "Open Library";

    public OpenLibraryProvider(HttpClient httpClient, ILogger<OpenLibraryProvider> logger, RateLimiterFactory rateLimiterFactory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiterFactory.GetLimiter("OpenLibrary");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        var title = item.Title;
        try
        {
            // Acquire rate limit lease (replaces manual SemaphoreSlim + delay)
            using var lease = await _rateLimiter.AcquireAsync(1);
            if (!lease.IsAcquired)
            {
                _logger.LogWarning("OpenLibrary rate limit exceeded for '{Title}', skipping", title);
                return null;
            }

            // Build search URL using structured params when author context is available.
            // OpenLibrary's search API supports title= and author= for more accurate results.
            // Use the promoted Director column for author context.
            // BookScanner stores parsed author in Director as the generic "primary creator" field.
            string? author = item.Director;

            string url;
            if (!string.IsNullOrWhiteSpace(author))
            {
                url = $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}&limit=10";
            }
            else
            {
                url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(title)}&limit=10";
            }
            
            var response = await _httpClient.GetStringAsync(url);
            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("docs", out var docs) && docs.GetArrayLength() > 0)
            {
                JsonElement? bestBook = null;
                int bestScore = int.MaxValue; // Lower is better

                foreach (var docEntry in docs.EnumerateArray())
                {
                    int score = 0;
                    
                    // 1. Title similarity
                    var docTitle = docEntry.TryGetProperty("title", out var tp) ? tp.GetString() : "";
                    if (!string.IsNullOrEmpty(docTitle))
                    {
                        score += MediaStringHelpers.CalculateLevenshteinDistance(title.ToLowerInvariant(), docTitle.ToLowerInvariant()) * 10;
                    }
                    else
                    {
                        score += 1000;
                    }

                    // 2. Year match
                    if (item.Year.HasValue && docEntry.TryGetProperty("first_publish_year", out var yp) && yp.ValueKind != JsonValueKind.Null)
                    {
                        var diff = Math.Abs(item.Year.Value - yp.GetInt32());
                        score += diff * 5; 
                    }

                    // 3. Prefer results with cover art
                    if (!docEntry.TryGetProperty("cover_i", out var ci) || ci.ValueKind == JsonValueKind.Null)
                    {
                        score += 50; // Penalty for no cover
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestBook = docEntry;
                    }
                }

                if (!bestBook.HasValue) return null;

                var book = bestBook.Value;
                var result = new MetadataResult();
                
                if (book.TryGetProperty("title", out var titleProp)) result.Title = titleProp.GetString();
                if (book.TryGetProperty("first_publish_year", out var yearProp) && yearProp.ValueKind != JsonValueKind.Null) result.Year = yearProp.GetInt32();
                
                if (book.TryGetProperty("author_name", out var authors))
                {
                    result.Cast = authors.EnumerateArray()
                        .Select(a => new CastMember { Name = a.GetString() ?? "Unknown", Character = "Author" })
                        .ToList();
                }
                
                if (book.TryGetProperty("publisher", out var publishers) && publishers.GetArrayLength() > 0)
                {
                    var publisher = publishers[0].GetString();
                    if (!string.IsNullOrEmpty(publisher))
                    {
                        result.Studio = publisher;
                        result.Publisher = publisher;
                    }
                }
                
                if (book.TryGetProperty("subject", out var subjects))
                {
                    result.Genres = subjects.EnumerateArray().Take(5).Select(s => s.GetString()!).ToList();
                }
                
                if (book.TryGetProperty("number_of_pages_median", out var pages) && pages.ValueKind != JsonValueKind.Null) 
                    result.PageCount = pages.GetInt32();
                
                if (book.TryGetProperty("isbn", out var isbns) && isbns.GetArrayLength() > 0)
                {
                    result.Isbn = isbns[0].GetString();
                }
                
                // Cover ID to URL
                if (book.TryGetProperty("cover_i", out var coverId) && coverId.ValueKind != JsonValueKind.Null)
                {
                    result.PosterUrl = $"https://covers.openlibrary.org/b/id/{coverId.GetInt32()}-L.jpg";
                }

                return result;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching OpenLibrary metadata for {Title}", title);
            return null;
        }
    }
}
