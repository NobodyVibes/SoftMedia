using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// The normaliser is the single gate every ISBN passes through, from both the EPUB OPF
/// reader and the metadata provider. If the two paths ever disagreed on the canonical form,
/// MetadataAggregator's "the file's ISBN wins" rule would be comparing different strings for
/// the same book — and the detail page would show whichever arrived first.
/// </summary>
public class IsbnNormalizerTests
{
    [Theory]
    [InlineData("9780385121675", "9780385121675")]
    [InlineData("978-0-385-12167-5", "9780385121675")]
    [InlineData("978 0 385 12167 5", "9780385121675")]
    [InlineData("urn:isbn:9780385121675", "9780385121675")]
    [InlineData("ISBN 0316769487", "0316769487")]
    [InlineData("0-8044-2957-X", "080442957X")]
    [InlineData("080442957x", "080442957X")]
    public void Normalize_ReducesRealWorldFormsToDigits(string raw, string expected)
    {
        Assert.Equal(expected, IsbnNormalizer.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]                                     // too short
    [InlineData("12345678901234567890")]                      // too long
    [InlineData("urn:uuid:8e2f1a3c-0000-4a1b-9c2d-3e4f5a6b7c8d")] // EPUB's other identifier
    [InlineData("X9780385121675")]                            // 'X' outside the check position
    public void Normalize_RejectsAnythingThatIsNotAnIsbn(string? raw)
    {
        Assert.Null(IsbnNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_KeepsLengthCorrectButMistypedIsbns()
    {
        // Check digits are deliberately not validated: catalogues carry a small number of
        // mistyped-but-right-length ISBNs, and dropping them loses more real data than it
        // filters. This asserts that trade-off is intentional rather than an oversight.
        Assert.Equal("9780385121670", IsbnNormalizer.Normalize("978-0-385-12167-0"));
    }

    [Fact]
    public void LooksLikeIsbn_AgreesWithNormalize()
    {
        Assert.True(IsbnNormalizer.LooksLikeIsbn("978-0-385-12167-5"));
        Assert.False(IsbnNormalizer.LooksLikeIsbn("not an isbn"));
    }
}
