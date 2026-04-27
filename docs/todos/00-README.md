# SoftMedia TODO Tracker

**Purpose:** Each document in this folder describes a discrete unit of work identified by the 2026-04-23 progress audit + peer review. Pick one up, complete it, and mark the checkbox in the acceptance criteria. Each file is self-contained: problem statement → target state → scope → implementation steps → files to touch → acceptance criteria → tests → rollback notes.

**Rule:** Do not expand a todo's scope mid-implementation. If you find adjacent work, open a new todo in this folder rather than bundling it in.

---

## Severity legend

| Tag | Meaning |
|---|---|
| **P0** | Ship-blocker. Product is not safe to expose beyond localhost until fixed. |
| **P1** | Important. Should land before the first tagged release. |
| **P2** | Polish / hygiene. Can wait. |

---

## Work items

| # | Title | Severity | Layer | Est. size |
|---|---|---|---|---|
| [01](01-controller-authorization.md) | Add `[Authorize]` to `AudioController` and `ImageController`; remove or gate `DumpBooks` | **P0** | Backend | S |
| [02](02-jwt-signing-secret.md) | Rotate JWT signing secret off committed placeholder; refuse-to-start guard | **P0** | Backend | S |
| [03](03-auth-rate-limiting.md) | Wire up the rate limiter; apply strict per-endpoint policy on `/auth/login` and `/auth/signup` | **P0** | Backend | S |
| [04](04-refresh-token-persistence.md) | Implement real refresh-token persistence + rotation; shorten access-token lifetime | **P0** | Backend | L |
| [05](05-frontend-401-interceptor.md) | Fix axios 401 interceptor so forbidden responses do not destroy the session | **P0** | Frontend | S |
| [06](06-universal-client-a11y.md) | Fix `<div onClick>` violations and missing `focus-visible` pairs; add CI guard | **P1** | Frontend | M |
| [07](07-audio-cover-path-jailing.md) | Port `AudioController.GetCoverArt` onto the `StreamSecurityService` pattern | **P1** | Backend | S |
| [08](08-library-path-canonicalization.md) | Canonicalize library paths at creation time; detect aliased duplicates | **P1** | Backend | S |
| [09](09-security-regression-tests.md) | Add controller tests for file-serving endpoints + `StreamSecurityService` | **P1** | Tests | M |
| [10](10-hardening-and-closure-tracker.md) | Phased tracker for the 2026-04-26 audit findings (Phases 1–6, ~25 tasks) | **P0–P1** | Mixed | XL |

Size key: **S** = < 1 day, **M** = 1–3 days, **L** = 3–5 days, **XL** = multi-PR effort split across phases.

---

## Dependency graph

```
02 ─► 01, 03, 04   (02 MUST ship first: rotating the JWT secret is load-bearing
                    for every other auth change. Shrinking access-token
                    lifetime in 04 or rate-limiting in 03 is meaningless if
                    anyone with the public repo can forge tokens.)

01 + 03 ─► can then ship alongside 02 as one "security hardening" PR

04 ─► 05            (05 cannot be finished correctly until 04 issues real tokens)

06 ─► independent, but touches CI config — coordinate with 09
07 ─► independent
08 ─► independent
09 ─► should follow 01 and 07 so the new tests exercise the hardened code;
      shares CI config with 06 — land one, then rebase the other
```

---

## Suggested PR bundling

1. **PR 1 — Security Hardening Bundle** → todos 01, 02, 03 in that order (02's secret-rotation work is prerequisite scaffolding even within the same PR). All touch `AuthController.cs` / `Program.cs` / `Extensions/ServiceCollectionExtensions.cs` / `appsettings.json` area and are verified together with one integration test that hammers `/auth/login`.
2. **PR 2 — Refresh Token Persistence** → todo 04 alone. Has a migration and a new service; deserves its own review.
3. **PR 3 — Frontend Session Correctness** → todo 05. Ships after PR 2 so the interceptor has a real endpoint to talk to.
4. **PR 4 — Accessibility Pass** → todo 06. Mechanical and visually reviewable; can be done by a different engineer in parallel with the above.
5. **PR 5 — Defense-in-Depth** → todos 07 + 08 together. Both are path-safety follow-ups.
6. **PR 6 — Security Regression Test Suite** → todo 09. Lands last so tests exercise final hardened behavior.

---

## Out-of-scope (deliberately deferred)

The audit surfaced several things that are real but not on this list:

- **CSRF double-submit cookie** promised in SDD §6.2 — unneeded given Bearer-token auth and SameSite=Strict refresh cookie. Amend the SDD rather than implementing.
- **WebOS / spatial navigation** promised in SDD §8 — defer. Either amend SDD to "desktop-first, TV later" or open a separate epic.
- **`src/features/` directory** promised in SDD §3.2 — cosmetic. Rename in a single mechanical PR when nothing else is in flight, or drop the requirement.
- **Docs tidying** — move `docs/ereader-plan-*.md` under `docs/plans/`. 15-minute chore, not worth a todo.
- **`PasswordHasher.VerifyPassword` ignores stored m/t/p params** — real latent bug but zero impact until Argon2 params are ever changed. Add a note when someone bumps the parameters.
- **`MetadataRouter` silent fallback on unknown provider name** — low-impact misconfiguration risk. Fix as a drive-by when next touching the file.
- **DNS rebinding on `ImageController` allowlist** — theoretical given the current strict allowlist.
- **Transcode `sid` path-separator validation** — low severity, gated by JWT. Fix as a drive-by.

---

## Definition of "done" (applies to every todo)

Before marking a todo complete, all of the following must be true:

- [ ] Every acceptance-criteria checkbox in the todo is ticked.
- [ ] Tests listed in the todo exist and pass in CI.
- [ ] No new `<div onClick>` without `role="button"` + `tabIndex` + keyboard handlers (enforced by todo 06's CI check).
- [ ] No new endpoint without `[Authorize]` unless explicitly marked `[AllowAnonymous]` with justification.
- [ ] No new `Path.Combine` on user-influenced input without canonicalization through `StreamSecurityService` (or equivalent).
- [ ] `dotnet test` passes on the server side; `npm test` passes on the client side.
- [ ] A short entry is added to the PR description mapping "which todo this completes" so the tracker stays accurate.
