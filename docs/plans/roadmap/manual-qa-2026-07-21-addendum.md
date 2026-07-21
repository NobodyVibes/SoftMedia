# Manual QA Addendum — July 2026 features (pre-1.0 sweep)

**Date:** 2026-07-21
**Use with:** [manual-qa-2026-05-30.md](./manual-qa-2026-05-30.md) (still valid for Phases 0–3 features).
**Scope:** everything shipped since — the July remediation waves, photos, and Sessions 1/2/4 of the native-app-readiness plan. Steps marked **[DEVICE]** need hardware a headless session can't drive; everything else was covered by automated tests and (where noted) live API verification on 2026-07-21.

---

## 1. Photos (NR-WI-013)

1. Settings → Library Management → Add Library → type **Photo** now appears. Point it at a folder of JPGs.
2. After the scan: the library grid shows photo cards with real thumbnails (portrait phone photos display **upright** — this exercises the EXIF-orientation fix).
3. Open a photo: the detail page shows the full-resolution image, EXIF cards (camera, ISO, date taken, GPS when present), and a resolution card. ← / → arrow keys page through the library.
4. **Right:** no Play/Watched/Watchlist buttons on photos; photo libraries don't appear in home rows or hero. **Wrong:** a photo card 401s (auth token missing on image URL) or renders sideways.
5. Known limitation: HEIC files show a fallback card (no browser support) — not a bug.

## 2. Quick Connect (NR-WI-006) — **[DEVICE recommended]**

*Server side verified live via API 2026-07-21 (full pairing flow, single-use claim, disabled-gate). What remains is the human/UI experience.*

1. Settings → Account Management → toggle **Enable Quick Connect** on (it ships off).
2. On a second device (or an incognito window), POST `/api/v1/quickconnect/initiate` — or once a native/TV client exists, use its sign-in screen. Note the 6-character code.
3. On your logged-in browser: My Account → **Link a Device** card → enter the code → device name/IP appear → **Authorize this device**.
4. The device's poll receives tokens within ~3 s and is signed in as you.
5. **Right:** a wrong code says "Code not found"; an expired code (>10 min) fails; the same code can't be authorized twice. **Wrong:** the card errors with Quick Connect enabled, or an API-token client can call authorize.

## 3. Extras / trailers (NR-WI-014)

1. Beside a movie file, drop `MovieName-trailer.mkv` (or an `extras/` subfolder with a clip). No rescan needed — extras are probed live.
2. Open the movie's detail page: an **Extras** row lists the clip; clicking plays it in a modal (Escape closes).
3. Run a library scan: the trailer must **not** appear as its own movie card (the scanner now skips companions; any old junk cards purge).
4. **Wrong:** trailer appears in search/home/hero anywhere, or the modal video 401s.

## 4. Server & Network page (NR-WI-010/011/012)

1. Settings → **Server & Network** (new sidebar entry, admin only).
2. Set **Server Name** to something personal + a **Login Message** → save → log out: the login page shows both, and the browser tab title updates.
3. **Log Level** → Debug → save → the Recent Logs card (same page) starts showing debug-era volume immediately, **no restart**. Set it back to Information.
4. Connection card shows your LAN IP(s) and how you're connected.
5. Webhook Delivery block renders the master switch + timeout + the three Allow* toggles under an amber SSRF warning.
6. `/swagger` in a browser serves the API reference; toggling **Enable Api Docs** off makes it 404 immediately.

## 5. Security-wave items — **[DEVICE / operator decision]**

1. **CSP enforce (T13.1/T13.2)**: with the SPA served in production mode, open DevTools console and browse/play/cast for a few minutes. If **zero** CSP violation reports appear, set `Security:EnforceCsp` to `true` in `appsettings.json`, restart, and repeat the sweep — everything must still work, **including Google Cast** (the policy allows gstatic; a real Chromecast session is the proof).
2. **[DEVICE] Chromecast**: cast a movie end-to-end (play/pause/seek/stop) from Chrome.
3. **[DEVICE] DLNA**: with DLNA enabled and a library exposed, browse + play from the TV.
4. **Session dashboard**: while something plays, Admin Dashboard shows the session with device + address; Stop terminates it and it stays stopped.

## 6. Release cut (NR-WI-016)

1. Fresh-install check: clone to a clean folder, follow the README on Windows only (Docker deferred), first login must **force** an admin password change.
2. When §1–§5 pass: `git tag -a v1.0.0 -m "SoftMedia 1.0.0"` on `main` and push the tag.
