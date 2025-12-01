using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class GameMetadataProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GameMetadataProvider> _logger;

    public LibraryType SupportedType => LibraryType.Game;

    public GameMetadataProvider(HttpClient httpClient, ILogger<GameMetadataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<string?> FetchMetadataAsync(string title, string path)
    {
        try
        {
            // Search Wikidata for entities that are instances of "video game" (Q7889)
            // Note: This is a simple search. A real SPARQL query would be better but complex to implement in a single HTTP GET without a query builder.
            // For now, we use the wbsearchentities API which is "smart" enough.
            var url = $"https://www.wikidata.org/w/api.php?action=wbsearchentities&search={Uri.EscapeDataString(title)}&language=en&format=json";
            
            var response = await _httpClient.GetStringAsync(url);
            
            // In a real implementation, we would parse the JSON, look for the 'id' (Q-code),
            // and then perform a second request to wbgetentities to get properties like P400 (Platform), P178 (Developer).
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching Game metadata for {title}");
            return null;
        }
    }
}
