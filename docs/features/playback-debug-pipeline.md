# Playback Debug Pipeline

The Playback Debug Pipeline is a diagnostic tool that shows you exactly how SoftMedia decided to stream your video.

## How to Access

While watching a video, press the **D** key on your keyboard to open the debug panel. Press **D** or **Escape** to close it.

## What You'll See

The debug panel shows the complete decision-making process in five stages:

### 1. Client Sent
What your browser told the server it can support:
- Video codecs (H.264, HEVC, AV1)
- Audio codecs (AAC, AC3, etc.)
- HDR support
- Maximum audio channels
- Subtitle formats (WebVTT)

### 2. Server Settings
Your configured limits and preferences:
- Output video codec
- Maximum resolution
- HDR preservation setting
- Hardware acceleration status
- Audio channel downmixing

### 3. Source File
Information detected from the original video file:
- Video codec
- Audio codec
- Resolution
- Container format
- Duration

### 4. Backend Decision
What the server ultimately decided to do:
- Target codec for transcoding
- Target resolution
- Whether to tonemap HDR to SDR
- Selected subtitle track and language
- Whether subtitles are burned-in (bitmap) or separate (text)

### 5. Actual Output
Live data from the transcoded segment file:
- Actual video codec
- Pixel format and color space
- HDR metadata presence
- Audio codec and channel count

## Export Debug Data

Click the **Export** button to copy all debug information to your clipboard in JSON format. This is helpful when:
- Troubleshooting playback issues
- Sharing diagnostics with support
- Understanding why a video transcoded instead of direct playing

> **Note:** File paths are hidden for standard users and only shown to Admin accounts for security.

## When to Use This

- **Unexpected transcoding** - Check why Direct Play wasn't used
- **Quality issues** - Verify the output resolution and codec match your expectations
- **HDR problems** - Confirm HDR is being preserved or tonemapped correctly
- **Subtitle debugging** - See which subtitle track is selected and whether it's burned-in
- **Performance troubleshooting** - Understand which codecs and settings are active
