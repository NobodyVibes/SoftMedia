# SoftMedia Feature Shortlist — Implementation Plan

**Author:** Senior engineering plan, derived from the 2026-04-30 self-hosted gap-analysis report
**Status:** Ready for execution
**Owner:** Project maintainer
**Branch posture:** Each wave below is its own branch + PR. Do not bundle waves together — every wave touches a different concern, and the per-library ACL wave (Wave C) intersects every list endpoint and deserves an isolated review.

---

## 0. Overview & ground rules

This plan turns the five shortlist findings from the gap analysis into concrete, sequenced work. The five items, in landing order:

| Wave | Theme | Severity | Est. effort | Blocks / depends on |
|------|-------|----------|-------------|---------------------|
| **A** | Hide the Photo library type until Phase 2 | Trivial UX bug | 0.5 day | None |
| **B** | Admin database backup endpoint | Medium (data-safety net) | 1 day | None |
| **C** | Per-library access control (per-user ACL) | Medium (parental-control parallel) | 3–4 days | None — but Wave E playlists/collections rely on it |
| **D** | `.nfo` (Kodi/XBMC) sidecar metadata reader | Low | 2 days | None |
| **E** | Persisted playlists, collections, and watchlist | Medium-large | 5–7 days (3 sub-PRs) | Wave C **must** land first |

Recommended landing order: **A → B → C → D → E**. A and B are quick wins that ship safety improvements without architectural risk; C must precede E because playlist visibility filtering inherits library-access rules; D can interleave with E.

### Ground rules (apply to every wave)

- **Backend-first.** Per `docs/rules/01-core-philosophy.md`, the endpoint, DTO, and xUnit tests must exist and pass before any React component consumes them.
- **DI registration.** All new services register exclusively in [src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs](../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs). No new DI in `Program.cs`.
- **Migrations.** Each schema change is one EF Core migration following the existing `yyyyMMddHHmmss_Name` naming pattern visible in [src/SoftMedia.Server/Migrations/](../../src/SoftMedia.Server/Migrations/).
- **Path safety.** Anything that touches the filesystem goes through `Canonicalise` / `CanonicaliseAll` from [LibraryService.cs:128-155](../../src/SoftMedia.Server/Services/Media/LibraryService.cs#L128-L155). Never raw `Path.Combine` user input into a library root.
- **Auth posture.** Admin endpoints carry `[Authorize(Roles = "Admin")]`, matching [AdminController.cs:20](../../src/SoftMedia.Server/Controllers/AdminController.cs#L20). Per-user reads use the standard `[Authorize]` and resolve `User.GetUserId()`.
- **404 over 403.** "Exists but you can't see it" returns 404, never 403 — matching the established pattern at [StreamController.cs:50](../../src/SoftMedia.Server/Controllers/StreamController.cs#L50) and per SDD §6.2.
- **Universal client.** Every interactive element must be a `<button>` (or `role="button"` + `tabIndex`), pair `hover:` with `focus-visible:`, and have a ≥44×44 px hit area in responsive contexts (per `docs/rules/01-core-philosophy.md` §8.3).

### Per-wave task files

Each wave has its own numbered task file under `docs/todos/feature-shortlist/`:

- [A — Photo library hide](../todos/feature-shortlist/01-hide-photo-library.md)
- [B — Backup endpoint](../todos/feature-shortlist/02-admin-backup-endpoint.md)
- [C — Per-library ACL](../todos/feature-shortlist/03-per-library-acl.md)
- [D — `.nfo` reader](../todos/feature-shortlist/04-nfo-metadata-reader.md)
- [E — Playlists, Collections, Watchlist](../todos/feature-shortlist/05-playlists-collections-watchlist.md)

The task files are the actionable engineering tickets — they list exact files, exact tests, and acceptance criteria. This file is the "why and in what order"; the task files are the "what to type."

---

## Wave A — Hide the Photo library type until Phase 2

**Why first:** smallest change, fixes a discoverable broken state. Right now the admin UI offers `Photo` in the library-type picker but `Services/Scanning/` has no `PhotoScanner`, so a "Photo" library scans empty silently. SDD §4.1 already labels Photos "Phase 2 (post-1.0)."

**Scope:** Remove `Photo` from the picker; reject Photo at the server boundary; preserve the enum value, the `ExifMetadataProvider`, the `PhotoProvider` setting, and the rating-filter wiring so re-enabling later is one line.

**Behavior after this wave:**
- Admins creating or editing a library no longer see "Photo" as an option. Existing Photo libraries (if any user created one as an experiment) continue to exist but are visibly empty; no destructive cleanup is performed.
- Direct API calls with `LibraryType.Photo` return `400 Bad Request` with a human-readable message indicating Phase 2 status.

Full task list: [docs/todos/feature-shortlist/01-hide-photo-library.md](../todos/feature-shortlist/01-hide-photo-library.md).

---

## Wave B — Admin database backup endpoint

**Why before C:** users will lose data if Wave C or E goes wrong. Ship the safety net first.

**Scope:** One admin-only endpoint that returns a consistent zip of `softmedia.db` + settings JSON + library-config JSON + a manifest. Uses the SQLite online-backup API (`SqliteConnection.BackupDatabase`) so the dump is safe under concurrent writes — a raw `File.Copy` while EF holds the connection open can produce a partial / inconsistent file regardless of journal mode. (Note: SDD §2.3 mentions WAL, but the current connection string `Data Source=softmedia.db` does not actually enable WAL — the online-backup API is the right answer either way; this is a SDD-vs-code drift to resolve separately.)

**Behavior after this wave:**
- A new "Download backup" button appears in the existing Admin Dashboard section of [SettingsPage.tsx](../../src/SoftMedia.Client/src/pages/SettingsPage.tsx).
- Clicking it streams a `softmedia-backup-YYYYMMDD-HHMMSS.zip` to the browser.
- The zip contains: `softmedia.db` (consistent SQLite snapshot), `settings.json`, `libraries.json`, `manifest.json`. Cover-art and transcoded segments are explicitly **not** included — those are reproducible from source media.
- The endpoint is admin-only (403 for non-admins, not 404 — the action exists and the role check is the gate).

**Explicitly deferred to a later PR:** restore. Restore is destructive; it deserves its own design with a maintenance-mode flag.

Full task list: [docs/todos/feature-shortlist/02-admin-backup-endpoint.md](../todos/feature-shortlist/02-admin-backup-endpoint.md).

---

## Wave C — Per-library access control (per-user ACL)

**Why this needs care:** intersects every list endpoint and the parental-control filter. Doing it right means following the existing parental-control pattern *exactly* — not inventing a new one — so reviewers and future contributors recognise the shape.

**Important architectural note (from your reminder):** the codebase already has a per-user content-rating control pattern. The admin opens the user list at [src/SoftMedia.Client/src/components/admin/UserListTable.tsx](../../src/SoftMedia.Client/src/components/admin/UserListTable.tsx), clicks "Edit Ratings" on a user row, and a [RatingsModal](../../src/SoftMedia.Client/src/components/modals/RatingsModal.tsx) opens with per-type rating selectors. The library-access UI must follow this same shape — a sibling "Edit Library Access" button on the same user row, opening a sibling `LibraryAccessModal`. We are **not** changing `MyAccountPage.tsx` (the user's self-service page); ACL is an admin-only setting *about* a user, exactly like content ratings.

The backend pattern is identical: `RatingFilterExtensions.ApplyContentRatingFilter` turns a `UserRatingCeilings` into an EF `Where` clause; we mirror that with `LibraryAccessFilterExtensions.ApplyLibraryAccessFilter` that turns a `UserLibraryAccess` set into a `Where(m => allowedLibraryIds.Contains(m.LibraryId))`. `UserContentRatingProvider` reads `HttpContext.User` and caches on `HttpContext.Items`; we mirror that with `UserLibraryAccessProvider`. Admins bypass.

**Behavior after this wave:**
- New `UserLibraryAccess` table: `(UserId, LibraryId)` composite PK.
- **Default semantics: no rows = unrestricted.** A user with zero `UserLibraryAccess` rows sees every library, exactly as today. This is mandatory — shipping "rows = allow, no rows = deny" would lock out every existing non-admin user at deploy time.
- **Admins always bypass**, mirroring `UserContentRatingProvider.ResolveAsync` at [UserContentRatingProvider.cs:54-57](../../src/SoftMedia.Server/Services/Security/ContentRating/UserContentRatingProvider.cs#L54-L57).
- The user-management page gains an "Edit Library Access" button on each user row, next to the existing "Edit Ratings" button. The modal is a checkbox list of every library; saving an empty selection clears all rows (= unrestricted) rather than blocking the user from everything. Copy makes that explicit.
- Filtering applies to: `GET /api/v1/libraries`, `GET /api/v1/libraries/{id}`, `GET /api/v1/libraries/{id}/items`, `GET /api/v1/media/{id}`, `GET /api/v1/media/recent`, `GET /api/v1/media/hero`, `GET /api/v1/media/search`, and all `/api/v1/stream/*`, `/api/v1/audio/*`, `/api/v1/books/*`, `/api/v1/transcode/*`. The streaming endpoints already 404 on jail violation; we extend that to "library not in user's allowed set."
- Cascade: deleting a library removes its `UserLibraryAccess` rows. Soft-deleting a user retains the rows (consistent with the existing soft-delete behavior at [UsersController.cs:243](../../src/SoftMedia.Server/Controllers/UsersController.cs#L243)).

**Where the filter is applied — single source of truth:** the `IUserLibraryAccessProvider` is injected into `LibraryRepository` and `MediaRepository`, mirroring how `IUserContentRatingProvider` is currently injected at [LibraryRepository.cs:13-19](../../src/SoftMedia.Server/Services/Infrastructure/LibraryRepository.cs#L13-L19). Controllers do not call the provider directly — they call repositories, which apply the filter once before any other narrowing. This is why every list endpoint inherits the gate "for free."

Full task list: [docs/todos/feature-shortlist/03-per-library-acl.md](../todos/feature-shortlist/03-per-library-acl.md).

---

## Wave D — `.nfo` (Kodi/XBMC) sidecar metadata reader

**Why this is small:** the metadata routing system already supports primary + fallback providers (see the comic provider chain at [MetadataRouter.cs:191-262](../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs#L191-L262)). We add two new `IMetadataProvider` implementations that read local `.nfo` XML — no API quota, no network — and wire them as configurable fallbacks for Movie and TV. Most users won't notice; users coming from Sonarr/Radarr/Kodi setups will find that their pre-curated metadata "just works."

**Behavior after this wave:**
- For a movie file `Avatar (2009).mkv`, the scanner looks for `Avatar (2009).nfo` first, then `movie.nfo` in the same folder.
- For a TV episode `S01E03 - Title.mkv`, it looks for the sibling `.nfo` matching the file stem.
- For a series root (passed via the series scanner), it looks for `tvshow.nfo` inside the series folder.
- The NFO provider runs as a **fallback** by default (not primary), so users with OMDb / Wikidata / TVMaze keep their current behavior. Users who want NFO-first set `MovieProvider=Nfo` in settings.
- Two new settings: `MovieFallbackProvider` (default `"Nfo"`) and `TVFallbackProvider` (default `"Nfo"`). `"None"` disables the fallback.
- XML is parsed with XXE protection — DTD processing is disabled, max document size is capped at 1 MiB. Files that fail to parse log a warning and return null (the next provider in the chain runs).

Full task list: [docs/todos/feature-shortlist/04-nfo-metadata-reader.md](../todos/feature-shortlist/04-nfo-metadata-reader.md).

---

## Wave E — Persisted Playlists, Collections, and Watchlist

This is three sub-PRs — playlists, collections, watchlist — landing in that order. **Wave C must land first** because playlist visibility filtering must respect per-library access (a public playlist shared by a user with Music+Movies must not show its tracks to a child whose ACL only includes Kids Movies).

### E1 — User-owned Playlists

**Behavior after this sub-PR:**
- A user can create named playlists ("Workout Mix", "Saturday Night"), add audio tracks to them, reorder them by drag, and delete them.
- A playlist can be marked public — public playlists are visible to other users on the same SoftMedia server, but not editable by them.
- The audio queue's existing "Play" affordance now also accepts a server-stored playlist (the in-memory `audioStore.playPlaylist` already exists at [audioStore.ts](../../src/SoftMedia.Client/src/store/audioStore.ts) — we hydrate it from a server response).
- Playlists respect per-library ACL: when user B views user A's public playlist that contains tracks from a library B can't access, those tracks are silently stripped from the response (mirrors content-rating filtering: the playlist isn't 404'd, but blocked items are removed before render).
- Initial scope is audio tracks only. Movie/show playlists land later.

### E2 — Auto-generated movie Collections

This is the part that needs the most thought given your reminder. The user said "most users will want to use OMDb." That matters because **OMDb does not expose franchise/collection data**. I confirmed this by reading [OMDbProvider.cs](../../src/SoftMedia.Server/Services/Metadata/OMDbProvider.cs) — its parser at lines 352-469 maps `Year`, `Plot`, `Director`, `Genre`, `Rated`, `Poster`, `imdbRating`, `imdbID`, `Production`, `Runtime`, `Writer`, `Awards`, `BoxOffice`. There is no franchise / collection / `belongs_to_collection` field in the OMDb API at all (that's a TMDb-specific thing, and TMDb is forbidden by SDD §4.3).

**The OMDb gap and how we close it:** OMDb gives us a stable IMDb ID per movie (already promoted to `MediaItem.ImdbId`). Wikidata's `wdt:P179` ("part of the series") is the property we need, and Wikidata supports lookup *by IMDb ID* via `wdt:P345` — given an IMDb ID, one SPARQL query returns the parent series QID and its label. So even users on OMDb-primary get collection data via a small Wikidata-by-IMDb-ID resolver. This is a **per-movie one-shot SPARQL query**, run during enrichment, cached on `Collection.WikidataId` so it never repeats. Wikidata is keyless and already rate-limited via the existing `RateLimiterFactory`.

**For users who explicitly disabled Wikidata** (or whose movies have no IMDb ID): collections fall back to admin-curated manual mode. The admin can open a collection management page, create "The Lord of the Rings", and tick the three movies. This is also useful for personal groupings ("Tarantino films I own") that no provider will ever populate automatically.

**View behavior — the part you specified:**
- **Library view: unchanged.** Movies in a collection still appear as their own cards in the library grid. We do **not** group them into a collection card. This matches your reminder verbatim.
- **Movie details view: new "More from this collection" section.** Mirrors how [TVDetailView.tsx](../../src/SoftMedia.Client/src/components/details/TVDetailView.tsx) lists episodes within seasons — same horizontal-scroll list pattern (`HorizontalScrollList` component already at [src/SoftMedia.Client/src/components/ui/HorizontalScrollList.tsx](../../src/SoftMedia.Client/src/components/ui/HorizontalScrollList.tsx)), each card is a sibling movie in the collection ordered by `ReleaseDate` ascending. Clicking a card navigates to that movie's detail page. The section header reads "More from *The Lord of the Rings*" with the collection name italicised.
- **Collection home-page row (optional):** if at least one collection has ≥2 movies the user can see, a "Collections" row appears on the home page below "Recently Added", listing each collection as a single card with the collection's poster (the first movie's poster as fallback). Clicking it goes to a collection detail page (a thin wrapper that lists all movies in the collection).
- **No standalone "Collections" library type.** Collections are a cross-cutting aggregation, not a library — they appear inside Movie libraries.
- **Per-library ACL applies:** if user B can see Movie Library X but not Y, and a collection spans both, B sees only the X movies in that collection's "More from" strip. Same filtering rule as playlist items.

### E3 — Watchlist

**Behavior after this sub-PR:**
- Each user gets a personal "Watchlist" — a flag on `UserMediaInteraction`, surfaced on the home page as a row next to "Continue Watching."
- An "Add to Watchlist" / "Remove from Watchlist" button appears on every media detail view (movies, TV series, books, comics, albums).
- Per-library ACL applies — items the user can no longer see are silently stripped from their watchlist response.

Full task list: [docs/todos/feature-shortlist/05-playlists-collections-watchlist.md](../todos/feature-shortlist/05-playlists-collections-watchlist.md).

---

## Sequencing summary

```
Day 1:        Wave A (Hide Photo)              ← merge same day
Day 2-3:      Wave B (Backup endpoint)         ← merge end of day 3
Day 4-7:      Wave C (Per-library ACL)         ← longest single PR; isolated review
Day 8-9:      Wave D (.nfo reader)             ← can interleave with E1 if desired
Day 10-12:    Wave E1 (Playlists)
Day 13-15:    Wave E2 (Collections + OMDb→Wikidata bridge)
Day 16:       Wave E3 (Watchlist)
```

A and B are quick wins; C is the heavy review; D is small and self-contained; E ships in three small sub-PRs because the test surface is wide.

## Out of scope (deliberately deferred)

- **Restore endpoint** — pairs with B but ships separately (destructive, needs maintenance-mode design).
- **Smart playlists** ("Top 25 Most Played", "Recently Rated") — adds a query DSL; defer to v2.
- **Photo scanner implementation** — Wave A only hides; the actual Phase 2 scanner is its own future plan.
- **Collection editing for auto-populated collections** — auto-managed collections cannot be edited; if a user wants to override they create a manual collection. This avoids the merge-conflict problem when a re-scan reasserts auto data.
- **Cross-library playlists** of mixed types (movies + audio + episodes in one playlist) — initial E1 scope is audio-only.
