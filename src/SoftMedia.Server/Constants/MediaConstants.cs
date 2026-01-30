namespace SoftMedia.Server.Constants;

public static class MediaConstants
{
    public static class Containers
    {
        public const string Mp4 = "mp4";
        public const string Mkv = "mkv";
        public const string Avi = "avi";
        public const string Webm = "webm";
    }

    public static class MimeTypes
    {
        public const string VideoMp4 = "video/mp4";
        public const string VideoMatroska = "video/x-matroska";
        public const string VideoWebm = "video/webm";
        
        public const string TextVtt = "text/vtt";
        
        public const string AudioFlac = "audio/flac";
        public const string AudioOpus = "audio/opus";
        public const string AudioMp4 = "audio/mp4"; // m4a
        public const string AudioOgg = "audio/ogg";
        public const string AudioMpeg = "audio/mpeg"; // mp3
        public const string AudioWav = "audio/wav";
        public const string AudioAac = "audio/aac";
        public const string AudioWebm = "audio/webm"; // weba

        public const string AppPdf = "application/pdf";
        public const string AppEpub = "application/epub+zip";
        public const string AppCbz = "application/vnd.comicbook+zip";
        public const string AppCbr = "application/vnd.comicbook-rar";
    }

    public static class Routes
    {
        public const string ImageProxy = "/api/v1/image/proxy";
    }
}
