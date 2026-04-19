namespace SoftMedia.Server.Services.Abstractions;

/// <summary>
/// Publisher-embedded metadata pulled from a book file itself (EPUB OPF package,
/// PDF Info dictionary). Always preferred over filename-derived metadata when
/// the file actually supplies usable fields — it's set by publishers/authors
/// rather than inferred from whatever the file was saved as.
/// </summary>
public sealed record BookFileMetadata(
    string? Title,
    string? Author,
    int? Year,
    string? Publisher,
    string? Description,
    string? Isbn,
    string? Language
)
{
    /// <summary>
    /// True when at least a title or author was extracted — if both are missing
    /// the caller should fall back to filename parsing.
    /// </summary>
    public bool HasUsableData =>
        !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Author);
}

public interface IBookMetadataExtractor
{
    /// <summary>
    /// Reads embedded metadata from the book file at <paramref name="filePath"/>.
    /// Returns <c>null</c> when the format is unsupported or the file is unreadable.
    /// Individual fields may be null when absent from the source document.
    /// </summary>
    Task<BookFileMetadata?> ExtractAsync(string filePath, CancellationToken cancellationToken = default);
}
