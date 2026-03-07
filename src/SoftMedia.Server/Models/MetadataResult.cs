using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftMedia.Server.Models;

/// <summary>
/// Strongly-typed DTO for metadata extraction, replacing raw Dictionary<string, object>.
/// </summary>
public class MetadataResult
{
    // Common
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("year")]
    public int? Year { get; set; }
    
    [JsonPropertyName("releaseDate")]
    public DateTime? ReleaseDate { get; set; }
    
    [JsonPropertyName("contentRating")]
    public string? ContentRating { get; set; }
    
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }
    
    [JsonPropertyName("imdbRating")]
    public double? ImdbRating { get; set; }
    
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }
    
    [JsonPropertyName("tvmazeId")]
    public int? TvMazeId { get; set; }
    
    [JsonPropertyName("musicBrainzId")]
    public string? MusicBrainzId { get; set; }
    
    [JsonPropertyName("poster")]
    public string? PosterUrl { get; set; }
    
    [JsonPropertyName("still")]
    public string? StillUrl { get; set; }
    
    [JsonPropertyName("genres")]
    public List<string>? Genres { get; set; }
    
    [JsonPropertyName("studio")]
    public string? Studio { get; set; }
    
    [JsonPropertyName("director")]
    public string? Director { get; set; }
    
    [JsonPropertyName("cast")]
    public List<CastMember>? Cast { get; set; }
    
    // Music-specific
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }
    
    [JsonPropertyName("album")]
    public string? Album { get; set; }
    
    [JsonPropertyName("trackNumber")]
    public int? TrackNumber { get; set; }
    
    [JsonPropertyName("discNumber")]
    public int? DiscNumber { get; set; }
    
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }
    
    [JsonPropertyName("hasEmbeddedArt")]
    public bool HasEmbeddedArt { get; set; }
    
    // TV-specific
    [JsonPropertyName("seasons")]
    public List<SeasonMetadata>? Seasons { get; set; }
    
    [JsonPropertyName("episodes")]
    public List<EpisodeMetadata>? Episodes { get; set; }
    
    // Game-specific
    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
    
    [JsonPropertyName("gameMode")]
    public string? GameMode { get; set; }
    
    // Book-specific
    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }
    
    [JsonPropertyName("isbn")]
    public string? Isbn { get; set; }
    
    [JsonPropertyName("pageCount")]
    public int? PageCount { get; set; }
    
    // Extensibility & Backward Compatibility — unmapped legacy fields
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class CastMember
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("character")]
    public string? Character { get; set; }
    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}

public class SeasonMetadata
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }
    [JsonPropertyName("number")]
    public int Number { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
    [JsonPropertyName("poster")]
    public string? PosterUrl { get; set; }
    [JsonPropertyName("premiereDate")]
    public DateTime? PremiereDate { get; set; }
    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }
}

public class EpisodeMetadata
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }
    [JsonPropertyName("season")]
    public int SeasonNumber { get; set; }
    [JsonPropertyName("episode")]
    public int EpisodeNumber { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
    [JsonPropertyName("still")]
    public string? StillUrl { get; set; }
    [JsonPropertyName("airdate")]
    public DateTime? AirDate { get; set; }
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }
}
