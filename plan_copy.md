# Implementation Plan: Scanning & Metadata Refactor

This plan systematically addresses the critical architectural issues discovered in the SoftMedia metadata and scanning systems, ensuring that we achieve a decoupled, scalable, and highly performant architecture.

## User Review Required

> [!IMPORTANT]
> - **Schema Deletion (`MetadataJson`)**: I will be explicitly removing the `MetadataJson` JSON string column from `MediaItem`. This prevents data duplication and lowers DB size, but requires a database migration. 
> - **Provider Caching**: TVMaze returns full series data (all episodes). To prevent losing this data when `MetadataJson` is dropped, I will introduce a `ProviderMetadataCache` table designed specifically for holding untyped, provider-native JSON responses locally.

---

## Proposed Changes

### Phase 1: Database Normalization & `MetadataJson` Removal
We will purge the duplicated JSON blob storage and replace it with a dedicated table designed strictly for provider caching, allowing `MediaItem` to remain pure and relational.

#### [MODIFY] src/SoftMedia.Server/Models/MediaItem.cs
- Remove `public string? MetadataJson { get; set; }`.

#### [MODIFY] src/SoftMedia.Server/Models/ProviderMetadataCache.cs
- Create entity `ProviderMetadataCache` containing `Id`, `MediaItemId` (Guid), `ProviderId` (string), `RawPayload` (string/JSON), and `LastUpdated` (DateTime).

#### [MODIFY] src/SoftMedia.Server/Data/AppDbContext.cs
- Register `DbSet<ProviderMetadataCache>`.

#### [MODIFY] src/SoftMedia.Server/Services/Metadata/EmbeddedMusicProvider.cs
- Update provider to avoid looking for `MetadataJson` and instead verify if fields like `Album` and `Duration` are already populated on the `MediaItem`, skipping expensive `TagLib` disk reads if the generic file scanner already extracted them.

#### [MODIFY] src/SoftMedia.Server/Services/Metadata/MetadataAggregator.cs
- Remove `MetadataJsonMerger` usage entirely.
- Stop writing standard fields back into JSON.
- For Series fetching, save the TVMaze payload directly into `ProviderMetadataCache`.
- Change `MetadataHash` logic to hash the *standardized model fields* instead of the JSON string.

#### [MODIFY] tests/ & src/SoftMedia.Server/Helpers/
- Delete `MetadataJsonMerger.cs` and `MetadataJsonHelper.cs` (dead code).

---

### Phase 2: Resolving N+1 Database Lookups
We will refactor the local scanners to use O(1) in-memory bulk lookups, matching the performance requirements of massive library scans.

#### [MODIFY] src/SoftMedia.Server/Services/Scanning/BaseMediaScanner.cs
- **Action:** At the beginning of `ScanLibraryAsync`, bulk-load all existing file paths for the given `LibraryId` into a thread-safe `ConcurrentDictionary<string, MediaItem> _knownFilesCache`.
- **Action:** Inside the `Parallel.ForEachAsync` file loop, replace `await context.MediaItems.FirstOrDefaultAsync(...)` with an `O(1)` dictionary lookup against `_knownFilesCache`.

---

### Phase 3: Unblocking the Metadata Queue (Simplified Approach)
The original plan proposed a persistent SQLite job table, but for a local-first application, achieving resilience via database polling introduces unnecessary overhead and complexity. A simpler, equally effective fix is to rely on SoftMedia's existing "Daily Rescan" for fault tolerance, and simply change the channels to be unbounded (or bound to a practically limitless size) so they never block the fast disk IO.

#### [MODIFY] src/SoftMedia.Server/Services/Metadata/MetadataQueueService.cs
- Change `Channel.CreateBounded` with `Wait` to `Channel.CreateUnbounded<MetadataQueueItem>()`. Even 100,000 queued items only consume ~3MB of RAM, which is completely negligible and permanently prevents the scanner threads from stalling on API constraints.
- Fix the Deduplication guard (`_pendingIds`) memory leak: ensure `_pendingIds.TryRemove()` executes in a `finally` block or is aggressively cleared if an unhandled exception violently crashes the task worker, preventing permanent lockout of item processing.

---

### Phase 4: Fixing SQLite Write Scaling
With the Metadata Job Queue hitting SQLite, and Scanners reading directories via `Parallel.ForEachAsync`, we must strictly prevent SQLite lock contention. While SQLite WAL handles concurrent reads, it only allows one writer. EF Core's default retry behavior frequently drops large concurrent transactions.

#### [MODIFY] src/SoftMedia.Server/Services/Scanning/BaseMediaScanner.cs
- Wrap `await context.SaveChangesAsync(ct);` calls inside the parallel directory loop with an application-level strict asynchronous semaphore (`static readonly SemaphoreSlim _dbWriteLock = new(1, 1)`). This explicitly serializes SQL transactions while completely preserving parallel disk I/O and CPU scanning, serving as the definitive long-term fix for SQLite write concurrency under heavy load.

---

### Phase 5: Event Debounce Logic Overhaul
We will finalize the refactor by resolving the broken logic inside the cache signal loop.

#### [MODIFY] src/SoftMedia.Server/Services/Metadata/MetadataQueueService.cs
- **Action:** Rewrite `CacheUpdateLoopAsync` to implement true debouncing.
- **Logic:**
  ```csharp
  var batch = new HashSet<Guid>();
  while (await _cacheUpdateChannel.Reader.WaitToReadAsync(ct)) {
      while (_cacheUpdateChannel.Reader.TryRead(out var id)) { batch.Add(id); }
      await Task.Delay(1000, ct); // Wait for the storm to settle
      foreach (var id in batch) { await UpdateRecentlyAddedCacheAsync(id); }
      batch.Clear();
  }
  ```

---

## Open Questions

None. The issues and solutions are strictly isolated to fixing local server architecture debt.

## Verification Plan

### Automated Tests
- `dotnet run test` backend xUnit tests.

### Manual Verification
- Compile and run standard scanner functions (via the API).
- Validate that the progress bar doesn't "freeze" when scanning music directories.
- Validate that TV episodes successfully load their episode titles from the new `ProviderMetadataCache` table dynamically.
