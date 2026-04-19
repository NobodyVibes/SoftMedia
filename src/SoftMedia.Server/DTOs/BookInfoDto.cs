namespace SoftMedia.Server.DTOs;

/// <summary>
/// Metadata required by the eReader frontend to render a book.
/// Format is one of: "pdf", "epub", "cbz", "cbr" (cbr not yet supported server-side).
/// PageCount is present for CBZ (counted from archive) and for any book whose scanner
/// populated MetadataJson.pageCount (e.g. OpenLibrary-enriched items).
/// </summary>
public class BookInfoDto
{
    public Guid Id { get; set; }
    public string Format { get; set; } = string.Empty;
    public int? PageCount { get; set; }
}
