# Music Media Player

The SoftMedia Music Player is a high-performance, gapless audio player designed for audiophiles and casual listeners alike. It integrates seamlessly with your music library, offering both advanced playback controls and smart streaming capabilities.

## High-Level Overview

The player is built on a **local-first** philosophy but includes intelligent backend negotiation to ensure smooth playback across all networks and devices. It supports a wide range of audio formats and automatically adapts to your client's capabilities.

### Smart Streaming: Direct Play vs. Transcoding

When you press play, the server performs a real-time negotiation to determine the best delivery method:

1.  **Format Negotiation**: The server compares the track's authentic audio codec (e.g., FLAC, MP3, Opus) against your browser's supported codecs.
2.  **Bitrate Limits**: It evaluates both server-side limits (e.g., bandwidth caps) and your client-side preferences (e.g., "Limit to 128kbps on cellular").
3.  **Decision**:
    *   **Direct Play**: If your browser supports the source format natively and no bitrate limits are exceeded, the file is streamed directly. This ensures bit-perfect quality with zero server CPU overhead.
    *   **Transcoding**: If the format is unsupported (e.g., playing FLAC on a limited device) or bitrate limits are active, the server transcodes the audio on-the-fly to high-quality **AAC** at the requested bitrate.

## Key Features

### 🎧 Gapless Playback
Enjoy albums as the artist intended. The player employs a dual-audio-engine architecture to preload the next track while the current one is playing.
- **True Overlap**: Transitions can be configured to crossfade (default 100ms smooth overlap) or cut precisely at zero-crossing points.
- **Preloading**: The next track begins buffering 10 seconds before the current track ends, "Ready" indicators show when the next song is primed.

### 📋 Queue Management
Full control over your listening session:
- **Drag & Drop**: Reorder your queue effortlessly. Grab the handle on the left of any track in the queue list to move it.
- **Persistent State**: The queue survives page navigations and even browser refreshes (session storage).
- **History**: Easily navigate back to previously played tracks.

### 🎛️ Advanced Controls
- **Shuffle & Repeat**: Standard Repeat-All, Repeat-One, and smart Shuffle modes.
- **Seek Options**: 30-second skip buttons (`⟲` `⟳`) and keyboard-friendly seeking (`Left`/`Right` arrows).
- **Expandable UI**:
    - **Mini Player**: Always visible at the bottom of the screen for quick access.
    - **Immersive Mode**: Expand to full screen (`Shift+F`) for high-resolution album art and lyrics (coming soon).

### ⌨️ Keyboard Shortcuts
| Key | Action |
| :--- | :--- |
| `Space` | Play / Pause |
| `Arrow Left` / `Right` | Seek -30s / +30s |
| `Ctrl` + `Arrow Left` / `Right` | Previous / Next Track |
| `Shift` + `F` | Toggle Fullscreen Player |
| `Escape` | Collapse Player |

## Mobile Experience
The interface is fully responsive, optimized for touch interaction with large tap targets and swipe-friendly drawers.
