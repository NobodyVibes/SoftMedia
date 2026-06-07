# DLNA Media Server (for smart TVs)

SoftMedia can act as a **DLNA/UPnP media server** so a smart TV's built-in media player —
including LG (webOS) and Samsung (Tizen) TVs that are **not** Chromecast receivers — can browse
and play your library directly over the local network. No Chromecast, no certificate, no
browser. This is the answer to "how do I watch on my LG TV without a Chromecast?".

It is **off by default** and, when enabled, **unauthenticated and LAN-only** — see
[Security](#security-read-this) before turning it on.

## How It Works

When enabled, SoftMedia announces itself on the LAN via SSDP (UPnP discovery). Your TV's media
player finds "SoftMedia" in its list of network servers, and you browse a simple tree:

```
SoftMedia
├── Movies   → your movies
├── Shows    → series → episodes
└── Music    → albums → tracks
```

The TV requests each file directly from SoftMedia and plays it with its own decoder. SoftMedia
serves the **original file** (with seek support) — it does **not** transcode for DLNA, so the TV
must natively support the file's format (modern LG/Samsung TVs handle most H.264/HEVC MP4/MKV).

Only the audio/video libraries (Movies, Shows, Music) are exposed; Books, Games and Photos are
not (a TV media player can't open them).

## Enabling it

1. **Settings → DLNA**:
   - Set **Enable DLNA** to on.
   - Optionally change **DLNA Server Name** (the name shown on your TV).
2. **Restart the backend.** Discovery (SSDP) reads this setting at startup, so a restart is
   required for the server to start announcing itself.
3. On your TV, open its media player / "Home Dashboard" / "Content Share" and look for the
   server name under network/DLNA devices.

## Security (read this)

DLNA has **no concept of a login** — a TV can't authenticate. So when you enable this:

- The DLNA surface is **unauthenticated**. Anyone able to reach it can browse and stream your
  whole audio/video library.
- It is therefore restricted to the **LAN only** — requests from non-local IP addresses are
  refused, and it is **never** meant to be exposed to the internet. Do **not** port-forward or
  reverse-proxy the `/dlna/*` paths or UDP 1900 to the outside.
- It is **opt-in** and **off by default**.
- Per-user library access controls and content ratings do **not** apply over DLNA (there is no
  user). Everything in the AV libraries is visible.

If those trade-offs aren't acceptable for your network, leave it off and use Chromecast (see
[casting.md](casting.md)) or a separate DLNA bridge instead.

## Testing it with your TV

SSDP discovery and on-device playback depend on your specific network and TV, so verify on the
real device:

1. Enable DLNA and **restart the backend** (above).
2. Make sure the server and the TV are on the **same subnet** (e.g. both `192.168.1.x`), not on
   a "guest" Wi-Fi that isolates clients.
3. Allow the server's firewall to accept **UDP port 1900** (SSDP) and the **HTTP port** (default
   `5011`) on the LAN.
4. On an **LG webOS** TV: open the **Home Dashboard** (or the **Media Player** app) → look under
   network/DLNA devices for your server name → browse Movies/Shows/Music → play a title.
5. Confirm playback and seeking work.

## Troubleshooting

**The TV doesn't see the server.**
- Did you **restart the backend** after enabling? (Required — SSDP starts at boot.)
- Same subnet? Guest/IoT VLANs and "AP isolation" block discovery.
- Firewall: UDP 1900 inbound and the HTTP port must be allowed on the LAN.
- Some networks block multicast; try the TV's "add network server / enter address manually"
  option if it has one, pointing at `http://<server-lan-ip>:5011/dlna/description.xml`.
- If another DLNA server (Plex, Windows Media Player, MiniDLNA) is already bound to UDP 1900,
  discovery may conflict — the backend log notes if it couldn't bind the SSDP port.

**The server shows up but a file won't play.**
- DLNA serves the original file without transcoding, so the TV must support that codec/container.
  An older TV may not play HEVC or some MKV audio tracks. (Casting via Chromecast *does*
  transcode; DLNA does not.)

## Current Limitations

- **No transcoding** over DLNA — direct-play only.
- The server identity (UDN) is regenerated each restart, so the TV may briefly show a stale
  entry until it times out.
- Discovery is single-server SSDP; multi-NIC hosts advertise the first detected LAN IPv4.

## Related

- [Chromecast Casting](casting.md) — the transcoding "throw to TV" path (needs a Cast device + HTTPS).
- Roadmap item **P4-004** in `docs/plans/roadmap/phase-4-deferred.md`.
