using SoftMedia.Server.Models;
using System.Text.Json;

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

    public MusicBrainzProvider(HttpClient httpClient, ILogger<MusicBrainzProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        // User-Agent is MANDATORY for MusicBrainz
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<string?> FetchMetadataAsync(string title, string path)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            var timeSinceLastRequest = DateTimeOffset.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest.TotalSeconds < 1.1) // Add a little buffer
            {
                var delay = TimeSpan.FromSeconds(1.1) - timeSinceLastRequest;
                await Task.Delay(delay);
            }

            try
            {
                // Simple search query for MusicBrainz (Release Group or Recording)
                // Searching for "release-group" (Album) by default as it maps best to a folder usually
                var url = $"https://musicbrainz.org/ws/2/release-group?query={Uri.EscapeDataString(title)}&fmt=json";
                
                var response = await _httpClient.GetStringAsync(url);
                _lastRequestTime = DateTimeOffset.UtcNow;
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching MusicBrainz metadata for {title}");
                return null;
            }
        }
        finally
        {
            _rateLimitLock.Release();
        }
    }
}
