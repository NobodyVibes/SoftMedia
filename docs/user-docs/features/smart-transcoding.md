# Smart Transcoding

SoftMedia automatically chooses the best playback method for your videos based on what your browser supports and your server settings.

## How It Works

When you start playing a video, SoftMedia analyzes three things:

1. **Your Browser Capabilities** - What video codecs, audio formats, and features your browser supports
2. **Server Settings** - Quality limits and codec preferences you've configured
3. **Source File** - The video file's codec, resolution, HDR status, and audio format

Based on this analysis, SoftMedia picks one of three streaming modes:

### 1. Direct Play (Best)
The video file is sent directly to your browser without any processing. This is the most efficient option and preserves the original quality.

**Used when:** Your browser can play the video codec, audio codec, and container format natively.

### 2. Remux (Good)
The video and audio streams are copied into a new container (HLS) without re-encoding. This is nearly as efficient as Direct Play.

**Used when:** Your browser supports the video/audio codecs but not the container format (e.g., MKV).

### 3. Transcode (Fallback)
The video is re-encoded on-the-fly to a format your browser supports. This uses server CPU/GPU but ensures compatibility.

**Used when:** Your browser doesn't support the source video codec, audio codec, or the resolution exceeds your quality settings.

## Settings That Affect Decisions

- **Default Streaming Quality** - Limits the maximum resolution (e.g., 1080p, 720p)
- **Output Video Codec** - Choose H.264, HEVC, or AV1 for transcoding
- **Preserve HDR** - Keep HDR color when your display supports it
- **Force Direct Play When Possible** - Prefer Direct Play over transcoding

## Why This Matters

- **Direct Play** = Zero processing, instant playback, original quality
- **Remux** = Minimal processing, fast startup, original quality
- **Transcode** = Maximum compatibility, adjustable quality, uses server resources

SoftMedia always tries to use the most efficient method for your setup.
