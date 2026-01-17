# Metadata Management

SoftMedia uses a smart metadata system to enrich your local media with rich information from the web while maintaining a strict "local-first" philosophy.

## Metadata Providers

We currently support the following providers:
- **TV Shows**: [TVMaze](https://www.tvmaze.com/) (Series info, Episode summaries, Air dates, Cast)
- **Movies**: [OMDb](https://www.omdbapi.com/) (Plot, Director, Year, Posters)
- **Music**: [MusicBrainz](https://musicbrainz.org/) (via Cover Art Archive for images) + Embedded ID3 Tags

## How It Works

### 1. Smart Matching
When you scan a library, SoftMedia parses your filenames (e.g., `Show.Name.S01E05.mkv`) and searches the relevant provider. It uses intelligent fuzzy matching to find the correct show or movie, handling year disambiguation automatically.

### 2. Local Storage
Metadata is stored directly in your local `SQLite` database within the `MediaItems` table.
- **Format**: Stored as a compressed JSON blob (`MetadataJson` column).
- **Pros**: This keeps the database schema flexible. We can store extensive details (guest stars, production codes, chapters) without complex relational tables.

### 3. Strict & Efficient Processing
SoftMedia utilizes a **Strict Existence Check** pipeline:
- Providers often return data for *all* seasons and episodes of a show (e.g., all 30 seasons of The Simpsons).
- **The Feature**: SoftMedia **only** applies metadata and downloads images for the seasons and episodes you actually have on disk.
- **Benefit**: This prevents your database and disk from being cluttered with thousands of records and images for content you don't own, significantly saving storage space and bandwidth.

## Updates & Refreshing

### Manual Refresh
You can force a metadata update for any item via the "Refresh Metadata" button in the UI. 
- This will re-fetch the latest data from the provider.
- It respects the same **Strict Existence Check** rules—it will never download images for missing episodes.

### Automatic Refresh
A background job runs periodically (configurable, default 24h) to keep your library up to date.
- **Criteria**: It **only** targets TV Series that are marked as `Running` in their metadata.
- **Purpose**: To fetch new episode air dates, season announcements, or status changes (e.g., if a show ends).
- **Efficiency**: Ended shows (like *Breaking Bad*) are skipped to save resources.
