# Smart Transcoding

SoftMedia automatically transcodes video files that your browser can't play directly (like MKV or HEVC/AV1 content) into a compatible format on-the-fly.

## How It Works

When you play a video that requires transcoding, SoftMedia intelligently manages the transcoding speed to balance **responsiveness** with **system resource usage**.

### Adaptive Speed Control

| Buffer Status | Transcoding Speed | What's Happening |
|:---|:---|:---|
| **< 30 seconds** | Maximum | Rapidly building buffer for smooth playback |
| **30 - 90 seconds** | 2x playback speed | Steadily growing buffer |
| **90 - 120 seconds** | 2x playback speed | Approaching target buffer |
| **≥ 120 seconds** | 1x playback speed | Cruising - minimal CPU usage |

### When You Pause

- If buffer ≥ 120 seconds: Transcoding stops completely while paused
- If buffer < 120 seconds: Transcoding continues until buffer reaches 120 seconds, then stops

This means pausing a video also pauses the transcoding work, saving CPU when you're not watching.

### When You Resume

Resume behavior depends on your buffer:
- **< 30 seconds buffer**: Maximum speed transcoding resumes
- **30 - 119 seconds buffer**: 2x speed transcoding resumes  
- **≥ 120 seconds buffer**: 1x speed transcoding (or no restart needed)

## Benefits

- **Lower CPU usage** during steady playback (1x speed instead of max)
- **No wasted work** when you pause or stop watching
- **Instant responsiveness** when buffer is low
- **Automatic adaptation** to your viewing behavior
