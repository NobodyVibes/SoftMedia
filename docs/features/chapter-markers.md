# Progress Bar Chapter Markers

SoftMedia displays visual chapter markers on the video player progress bar, extracted from your media files' embedded chapter metadata.

## Features

### All Chapters Displayed

Every chapter embedded in your video file is shown as a vertical line on the progress bar:

| Chapter Type | Color | Examples |
|:---|:---|:---|
| Credits/End/Outro | **Yellow** | "End Credits", "Outro", "Credits" |
| Other Chapters | **White/Gray** | "Intro", "Act 1", "Cold Open", "Recap" |

### Hover Tooltips

Hovering over any marker shows:
- Chapter title (e.g., "Cold Open")
- Timestamp (e.g., "2:45")

## How It Works

### Chapter Extraction

During library scanning, SoftMedia uses FFprobe to extract chapter information:

```bash
ffprobe -show_chapters -print_format json "video.mkv"
```

Extracted data includes:
- `startTime` - When the chapter begins (in seconds)
- `title` - Chapter name from metadata

### Storage

Chapters are stored in the media item's `MetadataJson`:

```json
{
  "chapters": [
    { "startTime": 0, "title": "Cold Open" },
    { "startTime": 145, "title": "Title Sequence" },
    { "startTime": 180, "title": "Act 1" },
    { "startTime": 1320, "title": "End Credits" }
  ],
  "creditsStart": 1320
}
```

### Frontend Rendering

The `VideoPlayer` component renders each chapter as an absolutely-positioned marker within the progress bar container.

## Requirements

- Video files must have embedded chapter metadata (common in MKV files)
- Library must be scanned (or rescanned) to extract chapters

## Related Features

- **Smart Continue**: Uses credits chapter marker to detect episode completion
- **Skip Intro** (future): Could use "Intro" chapter to enable skip functionality

