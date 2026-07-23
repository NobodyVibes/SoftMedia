# Photo Libraries

**Since:** 2026-07-21 (NR-WI-013)

SoftMedia can index and display photo libraries alongside your other media.

## What it does

- **Library type "Photo"** in Settings → Libraries. Point it at one or more folders;
  the scanner indexes `jpg, jpeg, png, webp, heic, bmp, gif, tiff`.
- **EXIF metadata** is read inline at scan time: camera make/model, ISO, f-stop,
  exposure, GPS coordinates, and the date taken (which also drives Year and the
  photo's release date for sorting). No network calls — photo metadata never
  leaves your machine, consistent with the privacy charter.
- **Dimensions** are read from the image header (no full decode) and shown on the
  detail page.
- **Albums from your folders** (2026-07-22): a photo library opens as a grid of
  album cards — one per folder, named after it, covered by its newest photo, newest
  album first. Loose photos in the library root land in "Unsorted"; a library that
  is a single folder of pictures skips the album layer and shows the photos
  directly. Inside an album: a square photo grid in date-taken order, and the
  photo viewer's ← / → keys page within that album.
- **Timeline view** (2026-07-23): an Albums | Timeline toggle at the top of the
  library — Timeline shows every photo newest-first under sticky month headers
  ("July 2026"). Filtering while in Timeline collapses to flat search results.
- **Favorites** (2026-07-23): hover a photo tile and click the heart (it stays
  visible on favorited photos); a Favorites chip in the filter bar narrows any
  view to hearted photos. Favorites are per-user.
- **Hover arrows & fullscreen** (2026-07-23): hovering the photo reveals
  previous/next chevrons (same targets as the ← / → keys) and a fullscreen
  button. Fullscreen shows the photo edge-to-edge on black with the same arrows
  and slideshow controls; it survives paging between photos, uses the browser's
  real fullscreen when permitted, and exits via ✕ or Escape.
- **Slideshow transitions** (2026-07-23): Client Settings → Playback → "Photo
  Slideshow Transition" picks how photos enter the viewer and slideshow — Fade
  (default), Zoom (a slow Ken Burns drift across the 5-second dwell), Slide, or
  None for instant cuts. Per-device, like all viewing preferences.
- **Slideshow** (2026-07-23): a play button on the photo viewer auto-advances
  every 5 seconds through the current album (or search scope), looping back to
  the first photo at the end — so it can start from any photo, including the
  last one. Pause with the same button; manual navigation keeps the show rolling.
- **HEIC support** (2026-07-23): iPhone HEIC photos now display — thumbnails and
  a full-size preview are converted server-side through the bundled ffmpeg (the
  original file stays untouched; "open original" still downloads the HEIC).
- **Photo-specialised search & filters** (2026-07-23): the photo filter bar offers
  text search, an EXIF camera filter, a year-taken filter, and an oldest/newest
  toggle — no genres/ratings/watched controls, which mean nothing for photos.
  Searching or filtering from the album grid searches across the whole library;
  the same controls inside an album narrow just that album. The camera and year
  dropdowns only appear when the library actually has those facets.
- **Cards & thumbnails**: grids serve server-generated WebP thumbnails with EXIF
  orientation baked in (portrait phone photos display upright).
- **Detail view**: full-resolution photo (letterboxed), EXIF cards, an
  open-original button, previous/next navigation — including ← / → arrow keys.
- **Access control**: photos honor per-user library ACLs and are served through
  the same path-jailed, token-authenticated route as all other media
  (`/api/v1/photos/{id}/image`, media-token query auth for `<img>` tags).

## Behaviour notes

- Photos never enter the metadata queue — they are fully self-describing, so a
  10k-photo library scans without flooding the enrichment pipeline.
- Photo libraries are excluded from the home-page rows and the hero rotation by
  design; browse them from the sidebar.
- Photos have no Play/Watched/Watchlist affordances — the detail page is the
  viewer.
- Content-rating ceilings do not apply to photos (there is no rating source for
  personal photos); per-user **library** access is the sharing control.
- DLNA does not expose photo libraries (AV libraries only).

## Known limitations

- **HEIC**: displayed via server-side ffmpeg conversion (thumbnails + preview).
  Dimension metadata is still unavailable for HEIC, and conversion requires the
  ffmpeg binary SoftMedia already uses for transcoding — without it, HEIC photos
  fall back to a placeholder card.
- Very large images beyond the decode-bomb pixel budget are served as originals
  but not thumbnailed.
- Albums come from folders only — no manual album editing, and no timeline/map
  views yet. Rearranging folders on disk rearranges the albums on the next scan.
