# Contributing to SoftMedia

Thanks for your interest in SoftMedia — a free, open-source, self-hostable, privacy-first media server.

## License & how contributions are licensed

SoftMedia is licensed under **AGPL-3.0-or-later** (see [`LICENSE`](LICENSE)). The project will
**remain AGPL for everyone, in perpetuity.**

Contributions are accepted under a **Contributor License Agreement (CLA)** (see [`CLA.md`](CLA.md)).
In plain terms:

- **You keep the copyright to your contribution.** The CLA is a *license grant*, not an assignment.
- You grant the project maintainer a copyright + patent license that **also** allows the maintainer
  to license your contribution under other terms.
- That additional right exists for **one reason**: to let the project offer an *optional, separate*
  proprietary component in the future (e.g. an ethical paid media store) **without** changing the
  fact that the core app stays AGPL and freely forkable for everyone.
- The relicensing right is **asymmetric** (maintainer-only). Contributors do not gain a right to
  relicense others' work.

We think being upfront about this matters. If you're not comfortable with the CLA, that's OK —
open an issue to discuss, or contribute in ways that don't require it (docs, testing, bug reports).

When you open a pull request, the CLA-assistant bot will ask you to sign once.

## Project conventions (please follow these)

- **Back-to-front:** implement and test backend logic (endpoints, schema, services) before the UI
  that consumes it. Verify with `curl`/unit tests first.
- **Layering:** Controllers → Services → Repositories → Database. Use built-in DI; avoid static
  global state; one clear responsibility per class.
- **Tests:** xUnit + Moq (backend, under `src/SoftMedia.Server.Tests` — the single live
  suite); Vitest + React Testing Library (client). Cover new logic and security paths.
- **Accessibility / TV-readiness (all new UI):** use `<button>`/`<input>` (not `<div onClick>` without
  `role`/`tabIndex`); pair hover with focus (`hover:bg-white/10 focus-visible:bg-white/10
  focus-visible:ring-2`); everything Tab-reachable.
- **Tailwind v4:** the brand palette lives in `@theme` (since 2026-07-18) — use the semantic
  classes (`bg-primary`, `text-primary`, `bg-background`), not raw hex like `bg-[#007AFF]`.
- **Docs:** XML `///` comments on new public APIs; keep `.docs/project_checklist.md` current.
- **Security:** sanitize inputs; parameterized queries; jail file access (`StreamSecurityService`);
  RBAC + parental `MaxRating`. See [`SECURITY.md`](SECURITY.md) to report vulnerabilities.

## Getting set up

Run `./setup.ps1` (Windows) — it checks .NET 8 SDK + Node 18+, fetches **jellyfin-ffmpeg**
(required for transcoding and intro/credits detection — generic ffmpeg lacks the `chromaprint`
muxer), and restores/builds both projects. See [`README.md`](README.md) for manual steps.
