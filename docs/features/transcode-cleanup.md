# Transcode Cleanup

SoftMedia automatically manages temporary transcoding files to prevent disk space issues.

## Automatic Cleanup Triggers

### 1. Video Completion
When a video finishes playing (or you navigate away), the temporary files for that video are deleted immediately.

### 2. Disk Space Protection
A background check runs every **30 seconds**. If free disk space drops below **500 MB**, the oldest paused sessions are removed first to recover space.

### 3. Stale Session Cleanup
An hourly check removes any paused transcoding sessions that have been inactive for more than **24 hours**.

## What Gets Cleaned

- HLS segment files (`.ts` files)
- Playlist files (`.m3u8`)
- Session tracking data

## When Cleanup Happens

| Trigger | Check Frequency | Condition |
|:---|:---|:---|
| Video ends | Immediately | Video playback completes |
| Navigate away | Immediately | Leave the player |
| Low disk space | Every 30 seconds | < 500 MB free |
| Stale sessions | Every hour | Inactive > 24 hours |

## Notes

- Active playback sessions are never cleaned up
- Paused sessions with buffer are kept unless disk space is critical
- You don't need to manually manage temporary files
