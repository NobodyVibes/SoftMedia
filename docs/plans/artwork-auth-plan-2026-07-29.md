# Artwork Authentication Plan — 2026-07-29

Require authentication for static artwork under `/cache/images/**` by extending the
EXISTING reduced-privilege media-token mechanism (WS-6) — no new auth machinery, no
cookies, no operator setup. Work items AA-WI-001..011.

> **STATUS: COMPLETE (2026-07-29, same day as spec + adversarial review).** Server suite
> 2001/0, client 580/0, `npm run build` green, live probes verified: anonymous poster →
> 401 (was 200), garbage token → 401, the previously exposed proxy/thumbnails sub-tree →
> 401, subtitles/trickplay still 404, API auth unchanged. Token-authorized 200s proven by
> the integration matrix (media token query, full token header; cast token correctly
> rejected). Notes: the two pre-existing L-3 ban-revocation integration tests ban via
> direct DB writes and were updated to invalidate the AA-WI-011 cache the way the real
> ban endpoint does. AA-WI-008's interactive checks (real Chromecast session, lock-screen
> art across a rotation) remain for the maintainer's next viewing session — degradation
> is cosmetic-only by design. AA-WI-010 (post-week sweep) intentionally open.
> Spec-to-implementation deviations: none. This file is retained as design history.

Original spec (ADVERSARIALLY REVIEWED — the review overturned the original
cast-token-widening decision and added the eligibility-cache prerequisite, see §3 #2 and
AA-WI-011):

## 1. Motivation and scope

Today `/cache/images/**` (posters, backdrops, stills, cast headshots, playlist covers,
and — unintentionally — the `proxy/` and `thumbnails/` sub-trees whose intended door is
the authorized `GET /api/v1/image/proxy`, ImageController.cs:12-14) is served by the
static-file middleware with no authentication. This plan gates it behind the media/cast
query token. `/cache/subtitles` + `/cache/trickplay` are already hard-404 (MC-WI-001)
and stay that way.

**Non-goals:** DLNA changes (verified: DLNA emits NO artwork at all —
DlnaContentDirectory.cs:269-289 writes title/class/`<res>` only); signed URLs; cookies.

## 2. Verified architecture facts the plan builds on

- **Media token:** minted `GET /api/v1/auth/media-token` (AuthController.cs:369-398),
  claims include `token_use=media`, NO role claim, default TTL 120 min
  (TokenService.cs:96-113). Client holds it in memory only (authStore.ts:17-83), renews
  at 75% TTL (api.ts:158-218), and the whole app already hard-depends on it.
- **Query lift:** `IsMediaRoute` (ServiceCollectionExtensions.cs:43-54) is the single
  source of truth for which paths may carry `?token=`/`?access_token=`;
  `OnMessageReceived` (:133-160) lifts it, `OnTokenValidated` (:161-236) enforces
  media = GET/HEAD on media routes, cast = ONLY `/api/{v1/}transcode/{id}` +
  `/api/v1/stream/{id}` (:222-224). `/cache/**` is currently NOT a media route.
- **Static pipeline:** `UseAuthentication` runs at Program.cs:315, the MC-WI-001 cache
  gate at :362-377, `UseStaticFiles()` at :379. CRITICAL: for a static (non-endpoint)
  path the JwtBearer handler never runs on its own — the new middleware must call
  `context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme)` explicitly.
- **Client URL plumbing:** `attachAuthToApiUrl` (lib/mediaImageUrl.ts:22-32) is the
  shared choke point but early-returns anything not starting `/api/v1/` — every
  `/cache/images` URL passes through tokenless today. `useMediaTokenRefresh.ts` already
  re-renders consumers on token rotation.
- **Materialized caches** (HeroCache / LibraryRecentCaches) store rendered DTO JSON with
  bare `/cache/images/...` strings (LibraryService.cs:472-493,
  RecommendationService.cs:360-372). A per-user expiring token can therefore NEVER be
  baked server-side — tokens attach at client render time only.
- **PWA:** no runtimeCaching; `navigateFallbackDenylist` already excludes `/cache/`
  (vite.config.ts:45-49). Service worker is a non-issue.
- **PlaylistCoverService.cs:114** already emits `?v={stamp}` — token append must go
  through `URL`/`searchParams` (which `attachAuthToApiUrl` already uses), never string
  concat.
- **Eligibility recheck cost (review finding):** `OnTokenValidated` calls
  `IsTokenUserEligibleAsync` on EVERY media-token request
  (ServiceCollectionExtensions.cs:78-88, invoked :207) — a scoped `AppDbContext` +
  `Users.AnyAsync` per request. That is the audit-L-3 revocation point. Today it costs
  one query per HLS segment; with images gated, a 200-poster grid would cost 200 DB
  hits per render. AA-WI-011 (prerequisite) caches it.
- **No global rate limiter** — `AddRateLimiter` registers endpoint-attached policies
  only (ServiceCollectionExtensions.cs:554-570, no `GlobalLimiter`), so static image
  requests are unthrottled today and remain so after this change. Auth rejection on the
  static path is cheap (HMAC validation + cache lookup once AA-WI-011 lands).
- **Native-app alignment:** the native-readiness plan's confirmed foundation is exactly
  this scoped-token model (docs/plans/native-app-readiness-plan-2026-07-21.md:14 —
  "capability-negotiated stream plans, scoped token model"); native clients must
  implement media-token handling regardless, so this plan adds no second mechanism.

## 3. Design decisions (settle before coding, defaults chosen here)

1. **Status code for unauthenticated artwork: 401, not the 404 anti-probe default.**
   Rationale: artwork paths are not probe-sensitive (ids are GUIDs already exposed to
   any authenticated user), a 401 lets the client distinguish "token expired → refresh
   and retry" from "file genuinely missing", and `LoadingImage` degrades to its fallback
   on any error either way (LoadingImage.tsx:78-80). Document the deviation next to the
   404 rule in Program.cs.
2. **Cast-token scope is NOT widened — the cast poster carries the MEDIA token.**
   (REVISED by the adversarial review; the original draft widened cast-token scope to
   image GETs.) Rationale: every cast load — including episode auto-advance — goes
   through a fresh `POST /transcode/{id}/plan`, and the sender rebuilds the poster URL
   at that moment while holding a live media token (renewed at 75% of its 120-min TTL,
   api.ts:158-218). So the poster fetch always happens inside token validity, and the
   cast token keeps its razor scope (three stream/transcode prefixes,
   ServiceCollectionExtensions.cs:222-224) completely untouched — strictly less new
   attack surface. Degradation if a receiver ever re-fetches the poster hours later: a
   blank cast-screen poster, cosmetic only; playback is unaffected. FALLBACK (only if
   AA-WI-008 live QA shows real receivers re-fetching after expiry): widen cast scope to
   GET `/cache/images/**` — and note the tighter filename-GUID==cast_media scoping does
   NOT work, because episode casts show the SERIES poster (`tv/{seriesId}_poster.jpg`)
   while `cast_media` holds the episode id.
3. **Client keeps `?access_token=` param via the existing helper** (both param names are
   accepted by `OnMessageReceived`, :139-148) — least churn, one choke point.
4. **Accepted cost — cache revalidation on token rotation:** tokenized URLs change every
   ~90 min, so browsers re-request art; static files carry ETag/Last-Modified, so these
   are 304s on a LAN, not re-downloads. If it ever matters, a follow-up can strip the
   token from the browser cache key via a service-worker fetch handler — explicitly out
   of scope now.

## 4. Work items

### AA-WI-001 — Server: authentication gate for `/cache/images`  (P1, server)
Extend the MC-WI-001 middleware block (Program.cs:362-377): for paths under
`/cache/images`, run `AuthenticateAsync(JwtBearerDefaults...)`; require an
authenticated principal whose `token_use` is `media` or `cast` (or a header-authed full
session); else 401 with no body. Subtitles/trickplay 404 branch unchanged and checked
FIRST. Keep the "images public by design" comment removed/rewritten.
**Verify:** anon GET real poster → 401; with valid media token → 200; expired token →
401; header Bearer full token → 200.

### AA-WI-002 — Server: route plumbing  (P1, server)
Add `/cache/images` to `IsMediaRoute` (ServiceCollectionExtensions.cs:43-54) so the
query token is lifted and media-token validation accepts the path (:185). The
cast-token branch (:212-236) is NOT touched (decision #2 revised).
**Verify:** unit tests on `IsMediaRoute`; integration: full access token in a query
string on an image path is still rejected (:170-177 behavior preserved); a CAST token
on an image path is still rejected (scope unchanged).

### AA-WI-003 — Server: integration tests  (P1, tests)
Update `CacheStaticServingTests` (:35-57 currently asserts anonymous 200 — flip to the
new matrix): anon → 401; media token query → 200; CAST token query → 401 (scope stays
locked, decision #2); full access token via query → 401; garbage token → 401; banned
user's still-valid media token → 401 within the AA-WI-011 cache TTL;
`/cache/subtitles`+`/cache/trickplay` still 404 (with and without tokens). Factory
needs a helper to mint a media token for a seeded user (reuse TokenService through DI).

### AA-WI-004 — Client: widen the shared helper  (P1, client)
`attachAuthToApiUrl` (mediaImageUrl.ts:22-32): accept `/cache/images/` in addition to
`/api/v1/`. Update `mediaImageUrl.test.ts:49-50` and the :103-109 regression note
(the invariant becomes "never prefixes, only appends"). This alone fixes every consumer
in the helper table: `LoadingImage`, `MediaCard`, `HeroSection` (incl. the :145 CSS
`background-image`), `MediaDetailLayout`, `GlobalSearchResults`, collections, playlist
covers, `MovieEndOverlay`, `NextEpisodeOverlay`, mediaSession artwork resolution.

### AA-WI-005 — Client: kill the `/cache` bypasses + dedupe  (P1, client)
Route every raw-path render through the shared helper and DELETE the special-casing:
- `CastStripItem.tsx:19-25` (explicit `/cache/` early-return — cast headshots)
- `TVDetailView.tsx:547-571` (four bypasses: episode stills ×2, bare returns, season
  posters)
- `BookDetailView.tsx:85`, `ComicSeriesDetailView.tsx:97`, `MovieDetailView.tsx:25,41`
- The FIVE duplicated 4-line `getImageUrl` helpers — `PersistentPlayer.tsx:177-187`,
  `QueueList.tsx:100-106`, `SortableQueueItem.tsx:37-43`,
  `SortablePlaylistItem.tsx:81-87`, `AddTracksPanel.tsx:74-79` — replace all five with
  one import from `mediaImageUrl.ts` (their `/api` branch is `attachAuthToApiUrl`
  already; the `/cache` branch becomes it too).
**Verify:** grep for `'/cache'` in client src returns only the helper + tests.

### AA-WI-006 — Client: Chromecast + mediaSession artwork  (P1, client)
- Cast: `VideoPlayer.tsx:2473-2475` builds the absolute poster URL — run it through
  `attachAuthToApiUrl` so it carries the MEDIA token current at load time (decision #2
  revised); `useCast.ts:211-213` unchanged. Do NOT use the cast token here.
- mediaSession: artwork already flows through `attachAuthToApiUrl` (VideoPlayer.tsx:
  1814-1815, PersistentPlayer.tsx:630) → carries the media token after AA-WI-004.
  Confirm `useMediaTokenRefresh` re-sets `navigator.mediaSession.metadata` on rotation
  (useMediaSession.ts:172) so the OS shell never holds a stale URL; wire the
  subscription if any of these components lack it.
**Verify:** live cast session > 2 h shows poster throughout; lock-screen art survives a
token rotation.

### AA-WI-007 — Client: test updates + new coverage  (P1, tests)
Update `HeroSection.test.tsx` (:37-104 assert bare `/cache` URLs) and any other
snapshot/URL assertions the type-check surfaces. New tests: helper tokenizes
`/cache/images` and preserves existing query strings (`?v=` playlist covers); a
detail-view smoke test that episode stills/cast headshots render tokenized URLs.

### AA-WI-008 — Live QA pass  (P1, verification)
With the dev server + real library: grids/hero/detail pages/cast strips/episode stills/
playlist covers/photos/music all render; logout → direct image URL stops working
(401); token expiry mid-session self-heals on renewal; Chromecast poster (AA-WI-006);
DLNA browse/play still works (expected no-op — DLNA has no artwork); check the server
log for a 401 storm (would indicate a missed consumer).

### AA-WI-009 — Docs + history  (P2)
CHANGELOG `[Unreleased]` Security entry; update the Program.cs MC-WI-001 comment and
`CacheStaticServingTests` doc comment; note in `docs/api/*.md` (external API docs
already document the media token) that artwork now requires it; tick this plan +
`.docs/project_checklist.md`; update the metadata-cache memory note (the "images public
by design" statement becomes historical).

### AA-WI-010 — Post-change sweep  (P2, optional)
After a week of real use, check the Cache Usage card + logs: no 401 noise, no broken-art
reports, browser-cache behavior acceptable (decision #4). If rotation-driven 304 chatter
is noticeable, spec the service-worker cache-key follow-up then — not before.

### AA-WI-011 — Server: user-eligibility cache  (P1, PREREQUISITE — land before AA-WI-001)
`IsTokenUserEligibleAsync` (ServiceCollectionExtensions.cs:78-88) currently costs one
scoped-DbContext + `Users.AnyAsync` per media/cast-token request (§2). Introduce a small
singleton eligibility cache (e.g. `IUserEligibilityCache` over `IMemoryCache`): per-user
entry, short TTL (30–60 s), consulted by the recheck; EAGERLY invalidated by the admin
ban / soft-delete / un-approve write paths (UserManagement service) so revocation stays
effectively instant — the audit-L-3 guarantee is preserved (worst case = cache TTL,
still far tighter than the 120-min token lifetime it was built to shorten). This also
removes today's per-HLS-segment DB hit as a side benefit.
**Verify:** unit tests — cache hit skips DB; ban invalidates immediately; TTL expiry
re-queries. Integration: banned user's media token stops working on both a stream route
and an image within one request of the ban.

## 5. Sequencing

One focused session: **AA-WI-011 first** (independent, immediately beneficial) →
AA-WI-001..003 (server, suite green) → AA-WI-004..007 (client, suite + `npm run build`
green) → AA-WI-008 live QA → AA-WI-009. The server gate and the client helper MUST land
together (gating without the client change blanks all artwork); if split across
commits, gate last.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Grid loads hammer the DB via the per-request eligibility recheck | AA-WI-011 cache (prerequisite) — ~1 query/user/min instead of 1 per image |
| A missed raw-`/cache` consumer goes blank | AA-WI-005 grep gate + AA-WI-008 401-storm check; `LoadingImage` fallback degrades gracefully |
| Cast poster blank if a receiver re-fetches after media-token expiry | Cosmetic only (playback unaffected); every cast load rebuilds the URL with a fresh token; documented fallback = widen cast scope (decision #2) |
| Static-path auth never runs | Explicit `AuthenticateAsync` in middleware (fact §2); integration tests are the proof |
| Token in URL leaks (server logs, browser devtools, right-click "copy image address" sharing a live token) | Accepted since WS-6 for streams — GET-only, role-less, 120-min token; strictly better than today's no-auth. Never log query strings on the static path |
| Residual gap: gated images are not per-user ACL/rating filtered (static middleware cannot join UserLibraryAccess/MaxRating) | Accepted and documented: GUID discovery requires catalog access — search (R-WI-017), hero (B-19), and browsing are all ACL+rating filtered, so a restricted user has no API path to learn a hidden item's GUID; filenames are unguessable v4 GUIDs. Full per-user filtering would require controller-served images (per-request DB joins, loses static-file ETag/sendfile) — rejected on performance grounds, revisit only with evidence |
| Materialized caches serve stale tokens | Impossible by design — tokens are never server-baked (fact §2) |
