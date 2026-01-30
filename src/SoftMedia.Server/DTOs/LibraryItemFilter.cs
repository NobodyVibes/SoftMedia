namespace SoftMedia.Server.DTOs;

public class LibraryItemFilter
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public int? MinRating { get; set; }
    public bool? IsFavorite { get; set; }
    public bool? Watched { get; set; }
    public string? ViewMode { get; set; }
    public Guid? UserId { get; set; }
}
