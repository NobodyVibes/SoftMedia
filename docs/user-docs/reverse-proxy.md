# Running SoftMedia Behind a Reverse Proxy

This guide explains how to operate SoftMedia behind a reverse proxy (Caddy, nginx, Tailscale Funnel, Cloudflare Tunnel, etc.) without breaking the per-IP rate limiter or other client-IP-aware features.

## Why this matters

SoftMedia uses the originating client IP for several security-sensitive purposes:

- **Rate-limiting** `/api/v1/auth/login` and `/api/v1/auth/signup` to defeat credential stuffing (see SDD §6.2).
- **Audit logging** of refresh-token issuance, rotation, and revocation events.
- **LAN-vs-WAN classification** for bandwidth-cap policy (forthcoming).

When SoftMedia is reached directly, ASP.NET Core resolves the client IP from the underlying TCP connection — accurate by definition. When a reverse proxy is in front, the TCP connection comes from the proxy itself, so without configuration the apparent "client IP" collapses to the proxy's loopback address.

The result of that collapse: every login attempt looks like it came from the same IP, and the rate limiter degrades to a single shared bucket. A credential-stuffing attack distributed across many real client IPs is no longer differentiable from a single legitimate user retrying their password.

## How SoftMedia handles it

The reverse proxy is responsible for adding standard `X-Forwarded-For` and `X-Forwarded-Proto` headers. SoftMedia's `ForwardedHeaders` middleware reads these headers — but **only when the connection originates from a trusted proxy**. That restriction is what prevents IP spoofing by arbitrary clients sending their own `X-Forwarded-For`.

By default, only loopback addresses (`127.0.0.1` and `::1`) are trusted. This covers the most common deployment — reverse proxy and SoftMedia on the same host — with no extra configuration.

If your proxy runs on a different host, inside a separate container, or on a VPN subnet, you must add the proxy's IP (or its network) to the trusted list in `appsettings.json`:

```json
"ForwardedHeaders": {
    "TrustedProxies": ["10.0.0.1"],
    "TrustedProxyNetworks": ["10.42.0.0/16"]
}
```

`TrustedProxies` is a list of individual IP addresses. `TrustedProxyNetworks` is a list of CIDR ranges — convenient when the proxy is part of a container network, a Kubernetes pod network, or a VPN subnet that may be assigned dynamically.

A server restart is required for changes to take effect.

## Security warning

**Do not** add wide network ranges (e.g. `0.0.0.0/0`) or untrusted hosts to either list. Any host you trust can forge `X-Forwarded-For` and arbitrarily impersonate any client IP. This defeats the rate limiter, poisons the audit log, and (once bandwidth caps ship) gives an attacker free choice of LAN-vs-WAN policy.

Safe rule of thumb: trust only the specific hosts you operate that are actually fronting SoftMedia.

## Sample configurations

### Caddy on the same host (most common)

```caddy
my-media.duckdns.org {
    reverse_proxy 127.0.0.1:8080
}
```

No SoftMedia configuration change needed — Caddy connects from loopback and is trusted by default.

### nginx on the same host

```nginx
server {
    listen 443 ssl;
    server_name my-media.duckdns.org;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Host $host;
    }
}
```

No SoftMedia configuration change needed.

### Caddy on a separate host or in Docker

```json
"ForwardedHeaders": {
    "TrustedProxies": ["192.168.1.100"]
}
```

Where `192.168.1.100` is the IP of the Caddy host as observed from SoftMedia.

### Tailscale Funnel

Tailscale Funnel terminates TLS on Tailscale's edge and forwards traffic to your local Tailscale node, which then connects to SoftMedia. From SoftMedia's perspective the connection arrives from a Tailscale CGNAT address (`100.x.y.z`).

```json
"ForwardedHeaders": {
    "TrustedProxyNetworks": ["100.64.0.0/10"]
}
```

`100.64.0.0/10` is the official Tailscale CGNAT range.

## Verifying the configuration

After restarting SoftMedia, attempt a few logins. Then inspect the server log. If you see real client IPs (or LAN IPs) in rate-limit and refresh-token audit lines, the configuration is correct. If every event shows your proxy's IP, the proxy is not in the trusted list.

A quick test from the command line:

```bash
# From a separate machine, send a deliberately-failing login.
curl -i -X POST https://my-media.duckdns.org/api/v1/auth/login \
    -H 'Content-Type: application/json' \
    -d '{"username":"nobody","password":"wrong"}'
```

Then check the server log for the resulting `Unauthorized` line. The IP shown should be the IP of the machine that ran `curl`, not the IP of the reverse proxy.

## Related

- SDD §6.1 — secure remote access overview (Tailscale, DuckDNS, direct).
- SDD §6.2 — application security including rate limiting.
- `src/SoftMedia.Server/Program.cs` — the trust posture is configured in the `ForwardedHeadersOptions` registration; the middleware is registered as the first item in the HTTP pipeline so all downstream components see the real client IP.
