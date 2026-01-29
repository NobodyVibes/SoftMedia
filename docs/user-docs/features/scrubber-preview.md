# Scrubber Preview Thumbnails

## Overview

When dragging the video progress bar scrubber, a live thumbnail preview shows the frame at the current position. This helps users quickly navigate to specific scenes without guessing.

## How It Works

1. **Drag to Preview** - Click and drag the scrubber ball on the progress bar
2. **Live Thumbnails** - A thumbnail appears above the scrubber showing the frame at that timestamp
3. **Pause During Drag** - Video pauses automatically while dragging
4. **Seek on Release** - When you release, the video seeks to that position and resumes playback

## Technical Details

- **Backend**: `GET /api/transcode/{id}/frame?time={seconds}` extracts JPEG frames via FFmpeg
- **Frame Caching**: Frames cached server-side for 30 seconds (1-second granularity)
- **Debouncing**: 100ms frontend debounce prevents excessive API calls
- **Timeout**: 5-second FFmpeg timeout with automatic process cleanup

## User Experience

- Scrubber ball follows mouse position during drag
- Thumbnail updates as you drag across the timeline
- Chapter title and timestamp displayed below the thumbnail
- Smooth transitions between frames (no loading flicker)
