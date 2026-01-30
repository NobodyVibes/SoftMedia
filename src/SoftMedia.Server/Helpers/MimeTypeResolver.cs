using Microsoft.AspNetCore.StaticFiles;

namespace SoftMedia.Server.Helpers;

using SoftMedia.Server.Constants;

public static class MimeTypeResolver
{
    private static readonly FileExtensionContentTypeProvider _provider = new();

    static MimeTypeResolver()
    {
        // Add mappings for common media types if missing
        if (!_provider.Mappings.ContainsKey(".mkv"))
        {
            _provider.Mappings.Add(".mkv", MediaConstants.MimeTypes.VideoMatroska);
        }
        if (!_provider.Mappings.ContainsKey(".vtt"))
        {
            _provider.Mappings.Add(".vtt", MediaConstants.MimeTypes.TextVtt);
        }
        // Audio formats
        if (!_provider.Mappings.ContainsKey(".flac"))
        {
            _provider.Mappings.Add(".flac", MediaConstants.MimeTypes.AudioFlac);
        }
        if (!_provider.Mappings.ContainsKey(".opus"))
        {
            _provider.Mappings.Add(".opus", MediaConstants.MimeTypes.AudioOpus);
        }
        if (!_provider.Mappings.ContainsKey(".m4a"))
        {
            _provider.Mappings.Add(".m4a", MediaConstants.MimeTypes.AudioMp4);
        }
        if (!_provider.Mappings.ContainsKey(".ogg"))
        {
            _provider.Mappings.Add(".ogg", MediaConstants.MimeTypes.AudioOgg);
        }
        if (!_provider.Mappings.ContainsKey(".mp3"))
        {
            _provider.Mappings.Add(".mp3", MediaConstants.MimeTypes.AudioMpeg);
        }
        if (!_provider.Mappings.ContainsKey(".wav"))
        {
            _provider.Mappings.Add(".wav", MediaConstants.MimeTypes.AudioWav);
        }
        if (!_provider.Mappings.ContainsKey(".aac"))
        {
            _provider.Mappings.Add(".aac", MediaConstants.MimeTypes.AudioAac);
        }
        if (!_provider.Mappings.ContainsKey(".weba"))
        {
            _provider.Mappings.Add(".weba", MediaConstants.MimeTypes.AudioWebm);
        }
        // Book formats
        if (!_provider.Mappings.ContainsKey(".pdf"))
        {
            _provider.Mappings.Add(".pdf", MediaConstants.MimeTypes.AppPdf);
        }
        if (!_provider.Mappings.ContainsKey(".epub"))
        {
            _provider.Mappings.Add(".epub", MediaConstants.MimeTypes.AppEpub);
        }
        if (!_provider.Mappings.ContainsKey(".cbz"))
        {
            _provider.Mappings.Add(".cbz", MediaConstants.MimeTypes.AppCbz);
        }
        if (!_provider.Mappings.ContainsKey(".cbr"))
        {
            _provider.Mappings.Add(".cbr", MediaConstants.MimeTypes.AppCbr);
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
