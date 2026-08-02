using SoftMedia.Server.Controllers;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// <summary>
/// QS-WI-005 follow-up — the server-side BlockHdrTranscode backstop on master.m3u8:
/// <see cref="TranscodeController.WouldToneMap"/> is the refusal predicate, delegated to
/// the profile builder's pipeline authority so it can never disagree with the plan's
/// ToneMapPlanned fact. The dialog is UX; this rule is what a rogue client hits.
/// </summary>
public class TranscodeHdrBlockTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sdr_sources_never_tone_map(string? hdrFormat)
        => Assert.False(TranscodeController.WouldToneMap(hdrFormat, preserveHdr: false, codec: "h264", subtitleSelected: false));

    [Theory]
    [InlineData("HDR10")]
    [InlineData("HDR10+")]
    [InlineData("Dolby Vision")]
    [InlineData("HLG")]
    public void Hdr_source_without_passthrough_tone_maps(string hdrFormat)
        => Assert.True(TranscodeController.WouldToneMap(hdrFormat, preserveHdr: false, codec: "hevc", subtitleSelected: false));

    [Fact]
    public void Genuine_hdr_passthrough_is_not_a_conversion_and_stays_allowed()
        => Assert.False(TranscodeController.WouldToneMap("HDR10", preserveHdr: true, codec: "hevc", subtitleSelected: false));

    [Fact]
    public void Subtitle_burn_in_defeats_passthrough_so_it_counts_as_a_conversion()
        => Assert.True(TranscodeController.WouldToneMap("HDR10", preserveHdr: true, codec: "hevc", subtitleSelected: true));

    [Theory]
    [InlineData("h264")]
    [InlineData("auto")]
    [InlineData(null)] // no codec param defaults to h264
    public void Eight_bit_output_defeats_passthrough_so_it_counts_as_a_conversion(string? codec)
        => Assert.True(TranscodeController.WouldToneMap("HDR10", preserveHdr: true, codec: codec, subtitleSelected: false));
}
