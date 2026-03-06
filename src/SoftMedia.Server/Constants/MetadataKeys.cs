namespace SoftMedia.Server.Constants;

/// <summary>
/// Centralized metadata JSON key constants. Eliminates magic strings across
/// scanners, providers, aggregators, and image pipeline services.
/// </summary>
public static class MetadataKeys
{
    // Images
    public const string Poster = "poster";
    public const string PosterSourceUrl = "posterSourceUrl";
    public const string Backdrop = "backdrop";
    public const string Still = "still";
    public const string Image = "image";

    // Core fields
    public const string Title = "title";
    public const string Year = "year";
    public const string Description = "description";
    public const string Summary = "summary";
    public const string Rating = "rating";
    public const string ImdbRating = "imdbRating";
    public const string ContentRating = "contentRating";

    // People & Credits
    public const string Cast = "cast";
    public const string Director = "director";
    public const string Studio = "studio";
    public const string Genres = "genres";

    // External IDs
    public const string ImdbId = "imdbId";
    public const string TvMazeId = "tvmazeId";
    public const string MusicBrainzId = "musicBrainzId";

    // TV structure
    public const string Seasons = "seasons";
    public const string Episodes = "episodes";
    public const string Number = "number";
    public const string Season = "season";
    public const string Episode = "episode";

    // Music
    public const string Artist = "artist";
    public const string Album = "album";
    public const string TrackTitle = "trackTitle";
    public const string HasEmbeddedArt = "hasEmbeddedArt";

    // Cast member fields
    public const string Id = "id";
    public const string Character = "character";
    public const string Name = "name";
}
