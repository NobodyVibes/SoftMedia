namespace SoftMedia.Server.DTOs;

public class LibraryItemFilter
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Search { get; set; }
    public string? SortBy { get; set; }

    /// <summary>
    /// "asc" | "desc". Null keeps each sort key's natural direction (title A-Z,
    /// dates/counts/ratings newest-highest-first), so existing callers are unaffected.
    /// See <see cref="Helpers.SortDirection"/>.
    /// </summary>
    public string? SortDir { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public int? MinRating { get; set; }
    public bool? IsFavorite { get; set; }
    public bool? Watched { get; set; }
    public string? ViewMode { get; set; }
    public Guid? UserId { get; set; }
}
