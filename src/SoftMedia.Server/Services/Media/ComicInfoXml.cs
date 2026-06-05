namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Typed representation of an Anansi-project <c>ComicInfo.xml</c> (v2.1 schema).
/// All fields are optional — missing values must never throw. Spec:
/// https://anansi-project.github.io/docs/comicinfo/documentation
/// </summary>
public class ComicInfoXml
{
    // Identity
    public string? Title { get; set; }         // Title of the issue (e.g. "The Beginning")
    public string? Series { get; set; }        // Series/collection name (e.g. "Amazing-Man Comics")
    public string? Number { get; set; }        // Issue number (string to allow "005", "1.5", "½", etc.)
    public int? Count { get; set; }            // Total issues in the series (when known)
    public int? Volume { get; set; }           // Volume number (when a series has multiple volumes)
    public string? AlternateSeries { get; set; }
    public string? AlternateNumber { get; set; }

    // Dates
    public int? Year { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }

    // Content
    public string? Summary { get; set; }
    public string? Notes { get; set; }
    public string? Genre { get; set; }         // Comma-separated per spec
    public string? Tags { get; set; }          // Comma-separated per spec
    public string? Web { get; set; }           // Reference URL (space-separated URLs per spec)
    public string? LanguageISO { get; set; }

    // Credits (comma-separated names per spec)
    public string? Writer { get; set; }
    public string? Penciller { get; set; }
    public string? Inker { get; set; }
    public string? Colorist { get; set; }
    public string? Letterer { get; set; }
    public string? CoverArtist { get; set; }
    public string? Editor { get; set; }
    public string? Translator { get; set; }
    public string? Publisher { get; set; }
    public string? Imprint { get; set; }

    // Technical
    public int? PageCount { get; set; }
    public string? Format { get; set; }         // e.g. "Annual", "One-Shot", "Trade Paperback"
    public string? AgeRating { get; set; }      // e.g. "Everyone", "Teen"
}
