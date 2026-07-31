# Playlists v2, Search Ranking & Client Lint-Debt Clearance — 2026-07-27

Record of the playlist overhaul and global-search rework shipped across
2026-07-26/27 (commit `f67c76d` and the working tree that followed), plus the
maintainer decisions taken along the way. Written after the fact, per repo
convention, so the reasoning survives the session that produced it.

## Scope shipped

### Playlist redesign (visual parity)
- Playlist index cards and the detail page now match the app's design language:
  cover-art-forward cards, blurred-backdrop detail page using the same
  two-column shell as `MediaDetailLayout` (rail poster, full-width content),
  gradient Play + Shuffle pair mirroring `AlbumDetailView`.
- Playlists with no artwork of their own borrow a 2×2 mosaic of their tracks'
  album covers (`PlaylistCover`); 1–3 distinct covers render the first
  full-bleed rather than a mosaic with holes.
- The shared `BackButton` (`src/components/ui/BackButton.tsx`) is now the app's
  ONE back control — extracted from the media-detail treatment and applied to
  playlist and collection pages. It is a `<Link>` (middle-click, new-tab) and
  its label is fixed at "Back" by design: pages that need to name a destination
  do it in their heading, not in the control.

### Playlist features
- **Add tracks in place** (`AddTracksPanel`): library search inside the detail
  page; the old flow required hunting ListPlus buttons across the app.
- **Queue integration**: whole-playlist and per-row add-to-queue.
- **Filter within a playlist** once it has ≥10 tracks (the Jellyfin 800-track
  finding). Filtering is display-only; playback and reorder use the full list,
  and row numbers report true playlist positions.
- **Save a copy** for shared playlists (they were read-only dead ends). Copies
  are always private — sharing is an explicit act here. Name suffixing is
  capped against the server's 120-char limit (`lib/playlistNaming.ts`).
- **Smart playlists** (`PlaylistKind.Smart` + `SmartPlaylistRules` JSON on the
  row): membership is a query re-evaluated on every read. Five presets
  (Recently Added, Most Played, Favourites, Never Played, Recently Played).
  - **Privacy constraint (deliberate):** every play-derived filter/sort reads
    the OWNER's `PlaybackHistory`/`UserMediaInteraction`, never
    `MediaItem.PlayCount`/`LastPlayed` — those are all-user aggregates (see
    `LibraryRepository`), and using them would rank a private playlist by the
    household's listening. For the same reason smart playlists CANNOT be made
    public, and their rules are withheld from non-owners.
  - Membership mutations (add/remove/reorder) 400 on smart playlists; the
    client omits those affordances entirely rather than disabling them.
- **M3U export/import** (`M3uPlaylistFormat`): export writes server-side paths
  (what local players against the same library need); smart playlists export
  their current snapshot. Import matches exact path → parent-folder+filename →
  bare filename, and REFUSES ambiguous filename matches ("01.mp3" exists in
  every album folder — importing the wrong track silently is worse than
  reporting the line unmatched). Unmatched lines are always reported with a
  sample. Import never opens a path; it only matches text against library rows.
- **Custom covers** (`PlaylistCoverService`): uploads are decoded and
  re-encoded to WebP before touching disk — stored bytes come from our encoder
  (no polyglot/HTML smuggling through the media cache), EXIF/GPS is stripped,
  filename derives from the playlist id (no traversal surface), decode is
  gated by the same `ImageSafety` pixel budget as thumbnails.
- **Playlists in the Music library tab respect the FilterBar search** (the box
  was previously wired only to the disabled media-items query); media-only
  filters are hidden on that tab.

### Global search rework
- **No pinned categories.** Playlist hits, library-name hits and media groups
  merge on one relevance scale (tier 0 = name/title prefix, 1 = contains,
  2 = secondary-field match). Ties break personal-first (playlists, libraries,
  then media by `Library.Order`) — being the user's own construct wins ties,
  not contests. Client module: `lib/searchRanking.ts`.
- **No vanishing libraries.** `/media/search` now runs one bounded query per
  matching library instead of a flat global `Take(limit*5)` that let a strong
  library push everyone else past the cutoff. Group order (best tier, then
  library position) is explicit code, not a `GroupBy` side effect.
- **Match reasons.** Tier-2 items say why they matched ("Matched cast: …",
  "Matched genre: …"); reasons ride in a parallel map on
  `GlobalSearchResultDto` rather than polluting the shared `MediaItemDto`.
  Cast reasons resolve via one bounded follow-up query, not an Include.
- **Library names are searchable** — client-side over the cached, ACL-filtered
  library list (previously a library appeared only via the coincidence of its
  contents matching).
- FTS5 remains the planned follow-up for typo tolerance / weighted relevance;
  this ranking layer sits on top of whatever matching feeds it.

### Client lint-debt clearance (2026-07-27)
The repo-wide client baseline (100 problems: 71 errors, 29 warnings) is now
**zero**, with exactly two knowing surface-level suppressions and three
informational notes:
- All 18 `setState`-in-effect sites converted to render-time adjustment
  (react.dev "adjusting state when props change") — modals seed during render,
  MainLayout's drawer closes in the same pass as navigation, `LoadingImage`
  retries in the pass a token rotates, etc.
- Refs-during-render eliminated (ProgressBar measures in its mouse handlers;
  `useMediaHub`/`useSequentialReveal` sync refs in effects).
- Self-referencing callbacks (`AudioVisualizer` rAF loop, `useTts` chain,
  `useSequentialReveal` mutual recursion) now route through refs.
- Helpers moved out of component files so Fast Refresh works everywhere:
  `lib/sortDirection.ts`, `lib/scrollSelectionIntoView.ts`,
  `formatResumeTime` → `lib/utils.ts`, `components/reader/readerTheme.ts`,
  `components/reader/highlightColours.ts`.
- `MediaItem.metadata` is `Record<string, unknown>` (was `any`); consumers
  narrow explicitly.
- Artist-page backdrop pick is a hash of the artist id, not `Math.random()`
  during render (which re-rolled — and flickered — on every re-render).
- Suppressed knowingly, each with an in-code justification: HeroSection's
  `mediaToken` memo dep (real dependency the linter can't see lexically),
  `useSequentialReveal`'s atomic timer+state reset, and 3×
  `react-hooks/incompatible-library` notes on `useVirtualizer` (TanStack
  Virtual is un-memoizable by the React Compiler by design).

## Maintainer decisions
- **2026-07-27 — Non-music playlists are NOT wanted.** The schema stays
  type-agnostic (a v1 design choice), but no controller/UI work will extend
  playlists beyond audio. Treat any future "video playlists" idea as rejected
  unless the maintainer reopens it.
- Smart playlists stay private (see privacy constraint above).
- Playlists remain a Music-library tab, not a global sidebar entry.

## Verification state
- Server: 1754 tests green (includes SQLite-backed suites for the smart
  evaluator, search ranking, M3U format, cover service — SQLite deliberately,
  because EF InMemory evaluates LINQ client-side and hides translation
  failures; that exact failure mode shipped and was caught by integration
  tests once during this work).
- Client: 575 tests green across 81 files; `npm run build` clean;
  `npx eslint .` exits 0.
- **Not verified visually.** Everything above is test- and type-verified; the
  redesigned playlist surfaces, search dropdown and create-modal presets still
  deserve one human pass in the running app.

## Incident — 2026-07-27: test/scratch instances pruned a real backup

While attempting an isolated visual-verification boot (scratch SQLite DB via a
connection-string override), the instance's `BackupRotationService` — whose
backup directory anchors to the CONTENT ROOT, not the database — wrote a 24 KB
scratch backup into the real `data/backups` and its retention pass **pruned one
genuine (June-era, unpinned) backup** to make room. Investigation showed the
same had been happening silently on every integration-test run: each
`SoftMediaWebApplicationFactory` host boots the real rotation service, which
fires on its first poll pass because a fresh test DB carries no "ran today"
marker (four test-sized zips from the 2026-07-26 suite runs were found in the
real directory).

Remediation:
- All six junk zips (verified 561 KB test/scratch databases inside; real
  backups are ~7–8 MB) removed from `data/backups`; the pinned June backup and
  all four remaining real backups verified intact.
- `SoftMediaWebApplicationFactory` now strips the `BackupRotationService`
  registration — verified by running the integration suite and confirming the
  backups directory count is unchanged. `ImageCacheCleanupService` (the other
  destructive sweeper; under a test DB every real cache file looks orphaned) is
  currently safe only because of its 5-minute initial delay — if that delay
  ever shrinks, strip it in the factory too.
- Lost: one ~6-week-old unpinned backup generation. Not recoverable
  (`File.Delete`, no recycle bin). Recent coverage (Jul 19, two Jul 27) and the
  pinned Jun 2 backup are unaffected.

Standing rule recorded in project memory: a scratch server instance is NOT
isolated by a scratch database — backups, `task-status.json` and the wwwroot
cache all anchor to the repo tree.

## Known follow-ups
- FTS5 search indexing (typo tolerance, weighted fields).
- `SmartPlaylistRules.ArtistId` is implemented and tested but has no UI and no
  preset; `describeSmartRules` doesn't mention it. Becomes real work only if
  artist filtering is exposed.
- dnd-kit + virtualization for very-large playlists remains deferred (original
  author's call, still sound); row artwork is lazy-loaded instead.
