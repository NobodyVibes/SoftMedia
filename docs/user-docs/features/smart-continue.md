# Smart Continue

SoftMedia automatically tracks your viewing progress and intelligently continues playback from where you left off, or advances to the next episode.

## How It Works

### For TV Episodes

When you click Play on a TV series, Smart Continue determines the best episode to play:

1. **Find Last Watched Episode** - Looks at your watch history for the series
2. **Check Completion Status** - Determines if the episode is "complete" based on:
   - **Credits Timecode**: If the episode has an embedded credits chapter marker, completion triggers when you reach it
   - **95% Threshold**: If no credits marker, an episode is complete when you've watched ≥95% of its duration
3. **Play Next or Resume**:
   - **Incomplete Episode**: Resume from your saved position
   - **Complete Episode**: Automatically advance to the next episode in the series
   - **Series Complete**: Starts from S01E01

### For Movies

Movies resume from your last saved playback position.

## Completion Detection

| Detection Method | Condition | Behavior |
|:---|:---|:---|
| Credits Chapter | Position ≥ credits start time | Mark complete, play next |
| Duration Threshold | Position ≥ 95% of duration | Mark complete, play next |
| Neither Available | Falls back to position tracking | Manual completion only |

## API Endpoint

```
GET /api/v1/series/{seriesId}/next-episode
```

Returns:
- `episodeId` - The episode to play
- `resumePosition` - Seconds to seek to (0 if starting fresh)
- `isSeriesComplete` - True if all episodes are watched

## Requirements

- Episodes must have **Duration** populated (extracted via FFprobe during library scan)
- For credits-based detection, videos must have embedded chapter markers

