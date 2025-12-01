using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class OpenLibraryProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenLibraryProvider> _logger;

    // Open Library limit: < 100 requests / 5 minutes
    // ~1 request every 3 seconds to be safe.
    private static readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public LibraryType SupportedType => LibraryType.Book;

    public OpenLibraryProvider(HttpClient httpClient, ILogger<OpenLibraryProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<string?> FetchMetadataAsync(string title, string path)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            var timeSinceLastRequest = DateTimeOffset.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest.TotalSeconds < 3.5) // Conservative 3.5s delay
            {
                var delay = TimeSpan.FromSeconds(3.5) - timeSinceLastRequest;
                await Task.Delay(delay);
            }

            try
            {
                // Search API: https://openlibrary.org/search.json?q=the+lord+of+the+rings
                var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(title)}";
                
                var response = await _httpClient.GetStringAsync(url);
                _lastRequestTime = DateTimeOffset.UtcNow;
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching OpenLibrary metadata for {title}");
                return null;
            }
        }
        finally
        {
            _rateLimitLock.Release();
        }
    }
}
