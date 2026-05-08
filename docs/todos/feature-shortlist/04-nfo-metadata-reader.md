# Task 04 — `.nfo` (Kodi/XBMC) sidecar metadata reader

**Wave:** D
**Plan:** [feature-shortlist-plan-2026-04-30.md](../../plans/feature-shortlist-plan-2026-04-30.md#wave-d--nfo-kodixbmc-sidecar-metadata-reader)
**Severity:** Low — additive feature; no behavior change for users without `.nfo` files.
**Estimated effort:** 2 days. Single PR.
**Branch:** `feat/nfo-metadata-provider`

---

## Background

Many users come to a self-hosted media server from Sonarr/Radarr/Kodi setups where movies and TV shows already have curated metadata in companion `.nfo` XML files (the [Kodi NFO spec](https://kodi.wiki/view/NFO_files)). SoftMedia has never read these — `Grep` for `.nfo` in the server returns zero matches in source code (only one match in `FileWatcherIssue.cs` which is unrelated to NFO parsing).

The metadata routing system already supports primary + fallback providers — see the comic provider chain at [MetadataRouter.cs:191-262](../../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs#L191-L262) which is the perfect template. We add two new `IMetadataProvider` implementations (`NfoMovieProvider`, `NfoTvProvider`) that read local `.nfo` XML — no API quota, no network — and wire them as configurable fallbacks for Movie and TV.

[ComicInfoXmlProvider.cs](../../../src/SoftMedia.Server/Services/Metadata/ComicInfoXmlProvider.cs) is also a near-perfect *implementation* template: a local-file-only provider that reads embedded XML, returns `MetadataResult`, runs without network. Mimic its structure exactly.

## Behavior after this task

### NFO discovery

Following Kodi convention:

- **Movies.** For movie file `Avatar (2009).mkv`, the provider checks (in order): `Avatar (2009).nfo`, then `movie.nfo` in the same folder. First hit wins.
- **TV episodes.** For an episode file `S01E03 - Title.mkv`, checks `S01E03 - Title.nfo` matching the file stem.
- **TV series root.** For a series MediaItem (`Type = Series`), `item.Path` points at the series folder (per [TvScanner.cs](../../../src/SoftMedia.Server/Services/Scanning/TvScanner.cs) conventions). The provider checks `tvshow.nfo` inside that folder.
- **Seasons.** Out of scope for v1 — Kodi spec allows `season.nfo` per season folder, but season metadata is mostly derived from TVMaze episode lists today. Add later if requested.

### Provider chain wiring

- Two new settings (with sensible defaults):
  - `MovieFallbackProvider` — default `"Nfo"`. Possible values: `"Nfo"`, `"None"`.
  - `TVFallbackProvider` — default `"Nfo"`. Possible values: `"Nfo"`, `"None"`.
- Routing semantics mirror the comic chain at [MetadataRouter.FetchComicMetadataAsync](../../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs#L191): primary runs first, sufficiency check, fallback fills gaps if primary insufficient. **Fields from the primary always win** when both providers return a value — fallback only fills holes.
- "Sufficiency" for a movie/TV `MetadataResult`: has `Title` AND (`Description` OR `PosterUrl` OR `Year`). Same shape as the comic check at [MetadataRouter.cs:218-221](../../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs#L218-L221).
- Users who explicitly want NFO-first set `MovieProvider=Nfo` or `TVProvider=Nfo` in settings. The router treats `Nfo` as just another `ProviderName`.
- Setting a fallback to `"None"` disables the chain — same convention used by [ComicFallbackProvider](../../../src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs#L128).

### Field mapping

Read these elements from `<movie>` (movie NFO) and `<tvshow>` / `<episodedetails>` (TV NFO), mapped onto `MetadataResult`:

| NFO element                               | `MetadataResult` field             |
|-------------------------------------------|------------------------------------|
| `<title>`                                 | `Title`                            |
| `<plot>` (preferred) or `<outline>`       | `Description`                      |
| `<year>` or year part of `<premiered>`    | `Year`                             |
| `<premiered>` (full ISO date)             | `ReleaseDate`                      |
| `<mpaa>`                                  | `ContentRating`                    |
| `<imdbid>` or `<uniqueid type="imdb">`    | `ImdbId`                           |
| `<studio>`                                | `Studio`                           |
| `<director>` (first if multiple `<director>` elements) | `Director`            |
| `<genre>` (multiple)                      | `Genres`                           |
| `<rating>` (single) or `<ratings>/<rating>/<value>` | `Rating`                  |
| `<thumb>` or `<art><poster>`              | `PosterUrl` (only if value starts with `http://`, `https://`, or is a local path that exists) |
| `<fanart><thumb>`                         | (not mapped to MetadataResult — backdrops are handled later by scanner image extraction) |
| `<season>` + `<episode>`                  | (TV-specific; episodes use these for hierarchy validation only — the scanner already parses these from filename) |
| `<actor><name>` (multiple)                | `Cast` (one `CastMember` per actor; map `<role>` to `Character`) |

Empty / whitespace / `"N/A"` values are ignored. Fields that don't appear in the NFO stay null.

### Security — XXE / XML expansion

Files come from the user's library, but the library jail isn't a defense against malicious XML — a path-traversal-safe library can still contain a hostile NFO. The parser must:

- Disable DTD processing — `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }`. Blocks classic XXE.
- Cap document size — `MaxCharactersInDocument = 1_000_000` (1 MiB of text). NFO files are normally 1–5 KB; anything beyond a meg is a billion-laughs / quadratic-blowup attempt.
- Read via `FileStream` opened with `FileShare.Read` so a concurrent scan doesn't fail with sharing-violation errors.
- Wrap the entire load in `try/catch (XmlException, IOException)` — log a warning and return null. The next provider in the chain runs.

## Files to add

### 1. Shared parser

**`src/SoftMedia.Server/Services/Metadata/Nfo/NfoXmlParser.cs`** (new folder + file). Pure-static helper. Public surface:

```csharp
public static class NfoXmlParser
{
    /// <summary>
    /// Loads an NFO file with XXE-safe settings. Returns null on missing file or
    /// any parse error (caller logs).
    /// </summary>
    public static XDocument? TryLoad(string path);

    /// <summary>
    /// Builds a MetadataResult from the root element. The root tag is informational
    /// only — both `<movie>` and `<episodedetails>` share most fields. Returns null
    /// if no usable data was extracted.
    /// </summary>
    public static MetadataResult? BuildFromRoot(XElement root);
}
```

`TryLoad` opens the file with the safety settings above. `BuildFromRoot` walks the element tree per the field-mapping table.

### 2. Movie provider

**`src/SoftMedia.Server/Services/Metadata/Nfo/NfoMovieProvider.cs`**.

**Use `IFileSystem` for filesystem access.** The codebase has [Services/Abstractions/IFileSystem.cs](../../../src/SoftMedia.Server/Services/Abstractions/IFileSystem.cs); inject it instead of calling `File.Exists` / `File.OpenRead` directly. This keeps the providers unit-testable without touching the disk and matches the pattern used elsewhere in scanners.

```csharp
public class NfoMovieProvider : IMetadataProvider
{
    private readonly IFileSystem _fs;
    private readonly ILogger<NfoMovieProvider> _logger;

    public LibraryType SupportedType => LibraryType.Movie;
    public string ProviderName => "Nfo";

    public NfoMovieProvider(IFileSystem fs, ILogger<NfoMovieProvider> logger)
    {
        _fs = fs;
        _logger = logger;
    }

    public Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        if (item.Type != MediaType.Movie) return Task.FromResult<MetadataResult?>(null);
        var nfoPath = ResolveMovieNfoPath(item);
        if (nfoPath is null) return Task.FromResult<MetadataResult?>(null);

        var doc = NfoXmlParser.TryLoad(_fs, nfoPath, _logger);
        if (doc?.Root?.Name.LocalName != "movie") return Task.FromResult<MetadataResult?>(null);

        var result = NfoXmlParser.BuildFromRoot(doc.Root);
        if (result?.Title is null) return Task.FromResult<MetadataResult?>(null);
        return Task.FromResult<MetadataResult?>(result);
    }

    private string? ResolveMovieNfoPath(MediaItem item)
    {
        if (string.IsNullOrEmpty(item.Path)) return null;
        var dir = Path.GetDirectoryName(item.Path);
        if (dir is null) return null;

        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var candidates = new[]
        {
            Path.Combine(dir, $"{stem}.nfo"),
            Path.Combine(dir, "movie.nfo")
        };
        return candidates.FirstOrDefault(_fs.FileExists);
    }
}
```

`NfoXmlParser.TryLoad` takes `IFileSystem` so it can read through the abstraction. `FetchMetadataAsync` returns `Task.FromResult` because the body is sync — `Task.Run` on tiny file reads is wasteful.

### 3. TV provider

**`src/SoftMedia.Server/Services/Metadata/Nfo/NfoTvProvider.cs`** — same shape as the movie provider with three differences:

- `SupportedType => LibraryType.TV`
- Path resolution branches on `item.Type`:
  - `MediaType.Series`: `item.Path` is the series folder; check `tvshow.nfo` inside.
  - `MediaType.Episode`: `item.Path` is the episode file; check `<stem>.nfo` next to it.
  - Anything else: return null.
- Root element validation: `<tvshow>` for a Series, `<episodedetails>` for an Episode.

### 4. Settings defaults

**[src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs](../../../src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs#L85)** — add to the defaults list, in the Metadata group, after the existing `MovieProvider` / `TVProvider` entries:

```csharp
new() { Key = "MovieFallbackProvider", Value = "Nfo", Group = "Metadata",
        Description = "Fallback provider for movies when the primary returns no usable metadata. Possible values: Nfo, None." },
new() { Key = "TVFallbackProvider", Value = "Nfo", Group = "Metadata",
        Description = "Fallback provider for TV when the primary returns no usable metadata. Possible values: Nfo, None." },
```

**Upgrade safety:** [SettingsService.InitializeDefaultsAsync at lines 144-148](../../../src/SoftMedia.Server/Services/Infrastructure/SettingsService.cs#L144-L148) only inserts a default when the key does **not** already exist (`if (!await _context.Settings.AnyAsync(s => s.Key == def.Key))`). Adding new entries here is therefore non-destructive on existing installs — a previously-set value is never overwritten. Fresh installs get `"Nfo"` as the default fallback; existing installs without these keys get `"Nfo"` on next startup.

### 5. DI registration

**[src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs](../../../src/SoftMedia.Server/Extensions/ServiceCollectionExtensions.cs#L143)** — register alongside the comic providers:

```csharp
services.AddScoped<IMetadataProvider, Services.Metadata.Nfo.NfoMovieProvider>();
services.AddScoped<IMetadataProvider, Services.Metadata.Nfo.NfoTvProvider>();
```

No `AddHttpClient` registration — these providers don't make network calls.

## Files to modify — `MetadataRouter`

**[src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs](../../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs)** — extend the routing for Movie and TV to use a primary+fallback chain analogous to `FetchComicMetadataAsync`. Refactor strategy:

1. Extract a private `Task<MetadataResult?> RunChainAsync(IMetadataProvider? primary, IMetadataProvider? fallback, MediaItem item, Func<MetadataResult, bool> sufficiencyCheck, ...)` helper that the comic, movie, and TV cases all share. The existing `FetchComicMetadataAsync` becomes the first caller.
2. Add `FetchMovieMetadataAsync(item)` and `FetchTvMetadataAsync(item)`.
3. Update the `switch` at [MetadataRouter.cs:46-62](../../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs#L46-L62) to delegate Movie/TV to the new methods.

The sufficiency check for Movie/TV: `r => !string.IsNullOrEmpty(r.Title) && (!string.IsNullOrEmpty(r.Description) || !string.IsNullOrEmpty(r.PosterUrl) || r.Year.HasValue)`.

**Important:** keyed providers (OMDb) use a different code path. The new `RunChainAsync` helper must call `FetchKeyedMetadataAsync` for the primary if it's `IKeyedMetadataProvider`, or the regular `FetchMetadataAsync`. Don't bypass the OMDb daily-limit logic.

## Frontend — optional polish

**[src/SoftMedia.Client/src/pages/SettingsPage.tsx](../../../src/SoftMedia.Client/src/pages/SettingsPage.tsx)** — in the Metadata Data Sources section, add two `<Combobox>` controls bound to `MovieFallbackProvider` and `TVFallbackProvider` with options `["Nfo", "None"]`. This is optional — the settings already work via the generic settings table; adding the dropdowns is a 30-minute UX improvement.

## Tests

### NfoXmlParser

20. **`src/SoftMedia.Server.Tests/Services/Metadata/Nfo/NfoXmlParserTests.cs`** — Theory tests against fixture XML strings:
    - Well-formed `<movie>` with all mapped fields → all fields populated correctly.
    - `<plot>` preferred over `<outline>` when both present.
    - `<premiered>` full date populates both `Year` and `ReleaseDate`.
    - `<imdbid>` and `<uniqueid type="imdb">` both work; the latter takes priority if both present.
    - Multiple `<director>` elements → first one wins.
    - Multiple `<actor>` blocks → all map to `CastMember` with name + character.
    - Empty / whitespace / `"N/A"` values are ignored.
    - **Security**: a payload with a DOCTYPE declaration → `TryLoad` returns null (DTD prohibited).
    - **Security**: a 2 MiB payload of nested elements → `TryLoad` returns null (size cap).

### Providers

21. **`src/SoftMedia.Server.Tests/Services/Metadata/Nfo/NfoMovieProviderTests.cs`** — uses `IFileSystem` abstraction or temp directory:
    - `<stem>.nfo` next to movie file → returns populated result.
    - `movie.nfo` in same folder when stem-named NFO is missing → returns populated result.
    - No NFO file → returns null.
    - Wrong root element (`<episodedetails>` for a movie) → returns null.
    - `item.Type != MediaType.Movie` → returns null without touching the filesystem.
    - Malformed XML → returns null and logs a warning (assert via `ILogger<T>` mock).

22. **`src/SoftMedia.Server.Tests/Services/Metadata/Nfo/NfoTvProviderTests.cs`** — analogous:
    - Series MediaItem with `tvshow.nfo` in folder → returns populated result.
    - Episode MediaItem with `<stem>.nfo` next to file → returns populated result.
    - Episode with wrong root element → null.
    - `item.Type` outside Series/Episode → null.

### Router

23. **`src/SoftMedia.Server.Tests/Services/Metadata/MetadataRouterNfoChainTests.cs`**:
    - Movie with `MovieProvider=OMDb`, OMDb succeeds → fallback (`NfoMovieProvider`) is **not** invoked. Assert via mock call count.
    - Movie with `MovieProvider=OMDb`, OMDb returns null → fallback runs and its result is returned.
    - Movie with `MovieProvider=OMDb`, OMDb returns `Title` only → fallback runs, returned result merges fallback's `PosterUrl` into OMDb's `Title`.
    - `MovieFallbackProvider=None` → fallback never runs even if primary returns null.
    - Both primary and fallback return null → router returns null.
    - User sets `MovieProvider=Nfo` → NFO is the primary; no fallback runs by default (`MovieFallbackProvider` value of `"None"` or matching the primary should be a no-op — mirror the comic guard at [MetadataRouter.cs:204-206](../../../src/SoftMedia.Server/Services/Metadata/MetadataRouter.cs#L204-L206)).

## Acceptance criteria

- A movie file `C:\Movies\Inception (2010)\Inception (2010).mkv` with sibling `Inception (2010).nfo` containing well-formed `<movie>` XML scans into `MediaItems` with `Title`, `Year`, `Overview`, `Director`, `Genres` populated from the NFO **even with no internet connection**.
- A TV episode `Show\S01E01 - Pilot.mkv` with sibling `S01E01 - Pilot.nfo` populates the episode's `Overview` and `ReleaseDate` from the NFO.
- A library with no NFO files behaves identically to today (regression baseline).
- Setting `MovieFallbackProvider=None` disables NFO fallback for movies.
- A maliciously crafted NFO with a DOCTYPE declaration logs a warning and is treated as if no NFO were present (no exception bubbles up to the scanner).
- `dotnet test` passes; all four new test files green.
- No new EF migration.
- No new HTTP client registrations.

## Out of scope

- **Writing back to NFO files.** SoftMedia is read-only with respect to user files. If the user edits a star rating in SoftMedia, that stays in `UserMediaInteraction`, not in the NFO.
- **`season.nfo`** at the season folder level. Season metadata comes from TVMaze episode lists for now.
- **Embedded `<thumb>` images as actual local file extraction.** If the NFO references `<thumb>file:///...</thumb>`, the provider records the path string; making the image cache pipeline copy local files is a separate task.
- **Sanitising NFO HTML in `<plot>`.** That's already the responsibility of the metadata-rendering layer per SDD §6.2. If the rendering path doesn't currently strip HTML, file a separate ticket — don't re-implement sanitization in the provider.
