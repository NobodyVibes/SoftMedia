using System.ComponentModel.DataAnnotations;

namespace SoftMedia.Server.Models;

/// <summary>
/// Stores user preferences at the TV series level, such as preferred subtitle language.
/// Preferences saved here apply to all episodes within the series.
/// </summary>
public class UserSeriesPreference
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// The SeriesId of the TV show (references MediaItem.Id where Type == Show)
    /// </summary>
    public Guid SeriesId { get; set; }
    public MediaItem Series { get; set; } = null!;

    /// <summary>
    /// Preferred subtitle language code (e.g., "eng", "spa", "jpn").
    /// Null means no subtitle preference / subtitles off.
    /// </summary>
    [MaxLength(10)]
    public string? PreferredSubtitleLanguage { get; set; }
}
