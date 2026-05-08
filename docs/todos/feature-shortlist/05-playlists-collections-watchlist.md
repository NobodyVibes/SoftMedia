# Task 05 — Persisted Playlists, Collections, and Watchlist

**Wave:** E
**Plan:** [feature-shortlist-plan-2026-04-30.md](../../plans/feature-shortlist-plan-2026-04-30.md#wave-e--persisted-playlists-collections-and-watchlist)
**Severity:** Medium-large — three independent user-facing features bundled by theme.
**Estimated effort:** 5–7 days, **three sub-PRs** (E1 Playlists, E2 Collections, E3 Watchlist).
**Branches:** `feat/playlists`, `feat/collections`, `feat/watchlist` — landed in that order.

**Hard dependency:** Wave C (per-library ACL — [task 03](./03-per-library-acl.md)) **must land first**. Playlist and watchlist visibility filtering both inherit library-access rules so a public playlist or shared watchlist doesn't leak items past a viewer's ACL.

---

## E1 — User-owned Playlists

**Branch:** `feat/playlists`. Audio-only initial scope.

### Behavior

- A user creates named playlists ("Workout Mix", "Saturday Dinner"), adds audio tracks, reorders by drag, deletes them.
- A playlist can be marked **public** — visible to other users on the same SoftMedia server, but not editable by them. Default is private.
- The audio queue's existing "Play" affordance now also accepts a server-stored playlist (the in-memory `audioStore.playPlaylist` already exists at [audioStore.ts](../../../src/SoftMedia.Client/src/store/audioStore.ts) — we hydrate it from a server response).
- **ACL inheritance:** when user B views user A's public playlist that contains tracks from a library B can't access, those tracks are silently stripped from the response before render. The playlist itself isn't 404'd; only blocked items are removed. Same filtering rule the parental-control filter already uses.
- Initial scope is **audio tracks only** (`MediaType.Audio`). Movie/show playlists may land later but are not in this PR.

### Schema

**`src/SoftMedia.Server/Models/Playlist.cs`** (new file):

```csharp
[Index(nameof(OwnerUserId))]
public class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }
    public User Owner { get; set; } = null!;

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsPublic { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();
}
```

**`src/SoftMedia.Server/Models/PlaylistItem.cs`**:

```csharp
[Index(nameof(PlaylistId), nameof(Order))]
public class PlaylistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlaylistId { get; set; }
    public Playlist Playlist { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public int Order { get; set; }   // 0-based, dense within a playlist
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
```

`Id` rather than composite key on `(PlaylistId, MediaItemId)` because **duplicates are allowed** — a user can put the same track in their playlist twice (intentional repeat).

**[AppDbContext.cs](../../../src/SoftMedia.Server/Data/AppDbContext.cs)** — add `DbSet<Playlist>` and `DbSet<PlaylistItem>`. Cascade-delete `PlaylistItem` when its `Playlist` is removed.

**Migration:** `AddPlaylists`.

### API

New controller **`src/SoftMedia.Server/Controllers/PlaylistsController.cs`** — class-level `[Authorize]`:

- `GET /api/v1/playlists` — list. Returns user's own playlists + public playlists owned by others. Caller-isolated semantics (no cross-user leakage).
- `GET /api/v1/playlists/{id}` — playlist with items in order. Items are passed through ACL filter so blocked-library tracks don't appear. Returns 404 if private and not owner.
- `POST /api/v1/playlists` — create. Body: `{ name, description?, isPublic }`. Owner is the caller.
- `PATCH /api/v1/playlists/{id}` — owner-only. Body: any subset of `{ name, description, isPublic }`.
- `DELETE /api/v1/playlists/{id}` — owner-only.
- `POST /api/v1/playlists/{id}/items` — owner-only. Body: `{ mediaItemIds: Guid[] }`. Appends in order at the end. Validates each `MediaItemId` exists and `MediaType == Audio`. Per-library ACL must allow the *owner* (not the caller — owner adds, caller plays; if owner can't see a track they can't add it; the filter applies on read for *the viewer*).
- `DELETE /api/v1/playlists/{id}/items/{itemId}` — owner-only.
- `PUT /api/v1/playlists/{id}/order` — owner-only. Body: `{ itemIds: Guid[] }` where `itemIds` are `PlaylistItem.Id` values (NOT `MediaItemId`, because duplicates are allowed and `MediaItemId` is not unique within a playlist). Must be a permutation of the playlist's current `PlaylistItem.Id` set — server validates set equality and rejects with 400 on mismatch. Replaces `Order` values in one transaction.

DTO: **`src/SoftMedia.Server/DTOs/PlaylistDto.cs`** with `Id, Name, Description, IsPublic, IsOwner, ItemCount, CreatedAt, UpdatedAt`. Item-level DTO reuses `MediaItemDto`.

### Frontend

- `services/playlistService.ts` + TanStack Query hooks (`usePlaylist`, `usePlaylistList`, `useCreatePlaylist`, `useReorderPlaylist`, etc.).
- `pages/PlaylistsPage.tsx` — grid of playlist cards.
- `components/playlists/PlaylistDetailView.tsx` — list of tracks, drag-to-reorder via `@dnd-kit/sortable` (already used by [SortableQueueItem.tsx](../../../src/SoftMedia.Client/src/components/player/SortableQueueItem.tsx); reuse the pattern).
- "Add to playlist" affordance on `MediaCard` and the audio queue context menu (a small `<Combobox>` listing the user's playlists + a "Create new..." entry).
- Wire `audioStore.playPlaylist(tracks, startFrom)` to accept an array hydrated from `playlistService.get(id)`.
- Add a "Playlists" entry in [Sidebar.tsx](../../../src/SoftMedia.Client/src/components/layout/Sidebar.tsx) routing to `/playlists`.

### Tests

- xUnit: ownership/visibility (private hidden from non-owner; public visible to others), ACL filter on items (creates user A with track in Library X; user B with no access to X views A's public playlist; B's response excludes that track), cascade delete, reorder validation (rejects non-permutation), audio-only validation (rejects `MediaType.Movie`).
- Vitest: drag-to-reorder triggers the order mutation; "Add to playlist" combobox creates a new playlist when "Create new..." is selected.

### Acceptance criteria

- A user can create, rename, delete, and play a playlist of audio tracks.
- A public playlist appears in another user's `GET /api/v1/playlists` response and is read-only there.
- A public playlist viewed by a user with restricted library access strips blocked tracks from the response.
- A playlist with one duplicate track plays the track twice in sequence.

---

## E2 — Auto-generated movie Collections

**Branch:** `feat/collections`. Depends on E1 only insofar as both ship after C; functionally independent.

### Background — the OMDb constraint

The maintainer's reminder is critical here: **most users will use OMDb** as their movie provider. I confirmed by reading [OMDbProvider.cs:352-469](../../../src/SoftMedia.Server/Services/Metadata/OMDbProvider.cs#L352-L469) that **OMDb has no franchise / collection / `belongs_to_collection` field**. Its API returns Title, Year, Plot, Director, Genre, Rated, Poster, imdbRating, imdbID, Production, Runtime, Writer, Awards, BoxOffice — and that's it. (The `belongs_to_collection` shape exists on TMDb, but TMDb is forbidden by SDD §4.3 — the allowed movie providers are Wikidata and OMDb.)

**The bridge:** OMDb gives every movie a stable IMDb ID (already promoted to `MediaItem.ImdbId`). Wikidata's `wdt:P179` ("part of the series") is the property we need, and Wikidata supports lookup *by IMDb ID* via `wdt:P345`. So even an OMDb-primary user can get collection data via a single SPARQL query per movie:

```sparql
SELECT ?series ?seriesLabel WHERE {
  ?film wdt:P345 "tt0120737" .       # IMDb ID of the movie
  ?film wdt:P179 ?series .
  SERVICE wikibase:label { bd:serviceParam wikibase:language "en" . }
}
```

This is keyless, runs once per movie at enrichment time, and is cached on the `Collection.WikidataId` so it never repeats. Wikidata's existing rate limiter (already wired in `RateLimiterFactory`) handles request pacing.

### Behavior — exactly as specified by the maintainer

- **Library view: unchanged.** Movies in a collection still appear as their own cards in the library grid. We do **not** group them into a single collection card. Each movie is independently browsable.
- **Movie details view: new "More from this collection" section.** Mirrors the per-season episode list pattern in [TVDetailView.tsx](../../../src/SoftMedia.Client/src/components/details/TVDetailView.tsx). Layout: a horizontal-scroll list (the existing `HorizontalScrollList` component at [src/SoftMedia.Client/src/components/ui/HorizontalScrollList.tsx](../../../src/SoftMedia.Client/src/components/ui/HorizontalScrollList.tsx)) of sibling movies in the same collection, ordered by `ReleaseDate` ascending. Each card links to that movie's detail page. The section header reads "More from *The Lord of the Rings*" with the collection name italicised. The current movie is included in the list with a "Now viewing" badge — this matches how TV view emphasises the currently selected episode.
- **Optional Home-page row.** If at least one collection has ≥2 movies the user can see, a "Collections" row appears on the home page below "Recently Added", listing each collection as a single card. Clicking goes to a collection detail page (a thin wrapper that lists all movies in the collection, same component as the details-view section but full-width).
- **Per-library ACL applies** — if user B can see Movie Library X but not Y, and a collection spans both, B's "More from" strip shows only the X movies. Same filtering rule as playlists.
- **Auto-only by default.** Collections populated from Wikidata are read-only; admins cannot edit them. **Manual collections** (admin-curated) live alongside in the same table, distinguished by `WikidataId IS NULL`. Manual collections are admin-editable.
- **Migration friendliness.** Re-scanning a movie does not destroy a manual collection assignment. Auto-population only runs for movies whose `MediaItem.CollectionId` is null **or** whose currently-assigned collection has a `WikidataId` (auto). Manual assignments win.

### Schema

**`src/SoftMedia.Server/Models/Collection.cs`**:

```csharp
public class Collection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Overview { get; set; }

    public string? PosterUrl { get; set; }      // can be auto from Wikidata or manual

    /// <summary>
    /// Wikidata QID (e.g., "Q170461") of the parent series, or null for manual collections.
    /// Manual collections never get auto-overwritten on re-scan.
    /// </summary>
    public string? WikidataId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaItem> Items { get; set; } = new List<MediaItem>();
}
```

Index `WikidataId` (sparse — most rows null). Unique constraint on `WikidataId WHERE WikidataId IS NOT NULL` (SQLite supports filtered indexes via `HasFilter` in EF).

**[MediaItem.cs](../../../src/SoftMedia.Server/Models/MediaItem.cs)** — add:
```csharp
public Guid? CollectionId { get; set; }
public Collection? Collection { get; set; }
```
Index `CollectionId`. Nullable; vast majority stay null.

**Migration:** `AddCollections`.

### Wikidata SPARQL extension

**[src/SoftMedia.Server/Services/Metadata/WikidataProvider.cs](../../../src/SoftMedia.Server/Services/Metadata/WikidataProvider.cs)** — extend the existing SPARQL query (or add a second one — the existing query already returns a movie by Wikidata QID; the lookup-by-IMDb-ID is a different query shape) to include `?series` and `?seriesLabel` when the film has a `wdt:P179`.

**Where to put the new fields on `MetadataResult`.** Two options were considered:

1. Add explicit nullable string properties `SeriesWikidataId` and `SeriesName` to [MetadataResult.cs](../../../src/SoftMedia.Server/Models/MetadataResult.cs) alongside `Studio`, `Director`, etc.
2. Stuff them into the existing `Extra` dictionary at [MetadataResult.cs:106-108](../../../src/SoftMedia.Server/Models/MetadataResult.cs#L106-L108).

**Pick option 1 (explicit fields).** The collection lookup is a known, structured concept — not a "miscellaneous extension" the way `Extra` is intended for. Explicit properties are typed, are reachable from `MetadataAggregator` without dictionary key string-matching, and make the OMDb-bridge resolver's contract obvious. Add:

```csharp
[JsonPropertyName("seriesWikidataId")]
public string? SeriesWikidataId { get; set; }

[JsonPropertyName("seriesName")]
public string? SeriesName { get; set; }
```

This is a backward-compatible additive change — existing JSON payloads simply leave the new fields null.

**`src/SoftMedia.Server/Services/Metadata/WikidataCollectionResolver.cs`** (new) — handles the OMDb-bridge case:

```csharp
public class WikidataCollectionResolver
{
    /// <summary>
    /// Given an IMDb ID, returns the parent collection's Wikidata QID and label,
    /// or null if the film isn't part of any series in Wikidata.
    /// </summary>
    public async Task<(string QId, string Name)?> ResolveByImdbIdAsync(string imdbId, CancellationToken ct);
}
```

**HttpClient registration:** add a typed `HttpClient` in [ServiceCollectionExtensions.cs](../../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L126) next to the existing `WikidataProvider` registration, with the same `SoftMediaUserAgentHandler` so SDD §4.3's User-Agent requirement is satisfied:
```csharp
services.AddHttpClient<WikidataCollectionResolver>()
        .AddHttpMessageHandler<SoftMediaUserAgentHandler>();
```
This shares the same SPARQL endpoint host as the existing provider; the project's `RateLimiterFactory` already covers Wikidata at the host-rate-limiter level.

This resolver runs once per OMDb-sourced movie during enrichment. **Negative caching:** when a movie is in no series, the aggregator records the resolution attempt by setting `MediaItem.MetadataHash` to a sentinel (mirrors the comic-provider sentinel pattern at [MetadataAggregator.cs:58-67](../../../src/SoftMedia.Server/Services/Metadata/MetadataAggregator.cs#L58-L67)) — or simpler, store a `bool? CollectionLookupAttempted` column on `MediaItem` so we never re-query. Pick one in implementation; the existing sentinel pattern is more consistent with the codebase.

### Aggregator wiring

**[MetadataAggregator.cs](../../../src/SoftMedia.Server/Services/Metadata/MetadataAggregator.cs)** — extend `ProcessMetadataResultAsync` (around line 99 where Studio/Director is set) with a new block that:

1. If `metadata.SeriesWikidataId` is non-null (Wikidata-primary path), look up or create the `Collection` by `WikidataId`. Set `item.CollectionId`.
2. If primary was OMDb (or Wikidata returned no series) AND `item.ImdbId` is non-null AND the user hasn't disabled the resolver, call `WikidataCollectionResolver.ResolveByImdbIdAsync`. Same upsert.
3. Auto-population only overwrites when `item.CollectionId` is null OR the currently-assigned collection has `WikidataId IS NOT NULL` (i.e., previously auto-assigned). Manual assignments are preserved.

Add an admin setting `EnableWikidataCollectionLookup` (default `true`) so users who want OMDb-only with no Wikidata calls can opt out — they keep manual collections only.

### API

**`src/SoftMedia.Server/Controllers/CollectionsController.cs`** — class-level `[Authorize]`:

- `GET /api/v1/collections` — list collections that have ≥2 visible movies for the caller (after ACL). Each entry: `{ id, name, posterUrl, movieCount }`.
- `GET /api/v1/collections/{id}` — collection detail with movies in `ReleaseDate` ascending order. ACL-filtered.
- `GET /api/v1/collections/by-movie/{movieId}` — convenience endpoint for the movie detail view's "More from" strip. Mounted on `CollectionsController` (not `MediaController`) so all collection logic lives in one controller. Returns the collection plus its movies in `ReleaseDate` ascending order, with each item carrying an `isCurrent: bool` flag (true for the queried movie). The current movie is **included** in the strip — this matches how TV episode lists highlight the currently-selected episode rather than hiding it. Returns `204 No Content` if the movie has no collection.

**Orphan collections after library deletion.** When a library is deleted (Wave C task spec) its `MediaItems` cascade-delete. Their `Collection` rows do **not** cascade — collections are reference-only via `MediaItem.CollectionId` (which becomes a dangling reference, but EF nullifies on delete because the FK is `Guid?`). Result: a collection may end up with zero items. The list endpoint already filters to collections with ≥2 visible movies, so empty/single-item collections become invisible automatically without a cleanup pass. A nightly background sweep to hard-delete zero-item collections is **not** in scope; document this as a known minor housekeeping gap.

Configure the FK explicitly so EF emits `ON DELETE SET NULL`:
```csharp
modelBuilder.Entity<MediaItem>()
    .HasOne(m => m.Collection)
    .WithMany(c => c.Items)
    .HasForeignKey(m => m.CollectionId)
    .OnDelete(DeleteBehavior.SetNull);
```

Admin-only collection-management endpoints (for manual collections):
- `POST /api/v1/admin/collections` — create a manual collection. Body: `{ name, overview?, posterUrl?, movieIds: Guid[] }`.
- `PATCH /api/v1/admin/collections/{id}` — edit name/overview/poster (manual only — auto-rejects with 400 if `WikidataId` is set).
- `POST /api/v1/admin/collections/{id}/items` — add movies to a manual collection.
- `DELETE /api/v1/admin/collections/{id}/items/{movieId}` — remove a movie from a manual collection.
- `DELETE /api/v1/admin/collections/{id}` — delete a manual collection (auto-collections with assigned items shouldn't be hard-delete-able; instead detach by setting `CollectionId = null` on the items, or just refuse).

### Frontend

- `services/collectionService.ts`.
- `components/details/CollectionStripSection.tsx` (new) — used inside `MovieDetailView.tsx`. Calls `/api/v1/collections/by-movie/{movieId}`, renders via `HorizontalScrollList`. Header: "More from *{collectionName}*". Skip rendering if the API returns 204. The current movie's card carries the `isCurrent` flag and renders with a "Now viewing" overlay badge.
- **[MovieDetailView.tsx](../../../src/SoftMedia.Client/src/components/details/MovieDetailView.tsx)** — render `<CollectionStripSection itemId={item.id} />` inside the existing `<div className="space-y-8 relative z-10">` block, after the Cast Grid, before the Crew & Details Grid. Maintains visual rhythm with TV view.
- `pages/CollectionsPage.tsx` — grid of collection cards.
- `pages/CollectionDetailPage.tsx` — full-width version of the strip view.
- Home page — add an optional "Collections" row below "Recently Added" if `GET /api/v1/collections` returns ≥1 collection with ≥2 visible movies.
- Admin: a Collections management section under Settings → Media Management for creating/editing manual collections.

### Tests

- xUnit: Wikidata-by-IMDb resolver returns the correct QID for a known IMDb ID (use a recorded/canned response, not a live network call); negative-cache prevents repeat queries; manual collections are not overwritten on re-scan; auto collections deduplicate by `WikidataId`.
- ACL: a viewer with restricted library access sees only their visible movies in the strip.
- Frontend (Vitest): `MovieDetailView` renders the section when the API returns siblings; renders nothing when 204; the "Now viewing" badge appears on the current movie.

### Acceptance criteria

- A user with three Lord of the Rings movies in their library, scanned via OMDb, sees a "More from *The Lord of the Rings*" strip on each of the three detail pages, populated automatically without any admin intervention.
- The same three movies appear individually in the Movies library grid (collection does not collapse them).
- An admin can manually create a "Tarantino Films" collection containing five movies that share no Wikidata series; that collection survives a re-scan.
- A user with library access to Library A only sees only A-residing movies in the strip when a collection spans A and B.

---

## E3 — Watchlist

**Branch:** `feat/watchlist`. Smallest sub-PR.

### Behavior

- Each user gets a personal "Watchlist" — items they want to watch/listen to/read later, surfaced on the home page as a row.
- An "Add to Watchlist" / "Remove from Watchlist" button appears on every media detail view (movies, TV series, books, comics, albums).
- Per-library ACL applies — items the user can no longer see are silently stripped from their watchlist response.

### Schema

**[UserMediaInteraction.cs](../../../src/SoftMedia.Server/Models/UserMediaInteraction.cs)** — add:
```csharp
public bool IsWatchlisted { get; set; }
public DateTime? WatchlistedAt { get; set; }
```

**Migration:** `AddWatchlistFlag`. Defaults to `false` for all existing rows.

### API

**[InteractionController.cs](../../../src/SoftMedia.Server/Controllers/InteractionController.cs)** — add, mirroring `ToggleFavorite` at [InteractionController.cs:52](../../../src/SoftMedia.Server/Controllers/InteractionController.cs#L52):

```csharp
[HttpPost("{mediaId}/watchlist")]
public async Task<IActionResult> ToggleWatchlist(Guid mediaId, [FromBody] WatchlistRequest request)
{
    var userId = GetUserId();
    await _interactionService.ToggleWatchlistAsync(userId, mediaId, request.IsWatchlisted);
    return Ok();
}

public class WatchlistRequest { public bool IsWatchlisted { get; set; } }
```

Add a new endpoint to fetch the current user's watchlist:
```csharp
[HttpGet("/api/v1/watchlist")]
public async Task<ActionResult<List<MediaItemDto>>> GetWatchlist();
```
Returns ACL-filtered, sorted by `WatchlistedAt DESC`, limit 50.

### Frontend

- `components/details/WatchlistButton.tsx` — small toggle button used in every detail view (`MovieDetailView`, `TVDetailView`, `MusicDetailView`, `BookDetailView`, `GameDetailView`, etc.). Pair `hover:` and `focus-visible:`, ≥44px hit target, `aria-pressed` for the toggle state.
- Home page — new "Watchlist" row below "Continue Watching".

### Tests

- xUnit: toggle on/off persists; GET returns ACL-filtered list; invalid media ID returns 404.
- Vitest: WatchlistButton toggles state on click; shows pending state while mutation is in flight.

### Acceptance criteria

- A user can add any movie/series/book/album to their watchlist from its detail page.
- The watchlist row on the home page shows their additions in reverse-chronological order.
- A user whose library access changes loses the corresponding items from their watchlist row without any explicit cleanup.

---

## Cross-cutting acceptance for Wave E

- All three sub-PRs use the exact `IUserLibraryAccessProvider` introduced in Wave C — no parallel filtering implementation.
- All three sub-PRs respect the parental-content-rating filter as well, by composing both filters in repository methods (the rating filter already exists; ACL filter chains alongside it).
- No PR in Wave E modifies `Library`, `MediaItem.LibraryId`, or any scanner. Collections add a `MediaItem.CollectionId` column; that's the only `MediaItem` change.
- Three migrations total: `AddPlaylists`, `AddCollections`, `AddWatchlistFlag`. Apply in that order.

## Out of scope

- **Smart playlists** ("Top 25 Most-Played", "Recently Rated") — adds a query DSL; defer.
- **Cross-type playlists** mixing movies + audio + episodes — initial E1 scope is audio-only.
- **TV-show collections** ("The Marvel Cinematic Universe TV") — collections are movie-only in E2.
- **Watchlist sharing.** Watchlists are private to the user. Public sharing would need a visibility flag and ACL story like playlists; defer.
- **Editing auto-collections.** If a user dislikes Wikidata's grouping, they create a manual collection. Mixing the two is a merge-conflict problem on re-scan.
