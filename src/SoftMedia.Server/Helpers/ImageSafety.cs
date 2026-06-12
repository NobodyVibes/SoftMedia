using SkiaSharp;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Guards against image decode-bombs / pixel-floods (security audit wave-2 H-3).
///
/// SkiaSharp's <c>SKBitmap.Decode</c> allocates the full decoded (typically RGBA, 4 bytes/pixel)
/// buffer BEFORE the caller can inspect the dimensions, so a tiny, highly-compressed file that
/// declares enormous dimensions (e.g. a few-KB 64000×64000 PNG ≈ 16 GB decoded) OOM-kills the
/// host. This helper reads ONLY the header via <see cref="SKCodec"/> and rejects anything whose
/// dimensions exceed a sane budget, so the giant buffer is never allocated. It is the pixel-budget
/// analogue of the wave-1 L4/L5 uncompressed-byte caps on archives/XML.
/// </summary>
public static class ImageSafety
{
    /// Maximum decoded pixel count (~50 MPixel ⇒ ~200 MB at RGBA8888). Comfortably above any
    /// legitimate poster/cover/comic page, far below a decode-bomb.
    public const long MaxPixels = 50_000_000;

    /// Hard per-dimension ceiling (also the common GPU texture limit). A sanity bound on top of
    /// the pixel budget so a 1×1e9 sliver can't slip through.
    public const int MaxDimension = 16384;

    public static bool IsWithinBudget(int width, int height) =>
        width > 0 && height > 0 &&
        width <= MaxDimension && height <= MaxDimension &&
        (long)width * height <= MaxPixels;

    /// Header-only check from a file path. Returns false (reject) when the file is unreadable,
    /// not a decodable image, or exceeds the budget. Never decodes the pixels.
    public static bool IsDecodableWithinBudget(string path) =>
        TryRead(() => SKCodec.Create(path));

    /// Header-only check from already-loaded bytes. Returns false (reject) when the bytes are not
    /// a decodable image or exceed the budget.
    public static bool IsDecodableWithinBudget(SKData data) =>
        TryRead(() => SKCodec.Create(data));

    /// Header-only check from a raw byte buffer (e.g. an extracted comic-archive page).
    public static bool IsDecodableWithinBudget(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        return IsDecodableWithinBudget(data);
    }

    private static bool TryRead(Func<SKCodec?> create)
    {
        try
        {
            using var codec = create();
            if (codec == null) return false;
            var info = codec.Info;
            return IsWithinBudget(info.Width, info.Height);
        }
        catch
        {
            // A header that won't even parse is not something we want to hand to a full decode.
            return false;
        }
    }
}
