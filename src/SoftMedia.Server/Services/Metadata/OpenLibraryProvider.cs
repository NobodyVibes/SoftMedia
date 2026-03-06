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

    public async Task<string?> FetchMetadataAsync(MediaItem item)
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

            // Search API: https://openlibrary.org/search.json?q=the+lord+of+the+rings
            var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(title)}";
            
            var response = await _httpClient.GetStringAsync(url);
            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("docs", out var docs) && docs.GetArrayLength() > 0)
            {
                var book = docs[0];
                var metadata = new Dictionary<string, object>();
                
                if (book.TryGetProperty("title", out var titleProp)) metadata["title"] = titleProp.GetString()!;
                if (book.TryGetProperty("first_publish_year", out var yearProp)) metadata["year"] = yearProp.GetInt32();
                
                if (book.TryGetProperty("author_name", out var authors))
                {
                    metadata["cast"] = authors.EnumerateArray().Select(a => a.GetString()).ToList(); // Map Authors to Cast for now
                    metadata["authors"] = authors.EnumerateArray().Select(a => a.GetString()).ToList();
                }
                
                if (book.TryGetProperty("publisher", out var publishers))
                {
                    metadata["studio"] = publishers[0].GetString()!; // Map Publisher to Studio
                    metadata["publisher"] = publishers[0].GetString()!;
                }
                
                if (book.TryGetProperty("subject", out var subjects))
                {
                    metadata["genres"] = subjects.EnumerateArray().Take(5).Select(s => s.GetString()).ToList();
                }
                
                if (book.TryGetProperty("number_of_pages_median", out var pages)) metadata["pageCount"] = pages.GetInt32();
                
                if (book.TryGetProperty("isbn", out var isbns) && isbns.GetArrayLength() > 0)
                {
                    metadata["isbn"] = isbns[0].GetString()!;
                }
                
                // Cover ID to URL
                if (book.TryGetProperty("cover_i", out var coverId))
                {
                    metadata["poster"] = $"https://covers.openlibrary.org/b/id/{coverId.GetInt32()}-L.jpg";
                }

                return JsonSerializer.Serialize(metadata);
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
