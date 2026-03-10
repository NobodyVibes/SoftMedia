namespace SoftMedia.Server.Constants;

/// <summary>
/// Centralized media file extension lists. Used by scanners and the file watcher
/// to determine which files are considered media files.
/// </summary>
public static class MediaExtensions
{
    public static readonly string[] Video = { "mkv", "mp4", "avi", "m4v", "wmv", "mov", "webm", "ts", "m2ts", "mpg", "mpeg", "flv" };
    public static readonly string[] Audio = { "flac", "mp3", "m4a", "ogg", "opus", "wma", "wav", "aac", "aif", "aiff", "alac", "ape", "wv", "weba" };
    public static readonly string[] Book = { "pdf", "epub", "cbz", "cbr", "mobi", "azw", "azw3", "djvu", "fb2" };
    public static readonly string[] Game = { "exe", "msi", "nes", "snes", "smc", "sfc", "gba", "gbc", "gb", "nds", "3ds", "n64", "z64", "gcm", "iso", "bin", "cue", "img", "nsp", "xci", "wbfs", "wad", "vpk", "pbp", "xbe", "xex" };
    public static readonly string[] Photo = { "jpg", "jpeg", "png", "webp", "heic", "bmp", "gif", "tiff" };

    /// <summary>
    /// Union of all media extension arrays. Used by LibraryWatcher.IsMediaFile()
    /// to ensure parity with all scanners.
    /// </summary>
    public static readonly string[] All = Video
        .Concat(Audio)
        .Concat(Book)
        .Concat(Game)
        .Concat(Photo)
        .ToArray();
}

