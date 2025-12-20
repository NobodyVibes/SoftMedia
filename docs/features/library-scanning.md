# Library Scanning

SoftMedia's library scanner extracts comprehensive metadata from your media files, including technical details and embedded chapter information.

## What Gets Scanned

### Video Files (Movies & TV)

| Field | Source | Notes |
|:---|:---|:---|
| Duration | FFprobe | Total runtime in seconds |
| Video Codec | FFprobe | e.g., h264, hevc, av1 |
| Audio Codec | FFprobe | e.g., aac, ac3, eac3 |
| Resolution | FFprobe | e.g., 1920x1080 |
| Chapters | FFprobe | All embedded chapter markers |
| Credits Start | Chapters | Auto-detected from chapter titles |

### Audio Files (Music)

| Field | Source | Notes |
|:---|:---|:---|
| Duration | TagLib | Total runtime in seconds |
| Title | TagLib | Track title |
| Artist | TagLib | Artist name |
| Album | TagLib | Album name |
| Track Number | TagLib | Position on album |
| Embedded Art | TagLib | Album artwork |

## Rescanning Existing Items

When you rescan a library, SoftMedia now **fully updates existing items**:

### What Updates on Rescan

- ✅ Duration (re-probed from file)
- ✅ Video/Audio Codec
- ✅ Resolution
- ✅ All chapter markers
- ✅ Credits start timecode
- ✅ Episode/season numbers (if filename parsing changed)
- ✅ Series associations

### Previously (Now Fixed)

Before this update, rescanning only:
- Added new files
- Updated title and year
- Re-enriched if metadata was completely missing

This meant you had to **delete and recreate libraries** to update technical metadata. **This is no longer necessary.**

## Triggering a Scan

1. **Settings → Libraries**
2. Click the **Scan** icon next to a library
3. Watch console logs for progress:
   - `"Updated X chapters for: Episode Title"`
   - `"Found credits at Xs for: Title"`

## Technical Details

### FFprobe Command

```bash
ffprobe -v quiet -print_format json -show_format -show_streams -show_chapters "path/to/file"
```

### Chapter Detection for Credits

Chapter titles matching these patterns are flagged as "credits":
- Contains "credit"
- Contains "end" 
- Contains "outro"
- Contains "ending"

