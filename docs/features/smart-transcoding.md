# Smart Transcoding

SoftMedia automatically converts video files that your browser can't play directly (like MKV or HEVC content) into a compatible format on-the-fly.

## How It Works

When you play a video that requires transcoding, SoftMedia streams the converted content to your browser in small segments. To save system resources, SoftMedia intelligently **pauses** and **resumes** the transcoding process based on how far ahead it has buffered.

### Buffer-Based Throttling

| Your Buffer | What SoftMedia Does |
|:------------|:--------------------|
| **Building up** | Transcodes at full speed until ~2 minutes of buffer |
| **Buffer full** (≥2 min) | **Pauses** transcoding to save CPU |
| **Buffer running low** (≤1 min) | **Resumes** transcoding automatically |

Think of it like filling a glass of water: SoftMedia fills it quickly at first, then stops the tap when it's full enough, and turns it back on only when you've drunk some.

### When You Pause Playback

When you pause a video, SoftMedia will finish buffering to ~2 minutes ahead, then stop transcoding entirely. Your CPU gets a break while you're away.

### Seamless Resumption

When you resume, or when buffer runs low during normal playback, transcoding picks up **exactly where it left off** — no gaps, no repeated frames, just smooth video.

## Benefits

- **Lower CPU usage** — transcoding pauses when buffer is full
- **No wasted work** — stops when you pause or navigate away
- **Smooth playback** — automatic resumption prevents buffering
- **Works across platforms** — Windows, Linux, and macOS
