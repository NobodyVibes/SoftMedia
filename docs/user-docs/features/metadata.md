# Metadata Management

SoftMedia uses a smart metadata system to enrich your local media with rich information from the web while maintaining a strict "local-first" philosophy.

## Metadata Providers

We currently support the following providers:
- **TV Shows**: [TVMaze](https://www.tvmaze.com/) (Series info, Episode summaries, Air dates, Cast)
- **Movies**: [OMDb](https://www.omdbapi.com/) and [Wikidata](https://www.wikidata.org/)
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

### 4. Smart ID Lookup & Caching
To prohibitively reduce API calls and improve performance (especially for paid tiers like OMDb), SoftMedia implements a **"Smart ID First"** strategy across all providers (OMDb, TVMaze, Wikidata). The lookup hierarchy is as follows:

1.  **Cached ID (Priority 1)**: The system first checks your local `MetadataJson` for a specific provider ID (e.g., `imdbId`, `tvmazeId`). If found, it performs a **single direct lookup** (1 API call).
2.  **Exact Match (Priority 2)**: If no ID is found, it attempts an "Exact Match" lookup (e.g., OMDb `t=` parameter) using the filename's title and year.
3.  **Broad Search (Last Resort)**: Only if the exact match fails (or returns no results) does the system fall back to a broader "Search" (e.g., OMDb `s=` parameter), which is more resource-intensive.

**Result**: This tiered approach ensures the vast majority of refreshes use only 1 API call, reserving expensive search operations for truly new or unrecognized content.

## Updates & Refreshing

### Manual Refresh
You can force a metadata update for any item via the "Refresh Metadata" button in the UI. 
- This will re-fetch the latest data from the provider.
- It respects the same **Strict Existence Check** rules—it will never download images for missing episodes.

### Automatic RefreshStrategy
A background service keeps your metadata fresh according to your preferences in **Settings > Scanning**.

- **Interval**: Configurable in **Days** (Default: 30 days).
- **Run on Startup**: Optional toggle (Default: Disabled) to prevent performance impact when restarting.
- **Refresh Modes**:
    1.  **Running (Default)**: Updates only TV Series marked as "Running". Efficient and fast.
    2.  **Variable**: Updates text metadata (ratings, votes, cast) for **ALL** content but **skips image downloading**. Great for keeping ratings fresh without bandwidth usage.
    3.  **All**: Performs a full update (text + images) for **ALL** content. Most comprehensive but resource-intensive.
