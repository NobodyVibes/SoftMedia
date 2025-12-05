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

    public async Task<string?> FetchMetadataAsync(string title, string path)
    {
        try
        {
            // Fetch show with cast and episodes
            var url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(title)}&embed[]=cast";
            _logger.LogInformation($"Fetching TVMaze for {title}: {url}");
            string response;
            try 
            {
                response = await _httpClient.GetStringAsync(url);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Fallback: If title ends with year (e.g. "Fallout 2024"), strip it and retry
                var match = System.Text.RegularExpressions.Regex.Match(title, @"^(.*?) \d{4}$");
                if (match.Success)
                {
                    var newTitle = match.Groups[1].Value;
                    _logger.LogInformation($"TVMaze 404 for '{title}'. Retrying with '{newTitle}'");
                    url = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(newTitle)}&embed[]=cast";
                    response = await _httpClient.GetStringAsync(url);
                }
                else
                {
                    throw;
                }
            }
            
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            var metadata = new Dictionary<string, object>();
            
            if (root.TryGetProperty("premiered", out var premieredVal))
            {
               var dateStr = premieredVal.GetString();
               if (!string.IsNullOrEmpty(dateStr))
               {
                   metadata["premiered"] = dateStr; // Store raw string for display
                   if (DateTime.TryParse(dateStr, out var date))
                   {
                       metadata["year"] = date.Year;
                       metadata["releaseDate"] = date.ToString("yyyy-MM-dd");
                   }
               }
            }

            if (root.TryGetProperty("status", out var statusVal))
            {
                var status = statusVal.GetString();
                if (!string.IsNullOrEmpty(status)) metadata["status"] = status;
            }

            if (root.TryGetProperty("summary", out var summary))
            {
                // Strip HTML tags
                var summaryText = System.Text.RegularExpressions.Regex.Replace(summary.GetString() ?? "", "<.*?>", "");
                metadata["description"] = summaryText;
            }
            
            if (root.TryGetProperty("rating", out var ratingObj) && ratingObj.TryGetProperty("average", out var avg))
            {
                if (avg.ValueKind != System.Text.Json.JsonValueKind.Null)
                    metadata["rating"] = avg.GetDouble();
            }
            
            if (root.TryGetProperty("genres", out var genresArray))
            {
                metadata["genres"] = genresArray.EnumerateArray().Select(g => g.GetString()).ToList();
            }

            if (root.TryGetProperty("image", out var imageObj) && imageObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (imageObj.TryGetProperty("original", out var original))
                {
                    var poster = original.GetString();
                    if (poster != null) metadata["poster"] = poster;
                }
            }

            if (root.TryGetProperty("network", out var networkObj) && networkObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (networkObj.TryGetProperty("name", out var netName))
                {
                    var studio = netName.GetString();
                    if (studio != null) 
                    {
                        metadata["studio"] = studio;
                        metadata["network"] = studio;
                    }
                }
            }
            else if (root.TryGetProperty("webChannel", out var webChannelObj) && webChannelObj.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (webChannelObj.TryGetProperty("name", out var webName))
                {
                    var channel = webName.GetString();
                    if (channel != null)
                    {
                        metadata["studio"] = channel;
                        metadata["network"] = channel;
                    }
                }
            }

            if (root.TryGetProperty("_embedded", out var embedded) && embedded.TryGetProperty("cast", out var castArray))
            {
                var castList = new List<object>();
                foreach (var castMember in castArray.EnumerateArray())
                {
                    if (castMember.TryGetProperty("person", out var person) && person.TryGetProperty("name", out var name))
                    {
                        var characterName = "Unknown";
                        if (castMember.TryGetProperty("character", out var character) && character.TryGetProperty("name", out var charName))
                        {
                            characterName = charName.GetString();
                        }

                        castList.Add(new { name = name.GetString(), character = characterName });
                    }
                }
                metadata["cast"] = castList.Take(10).ToList(); // Top 10
            }

            return System.Text.Json.JsonSerializer.Serialize(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching TVMaze for {title}");
            var errorData = new Dictionary<string, string> { { "error", ex.Message }, { "stack", ex.StackTrace ?? "" } };
            return System.Text.Json.JsonSerializer.Serialize(errorData);
        }
    }
}
