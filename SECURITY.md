# Security Policy

SoftMedia takes security seriously (it is built to be exposed to the internet behind a reverse
proxy). Thank you for helping keep it and its users safe.

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report privately via one of:

- GitHub's **private vulnerability reporting** ("Report a vulnerability" under the repository's
  **Security** tab) — preferred.
- If that is unavailable, open a minimal issue asking for a private contact channel (do **not**
  include details), and a maintainer will follow up.

Please include: affected version/commit, a description, reproduction steps, and impact. We aim to
acknowledge within a few days and to coordinate a fix and disclosure timeline with you.

## Supported versions

SoftMedia is pre-1.0 and under active security hardening. Security fixes target the **default
branch (`main`)** and the latest tagged release. Older tags are not maintained.

## Scope notes

- SoftMedia invokes **jellyfin-ffmpeg** as an external process; vulnerabilities in ffmpeg itself
  should be reported upstream to the FFmpeg/Jellyfin projects.
- SoftMedia makes **no** telemetry/analytics calls; the only outbound traffic is to the configured,
  opt-in metadata/cover-art providers (see the README "Privacy & network egress" section).
