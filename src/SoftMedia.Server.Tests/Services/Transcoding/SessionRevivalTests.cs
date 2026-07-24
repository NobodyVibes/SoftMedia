using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Services.Transcoding;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Transcoding;

/// <summary>
/// SR-WI-020 — session revival building blocks: playlist-fact parsing (segment count /
/// duration / ENDLIST) and the resume-args rebase that makes a revived ffmpeg APPEND to
/// the existing playlist instead of restarting it.
/// </summary>
public class SessionRevivalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"revival-{Guid.NewGuid():N}");

    public SessionRevivalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WritePlaylist(string content) =>
        File.WriteAllText(Path.Combine(_dir, "master.m3u8"), content);

    private HlsService CreateHls() => new(NullLogger<HlsService>.Instance);

    // ---- GetPlaylistInfo -------------------------------------------------------------

    [Fact]
    public void GetPlaylistInfo_ParsesSegments_Duration_AndNoEndlist()
    {
        WritePlaylist("#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:6.000,\nseg_000.ts\n#EXTINF:5.500,\nseg_001.ts\n");

        var info = CreateHls().GetPlaylistInfo(_dir);

        Assert.NotNull(info);
        Assert.Equal(2, info!.SegmentCount);
        Assert.Equal(11.5, info.TotalDurationSeconds, 3);
        Assert.False(info.HasEndList);
    }

    [Fact]
    public void GetPlaylistInfo_DetectsEndlist()
    {
        WritePlaylist("#EXTM3U\n#EXTINF:6.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");

        var info = CreateHls().GetPlaylistInfo(_dir);

        Assert.NotNull(info);
        Assert.True(info!.HasEndList);
        Assert.Equal(1, info.SegmentCount);
    }

    [Fact]
    public void GetPlaylistInfo_MissingPlaylist_ReturnsNull()
    {
        Assert.Null(CreateHls().GetPlaylistInfo(_dir));
    }

    // ---- ApplyResumeArgs -------------------------------------------------------------

    [Fact]
    public void ApplyResumeArgs_RebasesStartNumber_AndAddsAppendList()
    {
        var args = "-ss 30 -i \"in.mkv\" -f hls -hls_time 6 -hls_list_size 0 -hls_playlist_type event " +
                   "-start_number 0 -hls_segment_filename \"seg_%03d.ts\" \"master.m3u8\"";

        var patched = TranscodeService.ApplyResumeArgs(args, 42);

        Assert.NotNull(patched);
        Assert.Contains("-start_number 42 ", patched);
        Assert.DoesNotContain("-start_number 0 ", patched);
        Assert.Contains("append_list", patched);
    }

    [Fact]
    public void ApplyResumeArgs_ExtendsExistingHlsFlags_InsteadOfDuplicating()
    {
        var args = "-i \"in.mkv\" -f hls -hls_flags independent_segments " +
                   "-start_number 0 -hls_segment_filename \"seg_%03d.m4s\" \"master.m3u8\"";

        var patched = TranscodeService.ApplyResumeArgs(args, 7);

        Assert.NotNull(patched);
        Assert.Contains("-hls_flags independent_segments+append_list", patched);
        Assert.Contains("-start_number 7 ", patched);
        // exactly one -hls_flags option
        Assert.Equal(1, patched!.Split("-hls_flags").Length - 1);
    }

    [Fact]
    public void ApplyResumeArgs_AlreadyAppendList_LeavesFlagsAlone()
    {
        var args = "-i \"in.mkv\" -f hls -hls_flags append_list " +
                   "-start_number 0 -hls_segment_filename \"seg_%03d.ts\" \"master.m3u8\"";

        var patched = TranscodeService.ApplyResumeArgs(args, 3);

        Assert.NotNull(patched);
        Assert.Contains("-start_number 3 ", patched);
        Assert.Equal(1, patched!.Split("append_list").Length - 1);
    }

    [Fact]
    public void ApplyResumeArgs_WithoutFreshToken_ReturnsNull_SoCallerFallsBackToFullRestart()
    {
        Assert.Null(TranscodeService.ApplyResumeArgs("-i \"in.mkv\" -f hls \"master.m3u8\"", 5));
    }
}
