# SoftMedia Project Checklist

## Global Tasks

### Completed
- [x] Integrate Phase 1 (Critical Infrastructure) fixes 
- [x] Integrate Phase 2 (Architecture Improvements) 
- [x] Integrate Phase 3 (Duplication & Efficiency)
- [x] Integrate Phase 4 (Correctness & Storage)
- [x] Integrate Phase 5 (Resilience)

### Pending
- [x] Investigate and resolve warnings (e.g., `CS1998` in scanners and analysis strategies, `CS8604` in transcoding services).
- [x] Add explicit Unit tests regarding `MusicMetadataResolver` and `OpenLibraryProvider` scoring.

## Scanning & Metadata Fixes (March 2026)
- [x] Phase 1: Database schema normalization (Person, Genre, Cast, MediaItemGenre tables)
- [x] Phase 2: File watcher fixes (MediaExtensions.All, single-file processing)
- [x] Phase 3: Metadata provider fixes (SPARQL GROUP_CONCAT, BookScanner title fix)
- [x] Phase 4: TvScanner O(N²) JSON parse optimization
- [x] Phase 5: Persistent retry queue (MetadataRetry table)
- [x] Phase 6: Documentation & scanner improvements (SDD fix, BookScanner/GameScanner IMediaAnalysisService)
- [ ] Deferred: Music routing consolidation into MetadataRouter
- [ ] Future: Book/Game analysis strategies, frontend normalized genre/cast display

## Metadata Architecture Refactoring (April 2026)
- [x] Phase 1: Schema & Column Promotions (PosterUrl, BackdropUrl, IsRetryExhausted)
- [x] Phase 2: MetadataAggregator Batching & Dedup Fix (Genres, Cast batch persistence)
- [x] Phase 3: Settings Caching & Enrichment Policy Optimization (IMemoryCache, Single JSON parse)
- [x] Phase 4: DTO & Image Resolution Consolidation (ResolvePosterPath, ResolveBackdropPath)
- [x] Phase 5: Scanner Performance Optimization (FileDiscoveryResult caching, O(1) TV Episode lookups)
- [x] Phase 6: MetadataJson Dual-Write Cleanup (Removed legacy Genres column)

## Licensing & Repo Hygiene (June 2026)
_Plan: `docs/plans/licensing-and-repo-hygiene-plan-2026-06-18.md` (rev. 3)._
- [x] Relicense under AGPL-3.0-or-later (LICENSE + SPDX in csproj/package.json)
- [x] THIRD-PARTY-NOTICES.md + `scripts/gen-licenses.ps1` regenerator
- [x] De-vendor ffmpeg from git (4 binaries untracked, .gitignore, csproj ItemGroup removed)
- [x] Fetch jellyfin-ffmpeg at setup (install_ffmpeg.ps1 rewrite + install_ffmpeg.sh; chromaprint gate)
- [x] Harden BinaryLocationService (assembly-relative + jellyfin-ffmpeg candidates; warn on bare-PATH)
- [x] CONTRIBUTING.md + CLA.md + CLA-assistant workflow + SECURITY.md
- [x] README: License, Privacy/egress, AGPL §13 (repo-level) sections; CHANGELOG.md
- [ ] In-app (UI) AGPL §13 source link (repo-level offer done; UI link pending)
- [ ] Git-history purge of the old binaries (deferred pending repo public/private status)
- [ ] Wire CLA-assistant PAT secret + a CI license-compatibility gate (maintainer action)

