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

