using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public class TVMazeProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TVMazeProvider> _logger;

    public LibraryType SupportedType => LibraryType.TV;

    public TVMazeProvider(HttpClient httpClient, ILogger<TVMazeProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string?> FetchMetadataAsync(string title)
    {
        try
        {
            var url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(title)}";
            var response = await _httpClient.GetStringAsync(url);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching TVMaze for {title}");
            return null;
        }
    }
}
