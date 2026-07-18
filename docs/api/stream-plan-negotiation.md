# Stream-Plan Negotiation — Client API Contract

**Audience:** authors of SoftMedia playback clients — the built-in web player today; dedicated
desktop / TV / mobile apps and third-party clients later.
**Status:** reflects the server as of 2026-07. The web player (`src/SoftMedia.Client`) is the
reference implementation.

SoftMedia's playback model is **client-agnostic by design**: the client declares what it can do,
the server decides the cheapest delivery method that fits (Direct Play → Remux → Transcode) and
explains its decision. A client is never required to accept processing — a capable client that
declares broad support receives the untouched source file (Direct Play) and may apply its own
deinterlacing, scaling, or shaders locally. Future capability fields must be **additive** so old
clients keep working.

---

## 1. Authentication

| Purpose | How |
|---|---|
| JSON API calls | `Authorization: Bearer <accessToken>` (from `POST /api/v1/auth/login`) |
| URL-embedded media auth (`<video src>`, HLS segments) | `?token=<mediaToken>` query parameter |
| Obtain the reduced-privilege media token | `GET /api/v1/auth/media-token` (Bearer auth) |

Use the **media token** in every playback URL. It omits the role claim, is only accepted on
media/streaming routes, and rotates on every silent access-token refresh — re-read the current
token per request rather than caching it for the life of a stream (the web player re-stamps it via
its HLS loader; see `VideoPlayer.tsx` `xhrSetup`).

## 2. Request a plan

```
POST /api/transcode/{mediaItemId}/plan
Authorization: Bearer <token>
Content-Type: application/json
```

Body — `ClientCapabilities` (all fields optional; defaults shown):

| Field | Type / default | Meaning |
|---|---|---|
| `videoCodecs` | `string[]` = `["h264"]` | Codecs the client decodes: `h264`, `hevc`, `av1`, `vp9`, … |
| `audioCodecs` | `string[]` = `["aac"]` | `aac`, `ac3`, `eac3`, `opus`, … |
| `maxAudioChannels` | `int` = `2` | 2 = stereo, 6 = 5.1, 8 = 7.1 |
| `supportsHdr` | `bool` = `false` | Display **and** codec can do HDR end-to-end |
| `displaySupportsHdr` | `bool` = `false` | Display hardware reports HDR |
| `codecSupportsHdr` | `bool` = `false` | Decoder handles HDR codecs |
| `maxBitrate` | `int` = `0` (kbps) | 0 = unlimited |
| `maxResolution` | `int` = `0` (height) | 720/1080/2160…; 0 = original |
| `supportedSubtitleFormats` | `string[]` = `["vtt"]` | Sidecar subtitle formats |
| `supportedContainers` | `string[]` = `["mp4","webm"]` | Include `"hls"` if the client has an HLS stack |
| `requestedQuality` | `string?` | User's explicit pick: `auto`/`720p`/`1080p`/`4k`/`original` |
| `subtitleTrackIndex` | `int?` | Track to burn in (bitmap subs / HDR burn-in) |
| `streamId` | `string?` | Client-generated opaque id isolating this playback session (`[A-Za-z0-9_-]`, ≤ 64 chars) |

## 3. Read the plan

Response — `StreamPlan`:

| Field | Meaning |
|---|---|
| `method` | `"DirectPlay"` \| `"Remux"` \| `"Transcode"` |
| `url` | The playback URL to use (see §4) |
| `displayProfile` | Human-readable summary, e.g. `"1080p H.264 Transcode"` |
| `videoCodec` / `audioCodec` / `container` | What will actually be delivered |
| `isHdr` / `sourceIsHdr` | Output HDR vs. source HDR (differ when tone-mapped) |
| `audioChannels`, `resolution` | Delivered channel count / `WxH` |
| `reason` | Free-form English (logs/back-compat) |
| `reasonCodes` | Machine-readable decision reasons — stable dotted keys + params for localization: `directplay.supported`, `remux.container`, `video.codec.unsupported`, `audio.codec.unsupported`, `hdr.tonemap`, `resolution.exceeds-max`, `transcode.required`, `bitrate.clamped` |

## 4. Follow the plan

- **DirectPlay** — `url` points at the source stream; append `?token=<mediaToken>` (or `&token=`
  if a query exists). The file is byte-served with Range support. The server does **no**
  processing — deinterlacing/scaling are the client's job on this path.
- **Remux / Transcode** — `url` is an HLS master playlist
  (`/api/transcode/{id}/master.m3u8?...`) already carrying `resolution`, `codec` and `sid`.
  Append as needed:

  | Query param | Meaning |
  |---|---|
  | `token` | media token (also required on every segment request) |
  | `sub` | subtitle track index to burn (`-1` = none) |
  | `audio` | audio track index |
  | `seek` | start offset in whole seconds |
  | `burnSubtitles` | `true` to force burn-in |

  Sidecar text subtitles: `GET /api/transcode/{id}/subtitles.vtt?token=…&sub=…&sid=…`.

## 5. Session lifecycle

| Action | Call |
|---|---|
| Stop / abandon playback | `DELETE /api/transcode/{id}?sid=<streamId>&token=…` |
| Progress save (resume support) | `POST /api/v1/interaction/{id}/progress` `{ "position": seconds }` (Bearer auth) |
| Mark watched | `POST /api/v1/interaction/{id}/watched` `{ "watched": true }` |

The server's throttle monitor suspends idle ffmpeg sessions on its own, but clients should still
send the DELETE when leaving playback. Changing tracks/quality: DELETE the session, then request
the master playlist again with the new parameters and the same `sid`.

## 6. Server-side stream-quality guarantees (2026-07 "Tier 1")

When (and only when) the **Transcode** path runs, the server guarantees:

1. **Never upscales** — every scale target is clamped to the source width (`min(W, iw)`); a 720p
   request against a 704×264 source encodes at 704×264.
2. **Deinterlaces interlaced sources** (`bwdif` software / `yadif_cuda` on CUDA paths), detected
   from ffprobe `field_order`, applied before any subtitle burn-in. Necessary because browser
   clients cannot deinterlace.
3. **Tone-maps HDR→SDR** when the client can't do HDR end-to-end or subtitles must be burned into
   HDR content.
4. Downscales use lanczos.

Clients with superior local processing (e.g., an mpv-based desktop app with its own deinterlacer
and shader scalers) should simply declare broad capabilities: they'll receive Direct Play and full
control of the pixels. That *is* the "give me the untouched source" mechanism — no extra flag
exists or is needed.
