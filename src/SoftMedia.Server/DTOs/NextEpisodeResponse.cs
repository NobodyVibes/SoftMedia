namespace SoftMedia.Server.DTOs;

public class NextEpisodeResponse
{
    public Guid EpisodeId { get; set; }
    public Guid SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public double ResumePosition { get; set; }
    public bool IsSeriesComplete { get; set; }
    
    /// <summary>Poster image URL for the next episode</summary>
    public string? PosterPath { get; set; }
    
    /// <summary>Backdrop image URL for the next episode</summary>
    public string? BackdropPath { get; set; }
    
    // Debug fields
    public double DebugDuration { get; set; }
    public double DebugThreshold { get; set; }
    public bool DebugIsComplete { get; set; }
}
