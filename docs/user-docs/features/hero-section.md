# Hero Section

The Hero Section is the featured carousel at the top of your Home Page. It showcases a curated selection of 12 media items from your libraries to help you discover high-quality content and new additions.

## Content Selection

The Hero Section displays exactly **12 media items**, chosen using a specialized recommendation algorithm designed for both quality and variety:

1.  **Top Rated (2 Items)**: The algorithm first selects the top 2 highest-rated Movies or TV Series based on community ratings (e.g., IMDb, TVMaze).
2.  **Diverse Mix (10 Items)**: The remaining 10 slots are filled using a "round-robin" variety sampler. It pulls a balanced mix from all your available media types:
    *   Movies
    *   TV Series
    *   Music Albums
    *   Books
    *   Games

After selection, the entire list is **shuffled randomly** so the top-rated items don't always appear first in the carousel. This creates a fresh, dynamic experience every time the Hero Section updates.

## Automatic Updates

The Hero Section is designed to stay current without any manual intervention. It refreshes automatically in the following scenarios:

*   **Daily Maintenance**: Every day at **12:01 AM**, the system performs a scheduled refresh to rotate the featured content.
*   **New Content Arrival**: When you scan a library and new media is added, the system waits for the poster art and images to be locally cached, then immediately refreshes the Hero Section so the new items have a chance to be featured.
*   **Library Cleanup**: When a library is deleted, the Hero Section refreshes instantly to ensure no "ghost" items from the deleted library remain in the carousel.
*   **Initial Setup**: If you are visiting the Home Page for the first time or the cache is empty, the system generates the featured list on-demand.

## Privacy & Performance

To protect your privacy and ensure maximum performance, the Hero Section only displays images that have been **locally cached** on your SoftMedia server. It never leaks your viewing habits to external metadata providers by requesting images directly from their servers in the browser.
