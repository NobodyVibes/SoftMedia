# SoftMedia 1.0 — Master Implementation Plan

_Lead architect plan consolidating six per-feature blueprints + adversarial critiques. Date: 2026-06-16. Branch base: `security/hardening-wave-2` (cut feature branches from `main`)._

## 1. Overview & Guiding Constraints

This plan delivers six features: **Continue Watching**, **External/sidecar subtitles** (2 phases), **Photos library**, **HDR software tone-mapping fallback**, **VAAPI hardware acceleration**, and **Docker image + `/config` data layout**. The critiques surfaced ~12 blockers and many majors that are folded in below as concrete corrections; anything a critique marked false/hallucinated is dropped.

### Non-negotiable conventions (apply to every task)
- **Backend:** .NET 8, EF Core + SQLite. New entity/field => EF migration via `dotnet ef ... --project src/SoftMedia.Server --startup-project src/SoftMedia.Server`; **set `JwtSettings:Secret` (user-secrets/env) first**; **stop the running backend** (it locks `bin/`). DI in `Extensions/ServiceCollectionExtensions.cs`.
- **Scanners:** derive `BaseMediaScanner`, declare `SupportedType`/`SupportedExtensions`/`DisplayName`; dispatched at `ScannerOrchestrator.cs:35`. **A `LibraryType` with no registered scanner silently no-ops** — registration is load-bearing.
- **Frontend:** React + Vite + Tailwind v4. **`bg-primary`/`text-primary`/`bg-background` render NOTHING** — use explicit hex (`bg-[#007AFF]`, brand gradient `#007AFF`→`#8A2BE2`). Client builds to `src/SoftMedia.Client/dist`; **no step copies dist → server `wwwroot`** today.
- **Gitignore trap:** `.gitignore` anchored rules `/data/` and `/media/` silently exclude such dirs on Windows. Do NOT introduce a repo-root `data/` dir; for Docker use named volumes or `./softmedia-config`.
- **Path jail:** file access via `StreamSecurityService.IsPathAuthorized`. Caches under `wwwroot/cache`; backups OUTSIDE wwwroot. DB `Data Source=softmedia.db` resolved vs CWD (`Program.cs:280-288`).
- **Use `127.0.0.1:5011`** not `localhost` for manual verification (IPv6 stall, per MEMORY).

### Verified cross-area facts (checked against source this session)
- `TranscodeProfileBuilder.cs`: `useToneMappingPipeline` @133, `GetHardwareDecodeOptions` call @145 / def @395, filter-assembly branches @226/279/307, the **standalone** `if (!useToneMappingPipeline)` @210 (must become `else if`), `useFmp4 = skipTonemapping || useAv1` @338, `GetScaleFilter` @494, `GetVideoEncoder` @542 with unconditional `("av1",_) => hevc` @554. **`EnableAV1Encoding` is NOT read here.**
- `Program.cs`: single `app.UseStaticFiles()` @268 (serves `/cache` from `wwwroot/cache` only); restore-apply DB-path block @277-288; `MapControllers`/`MapHub` @270-271.
- `MetadataAggregator.ProcessMetadataResultAsync`: `deferImageCaching || !refreshImages` early-return @135 sits **before** `MetadataHash` stamping @154.
- `SettingsService.InitializeDefaultsAsync`: seed loop @185-191 is **additive** (skips existing keys) — description changes to existing rows need explicit UPDATE.

---

## 2. FOUNDATION FIRST — Data-Root Decision (Docker `/config`)

**This decision gates Photos thumbnail/full-res serving and Subtitles cache placement. It ships before the container and before any path-touching feature so bare-metal/Windows users never face a later data migration.**

### Decision
Introduce **one** singleton `IDataPathProvider` (`src/SoftMedia.Server/Services/Infrastructure/IDataPathProvider.cs`, interface+impl co-located like `BinaryLocationService.cs`). It reads `SOFTMEDIA_DATA` env → `DataDirectory` config key. **When unset, every path equals today's exact value** (byte-for-byte legacy). Properties: `DataRoot`, `CacheRoot`, `BackupRoot`, `TaskStatusPath`, `TranscodeTempRoot`, `DictionaryPath`, `WebServingRoot`, plus `ResolveDbPath(connString)` and `EnsureSubdir(params string[])`.

### Critique-driven corrections folded in
- **BLOCKER (cache serving):** `app.UseStaticFiles()` only serves `/cache` from `wwwroot/cache`. Moving `CacheRoot` outside wwwroot in container mode 404s every `/cache/**` URL. **Resolution (hard prerequisite, not deferred):** add a second static provider after `Program.cs:268` — `app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(paths.CacheRoot), RequestPath = "/cache" })` active only when `CacheRoot != wwwroot/cache`. Add an explicit `WebServingRoot` property (`wwwroot` legacy; `Path.GetDirectoryName(CacheRoot)` container) so MusicImageService/AudioController rebase against it explicitly rather than deriving per-call.
- **BLOCKER (`GetLiveDbPath`):** Do **NOT** route `BackupService.GetLiveDbPath()` (@367-374) through `ResolveDbPath` — it reads the live `SqliteConnection.DataSource` and correctly reflects dev user-secrets (`app.db`). Only the two startup-time, no-live-connection sites use `ResolveDbPath`: `Program.cs:284` and `ArtworkRepairOnRestoreService.cs:99`.
- **BLOCKER (TaskStatusPersistence):** `TaskStatusPersistenceService` hardcodes `_path = TaskStatusStore.DefaultPath()` at field init (~line 104). Refactor its constructor to inject `IDataPathProvider` and set `_path = paths.TaskStatusPath`; route the `Program.cs:303-304` load through the same singleton so load/save agree.
- **MAJOR (BackupService second site):** patch BOTH `GetSettingAsync` default arg (~339) AND the L-20 fallback `Path.GetFullPath(DefaultBackupDir, CWD)` (~356) to `paths.BackupRoot`; add `IDataPathProvider` ctor param. Keep the L-20 "reject inside wwwroot" guard.
- **MAJOR (bare-metal SPA):** `MapFallbackToFile("index.html")` needs `wwwroot/index.html`. Add an MSBuild pre-publish target in `SoftMedia.Server.csproj` copying `../SoftMedia.Client/dist/**` → `wwwroot/` for non-Docker publishes; log a startup warning if `wwwroot/index.html` is absent.
- **MISSING:** `MusicScanner.cs:356` stores `album.CoverArtPath` as an **absolute** path on every scan → invalidated on any data-root move. Change MusicScanner to store the web-relative `/cache/images/music/...` form (resolved through `MusicImageService.ResolveToFileSystemPath`) so it survives container migration; `ArtworkRepairService` re-extracts otherwise.
- **MINOR (.dockerignore):** explicitly exclude `src/SoftMedia.Server/Data/*.db*`, `Data/backups/`, `Data/task-status.json` (present in repo, would leak dev DB into image).
- **MINOR (Dockerfile syntax):** no pseudo-ternary; use `ARG TARGETARCH` + shell `if [ "$TARGETARCH" = "amd64" ]; then ...`; **default to the FREE `intel-media-va-driver` + `mesa-va-drivers`** — Debian non-free `intel-media-va-driver-non-free` (Intel HW *encode*) is opt-in only via `ARG ENABLE_NONFREE_INTEL=0`, see §7.1; `--no-install-recommends`.
- Drop `appsettings.Development.json` from the file inventory (does not exist; dev uses user-secrets).

### No EF migration (data-root is path plumbing only).

---

## 3. Sequencing & Phasing (dependency-ordered)

| Phase | Contents | Rationale |
|---|---|---|
| **P0 Foundation** | `IDataPathProvider` + rebase all path sites + second `/cache` static provider + SPA fallback + bare-metal dist copy | All path-touching features (Photos serving, Subtitles cache) build on this. Ships before container so bare-metal users never migrate. |
| **P1 Docker image** | Dockerfile, compose, `.dockerignore`, GHCR multi-arch workflow, deploy docs | Uses P0 paths. Exposes `/dev/dri` — prerequisite for VAAPI. |
| **P2 Quick wins (parallel)** | Continue Watching (independent); Subtitles **Phase A** (sidecar, independent of P0 except cache dir is irrelevant for sidecars-in-library — but external cache dir = P0) | Independent, low-risk, no transcode/path conflicts. CW touches no foundation. |
| **P3 Transcode pipeline (sequential, adjacent)** | HDR software tonemap **first**, then VAAPI **second** | Both edit the same `TranscodeProfileBuilder` filter-assembly + `useToneMappingPipeline` gate @133. Sequencing avoids merge conflicts; HDR is the cheap correctness fix, VAAPI reuses the refactor + needs P1's `/dev/dri`. |
| **P4 Photos** | Multi-step arc: data model → scanner → metadata persistence → thumbnail/serving → DTO → client | Serving depends on P0 data-root; EXIF arc must land atomically or PhotoDetailView stays blank. |
| **P5 Subtitles Phase B** | OpenSubtitles provider + on-demand download + first-play trigger | Depends on Phase A's track-merging and P0's external-subs cache dir under data-root. |

Within a phase, tasks follow their `dependsOn`. P2 and P3/P4 can overlap across engineers since they touch disjoint files (CW=controllers/hooks; transcode=`TranscodeProfileBuilder`; photos=scanner/DTO). **P3a (HDR) and P3b (VAAPI) MUST NOT be developed in parallel** — same file.

---

## 4. Per-Feature Sub-Plans

### 4.A Foundation — Data-Root (`IDataPathProvider`)
**Data model:** none (no migration).
**Backend:**
- Create `Services/Infrastructure/IDataPathProvider.cs` (interface + `DataPathProvider(IConfiguration, IWebHostEnvironment)`). Legacy-mode values must equal: `CacheRoot=(WebRootPath??CWD/wwwroot)/cache`, `BackupRoot=./data/backups`, `TaskStatusPath=CWD/data/task-status.json`, `TranscodeTempRoot=CWD/transcode-temp`, `DictionaryPath=<sibling-of-wwwroot>/data/dictionary.json`, `WebServingRoot=wwwroot`. `ResolveDbPath` uses `SqliteConnectionStringBuilder`, rebases relative `DataSource` onto `DataRoot` (container) or CWD (legacy).
- Register singleton in `ServiceCollectionExtensions.AddMediaServices` after `AddHttpClient()` (~line 222).
- Rebase **writers**: `ImageCacheService.cs:65`, `TrickplayService.cs:53-56`, `ThumbnailService.cs:23-26`, `MusicScanner.cs:344-348` (+store web-relative CoverArtPath @356), `ImageController.cs:73-76` → `paths.EnsureSubdir(...)`.
- Rebase **readers/jails**: `MusicImageService.cs:171-181`/`198-210` (use `WebServingRoot` + trailing-sep anchor), `AudioController.cs:60-64`, `ArtworkRepairService.cs:58-59`, `DictionaryService.cs:104-113`.
- Rebase **backups/state/temp/creds/restore-DB**: `BackupService` (339 AND 356, +ctor param, keep L-20), `TaskStatusPersistenceService` (ctor inject + `Program.cs:303-304`), `TranscodeService.cs:54`, `DbInitializer.cs:148` (resolve from scope), `Program.cs:277-288` + `ArtworkRepairOnRestoreService.cs:99` via `ResolveDbPath`. Leave `AppDbContextFactory.cs:16` literal (design-time) + add comment.
- `Program.cs`: add second `/cache` static provider (when CacheRoot moved) + `MapFallbackToFile("index.html")` AFTER `MapControllers`/`MapHub` (must sit after auth middleware so the forced-password-change gate @250-266 isn't bypassed; verify `/api/x` still returns JSON 404).
- `SoftMedia.Server.csproj`: pre-publish target copying client `dist` → `wwwroot`.
**Client:** none functional (optional vite.config.ts comment).
**Settings:** `SOFTMEDIA_DATA` (env), `DataDirectory` (config, env wins), `ConnectionStrings:DefaultConnection`, `ASPNETCORE_URLS=http://+:8096`, `JwtSettings__Secret`, `ForwardedHeaders__TrustedProxyNetworks`. Add commented `DataDirectory` to `appsettings.json`.
**Packages:** none.
**Edge cases:** read-only rootfs (temp/creds/task-status must move to writable volume); absolute CoverArtPath rows from prior bare-metal runs (repair re-extracts); SPA fallback must not shadow `/api`/`/cache`/`/hubs`; index.html must not be immutable-cached (PWA autoUpdate).
**Tests:** `DataPathProviderTests` (unset==legacy; set→rebases; relative DB-source). Update 7 fixtures that construct services directly to supply a fake `IDataPathProvider` over their temp WebRootPath: `ImageCacheServiceSsrfTests`, `TrickplayServiceTests`, `ArtworkRepairServiceTests`, `MusicImageServiceArtistFallbackTests`, `AudioControllerCoverArtTests`, `ImageControllerSsrfTests`, `BackupServiceTests` (L-20 fallback assertion now sourced from provider). SPA-fallback integration test (`/library`→index.html 200; `/api/x`→JSON 404; `/cache` not shadowed).

### 4.B Docker image + compose (P1)
**Backend:** none beyond P0.
**Create:** `/Dockerfile` (stage1 `node:22` build client WITH devDeps → dist; stage2 `dotnet/sdk:8.0` copy dist→server wwwroot, `dotnet publish -c Release -o /app`; stage3 `dotnet/aspnet:8.0` debian-slim, `apt-get install --no-install-recommends ffmpeg` + free `mesa-va-drivers` + `intel-media-va-driver` by default (non-free `intel-media-va-driver-non-free` opt-in via `ARG ENABLE_NONFREE_INTEL=0`, amd64 only — §7.1); `COPY` FFmpeg/x264/x265 license texts to `/app/THIRD-PARTY`; `ENV SOFTMEDIA_DATA=/config ASPNETCORE_URLS=http://+:8096`; `VOLUME /config`; `EXPOSE 8096`; explicit `COPY --from=client /…/dist /app/wwwroot`; non-root user). `/docker-compose.yml` (named volume `softmedia_config:/config` NOT `./data`; media `:ro`; commented `/dev/dri` + `group_add: [video, render]`; env `JwtSettings__Secret`, `ConnectionStrings__DefaultConnection=Data Source=/config/softmedia.db`, commented `ForwardedHeaders__TrustedProxyNetworks`). `/.dockerignore` (incl. `Data/*.db*`, `Data/backups/`, `Data/task-status.json`, bin/obj/node_modules/dist/.git/docs). `.github/workflows/docker-publish.yml` (buildx+QEMU, amd64+arm64, push on tag/main, dry-run on PR). `docs/user-docs/docker.md` + README pointer (server-side `MapFallbackToFile` vs workbox navigateFallback distinction; `--generate-jwt-secret`; creds now under `/config/ADMIN_CREDENTIALS.txt`).
**Edge cases:** no `/dev/dri` → software transcode (don't hard-require); arm64 has no intel-media driver (software/mesa only for 1.0); JwtSettings:Secret empty → fail fast.
**Tests:** docker build smoke (CI), ffmpeg on PATH, wwwroot has index.html; multi-arch buildx dry-run on PR.

### 4.C Continue Watching (P2, independent)
**Data model:** none (reads existing `PlaybackPosition`/`LastPlayed`/`IsWatched`). Optional composite index DEFERRED.
**Backend:**
- `IUserMediaInteractionService.cs` (after line 12): add `Task<IReadOnlyList<UserMediaInteraction>> GetInProgressAsync(Guid userId, int limit)`. **Style note (critique minor):** existing methods return `IEnumerable`; `IReadOnlyList` is an intentional divergence — comment it.
- `UserMediaInteractionService.cs`: implement — `AsNoTracking().Where(!IsWatched && PlaybackPosition!=null && PlaybackPosition>0).OrderByDescending(LastPlayed).Take(Math.Min(limit*4,200))`. **Document the over-fetch is best-effort** (heavy-pruning users may get < limit). Note `LastPlayed` is set on every `UpdateProgressAsync` so null-ordering isn't a practical issue.
- **NEW prerequisite (critique blocker):** `RecommendationService.IsEpisodeComplete` is **private** — cannot be called. Create `Helpers/MediaCompletionHelper.cs` with `static bool IsComplete(double? position, double duration, double? creditsStart)` encoding the 0.95 + CreditsStart logic (copied verbatim from `RecommendationService.cs:242-266`); refactor RecommendationService to call it too (prevent divergence).
- Create `Controllers/ContinueWatchingController.cs`: `[Authorize]` (plain, read-only — NOT `WriteState`), `[Route("api/v1/continue-watching")]`. **Critique clarification:** constructor adds `IUserMediaInteractionService _interactions` ON TOP of WatchlistController's `AppDbContext`+`IUserLibraryAccessProvider`+`IUserContentRatingProvider` (WatchlistController does NOT inject the interaction service). Steps: clamp limit [1,50]; `GetInProgressAsync`; load MediaItems with `.Include(MediaItemGenres).ThenInclude(Genre).Include(m=>m.Series)` (**required** so episode posters fall back to series), `.Where(ids.Contains && Type!=Audio/Album/Artist)`, `ApplyLibraryAccessFilter`, `ApplyContentRatingFilter`; **iterate `candidates` IN ORDER** (preserves LastPlayed desc — do NOT `items.GroupBy`); apply completion exclusion via `MediaCompletionHelper`; dedupe episodes by non-null `SeriesId` (orphan episodes with null SeriesId treated as movies); `MediaItemDto.FromMediaItem(m, MediaConstants.Routes.ImageProxy, interaction)` THEN **manually set** `dto.PlaybackPosition` + `dto.Progress = m.Duration>0 ? position/m.Duration*100 : null` (use raw `m.Duration` not `dto.Duration` string) — **this manual step is the most error-prone; WatchlistController has no analog**; `Take(limit)`.
**Client:** `services/continueWatchingService.ts` (mirror watchlistService, `/continue-watching`); `useContinueWatching` in `useLibrary.ts` queryKey `['continueWatching']`; `ContinueWatchingRow` in `HomePage.tsx`, rendered FIRST in user-state block (@108-111) above WatchlistRow, self-suppress when empty/loading. MediaCard already renders the resume bar from `item.progress`.
**Edge cases:** music excluded by Type; ACL/rating strip leaves interaction row intact; near-complete excluded; v1 scope **video-only (Movie+Episode)** unless maintainer confirms Book/Comic (open question).
**Tests:** `ContinueWatchingControllerTests` mirroring WatchlistControllerTests — ordering, watched/zero-position/near-complete exclusion, ACL strip (row preserved), music exclusion, episode dedupe, **Progress/PlaybackPosition populated (seed Duration>0, e.g. 3600.0)**, limit clamp.

### 4.D Subtitles Phase A — sidecar discovery & serving (P2)
**Data model (MIGRATION `AddExternalSubtitleTracks`):** add `IsExternal(bool=false)`, `ExternalPath(string?)`, `Source(string?)`, `Format(string?)` to `Models/SubtitleTrack.cs`. Cascade already deletes with parent (`AppDbContext.cs:330-334`).
**Backend:**
- `Constants/MediaExtensions.cs`: add `Subtitle = {srt,ass,ssa,vtt,sub,idx}`. **Do NOT add to `.All`** (would mis-ingest subs as media via `LibraryWatcher.IsMediaFile:531-538`). Comment the coupling.
- Create `Helpers/SidecarSubtitleDiscovery.cs` (same-dir + `Subs/`/`Subtitles/`; lang/forced/sdh parsing, ignore 4-digit years; `MediaPathSafety.HasArgumentInjectionRisk` guard; reuse FileNameParser).
- Create `Services/Media/SidecarSubtitleService.cs` (the ONLY one — not in Services/Scanning) `SyncSidecarsForItemAsync`: reconcile add/update/remove by `ExternalPath`, never touch embedded rows, assign **negative pseudo-indices starting at -100, decrementing** (resolve the blueprint contradiction in favor of `-100` base; share const `ExternalSubtitleIndexBase=-100`). File-existence reconcile prunes deleted `.srt`.
- **CRITICAL ordering (critique blocker):** call `SyncSidecarsForItemAsync` AFTER `context.MediaItems.Add(movie/episode)` (MovieScanner ~@80, TvScanner ~@222) so the entity is tracked (FK valid). Gate behind `EnableSidecarSubtitles` read once per scan via a new `protected bool _enableSidecars` on `BaseMediaScanner` set alongside `_strictEnrichment` (@89-94). Inject `ISidecarSubtitleService` into both scanners' constructors.
- `IMediaRepository` (+impl): add `GetExternalSubtitleTracksAsync(Guid mediaItemId)` (critique missing step).
- `MediaTracksController.ExtractTracksAsync` currently takes `(string path)` only and does NOT inject AppDbContext. Refactor `GetTracks` to also call `_mediaRepository.GetExternalSubtitleTracksAsync(id)` and merge external rows (negative index) into `SubtitleTracks`; add `IsExternal` to `MediaTrackInfo` DTO.
- `ISubtitleService.ConvertSidecarFileToVttAsync(inputPath, ct)` (ffmpeg `-c:s webvtt` pipe; passthrough `.vtt`); migrate the controller's inline `ExtractSubtitleAsWebVTTAsync` to it. Extend `GetSubtitle` for `trackIndex < 0`: resolve external row, **for `Format` in {sub,idx} return 415/404** (VOBSUB can't become VTT). Path authorization: Sidecar → `IsPathAuthorized(path, library.Paths)`.
**Client (VideoPlayer.tsx — critique blocker: FIVE sites, not two):** add `isExternalSub = selectedSubtitleTrack != null && selectedSubtitleTrack <= -100`. (1) finalUrl @659, (2) subtitleUrl @755 → route to `/api/media/{id}/subtitles/{neg}` VTT `<track>`, (3) seek-restart hlsUrl @1337, (4) backward-seek hlsUrl @1357, (5) pause @1044 / resume @1035 fetch — omit `&sub` for external at every site. `useTrackSelection.ts`: external arrives in same array, auto-selected by language; `-1`(OFF)/`-2`(internal) preserved (use `<= -100`). `types/index.ts`: add `isExternal?: boolean` to TrackInfo. External badge in track menu (explicit hex, e.g. `text-[#8A2BE2]`).
**Settings:** `EnableSidecarSubtitles=true` (Group=Subtitles).
**Edge cases:** SQLite FK enforcement — **verify connection has `Foreign Keys=True`** so `ExecuteDeleteAsync` orphan cleanup cascades SubtitleTracks; apostrophe paths use VTT overlay (sidesteps `subtitles=` filter); external sub stays DirectPlay/Remux (no burn).
**Tests:** discovery parse cases; `SyncSidecarsForItemAsync` idempotency/reconcile/never-touch-embedded; scanner integration (adjacent .srt → external row, single-file watcher path too); track merge (embedded≥0, external≤-100); VTT passthrough+conversion (mock IProcessRunner); security 404 for unauthorized/other-user/injection; VOBSUB 415.

### 4.E HDR software tone-mapping fallback (P3a — first in transcode phase)
**Data model:** none.
**Backend (`TranscodeProfileBuilder.cs` only):**
- After @133 add `bool useSoftwareToneMapping = isHdr && settings.HardwareAcceleration.ToLower() != "nvidia" && (!skipTonemapping || forceToneMappingForSubtitles)` + log. Computed AFTER the apostrophe workaround so it reflects cleared `hasSubtitleOverlay`.
- **Canonical signature (critique blocker):** `GetSoftwareToneMapFilter(string algorithm, string? colorTransfer, string maxResolution)` — NO `hasSubtitleOverlay` param. Place after `GetScaleFilter` (**@494, not ~525**). Private **instance** method (uses `_logger`). Chain: `zscale=t=<linear|arib-std-b67 for HLG>:npl=<100 PQ | 1000 HLG>`, `format=gbrpf32le`, `zscale=p=bt709`, `tonemap=tonemap=<hable|reinhard|mobius default hable>`, `zscale=t=bt709:m=bt709:r=tv`, optional folded `scale=W:-2`, `format=yuv420p`. Bare comma-joined, no `-vf`, no hwdownload.
- `GetHardwareDecodeOptions`: add `bool useSoftwareToneMapping` param, `if (useSoftwareToneMapping) return "";` first; update call site @145.
- Filter-prep restructure (**critique minor blocker @210**): `if (useToneMappingPipeline){…} else if (useSoftwareToneMapping){ toneMapFilter = GetSoftwareToneMapFilter(algo, probe?.ColorTransfer, MaxResolution); scaleFilter=""; } else if (!useToneMappingPipeline && !useSoftwareToneMapping){…existing @211-223…}` — the standalone `if (!useToneMappingPipeline)` MUST become `else if` or scaleFilter gets populated then overwritten.
- Splice into all three branches: replace tonemap-gating predicates with `(useToneMappingPipeline || useSoftwareToneMapping)` at bitmap apply @238, text prepend @292, no-sub `-vf` @312; scaleFilter re-append guards become `!useToneMappingPipeline && !useSoftwareToneMapping` @249/@297. **Bitmap path:** still fold scale into the zscale chain (scale2ref handles subtitle stream sizing); toneMapFilter embedded **bare** inside filter_complex (no extra quoting).
- **fMP4 fix @338:** `bool useFmp4 = (skipTonemapping && !useSoftwareToneMapping && !useToneMappingPipeline) || useAv1` so forced-tonemap-for-subs emits real SDR `.ts` (also fixes latent NVIDIA bug).
**Edge cases:** zscale requires `--enable-libzimg` — **add a concrete fallback** (T8): cache a one-time capability probe; if absent, log a clear error pointing to libzimg (don't ship silent-fail). PreserveHDR three-way default (class=false, seed=true, FFmpegService=false) — tests must set it explicitly. Document npl/HLG handling; 4K software tonemap is CPU-heavy.
**Tests (`tests/SoftMedia.Tests/...TranscodeProfileBuilderTests.cs`):** no-sub software chain (no `-hwaccel`/`tonemap_cuda`/`init.mp4`); algorithm `[Theory]` incl invalid→hable; text-sub composes before `subtitles=` with `-vf`; bitmap `[0:v]{chain}[tm]` then `[0:2][tm]scale2ref` no double-scale; PreserveHDR+no-subs passthrough; PreserveHDR+subs forces tonemap AND `.ts`/append_list; 1080p folds `scale=1920:-2`; regression NVIDIA path unchanged.

### 4.F VAAPI hardware acceleration (P3b — second in transcode phase, after P3a + P1)
**Data model:** none (AppSetting key, no migration).
**Backend (`TranscodeProfileBuilder.cs`, `FFmpegService.cs`, `SettingsService.cs`, `TranscodeDebugService.cs`):**
- `TranscodeSettings`: add `string DevicePath = "/dev/dri/renderD128"` (transient POCO comment) AND **`bool EnableAV1Encoding`** (critique blocker — not currently in POCO/LoadSettings). `FFmpegService.LoadSettingsAsync` (@144-168): read `HardwareAccelDevice` + `EnableAV1Encoding` into the initializer.
- `GetHardwareDecodeOptions`: add `vaapi` arm threading `DevicePath` (new param). GPU-only: `-hwaccel vaapi -hwaccel_output_format vaapi -vaapi_device <path>`; subtitle path: `-hwaccel vaapi -vaapi_device <path>` (software surfaces). **`-vaapi_device` sets the device session-globally → `GetScaleFilter` does NOT need DevicePath** (document, no redundant param).
- `GetVideoEncoder`: add `(h264/hevc/av1, vaapi) => *_vaapi`. **AV1 gate (critique blocker):** when `!EnableAV1Encoding` and codec=av1, fall through to `hevc_vaapi` (mirror StreamPlanService gate).
- `GetEncoderOptions`: add `h264_vaapi`/`hevc_vaapi`/`av1_vaapi`. **Remove `-pix_fmt vaapi` (critique blocker — invalid).** Rate control `-rc_mode CQP -qp <CRF>` (or `-rc_mode VBR -b:v` with maxBitrate); NOT `-cq`/`-global_quality`. Prepend `format=nv12,hwupload` only when input is system memory (subtitle/software path). Set `-bf 0` for h264_vaapi (older-driver safety). **HDR passthrough:** when `skipTonemapping && hevc_vaapi`, emit `-profile:v main10` (critique missing step).
- `GetScaleFilter`: add `scale_vaapi=W:-2:format=<nv12|p010 when preserve10Bit>` vaapi branch before software, no-sub GPU path only.
- HDR gate @133: broaden `useToneMappingPipeline` to also accept `vaapi`; build parallel chain `scale_vaapi=…:format=p010,tonemap_vaapi=format=nv12` (**no `:t=` / no algorithm selector** — critique blocker; `tonemap_vaapi` has none). Append `hwdownload,format=nv12` for subtitle overlay (mirror cuda @200-205). **Coordinate with P3a:** both touch @133 and @176-208 — VAAPI extends the if/else-if chain P3a established.
- `SettingsService`: seed `HardwareAccelDevice=/dev/dri/renderD128` (Group=Transcoding); **explicit UPDATE** of the existing `HardwareAcceleration` description row (additive loop skips existing) to mention vaapi + clarify `amd`=AMF/Windows. Do NOT repoint `amd` to vaapi.
- `TranscodeDebugService` (now **recommended** not optional): surface `hardwareAccelDevice` in serverSettings @90-98 and @129-139.
- Device-path validation: reject/sanitize `DevicePath` not matching `^/dev/dri/[A-Za-z0-9]+$` (document `by-path/` symlinks intentionally excluded); fall back to default + warn. Non-Linux host selecting vaapi → log clear warning.
**Client (`SettingsPage.tsx`):** add `{value:"vaapi",label:"VAAPI (Linux AMD/Intel)"}` to `hardwareAccelOptions` @384-389; add `'HardwareAccelDevice'` to `transcodingOrder` @453-462 after `HardwareAcceleration` (else renders at bottom). Device field auto-renders via default text input. No `bg-primary`.
**Settings:** `HardwareAccelDevice=/dev/dri/renderD128`, `HardwareAcceleration` gains `vaapi`.
**Edge cases:** non-Linux/no `/dev/dri` → must still software-transcode; arm64 no intel driver; 10-bit preserve path uses p010.
**Tests:** vaapi SDR no-sub (`-hwaccel vaapi`,`-vaapi_device`,`h264_vaapi`); rate-control (`-rc_mode`/`-qp`, no `-cq`); scale (`scale_vaapi=1920:-2`); bitmap-sub (decode omits `-hwaccel_output_format vaapi`, `hwupload` before encoder); HDR+vaapi (`tonemap_vaapi`; +sub → `hwdownload`); AV1-disabled→hevc_vaapi; device-path injection rejected. Integration test at **`src/SoftMedia.Tests/Services/TranscodingIntegrationTests.cs`** (NOT `tests/...`) — custom device path propagates.

### 4.G Photos library (P4, depends on P0 serving)
**Data model (MIGRATION `AddPhotoExifFields`):** `MediaItem.cs` add `DateTime? DateTaken` (+`[Index(nameof(DateTaken))]`) and `string? ExifJson`. Width/Height already exist (just populate).
**Backend:**
- `LibraryService.cs`: delete Photo throw blocks @61-68 + @108-111.
- Create `Services/Scanning/PhotoScanner.cs` (mirror BookScanner flat-file, no series). `SupportedType=Photo`, `SupportedExtensions=MediaExtensions.Photo`. **Width/Height (critique blocker):** no `IMediaAnalysisStrategy` handles Photo → call `SKCodec.Create` inline (reuse `ImageSafety.IsDecodableWithinBudget`) to set dimensions; return New+EnqueueMetadata.
- Register `services.AddScoped<IMediaScanner, PhotoScanner>()` AFTER BookScanner (~line 245).
- **`IsMediaRoute()` (critique blocker):** add `/api/v1/photos` to `ServiceCollectionExtensions.cs:42-51` so `<img>` query-token lift works (else 401).
- `MetadataAggregator` (**critique blocker — placement**): persist Photo EXIF Extra → `ExifJson` and `dateTaken`→`DateTaken` **BEFORE the early-return @135**, and stamp a Photo sentinel `MetadataHash` at that same point (mirror ComicSeries EMPTY pattern) — otherwise data loss + infinite re-enrich.
- **`MetadataEnrichmentPolicy.NeedsEnrichment` (critique blocker):** add Photo case returning false once MetadataHash set (default `!hasPoster` perpetually re-enqueues).
- `ThumbnailService.GenerateThumbnailAsync` @56: decode via `SKCodec.Create`+`EncodedOrigin`, transform before resize (EXIF orientation; SkiaSharp does NOT auto-orient); attempt HEIC when `EnableHeicDecode`, return null (no throw) when codec absent. Keep ImageSafety guard. **Drop the "update stale video-frame header comment" step — it's hallucinated.**
- Create `Services/Media/PhotoImageService.cs`/`IPhotoImageService` (resolve path/mime + orchestrate thumbnail; null on missing/HEIC-fail).
- Create `Controllers/PhotoController.cs` (`api/v1/photos`, mirror BookController): `GET {id}/thumbnail?size=sm|md|lg` (named presets), `GET {id}/original` (`PhysicalFile enableRangeProcessing:true`). Every endpoint `ValidateMediaAccessAsync`→404; ETag/CacheControl private.
- `MediaItemDto`: Photo case in `ResolvePosterPath` @291 → `/api/v1/photos/{id}/thumbnail?size=md`; in `FromMediaItem` deserialize `ExifJson`→Metadata + inject `DateTaken`. Width/Height already mapped @173-174.
- `LibraryRepository.cs:222-229`: add `"datetaken" => OrderByDescending(DateTaken)`.
**Client:** `LibraryForm.tsx:22` add `'Photo'` (remove stale comment). `PhotoDetailView.tsx` render `/api/v1/photos/{id}/original` + new `PhotoLightbox.tsx` (fullscreen, ESC/arrows reuse prev/next; explicit hex). `FilterBar.tsx` add 'Date Taken' sort + fix `bg-primary`@141→`bg-[#007AFF]`. `LibraryPage.tsx` (**critique blocker**): pass `viewMode/onViewModeChange` for `type==='Photo'` (currently Music-only @197-198) else timeline toggle never renders; timeline groups by month-of-dateTaken (fallback dateAdded). `MediaCard.tsx` (**critique major**): add `isPhoto` → `aspect-square` (else landscape distorted in 2:3 @108). **Client `<img>` token:** photos use `/api/v1/photos` — attach bearer/`?access_token=` like music covers.
**Settings:** `EnableHeicDecode=false` (official build ships no HEVC decoder — §7.1), `PhotoProvider=Exif` (exists), optional `PhotoThumbnailQuality=80`.
**Packages:** thumbnailing via SkiaSharp (MIT). **HEIC correction (§7.1): SkiaSharp does NOT decode HEIF server-side on Linux/Windows** (only on Apple via the OS codec). If HEIC is wanted it must come from Magick.NET-Q8 built with a verified libheif delegate, or an OS/user-provided codec, routed through `ImageSafety.IsDecodableWithinBudget`. Gate behind `EnableHeicDecode` (default false); degrade gracefully (thumbnail 404, `/original` still streams). HEVC is patent-encumbered — see §7.1.
**Edge cases:** HEIC no-codec → thumbnail 404, original still streams; decode-bomb guard reused; animated GIF first-frame; missing dateTaken→dateAdded sort.
**Tests:** PhotoScannerTests (Width/Height set, idempotent, orphan cleanup); orientation (Orientation=6 → swapped dims); HEIC null-no-throw; MetadataAggregator EXIF persistence + hash; PhotoController access 404/200/400; DTO mapping; guard-removal; sort; PhotoLightbox RTL. Regression: ThumbnailService shared by Image/MusicController (orientation no-op for upright covers).

### 4.H Subtitles Phase B — OpenSubtitles (P5, depends on Phase A + P0 cache dir)
**Data model:** none new (reuse Phase A columns + AppSetting daily counter mirroring OMDb).
**Backend:** `Services/Media/Subtitles/ISubtitleProvider.cs` + `OpenSubtitlesProvider.cs` (typed HttpClient + `SoftMediaUserAgentHandler` + rate-limit handler bound to new `RateLimiterFactory` "OpenSubtitles" slot; optional API key; daily-count guard via AppSetting). **Registration:** `AddHttpClient<OpenSubtitlesProvider>()` concrete + separate `ISubtitleProvider→OpenSubtitlesProvider`. `ExternalSubtitleService.cs`/`IExternalSubtitleService.EnsureSubtitleForLanguageAsync` (skip if pref-lang sub exists; else search/download to `ExternalSubtitleCacheDirectory` as `<stem>.<lang>.srt`; register external SubtitleTrack ≤-100; de-dupe via existing `Helpers/KeyedLock.cs` — note its unbounded-growth caveat; best-effort, never throws). `TranscodeController.GetStreamPlan` @84: best-effort call gated by `EnableExternalSubtitles`; **inject `IUserPreferencesService`+`IExternalSubtitleService`+`ISettingsService` (new ctor params)**; pref lang `GetPreferenceAsync(userId,"SubtitleLanguage","en")`. **Managed-dir authorization (critique blocker):** `IsPathAuthorized` only gates library roots — add `IsPathUnderManagedDirectory(filePath, managedDir)` to `IStreamSecurityService` (+impl); Phase A feed route branches on `Source`: Sidecar→library jail, OpenSubtitles→managed-dir check against `ExternalSubtitleCacheDirectory`.
**Client (`SettingsPage.tsx` — critique major):** settings page does NOT auto-render unknown groups — **explicitly add a Subtitles section** (`EnableSidecarSubtitles`, `EnableExternalSubtitles`, `OpenSubtitlesApiKey` masked, `ExternalSubtitleCacheDirectory`). Explicit hex.
**Settings:** `EnableExternalSubtitles=false` (opt-in), `OpenSubtitlesApiKey=""`, `ExternalSubtitleCacheDirectory=<DataRoot>/external-subs` (under data-root — NOT `./data` bare), `OpenSubtitlesDailyLimit=5` (realistic anonymous quota — §7.2; **user-supplied key only, no shared/maintainer key**), Internal `OpenSubtitlesDailyCount`/`CountDate`.
**Edge cases:** 429/failure never blocks /plan; daily-limit skip (no error); concurrent first-plays de-duped; restore re-downloads (backup-restore artwork philosophy).
**Tests:** no-op when sub present; respects EnableExternalSubtitles=false + daily limit; de-dupe; failure-swallow; provider request shape/headers + rate-limiter wiring; managed-dir auth (404 outside cache+library).

---

## 5. Consolidated Phased TASK CHECKLIST

### Phase 0 — Foundation: data-root
- [ ] **F1** Create `IDataPathProvider`+`DataPathProvider`. _AC: unit test proves unset==legacy paths; SOFTMEDIA_DATA set rebases all; ResolveDbPath rebases relative source per mode._
- [ ] **F2** Register singleton in `AddMediaServices` (~@222). _AC: resolves; services inject it._
- [ ] **F3** Rebase cache writers (ImageCacheService/TrickplayService/ThumbnailService/MusicScanner+web-relative CoverArtPath/ImageController). _AC: legacy mode files land under wwwroot/cache exactly as before._
- [ ] **F4** Rebase readers/jails (MusicImageService via WebServingRoot/AudioController/ArtworkRepairService/DictionaryService). _AC: path-auth tests pass; /cache DB rows resolve legacy._
- [ ] **F5** Rebase BackupService (339+356, ctor, keep L-20), TaskStatusPersistenceService (ctor inject + Program.cs:303-304), TranscodeService:54, DbInitializer:148, Program.cs:277-288 + ArtworkRepairOnRestoreService:99 via ResolveDbPath. **Do NOT touch GetLiveDbPath.** _AC: all DB-path sites agree; nothing writes CWD when SOFTMEDIA_DATA set; legacy unchanged._
- [ ] **F6** `Program.cs` second `/cache` static provider (when CacheRoot moved) + `MapFallbackToFile` after MapControllers/MapHub/auth. _AC: container `/cache/images/...`→200; `/library`→index.html; `/api/x`→JSON 404._
- [ ] **F7** csproj pre-publish dist→wwwroot target + startup warning if index.html missing. _AC: bare-metal publish serves SPA deep-links._
- [ ] **F8** Update 7 test fixtures + add `DataPathProviderTests` + SPA-fallback integration test. _AC: `dotnet test` green (backend stopped, explicit --project)._

### Phase 1 — Docker
- [ ] **D1** Multi-stage Dockerfile (node→sdk→aspnet, ffmpeg+VAAPI conditional by TARGETARCH, explicit dist→/app/wwwroot, non-root, ENV/VOLUME/EXPOSE). _AC: amd64 build; ffmpeg on PATH; wwwroot has index.html+assets._
- [ ] **D2** `.dockerignore` (incl Data/*.db*, backups/, task-status.json). _AC: no dev DB/creds in image; small context._
- [ ] **D3** `docker-compose.yml` (named config volume, media :ro, commented /dev/dri+group_add, env). _AC: `compose up` starts; data persists across recreate._
- [ ] **D4** GHCR multi-arch workflow (PR dry-run, tag/main push). _AC: PR builds both arches no-push; tag pushes manifest._
- [ ] **D5** `docs/user-docs/docker.md` + README. _AC: operator deploys from docs without reading code._
- [ ] **D6** Container smoke + bare-metal regression. _AC: container login/scan/serve/transcode/backup under /config; bare-metal byte-for-byte unchanged._

### Phase 2 — Quick wins (parallel)
- [ ] **CW1** `MediaCompletionHelper` + refactor RecommendationService to use it. _AC: solution compiles; RecommendationService tests pass._
- [ ] **CW2** `GetInProgressAsync` (interface+impl, over-fetch capped, document best-effort). _AC: returns over-fetched in-progress newest-first._
- [ ] **CW3** `ContinueWatchingController` (+interactions injection on top of Watchlist deps; iterate candidates in order; manual Progress/PlaybackPosition; Series include; dedupe). _AC: 200 ACL/rating-filtered deduped list, progress populated, finished/watched/music absent._
- [ ] **CW4** Client service + hook + `ContinueWatchingRow` above WatchlistRow. _AC: row appears below hero on populated account, resume bars show, click resumes, absent when empty._
- [ ] **CW5** `ContinueWatchingControllerTests` (seed Duration>0). _AC: all listed tests pass._
- [ ] **SA1** `MediaExtensions.Subtitle` (NOT in .All) + comment. _AC: `.All` unchanged (unit assert); Subtitle accessible._
- [ ] **SA2** SubtitleTrack columns + migration `AddExternalSubtitleTracks`. _AC: applies clean fresh+existing; embedded default IsExternal=false._
- [ ] **SA3** `SidecarSubtitleDiscovery`. _AC: parse/locate unit tests pass._
- [ ] **SA4** `SidecarSubtitleService` (index base -100) + DI. _AC: idempotency/reconcile tests pass; never touches embedded._
- [ ] **SA5** Wire scanners AFTER MediaItems.Add; `_enableSidecars` on BaseMediaScanner. _AC: adjacent .srt → external rows; embedded untouched; single-file watcher path covered._
- [ ] **SA6** `IMediaRepository.GetExternalSubtitleTracksAsync` + merge into MediaTracksController + IsExternal DTO. _AC: /tracks returns embedded≥0 + external≤-100._
- [ ] **SA7** `ConvertSidecarFileToVttAsync` + GetSubtitle neg-index branch (VOBSUB 415) + sidecar path jail; verify SQLite FK on. _AC: `/subtitles/-100`→valid VTT; 404 unauthorized; VOBSUB rejected._
- [ ] **SA8** VideoPlayer all 5 sub-URL sites guarded + useTrackSelection/types + External badge. _AC: external sub shows via `<track>` no burn; OFF works; embedded unchanged._

### Phase 3 — Transcode (sequential)
- [ ] **H1** `useSoftwareToneMapping` flag + log. _AC: mutually exclusive with CUDA flag._
- [ ] **H2** `GetSoftwareToneMapFilter(algorithm, colorTransfer, maxResolution)` instance method after GetScaleFilter@494. _AC: valid chain; whitelist; res-fold; HLG npl=1000/arib-std-b67; no hwdownload._
- [ ] **H3** `GetHardwareDecodeOptions` force software decode param + call site. _AC: no -hwaccel when software tonemap active; other paths unchanged._
- [ ] **H4** Filter-prep restructure (standalone if@210→else if). _AC: toneMapFilter populated, scaleFilter empty; CUDA byte-identical._
- [ ] **H5** Splice into 3 branches + scale-guard exclusions. _AC: software chain in all branches, no double-scale, bitmap graph valid._
- [ ] **H6** fMP4 guard fix @338. _AC: forced-tonemap→.ts/append_list; AV1 still fMP4; passthrough still fMP4._
- [ ] **H7** Software-tonemap unit tests. _AC: all pass; NVIDIA regression green._
- [ ] **H8** ffmpeg zscale capability probe + concrete error fallback; manual PQ+HLG playback. _AC: correct SDR colors; documented dep._
- [ ] **V1** `DevicePath`+`EnableAV1Encoding` on TranscodeSettings + LoadSettingsAsync. _AC: builds; loaded from settings._
- [ ] **V2** Seed `HardwareAccelDevice` + explicit UPDATE of HardwareAcceleration description (vaapi, amd=Windows). _AC: fresh+existing DB expose key + updated descriptions; no migration._
- [ ] **V3** VAAPI decode arm (DevicePath threaded). _AC: correct flags with/without subs._
- [ ] **V4** VAAPI encoders (no `-pix_fmt vaapi`; `-rc_mode`; `-bf 0`; main10 HDR passthrough; AV1→hevc when disabled; hwupload for software frames). _AC: encoder maps correctly; rate-control not -cq; AV1 gated._
- [ ] **V5** `scale_vaapi` branch (p010 when 10-bit). _AC: 720/1080/4k widths; preserve10Bit p010._
- [ ] **V6** VAAPI HDR tonemap (`tonemap_vaapi`, no algo selector) + subtitle reconcile, extending H1-H5 chain. _AC: HDR→tonemap_vaapi; +sub→hwdownload+scale2ref; CPU tests unchanged._
- [ ] **V7** Device-path validation. _AC: malicious rejected→default+warn._
- [ ] **V8** Client vaapi option + transcodingOrder. _AC: dropdown shows VAAPI, device field renders beneath, persists._
- [ ] **V9** TranscodeDebugService device surfacing. _AC: overlay shows device path._
- [ ] **V10** VAAPI tests (unit + integration at `src/SoftMedia.Tests`). _AC: all + existing transcoding tests pass._

### Phase 4 — Photos
- [ ] **P1** DateTaken/ExifJson + `[Index]` + migration `AddPhotoExifFields`. _AC: migration applies; columns nullable._
- [ ] **P2** Remove LibraryService Photo guards. _AC: create/update Photo library enqueues scan._
- [ ] **P3** PhotoScanner (inline SKCodec Width/Height) + register after BookScanner. _AC: Photo rows populated; orphan cleanup; no no-scanner warning._
- [ ] **P4** `IsMediaRoute` += `/api/v1/photos`. _AC: `<img>` token-lift requests authorize._
- [ ] **P5** MetadataAggregator EXIF persist + Photo MetadataHash sentinel BEFORE early-return@135; MetadataEnrichmentPolicy Photo=false. _AC: enrichment populates ExifJson/DateTaken once; no re-enqueue loop._
- [ ] **P6** ThumbnailService EXIF orientation + HEIC gate. _AC: Orientation=6 swaps dims; HEIC→WebP when codec present, null-no-throw absent._
- [ ] **P7** HEIF decode package + EnableHeicDecode. _AC: builds; sample HEIC decodes; absence degrades._
- [ ] **P8** PhotoImageService + PhotoController. _AC: authorized 200, denied/jailed 404, invalid size 400, range on /original._
- [ ] **P9** DTO PosterPath Photo case + ExifJson→Metadata + DateTaken. _AC: /media + /libraries items carry posterPath + metadata.camera/dateTaken._
- [ ] **P10** LibraryRepository datetaken sort. _AC: sortBy=datetaken newest-capture-first._
- [ ] **P11** LibraryForm Photo + PhotoDetailView image + PhotoLightbox + token attach. _AC: image shows; lightbox ESC/arrows; EXIF cards populate._
- [ ] **P12** FilterBar Date Taken + LibraryPage Photo viewMode pass-through + MediaCard aspect-square + bg-primary fix. _AC: timeline groups by month; toggle visible; landscape not distorted; infinite scroll preserved._
- [ ] **P13** Photo tests. _AC: all new pass; ThumbnailService shared-caller regression green._

### Phase 5 — Subtitles Phase B
- [ ] **SB1** Subtitles settings + OpenSubtitles RateLimiterFactory slot. _AC: defaults seed; GetLimiter("OpenSubtitles") configured._
- [ ] **SB2** ISubtitleProvider + OpenSubtitlesProvider (typed client, UA+rate-limit, daily guard). _AC: request shape/headers + limiter wiring; daily short-circuit._
- [ ] **SB3** ExternalSubtitleService (KeyedLock de-dupe, best-effort) + `IsPathUnderManagedDirectory`. _AC: no-op when present; de-dupe; failure-swallow; file written+row registered._
- [ ] **SB4** TranscodeController first-play trigger (new ctor deps) + feed route managed-dir auth. _AC: first /plan downloads+registers; subsequent /tracks lists it; provider failure doesn't affect plan._
- [ ] **SB5** Settings page explicit Subtitles section (masked API key). _AC: admin toggles persist via settings endpoint._

---

## 6. Risks & Open Questions (maintainer sign-off)
1. **Continue Watching scope:** video-only (Movie+Episode) vs include Book/ComicIssue? Plan assumes video-only.
2. **Continue Watching cache:** invalidate `['continueWatching']` on player unmount for live refresh? Plan defers.
3. **HEIC decoder:** SkiaSharp HEIF native vs Magick.NET-Q8 for the Windows-primary deploy matrix?
4. **VAAPI tonemap algorithm:** silently ignore `ToneMappingAlgorithm` under vaapi, hide it, or fall back to the P3a CPU zscale chain (overlaps HDR work)?
5. **VAAPI `amd` option:** keep value, only clarify label (Windows)? Renaming the stored value would need settings-row migration.
6. **arm64 VAAPI:** software-only acceptable for 1.0 (no intel-media driver)?
7. **PreserveHDR three-way default** (class=false/seed=true/FFmpegService=false): which is authoritative for fresh non-seeded installs? Affects whether non-NVIDIA users tonemap by default.
8. **zscale availability:** runtime capability probe (cached) vs documenting the libzimg build requirement? Plan ships the probe + clear-error fallback.
9. **Data-root migration for existing bare-metal→container users:** ship a one-time copy helper or document manual steps? Plan documents manual.
10. **OpenSubtitles key mode:** ~~ship a SoftMedia consumer key (OMDb-style)~~ **RESOLVED → user-supplied-key-only** (a shared/maintainer key violates OpenSubtitles' API ToS *and* is a remote kill-switch the charter forbids — §7.2). Empty key self-suppresses the feature; no `OpenSubtitlesApiKeyMode` shared mode.
11. **Container port:** 8096 (Jellyfin convention) vs keep 5011?
12. **Dedupe for movies in a Collection:** confirmed NO (only episodes dedupe by SeriesId).

---

## 7. Charter & Engineering-Rigor Review

_Lead-architect consolidation of a four-lens review (OSS/Licensing, Privacy/Self-hostability, Engineering Rigor, Cross-Cutting Consistency). Verdict: **aligned-with-fixes** — the plan is sound and code-grounded; the items below are mandatory before this is a charter-compliant 1.0. All file/line claims here were re-verified against source this session._

### 7.1 OSS / Licensing Decisions (binding)

These resolve the FREE & OPEN SOURCE charter and third-party redistribution obligations. They are **must-fix** and block release.

1. **Add license deliverables (new tasks F-license + D-license).**
   - Repo-root `LICENSE` for SoftMedia itself.
   - Repo-root `THIRD-PARTY-NOTICES.md` enumerating each dependency with its SPDX identifier:
     SkiaSharp = **MIT**; Magick.NET = **Apache-2.0** (ImageMagick = ImageMagick license, OSI-approved); MetadataExtractor = **Apache-2.0**; TagLibSharp = **LGPL-2.1**; SharpCompress = **MIT**; PdfPig = **Apache-2.0**; Konscious.Security.Cryptography (Argon2) = **MIT**; FFmpeg = **LGPL-2.1-or-later / GPL-2.0-or-later**; libx264 / libx265 = **GPL-2.0-or-later**; mesa-va-drivers = **MIT**; libheif = **LGPL-3.0**; libde265 = **LGPL-3.0 (HEVC PATENT)**.
   - The **already-vendored** `src/SoftMedia.Server/ffmpeg-bin/ffmpeg.exe` + `ffprobe.exe` (no accompanying license today) must ship with the corresponding FFmpeg/x264/x265 license texts; record which build/flavor they are.
   - In the Docker image, `COPY` the FFmpeg + x264/x265 license texts to `/app/THIRD-PARTY`, and state in `docs/user-docs/docker.md` that Debian apt `ffmpeg` is the **GPL** build (libx265/libx264 present → GPL source-availability/labeling obligations apply). Pin/record the FFmpeg version.

2. **Default Intel VAAPI driver = FREE.** The official Dockerfile installs the **free** `intel-media-va-driver` + `mesa-va-drivers` by default (covers hardware decode). Debian **non-free** `intel-media-va-driver-non-free` (Intel hardware *encode*) is **opt-in only** via `ARG ENABLE_NONFREE_INTEL=0` or a separate image tag, with a docs note that it pulls a non-free component. This resolves the line-40 ambiguity decisively toward free and keeps the default image 100% main-component.

3. **HEIC defaults OFF; the project ships no HEVC decoder.** Set `EnableHeicDecode=**false**` in the official build/image (HEVC is patent-encumbered; shipping it ON by default creates patent exposure for the project and its redistributors). Keep the existing graceful degradation: thumbnail 404, `/original` still streams. **Correction to §4.G line 157:** SkiaSharp does **not** decode HEIF on Linux/Windows server (the csproj references only base SkiaSharp 3.119.2, no NativeAssets/HEIF; SkiaSharp HEIF works only on Apple via the OS codec). If HEIC is wanted it must come from **Magick.NET-Q8 built with a verified libheif delegate** (confirm the chosen NuGet actually bundles libheif) or an OS/user-provided codec, and it must route through the same `ImageSafety.IsDecodableWithinBudget` guard. Add a docs note that enabling HEIC may carry codec-patent obligations in the operator's jurisdiction. Open Question #3 now also decides the **default state**, not just the package.

### 7.2 Privacy / Self-Hostability Guarantees (binding)

These resolve LOCAL-FIRST, no-remote-kill-switch, no-account-wall, and the README's "privacy-focused" promise.

1. **OpenSubtitles = user-supplied-key-only (resolves Open Question #10).** `OpenSubtitlesApiKey` is empty by default; when empty the provider self-suppresses (feature simply unavailable, like sidecar-only mode). **Do not bundle or inject a maintainer key, and do not add a `softmedia` shared-key mode** — a shared key is both an OpenSubtitles ToS violation (their keys are per-application; apps that let users insert their own key are banned) and a remote kill-switch/single-point-of-control the charter forbids. The OMDb "not a cloud dependency" reasoning does **not** transfer because OpenSubtitles' ToS materially differs. Each operator registers their own free key (mirror the OMDb `custom`-mode UI, without the shared mode). Set the documented default `OpenSubtitlesDailyLimit` to the realistic **anonymous** value (~5), not 50; any OpenSubtitles user login is a strictly optional advanced field, **never** a precondition (an enforced login would be a mandatory third-party account wall).

2. **Point-of-enablement disclosure (mirror the OMDb helper).** When `EnableExternalSubtitles` is toggled on, the new Subtitles settings section must surface a disclosure stating: the third party (opensubtitles.com), exactly what leaves the box, that it is off by default and stays local otherwise, and a link to OpenSubtitles' privacy policy. This is an AC on **SB5**.

3. **Least-revealing search by default.** The OpenSubtitles provider searches by **IMDb/tmdb-id + language first, then title+year**, and does **not** send `moviehash` by default (moviehash leaks a fingerprint of the exact file the user holds). If offered, moviehash is a separate, clearly-labeled, off-by-default sub-option. The chosen parameter set is asserted in the SB2 request-shape test.

4. **Untrusted-input hardening on the download path.** Validate the language code against `^[a-z]{2,3}$`; cap download size; verify content-type; sanitize the derived `<stem>`; assert via `Path.GetFullPath` that the write path stays under `ExternalSubtitleCacheDirectory` before writing; convert downloaded subtitles through the existing `ConvertSidecarFileToVttAsync` ffmpeg pipeline before serving; deliver as a track content-type, not navigable HTML. Add negative tests (malicious lang, oversized body, path-escape).

5. **Outbound UA carries no per-instance identifier.** The reused `SoftMediaUserAgentHandler` for OpenSubtitles sends app+version only — no instance-id/user-id appended (keeps requests non-correlatable). State this in §4.H.

6. **No telemetry confirmed.** The OpenSubtitles daily-count guard is a purely local AppSetting counter with no central reporting; `docs/user-docs/docker.md` gains a "Network egress" section listing every optional outbound integration (metadata providers, OpenSubtitles) as opt-in/disclosed and confirming nothing phones home for telemetry/updates.

### 7.3 Engineering-Rigor Requirements (apply to every task)

Add to the Section 1 non-negotiable conventions and echo in the named ACs:

- **XML docs:** every new public interface, class, controller action, and DTO gets `///` doc comments matching existing controllers (`IDataPathProvider`, `ISidecarSubtitleService`, `IPhotoImageService`, `ISubtitleProvider`, `IExternalSubtitleService`, `MediaCompletionHelper`, new controllers/DTOs). ACs: F1, CW3, SA4/SA6, P8, SB2/SB3.
- **TV-readiness / a11y:** every new interactive client element is a `<button>`/`<input>`, Tab-reachable, with `hover:bg-white/10 focus-visible:bg-white/10 focus-visible:ring-2` and an `aria-label`; PhotoLightbox close/prev/next are focusable and its ESC/arrow handlers do not trap focus. Add RTL role/label assertions. ACs: CW4, SA8, P11/P12, V8, SB5.
- **i18n:** new user-visible strings get i18n keys with en/es entries (ContinueWatchingRow heading, PhotoLightbox controls, Subtitles section labels, the `VAAPI (Linux AMD/Intel)` label, the "Date Taken" sort label, External badge) — or an explicitly documented deferral. ACs: 4.C/4.D/4.F/4.G/4.H client tasks.
- **Test placement (convention):** controller/service unit tests → `src/SoftMedia.Server.Tests`; transcode-builder unit tests → `tests/SoftMedia.Tests`; cross-cutting integration → `src/SoftMedia.Tests`. Name the target project in each test AC (CW5, P13, V10, SA-/SB-tests).
- **Data-root regression rigor:** `DataPathProviderTests` (F1/F8) asserts each property's legacy value against the exact pre-refactor literal **and** a container-mode case rebasing under `SOFTMEDIA_DATA`; assert `GetLiveDbPath` is NOT routed through `ResolveDbPath`; make D6's "bare-metal byte-for-byte unchanged" an automated check. V2 adds a test seeding an old description row and asserting the description changed after `InitializeDefaultsAsync`.
- **Bounded concurrency:** cap concurrent photo thumbnail generation (reuse an existing queue/semaphore or `Environment.ProcessorCount`-derived cap) and state the software-tonemap active-transcode cap; document expected memory ceilings for 4K software tonemap and HEIC decode (arm64/NAS targets). ACs: P3/P6 edge cases.
- **Completion-helper correctness:** CW1 clarifies `MediaCompletionHelper` covers only position/duration/creditsStart math; the `IsWatched` short-circuit stays at each caller; add a RecommendationService regression test that `IsWatched=true` still returns complete after the refactor.
- **Route-naming decision (documented):** subtitle/transcode endpoints intentionally remain on legacy `/api/media` and `/api/transcode` (avoid breaking VideoPlayer, keep PRs scoped) while new controllers use `/api/v1/*`; confirm `IsMediaRoute()`/token-lift covers `/api/media` the same way P4 adds `/api/v1/photos`.
- **Read-only rootfs:** D3's AC makes `TranscodeTempRoot` resolve under the `/config` volume (or a documented scratch mount) so read-only-rootfs deployments still transcode; document the `read_only: true` hardening recipe in `docker.md`.

### 7.4 Process / Docs Additions

- **Per-feature user docs (project convention, currently only `docker.md` planned):** add `docs/user-docs/features/continue-watching.md`, `photos.md`, and `external-subtitles.md` (covers sidecar Phase A + OpenSubtitles Phase B), and extend `hdr-playback.md` to cover the new software/AMD/Intel/VAAPI paths. Follow the existing How-It-Works / API Endpoint / Requirements template.
- **Charter-mandated checklist + release notes:** add a closing task per phase to append a "Feature Implementation (June 2026)" section to `.docs/project_checklist.md` (charter-required, currently untouched), and create a root `CHANGELOG.md` for the 1.0 tag (D4 pushes a GHCR manifest on tag and needs accompanying release notes).
- **SDD reconciliation (one consolidated task):** update `docs/SDD.md` to mark Continue Watching, VAAPI, and sidecar subtitles as **shipped** (currently described as design intent), document the `/config` data-root layout and `IDataPathProvider` abstraction, the Photos API, and the negative-index external-subtitle scheme; annotate the older roadmap docs that listed these as future. Also mark `docs/reports/feature-gap-analysis-2026-05-07.md` superseded by the 2026-06-16 reports.
- **CI wiring:** add a frontend CI job (Vitest + the existing `a11yGuards.test.ts`) — the `docker-publish.yml` workflow (D4) is the natural home — so the new Vitest tests the plan promises (e.g. PhotoLightbox RTL) actually gate merges; extend a11yGuards/RTL coverage to the new interactive components.

### 7.5 Updated Open-Question Resolutions

- **#3 (HEIC):** Magick.NET-Q8 + libheif (if adopted) **or** OS/user codec; default `EnableHeicDecode=false`; SkiaSharp cannot decode HEIF server-side.
- **#6 (arm64 VAAPI):** software-only acceptable for 1.0; free driver only.
- **#10 (OpenSubtitles key):** **user-supplied-only**, no shared/maintainer key, default daily limit ~5, login never required.
- **#11 (port):** 8096 acceptable (Jellyfin convention); document the choice.

_All other items in §1–§6 stand as written; the plan's code-grounding, sequencing, and security posture are affirmed._

---

_Method: 13-agent code-grounded planning workflow (6 blueprints → 6 adversarial critiques → architect synthesis) + 5-agent charter review (4 lenses: OSS/licensing, privacy/self-hostability, engineering rigor, consistency → synthesis). 62 tasks across 6 phases + Section 7 corrections. Review verdict: **aligned-with-fixes** — code-grounding, sequencing, and security posture affirmed; OSS/licensing & privacy defaults corrected before 1.0._
