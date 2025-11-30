using Microsoft.AspNetCore.StaticFiles;

namespace SoftMedia.Server.Helpers;

public static class MimeTypeResolver
{
    private static readonly FileExtensionContentTypeProvider _provider = new();

    static MimeTypeResolver()
    {
        // Add mappings for common media types if missing
        if (!_provider.Mappings.ContainsKey(".mkv"))
        {
            _provider.Mappings.Add(".mkv", "video/x-matroska");
        }
        if (!_provider.Mappings.ContainsKey(".vtt"))
        {
            _provider.Mappings.Add(".vtt", "text/vtt");
        }
        // Audio formats
        if (!_provider.Mappings.ContainsKey(".flac"))
        {
            _provider.Mappings.Add(".flac", "audio/flac");
        }
        if (!_provider.Mappings.ContainsKey(".opus"))
        {
            _provider.Mappings.Add(".opus", "audio/opus");
        }
        if (!_provider.Mappings.ContainsKey(".m4a"))
        {
            _provider.Mappings.Add(".m4a", "audio/mp4");
        }
        if (!_provider.Mappings.ContainsKey(".ogg"))
        {
            _provider.Mappings.Add(".ogg", "audio/ogg");
        }
        if (!_provider.Mappings.ContainsKey(".mp3"))
        {
            _provider.Mappings.Add(".mp3", "audio/mpeg");
        }
        if (!_provider.Mappings.ContainsKey(".wav"))
        {
            _provider.Mappings.Add(".wav", "audio/wav");
        }
        if (!_provider.Mappings.ContainsKey(".aac"))
        {
            _provider.Mappings.Add(".aac", "audio/aac");
        }
        if (!_provider.Mappings.ContainsKey(".weba"))
        {
            _provider.Mappings.Add(".weba", "audio/webm");
        }
        // Book formats
        if (!_provider.Mappings.ContainsKey(".pdf"))
        {
            _provider.Mappings.Add(".pdf", "application/pdf");
        }
        if (!_provider.Mappings.ContainsKey(".epub"))
        {
            _provider.Mappings.Add(".epub", "application/epub+zip");
        }
        if (!_provider.Mappings.ContainsKey(".cbz"))
        {
            _provider.Mappings.Add(".cbz", "application/vnd.comicbook+zip");
        }
        if (!_provider.Mappings.ContainsKey(".cbr"))
        {
            _provider.Mappings.Add(".cbr", "application/vnd.comicbook-rar");
        }
    }

    public static string GetMimeType(string fileName)
    {
        if (_provider.TryGetContentType(fileName, out var contentType))
        {
            return contentType;
        }
        return "application/octet-stream";
    }
}
