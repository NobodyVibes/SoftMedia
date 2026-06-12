using SkiaSharp;
using SoftMedia.Server.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Helpers;

/// Audit wave-2 H-3 — image decode-bomb (pixel-flood) guard. Verifies the header-only budget
/// check accepts legitimate images and rejects oversized/garbage input before a full decode.
public class ImageSafetyTests
{
    [Theory]
    [InlineData(1000, 1500, true)]    // a normal poster
    [InlineData(16384, 3000, true)]   // 49.1 MP — just under the budget and dimension cap
    [InlineData(64000, 64000, false)] // the classic 4-gigapixel bomb
    [InlineData(16384, 16384, false)] // 268 MP — within dim cap but over the pixel budget
    [InlineData(20000, 1, false)]     // exceeds the per-dimension hard cap
    [InlineData(0, 100, false)]       // degenerate
    [InlineData(-1, 100, false)]      // degenerate
    public void IsWithinBudget_EnforcesPixelAndDimensionCaps(int w, int h, bool expected)
    {
        Assert.Equal(expected, ImageSafety.IsWithinBudget(w, h));
    }

    [Fact]
    public void IsDecodableWithinBudget_AcceptsARealSmallImage()
    {
        using var bmp = new SKBitmap(200, 200);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);

        Assert.True(ImageSafety.IsDecodableWithinBudget(data.ToArray()));
    }

    [Fact]
    public void IsDecodableWithinBudget_RejectsGarbageAndEmpty()
    {
        Assert.False(ImageSafety.IsDecodableWithinBudget(new byte[] { 1, 2, 3, 4 }));
        Assert.False(ImageSafety.IsDecodableWithinBudget(System.Array.Empty<byte>()));
    }
}
