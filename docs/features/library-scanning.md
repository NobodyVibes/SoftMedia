# Library Scanning

SoftMedia automatically manages your media library by intelligently scanning your folders, detecting file changes in real-time, and keeping your library organized and up-to-date.

## How It Works

### Auto-Refresh on Scan Completion

Library pages automatically refresh when scans complete - no manual browser refresh needed:

1. **Background Polling** - The page polls the scan queue every 2 seconds
2. **Completion Detection** - When a scan for the current library finishes
3. **Cache Invalidation** - Automatically refreshes the media list
4. **UI Update** - New, updated, or removed items appear instantly

This works for all scan speeds and library types, whether scans complete in seconds or minutes.

### Smart File Watcher

SoftMedia monitors your library folders and automatically detects when media files are added, changed, or removed:

1. **File Stability Detection** - When a new file appears, SoftMedia waits for it to be fully written
   - Monitors file size every 5 seconds for stability
   - Requires the file to remain unchanged for 10 seconds
   - Checks that the file isn't locked by another process
2. **Scan Debouncing** - Groups multiple file changes together
   - Waits 15 seconds after detecting changes
   - Triggers a single scan instead of multiple rapid scans
   - Targets only the specific library that changed
3. **Orphan Cleanup** - Automatically removes entries when files are deleted
   - Detects missing media files and removes database entries
   - Removes empty TV series/albums when all episodes/tracks are gone
   - Cleans up associated images and watch history

### Auto-Scan on Library Creation

When you create a new library, SoftMedia automatically:
- Triggers an initial scan to discover all media
- Indexes file metadata and fetches enrichment data
- Updates the UI with real-time progress

### Optimized Scanning

Library scans are highly optimized for performance:

**HashSet Lookup** - Instead of querying the database for every file:
- Pre-loads all existing file paths into memory (1 database query)
- Uses instant HashSet lookups to check if files are new
- Only queries the database for files that need updates

**Strict Existence Check** - To save bandwidth and storage:
- Verifies that seasons and episodes *actually exist* on disk before fetching their metadata
- Prevents downloading thousands of images for missing content

### Progressive Scanning

Library scans use a 3-phase approach for instant content visibility:

1. **File Indexing (Fast)** - Discovers files, creates database entries, establishes relationships
2. **Text Metadata (Fast)** - Fetches ratings, descriptions, and genres from providers
3. **Deferred Image Caching (Background)** - Downloads posters and artwork asynchronously *after* the scan ensures all items are indexed

Media items appear in the library within seconds. Images load progressively as the background service processes the verified queue.

### Real-Time Updates

Library pages automatically update during scans via SignalR push notifications:

- **Instant Visibility** - New media items appear as they're indexed, not after the scan completes
- **Multi-Client** - All connected browsers receive updates simultaneously
- **Efficient** - No polling; updates only when changes occur

## Progress Tracking

The scan progress UI shows:
- **File Discovery**: "Discovering files..." while counting media
- **Processing**: "X / Y files" with actual progress percentage
- **Completion Stats**: New, updated, and skipped items when done

## Progressive File Timeout

When adding large files (downloading, copying, etc.), SoftMedia intelligently waits:

- **Growing Files** - Waits indefinitely while file size is changing
- **Locked Files** - Gives up after 15 minutes if file can't be accessed
- **Stalled Downloads** - Gives up after 15 minutes with no progress
- **Maximum Wait** - 2 hours absolute limit as a failsafe

This ensures large 4K movies being downloaded can take as long as needed, while stuck files don't wait forever.

## Admin Dashboard

Admins can monitor file processing issues in **Settings → Admin Dashboard**:

- **File Watcher Issues** - Shows files that timed out or couldn't be processed
- **Status Display** - "File locked", "Download stalled", or "Timeout"
- **Retry Button** - Re-queue a file for processing
- **Dismiss Button** - Remove the issue from the list

Issues are automatically cleared when the file is successfully processed on retry.

## Requirements

- Library folders must be accessible and not on network drives with high latency
- Supported file formats depend on library type (video, audio, images, etc.)
- FFprobe must be available for video/audio metadata extraction

## API Endpoints

```
POST /api/v1/libraries/{id}/scan
```
Manually trigger a scan for a specific library

```
GET /api/v1/libraries/{id}/scan-status
```
Get current scan progress and queue status

```
GET /api/v1/admin/file-watcher-issues
```
Get list of file watcher issues (Admin only)
