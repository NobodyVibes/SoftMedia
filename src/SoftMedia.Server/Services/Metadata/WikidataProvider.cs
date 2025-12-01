using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class WikidataProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WikidataProvider> _logger;

    public LibraryType SupportedType => LibraryType.Movie;

    public WikidataProvider(HttpClient httpClient, ILogger<WikidataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
    }

    public async Task<string?> FetchMetadataAsync(string title, string path)
    {
        try
        {
            // Simple search query for Wikidata
            // In a real app, we would use SPARQL for precise data
            var url = $"https://www.wikidata.org/w/api.php?action=wbsearchentities&search={Uri.EscapeDataString(title)}&language=en&format=json";
            var response = await _httpClient.GetStringAsync(url);
            
            // For now, just return the raw JSON response as metadata
            // Real implementation would parse this and extract P-codes (P577, P136, etc.)
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching Wikidata for {title}");
            return null;
        }
    }
}
