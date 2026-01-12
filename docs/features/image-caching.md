# Image Caching

SoftMedia automatically caches remote images locally for faster loading and offline access.

## What Gets Cached

When media is scanned and metadata is fetched, the following images are downloaded and stored locally:

- **Movie Posters** - Cover art for movies
- **TV Series Posters** - Main poster for each series
- **Season Posters** - Individual poster for each season
- **Episode Stills** - Thumbnail images for TV episodes
- **Album Covers** - Artwork for music albums

## Benefits

- **Faster Loading** - Images load instantly from local storage
- **Reduced Bandwidth** - No repeated downloads from external sources
- **Offline Access** - Images remain available even without internet
- **Improved Privacy** - Fewer requests to external image servers

## How It Works

1. During library scanning, metadata providers (TVMaze, MusicBrainz, etc.) return image URLs
2. Text metadata is saved immediately, making content visible in the UI
3. A background service queues image downloads to avoid blocking scans
4. Images are downloaded asynchronously and stored in `wwwroot/cache/images/`
5. Metadata is updated with local cached paths as downloads complete

### Background Caching

Image downloads run in the background with:

- **Rate Limiting** - 10 images per second to avoid overloading servers
- **Deduplication** - Same items aren't queued multiple times
- **Graceful Shutdown** - In-progress downloads complete on server stop
- **Real-Time Updates** - Media detail pages auto-refresh when images are cached

## Smooth Image Loading

Media cards and episode thumbnails feature smooth loading with:

- **Skeleton Placeholders** - Animated gradient shows while images load
- **Fade-in Transitions** - Images appear smoothly when ready
- **Prefetching** - Episode images are preloaded when browsing seasons

This eliminates the "pop-in" effect and creates a polished browsing experience.

## Cache Location

All cached images are stored in: `wwwroot/cache/images/`

Subdirectories organize images by type:
- `tv/` - TV series, season, and episode images
- `movies/` - Movie posters
- `music/` - Album artwork
- `proxy/` - Fallback cache for proxied images
