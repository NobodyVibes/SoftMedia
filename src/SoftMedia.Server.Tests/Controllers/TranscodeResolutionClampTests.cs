using SoftMedia.Server.Controllers;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// B-02 — the server-wide resolution clamp on the fabricated-sid master.m3u8 path
/// compares quality labels via ResolutionRank; the ordering IS the security rule.
public class TranscodeResolutionClampTests
{
    [Theory]
    [InlineData("480p", 480)]
    [InlineData("720p", 720)]
    [InlineData("1080p", 1080)]
    [InlineData("1440p", 1440)]
    [InlineData("4k", 2160)]
    [InlineData("2160p", 2160)]
    [InlineData("8k", 4320)]
    [InlineData("4320p", 4320)]
    public void KnownLabels_RankByHeight(string label, int expected)
        => Assert.Equal(expected, TranscodeController.ResolutionRank(label));

    [Theory]
    [InlineData(null)]
    [InlineData("original")]
    [InlineData("ORIGINAL")]
    [InlineData("weird-value")]
    public void NullOriginalAndUnknown_RankAsUncapped_SoTheServerMaxAlwaysWins(string? label)
    {
        // A null/original/unknown REQUEST must rank above every concrete server max —
        // i.e., it gets clamped down to the setting rather than sailing through.
        Assert.True(TranscodeController.ResolutionRank(label) > TranscodeController.ResolutionRank("8k"));
    }

    [Fact]
    public void RequestAboveServerMax_OutranksIt_RequestBelowDoesNot()
    {
        Assert.True(TranscodeController.ResolutionRank("4k") > TranscodeController.ResolutionRank("720p"));
        Assert.False(TranscodeController.ResolutionRank("480p") > TranscodeController.ResolutionRank("720p"));
    }
}
