# Chromecast — Production-Grade Self-Hosted Support

**Status:** Proposed · **Created:** 2026-06-04 · **Owner:** unassigned
**Supersedes the open questions in:** `roadmap/phase-3-differentiation.md` → P3-WI-001 (the SPA-only sender shipped, then hit the HTTPS/trusted-cert wall during manual QA — see `roadmap/manual-qa-2026-05-30.md`).

## 1. Context

The Phase-3 Chromecast sender (P3-WI-001) is implemented client-side (`src/SoftMedia.Client/src/hooks/useCast.ts`, cast button in `VideoPlayer.tsx`). Manual QA surfaced that it cannot actually play on a TV, for two platform reasons that no amount of app code can bypass:

1. **The Cast Web Sender API only initialises in a secure context.** `localhost` is a browser exception, so the cast button appears there — but the cast URL is then `localhost`, which the TV can't reach. On a plain-HTTP LAN IP (`http://192.168.x.x`) the API never initialises, so the button is hidden entirely.
2. **The Chromecast fetches the media itself and only trusts publicly-recognised CAs.** Self-signed / private-CA certs are rejected by the device (users cannot install a CA on a Chromecast). You cannot get a publicly-trusted cert for a *private* LAN IP, and a NAT'd home server has no public IP to certify — so a domain is required in practice.

**Therefore the irreducible requirement is: a real domain name + a publicly-trusted TLS certificate, with the app served over HTTPS.** Plex automates this with a cloud cert/DNS service (`*.plex.direct`); Jellyfin makes the operator set up a reverse proxy + real cert themselves. SoftMedia is "100% self-hostable with no dev-hosted cloud," so it follows the **Jellyfin model but automates as much of it as possible**: the *operator* brings a domain (free options exist), and SoftMedia makes obtaining/using the certificate as close to one-click as we can.

### Already shipped this session (foundation, not re-listed as work)
- Cast now requests a **Chromecast-tuned stream plan** (H.264/AAC/1080p HLS, own session id) instead of reusing the desktop plan, which was targeting AV1@4320p — `CHROMECAST_CAPABILITIES` in `VideoPlayer.tsx`.
- **Loopback-origin guard**: clicking Cast on `localhost` now shows a clear in-player message instead of silently failing.
- **Hand-off**: local playback pauses when a cast starts.
- The backend already honours `X-Forwarded-Proto`/`-For` from trusted proxies (`Program.cs`), so reverse-proxy TLS termination is supported today.

## 2. Goals / Non-Goals

**Goals**
- An operator with a domain name can run SoftMedia over HTTPS with a publicly-trusted cert and cast reliably to any Chromecast generation, with no dev-operated infrastructure.
- Make the certificate step turnkey: a documented reverse-proxy path **and** an optional built-in ACME (Let's Encrypt) path that works for LAN-only servers (DNS-01).
- Casting survives full-length movies (no mid-stream auth expiry) and uses a format every Chromecast can decode.
- The UI tells the operator *why* casting is/ isn't available instead of silently hiding the button.

**Non-Goals**
- No dev-hosted cloud service (no `*.plex.direct` clone, no shared cert/DNS service).
- No self-signed / private-CA "trust the Chromecast" hack (impossible by design) and no reliance on the deprecated HTTP-media mixed-content loophole.
- No custom Cast **receiver** app in v1 — the zero-infrastructure Default Media Receiver (`CC1AD845`) is sufficient once streams are DMR-compatible. (A custom receiver is a documented future option; it needs a one-time Google Cast dev registration + a static receiver page, called out in §6.)

## 3. Architecture Decision

**Bring-your-own-domain + automated Let's Encrypt (DNS-01) + Default Media Receiver.**

- **TLS**: two supported paths — (A) reverse proxy (Caddy, recommended/least-code) and (B) embedded ACME in the server for operators who don't want a separate proxy. Both must use a **publicly-trusted** cert; DNS-01 is the blessed challenge because LAN-only servers have no public ingress for HTTP-01.
- **DNS**: the domain resolves to the server's LAN IP via split-horizon DNS (preferred) or a public A record to the private IP. Documented, operator-owned.
- **Receiver**: Default Media Receiver; SoftMedia transcodes to H.264/AAC/HLS for casts (done).
- **Auth for the device fetch**: a narrow, longer-lived **stream-scoped token** (the receiver can't refresh a JWT), built on the existing `ApiTokenService`/`ScopeAuthorization` infrastructure.

## 4. Work-Item Summary

| ID | Title | Phase | Effort | Depends on |
|----|-------|-------|--------|------------|
| CC-WI-001 | Reverse-proxy TLS path + casting deployment guide | A | 1-2 d | — |
| CC-WI-002 | Finalise Chromecast-tuned stream plan + bandwidth-cap parity | A | 1-2 d | — |
| CC-WI-003 | Stream-scoped, long-lived cast tokens | A | 2-3 d | — |
| CC-WI-004 | Embedded automatic HTTPS (ACME DNS-01) | B | 4-6 d | CC-WI-001 |
| CC-WI-005 | Cast-readiness diagnostics + UI hint | B | 1-2 d | CC-WI-001 |
| CC-WI-006 | End-to-end verification + user docs | C | 1-2 d | 001-005 |

**Sequencing:** Phase A makes casting *work correctly* for any operator willing to run a reverse proxy (the common self-hosting case). Phase B makes it *turnkey* (no proxy needed) and *discoverable*. Phase C is close-out. A→B→C; within Phase A the three items are independent.

## 5. Work Items

### CC-WI-001 — Reverse-proxy TLS path + casting deployment guide

#### Motivation
The fastest, most robust, least-code route to a trusted cert. Caddy ships automatic Let's Encrypt incl. DNS-01 with dozens of providers. The app is already proxy-ready (`ForwardedHeaders` configured); the gap is documentation and verifying every cast URL respects the forwarded scheme/host.

#### Specification
- New doc `docs/user-docs/features/casting.md` and an extension to `docs/user-docs/reverse-proxy.md` covering **TLS** (today it only covers client-IP forwarding):
  - A blessed `Caddyfile` for `media.example.com` → SoftMedia, with TLS via DNS-01 (Cloudflare example) so it works with no public port 80/443 ingress.
  - **Split-horizon DNS** setup (Pi-hole / router / `hosts`) so `media.example.com` resolves to the LAN IP; plus the public-A-record-to-private-IP alternative and its caveats (DNS-rebinding protection).
  - Free-domain options (DuckDNS, Cloudflare-managed cheap domains) so cost isn't a blocker.
  - A short "why casting needs this" explainer linking back to the secure-context + trusted-CA facts.
- Code: audit for any **server-generated absolute URLs** in the cast/stream path and ensure they derive from the forwarded host/scheme (the sender builds URLs from `window.location.origin`, which is already correct behind a proxy; verify the plan/manifest endpoints don't emit hard-coded `http://`/host).

#### Acceptance
- Following the guide end-to-end on a LAN-only host yields a working HTTPS origin with a Let's Encrypt cert and a reachable cast.
- No cast/stream URL downgrades to `http://` or the proxy's internal host when behind TLS.

#### Effort 1-2 d · #### Dependencies none · #### Risks DNS-01 provider variety — mitigate by documenting one first-class provider (Cloudflare) + linking Caddy's provider list.

---

### CC-WI-002 — Finalise Chromecast-tuned stream plan + bandwidth-cap parity

#### Motivation
The cast-tuned plan landed this session (H.264/AAC/1080p HLS). Two gaps remain: (a) capability headroom for 4K/HEVC-capable receivers (Ultra / Google TV) is left on the table by the hard 1080p cap, and (b) the original P3-WI-001 acceptance requires the P1-WI-003 bandwidth cap to apply to cast streams.

#### Specification
- Optionally read the **receiver's capabilities** from the Cast session (`session.getSessionObj()` / receiver capabilities, or device categorisation) to lift the cap to 4K H.264/HEVC where the device supports it; otherwise keep the safe 1080p/H.264 default.
- Confirm the cast transcode session is subject to the same bandwidth-cap policy as browser streams (no special-casing); add a regression test.
- Add a unit/integration test that requesting a plan with `CHROMECAST_CAPABILITIES` yields an H.264/AAC HLS URL for an HEVC/AV1/MKV source.

#### Acceptance
- Casting an MKV/HEVC/AV1 source plays on a 1st-3rd-gen Chromecast (transcoded to H.264 1080p).
- On a 4K-capable receiver, ≤4K H.264/HEVC is selected when available.
- Bandwidth cap observed on cast streams (test).

#### Effort 1-2 d · #### Dependencies none · #### Risks Receiver-capability reporting is uneven across devices — default to the safe profile when unknown.

---

### CC-WI-003 — Stream-scoped, long-lived cast tokens

#### Motivation
The cast URL currently carries the user's short-lived access JWT as `?token=`. The receiver cannot refresh it, so a long movie 401s mid-playback once it expires. Casting needs a token that outlives a movie but is tightly scoped.

#### Specification
- Mint a **playback-scoped token** bound to `(userId, mediaId)`, read-only, valid only for that media's stream/transcode endpoints, with a TTL covering a long movie (e.g. 8-12 h), revocable. Build on `ApiTokenService` + `ScopeAuthorization` (add a `scope:stream:media` style scope, or a dedicated short-lived cast-token type validated in `JwtBearerEvents.OnMessageReceived`).
- The `/plan` request for casting (or a dedicated `POST /api/.../cast-token`) returns the scoped token; the cast URL uses it instead of the session JWT.
- Server-side enforcement: the cast token authorises **only** the transcode/stream/subtitle/segment routes for its `mediaId`, nothing else.

#### Acceptance
- A 2 h+ movie casts to completion without re-auth.
- The cast token is rejected on any non-stream endpoint and for any other `mediaId` (tests).
- Revoking the session / changing password invalidates outstanding cast tokens (or they expire independently — documented).

#### Effort 2-3 d · #### Dependencies none · #### Risks Token scope leakage — cover with explicit negative authZ tests.

---

### CC-WI-004 — Embedded automatic HTTPS (ACME DNS-01)

#### Motivation
For operators who don't want to run a separate proxy, SoftMedia obtains and renews its own Let's Encrypt cert (DNS-01) and serves HTTPS directly via Kestrel — the most turnkey, single-binary path.

#### Specification
- Admin settings: enable toggle, domain, ACME contact email, DNS provider + API token (start with Cloudflare + a generic "manual TXT" mode), staging/prod toggle.
- Obtain + auto-renew the cert (~60-day cycle) via a background service; persist to `data/certs/` (git-ignored, 0600). Wire Kestrel to the managed cert.
- Implementation options to evaluate: `LettuceEncrypt` (HTTP-01-centric; would need DNS-01 extension), `Certes` (DNS-01 with provider calls in our code), or bundling the `lego` binary (broadest DNS-provider support, shell-out). **Recommend a spike comparing `Certes` vs bundled `lego`.**
- Hard-fail safe: if cert acquisition fails, keep serving HTTP and surface the error in the readiness panel (CC-WI-005) — never crash the server.

#### Acceptance
- With a domain + Cloudflare token, a fresh LAN-only install reaches `https://media.example.com` with a trusted cert and auto-renews, no reverse proxy.
- Renewal failure degrades gracefully with a clear admin warning.

#### Effort 4-6 d · #### Dependencies CC-WI-001 (shared concepts/docs) · #### Risks DNS-provider breadth and renewal reliability — mitigate by also keeping the proxy path (CC-WI-001) as the supported default; embedded ACME is the convenience option.

---

### CC-WI-005 — Cast-readiness diagnostics + UI hint

#### Motivation
This session's confusion ("why does the icon show on localhost but not the LAN IP?") is a UX failure: the button silently disappears. Tell the operator what's wrong.

#### Specification
- Admin "Casting readiness" panel that checks and reports: served over HTTPS? cert publicly-trusted (not self-signed)? hostname resolves to a LAN-reachable address? — each with a pass/fail and a one-line fix linking to `casting.md`.
- In the player, when the Cast API is unavailable due to an **insecure context** (`!window.isSecureContext` on a non-loopback host), show a small dismissible hint ("Casting needs HTTPS — see setup") instead of nothing. Surface a reason from `useCast` (e.g. `castUnavailableReason: 'insecure-context' | 'no-sdk' | null`).

#### Acceptance
- On plain-HTTP LAN, the player shows the HTTPS hint and the admin panel flags the missing-HTTPS check.
- On a correct HTTPS deployment, all checks pass and the hint is absent.

#### Effort 1-2 d · #### Dependencies CC-WI-001 · #### Risks none significant.

---

### CC-WI-006 — End-to-end verification + user docs

#### Motivation
Casting has many device/format permutations; lock in a manual matrix and finish user-facing docs.

#### Specification
- Manual test matrix: receivers (1st-3rd-gen 1080p, Ultra/Google TV 4K) × sources (H.264 MP4 direct, MKV transcode, HEVC, AV1) × TLS paths (Caddy, embedded ACME) × movie length (short, 2 h+).
- Finish `docs/user-docs/features/casting.md`: supported formats, the domain+cert requirement (with the "why"), setup (both TLS paths), and troubleshooting (the `[Cast]` console line, the readiness panel).
- Record results into `roadmap/manual-qa-*.md`.

#### Acceptance
- Matrix executed; all green or documented limitations.
- Docs reviewed and linked from the player's cast hint and admin readiness panel.

#### Effort 1-2 d · #### Dependencies CC-WI-001..005 · #### Risks none.

## 6. Future option — custom Cast receiver (not in scope)

If a branded receiver UI / richer subtitle+track control is wanted later: register a Google Cast receiver **app ID** (one-time $5 dev registration) and host a **static receiver HTML page** at a fixed HTTPS URL (free on GitHub Pages — a static asset, *not* a cloud backend; holds no user data). This is the only piece that adds a small standing project responsibility, hence deferred. The Default Media Receiver keeps v1 at zero dev infrastructure.

## 7. Task Checklist

**Phase A — make it work (reverse-proxy operators)**
- [x] CC-WI-001 Write `docs/user-docs/features/casting.md` (why HTTPS/certs, formats, long-lived token, troubleshooting, deferred-4K)
- [x] CC-WI-001 Extend `reverse-proxy.md` with a "TLS termination for Chromecast casting" section: Caddy + Let's Encrypt DNS-01 (Cloudflare) + split-horizon DNS + the public-A-record alternative. Also fixed an inaccuracy (LAN/WAN bitrate caps were marked "forthcoming" but are implemented).
- [x] CC-WI-001 Audit server for hard-coded host/scheme — **clean** (investigation workflow): every stream/plan/manifest/image URL is relative, `UseForwardedHeaders` is correctly positioned, cookies use `Request.IsHttps`. No code change needed.
- [~] CC-WI-002 Lift 1080p cap to 4K/HEVC — **DEFERRED by design** (investigation workflow): the Web Sender SDK cannot reliably detect a device's 4K/HEVC/AV1 support (`VIDEO_OUT` is identical on 1080p and 4K devices; authoritative probes are receiver-SDK-only). Lifting on a weak signal would black-screen 1080p-only devices. Requires a custom receiver app (future). Decision documented in `casting.md` + §CC-WI-002 below.
- [x] CC-WI-002 Test: bandwidth cap applies to cast streams — `StreamPlanServiceCastTests.CastCaps_ZeroClientBitrate_StillEnforcesNetworkCap` (verified the `MaxBitrate=0` cast case still clamps to the network cap).
- [x] CC-WI-002 Test: `CHROMECAST_CAPABILITIES` plan → H.264/AAC HLS for HEVC/AV1/VP9/4K source — `StreamPlanServiceCastTests` (Theory over hevc/av1/vp9, 4K → H.264/AAC/HLS/1080p). All green (15/15 with the existing bitrate suite).
- [x] CC-WI-003 Add media-scoped, long-TTL cast token — implemented as a dedicated JWT (`TokenService.GenerateCastToken`, `CastTokenClaims`), enforced in `JwtBearerEvents.OnTokenValidated` (rejects the token on any path that isn't `/api/transcode/{mediaId}` or `/api/v1/stream/{mediaId}`). TTL via `JwtSettings:CastTokenExpiryHours` (default 12).
- [x] CC-WI-003 Return cast token from the cast plan path — `POST /api/transcode/{id}/plan?cast=true` embeds it; client cast button requests `?cast=true`.
- [x] CC-WI-003 Negative authZ tests (wrong mediaId, non-stream route incl. admin endpoint) + scope-claim/expiry test — `CastTokenIntegrationTests` (4 tests, green).

**Phase B — make it turnkey + discoverable**
- [~] CC-WI-004 Embedded ACME (DNS-01) — **DEFERRED (see §9).** Large, credential-dependent, cannot be end-to-end verified in-repo (no live ACME/DNS), and does not unblock anything the documented Caddy path doesn't already cover. Build as a dedicated, separately-validated effort when wanted.
- [x] CC-WI-005 `useCast` exposes `castState` / `isSecureContext` / `castUnavailableReason`; player shows a readiness affordance (replaces the silently-missing button) instead of an insecure-context-only hint.
- [x] CC-WI-005 Cast-readiness diagnostics popover (`CastDiagnostics` + pure `describeCastReadiness`, unit-tested): checks reachable-HTTPS, browser Cast support, **and whether any Google Cast device is on the LAN** — the last being the most valuable check (a plain LG/Samsung smart TV is not a Cast receiver). Built at the player (point of use) rather than an admin panel, since casting is a playback action.

## 9. CC-WI-004 deferral (2026-06-05)

Embedded ACME is deliberately **not** shipped now, for three reasons:
1. **Unverifiable here.** Cert issuance needs a live Let's Encrypt + DNS-provider round-trip with real credentials; shipping unverified cert-acquisition/Kestrel-rebind code that runs at server startup is a real risk (my own §CC-WI-004 spec flagged "hard-fail safe… never crash" and a spike).
2. **Doesn't unblock the use case.** It only removes the separate reverse proxy; the documented **Caddy + Let's Encrypt DNS-01 + split-horizon DNS** path (CC-WI-001) already gives a working, publicly-trusted HTTPS origin for casting today.
3. **Not the gating factor for testing.** The common blocker is device compatibility (most smart TVs aren't Cast receivers), which CC-WI-005 now surfaces.

When built, do the `Certes`-vs-`lego` spike first, keep the reverse-proxy path as the supported default, and gate behind an explicit admin opt-in with graceful HTTP fallback on failure.

**Phase C — close out**
- [ ] CC-WI-006 Execute device × format × TLS × length matrix
- [ ] CC-WI-006 Finalise docs; link from player hint + readiness panel; log results in manual-qa

## 8. Adversarial-review hardening (2026-06-04)

A 4-dimension review workflow (each finding independently verified, default-refute) over CC-WI-003 + Phase A surfaced 13 confirmed issues; all were fixed:

**Cast-token security (CC-WI-003):**
- [x] Drop the `role` claim from cast tokens — a cast token now carries only `sub` + `MaxRating` + its scope claims, so it can never act as an admin even if the path-scope check regressed (defense in depth).
- [x] Per-request user-state recheck in `OnTokenValidated` for cast tokens (ban / soft-delete / un-approve now takes effect within the token's life, mirroring the ApiToken scheme — a stateless JWT is otherwise unrevocable).
- [x] Block cast-token self-renewal: a cast token calling `POST /plan?cast=true` (in its own scope) is rejected `403` early, so it can't mint fresh tokens indefinitely.

**Tests:** expired-token → 401; banned-user → 401 per-request; self-renewal → 403; `AcceptedOnItsOwnStreamRoute` strengthened to anon-401 control + exact 404; TTL test now proves config is read (factory sets 9h); cast-plan DirectPlay positive-path + capability-contrast (cast vs fully-capable client) + anchored URL regexes.

**Client cast UX:** tear down a just-started session if `loadMedia` fails (no stranded idle receiver); added an `error` toast variant (red + `role="alert"`) so cast failures aren't shown as calm blue info; double-click in-flight guard on the cast button; corrected the loopback guard (dropped dead `[::1]`, broadened to `127.0.0.0/8` + `0.0.0.0`).

**Docs:** narrowed the now-outdated "no public cert for a bare IP" claim to *private* LAN IP (Let's Encrypt issues public-IP certs, but a NAT'd home server still needs a domain); softened the `*.plex.direct` "public A record" analogy.
