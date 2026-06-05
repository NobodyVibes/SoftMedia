# Chromecast Casting

SoftMedia can cast video to a Chromecast (or Google TV / Android TV) on your network from the
web player. Because of how Chromecast works, this requires your server to be served over HTTPS
with a real certificate — see [Requirements](#requirements) below.

## How It Works

When you press the **Cast** button in the player and pick a device:

1. **A Chromecast-tuned stream is prepared.** The Chromecast's built-in (Default Media)
   receiver only reliably decodes **H.264 video + AAC audio up to 1080p** — not HEVC, AV1, VP9,
   MKV, or HDR. So SoftMedia requests a plan tuned to *the device*, not your desktop browser:
   the source is transcoded to H.264/AAC and packaged as HLS. (Your browser may direct-play a
   4K HEVC `.mkv` fine, but the TV would not — hence the dedicated plan.)
2. **The device fetches the stream itself.** Casting does **not** relay video from your
   computer; the Chromecast opens the stream URL directly. That URL must therefore be reachable
   *from the TV* and use a certificate the TV trusts — which is why HTTPS with a public
   certificate is mandatory.
3. **A long-lived, single-movie token is used.** The receiver can't refresh your normal login,
   so SoftMedia issues a token scoped to just that one title's stream, valid long enough for a
   full movie. It grants nothing else, so a cast URL leaking from a TV can't be used to access
   your account.
4. **Local playback pauses** on your computer once the cast starts, so it isn't playing in two
   places at once.

Your per-user and LAN/WAN **bitrate caps apply to cast streams** exactly as they do to browser
playback.

## Requirements

Chromecast imposes two hard rules that no application can work around:

1. **A secure context.** Browsers only enable the Cast button over **HTTPS** (or `localhost`).
   On a plain-HTTP LAN address like `http://192.168.x.x`, the Cast button does not appear at all.
2. **A publicly-trusted certificate.** The Chromecast fetches the stream and only accepts a
   certificate that chains to a **public** Certificate Authority. Self-signed or `mkcert`/private
   -CA certificates work in your browser but are **rejected by the device**, so casting fails.

Together these mean you need:

- **A real domain name** (free options exist: DuckDNS, a cheap Cloudflare-managed domain, etc.).
- **A Let's Encrypt (or other publicly-trusted) certificate**, typically via a reverse proxy.
- Served so the domain **resolves to your server** — locally via split-horizon DNS for a LAN-only
  setup.

The full setup — a Caddy reverse proxy with the Let's Encrypt **DNS-01** challenge (works even
when your server has no public ports open) plus split-horizon DNS — is documented in
**[reverse-proxy.md → TLS termination for Chromecast casting](../reverse-proxy.md#tls-termination-for-chromecast-casting)**.

> **Why a domain is effectively required:** a public CA will not issue a certificate for a
> *private* LAN IP (`192.168.x.x`, `10.x.x.x`, …), and a home server behind NAT usually has no
> public IP to certify either — while the Chromecast accepts nothing less than a publicly-trusted
> certificate. So in practice you need a domain. (Let's Encrypt now issues certs for *public* IPs,
> but that needs a publicly-reachable address, which a NAT'd home server doesn't have.) This is the
> same reason Plex uses its `*.plex.direct` certificate service and Jellyfin recommends a reverse
> proxy with a real domain — SoftMedia follows the Jellyfin model (you bring a domain; the project
> hosts nothing).

## Troubleshooting

**The Cast button doesn't appear.**
You're almost certainly on an insecure origin. The button only shows over HTTPS or on
`localhost`. If you're browsing by LAN IP (`http://192.168.x.x:…`), set up HTTPS as above. Note
the catch-22: `localhost` shows the button but the resulting URL (`localhost`) is unreachable by
the TV; a LAN IP is reachable but hides the button. Only HTTPS on a real domain satisfies both.

**The receiver app opens but the movie only plays on my computer.**
The device couldn't load the stream. Common causes: you're casting from `localhost` (the TV
can't reach it — SoftMedia shows an in-player notice in this case), or the certificate isn't
publicly trusted. Open the browser console and look for a line beginning with `[Cast]` for the
specific error.

**It worked, then stopped partway through a long movie.**
This should not happen with the long-lived cast token, but if you see authentication errors late
in playback, check that your reverse proxy isn't stripping the query string from stream URLs.

## Current Limitations

- **1080p / H.264 ceiling.** SoftMedia casts at up to 1080p H.264 for universal compatibility.
  A 4K-capable receiver (Chromecast Ultra, Google TV) will still play — just at 1080p. Lifting
  this safely requires a *custom* Cast receiver app, because the Web Sender SDK cannot reliably
  detect a specific device's 4K/HEVC support (the `VIDEO_OUT` capability flag is identical on a
  1080p Chromecast and a 4K Google TV). That is deferred to avoid black-screening 1080p-only
  devices.
- **Default Media Receiver only.** SoftMedia uses Google's built-in receiver, which needs no
  developer registration or project-hosted infrastructure. A branded custom receiver (richer UI,
  on-device codec probing, 4K) is a possible future addition.

## Related

- [Running SoftMedia Behind a Reverse Proxy](../reverse-proxy.md) — TLS setup, DNS-01, split-horizon DNS.
- [Smart Transcoding](smart-transcoding.md) — how the stream plan is chosen for browser and cast.
