namespace SoftMedia.Server.DTOs;

/// <summary>
/// Metadata required by the eReader frontend to render a book.
/// Format is one of: "pdf", "epub", "cbz", "cbr" (cbr not yet supported server-side).
/// <para>
/// PageCount here is strictly the PHYSICAL page count of the file, counted from the comic
/// archive at request time; it drives page navigation, so it is never sourced from the
/// catalogue. The display figure shown on the detail page is a separate value
/// (<see cref="MediaItemDto.PageCount"/>) that may come from a metadata provider's print
/// edition — feeding that into the reader would desynchronise its pager. PDFs report null
/// and let pdf.js count their own pages client-side.
/// </para>
/// </summary>
public class BookInfoDto
{
    public Guid Id { get; set; }
    public string Format { get; set; } = string.Empty;
    public int? PageCount { get; set; }
}
