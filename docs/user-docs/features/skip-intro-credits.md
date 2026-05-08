# Skip Intro & Skip Credits

SoftMedia automatically detects the intro theme and end credits of TV episodes and lets you skip them with a single click or keystroke. Detection runs in the background after a library scan — there's nothing to mark up by hand.

## How It Works

### What you see in the player

When the playhead enters a detected intro or credits segment, a **Skip Intro** or **Skip Credits** pill appears in the bottom-right corner of the video. The pill stays visible for 8 seconds, then fades out so it doesn't loiter on top of long intros.

You can dismiss the pill three ways:

| Action | Result |
|:---|:---|
| Click the pill | Seeks to the end of the segment |
| Press **S** on your keyboard | Same as clicking |
| Wait | Pill auto-fades, video keeps playing |

The progress bar also shows the detected segments as colored bands so you can see at a glance where the skippable parts are:

| Band | Color | Meaning |
|:---|:---|:---|
| Intro | Blue tint | Detected opening theme |
| Credits | Yellow tint | Detected end credits |

### Where the detected times come from

Two sources, in priority order:

1. **Embedded chapters** — if your video file has a chapter named "Intro", "End Credits", "Outro", etc., SoftMedia uses those timecodes directly. This is always trusted.
2. **Cross-episode auto-detection** — if no chapter exists, SoftMedia compares the audio of episodes within the same season to find segments that repeat. The matched segment is recorded as that episode's intro or credits.

Auto-detection runs once per series after a library scan. Subsequent scans are cheap because the audio fingerprints are cached. Detection is **per-season**, so a show whose intro changed between seasons gets each season detected separately.

## When detection works best

Auto-detection looks for audio that's identical (or near-identical) across multiple episodes in the same season. It works reliably when:

- The series has at least 2 episodes per season
- All episodes use the same theme music
- Episodes are encoded with consistent audio (typical for modern releases)

It may produce no detection or weaker results for:

- Series with only one episode in a season
- Shows whose intro changes per episode
- Older content with inconsistent audio mastering across episodes

When detection can't find consensus across enough episodes in a season, no intro is recorded and the pill simply doesn't appear — better than guessing.

## Auto-Skip (per-device preference)

By default the pill is a button — you choose to skip. If you'd rather have intros and credits skipped automatically, turn on Auto-Skip:

**Settings → Client Settings → Playback**

| Toggle | Behavior |
|:---|:---|
| Auto-Skip Intros | Player seeks past the intro the moment it starts |
| Auto-Skip Credits | Player seeks past the credits the moment they start |

These preferences are saved per device and per user — flipping them on your laptop doesn't change behavior on the TV in your living room. They only fire once per segment, so you can seek backward into an intro to re-watch it without it auto-skipping again.

## Detection settings (server-wide)

Server administrators can disable detection entirely if they don't want the CPU cost during scans. These live in the admin Settings page, under **Playback Detection**:

| Setting | Default | What it controls |
|:---|:---|:---|
| AutoDetectIntros | On | Whether to fingerprint and analyze the head of each episode |
| AutoDetectCredits | On | Whether to fingerprint and analyze the tail of each episode |

Turning these off doesn't affect existing detected timecodes — only future scans. Re-enable and re-scan to bring detection back.

## Manual re-detection

If you've added or replaced episodes for a series and want detection to run immediately (rather than waiting for the next scan), an admin can trigger it for a single series:

```
POST /api/v1/admin/series/{seriesId}/detect-intros
```

This enqueues a detection job and returns a job id. The job runs in the background; results show up on the affected episodes once it completes and the page is refreshed.

## Requirements

- Episodes must have audio that FFmpeg can decode (any common video format)
- The series must have at least 2 episodes in a season for cross-episode detection
- The library scan must have completed at least once after enabling detection

## Related Features

- **Smart Continue** — uses detected credits to decide when an episode is "complete" and advance to the next one
- **Progress Bar Chapter Markers** — chapter-derived intros and credits take priority over auto-detection and are shown alongside the band tints
