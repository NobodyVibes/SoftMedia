# 05 · Fix the axios 401 interceptor

**Severity:** P0 · **Layer:** Frontend · **Est. size:** S (< 1 day)
**Depends on:** [04](04-refresh-token-persistence.md) — refresh-token endpoint must actually work.

## Problem

`src/SoftMedia.Client/src/services/api.ts` lines 27–60 contain the axios response interceptor (the block opens near line 27 and the closing brace is at line 60 — doc originally cited `:27-56` which truncated the tail). It treats **every** 401 as a signal to try refresh, and if refresh fails (which it always does today — see todo [04](04-refresh-token-persistence.md)) it calls `useAuthStore.getState().logout()` which nukes the session.

Concrete failure modes today:

1. User opens the app, their session is valid, but they click a media item they are not permitted to view. Backend returns 401 or 403. Interceptor panics, tries refresh, refresh fails, user is logged out for no reason.
2. At the 24-hour access-token boundary, every active user is silently logged out with no retry, even though a valid refresh cookie is sitting in their browser.
3. The interceptor sets `originalRequest._retry = true` before calling `/refresh-token`, so even in a world where refresh succeeds, the original request is not replayed — the user sees a failure anyway.

Once todo [04](04-refresh-token-persistence.md) is in place, (2) and (3) become fixable. (1) requires tightening the interceptor's scope regardless.

## Target state

The interceptor follows this flow:

```
response.status === 401
 ├── If request URL is /auth/refresh-token itself → do not retry, forward error.
 ├── If request already has _retry flag → do not loop, forward error.
 ├── Else:
 │     1. POST /auth/refresh-token.
 │     2. If 200: update access token in auth store, set _retry flag, replay original request, return the replayed response.
 │     3. If 4xx: logout + forward error.
response.status === 403
 └── Do not attempt refresh. Forward to caller. Caller decides whether to show a "forbidden" toast.
```

Key behaviours:

- **403 never triggers logout.** Forbidden ≠ unauthenticated.
- **Refresh failure on an auth-adjacent 401 is treated as real logout.** Refresh failure on a random 401 should log a warning and surface the error to the caller, but only force a full logout if the failure was "refresh token itself expired/revoked" (server signals this explicitly — see todo [04](04-refresh-token-persistence.md)).
- **Concurrent 401s do not trigger a refresh storm.** Queue pending requests behind a single in-flight refresh promise; replay them when it resolves.
- **The original failing request is replayed** after a successful refresh, so the caller sees success instead of "you were refreshed but your request still failed."

## Scope

**In scope:**
- Rewriting `src/services/api.ts` response interceptor.
- Updating `src/store/authStore.ts` if needed to expose `setAccessToken` for replay flow.
- Vitest unit tests for the interceptor.

**Out of scope:**
- UI changes to show "session expiring soon" warnings.
- Per-request retry policies beyond the 401 replay.
- Refactoring the auth store beyond what the interceptor needs.

## Implementation steps

1. Add a single-flight refresh guard — a module-level `let refreshInFlight: Promise<string | null> | null` that other 401s await.
2. When a 401 arrives on a non-`/auth/refresh-token` URL:
   - If `refreshInFlight` is null, set it to a promise that POSTs `/auth/refresh-token`.
   - Await it.
   - If it resolves with a new access token → update the auth store, flag `originalRequest._retry = true`, and return `axios(originalRequest)` to replay.
   - If it rejects → forward the original 401 and, only if the rejection was "refresh token invalid" (distinguishable by status or body), also call `authStore.logout()`.
3. On 403 → pass through unchanged. Let calling code handle "forbidden" UI.
4. Clean up: after `refreshInFlight` settles, reset it to `null`.
5. Unit-test every branch with `msw` or `axios-mock-adapter`.

## Files to touch

- `src/SoftMedia.Client/src/services/api.ts`
- `src/SoftMedia.Client/src/store/authStore.ts` (if token setter needs exposing)
- `src/SoftMedia.Client/src/services/api.test.ts` (new)

## Tests required

Using `axios-mock-adapter` or `msw`:

- `Non401Response_PassesThrough`
- `403Response_DoesNotTriggerRefresh_AndDoesNotLogout`
- `401OnRefreshEndpoint_DoesNotLoop`
- `401OnDataEndpoint_WithSuccessfulRefresh_ReplaysOriginalRequest_AndReturnsData`
- `401OnDataEndpoint_WithFailedRefresh_ForwardsError_AndLogsOutOnlyWhenRefreshTokenInvalid`
- `MultipleConcurrent401s_TriggerSingleRefresh_AndAllReplayAfter`
- `AuthStore_AccessTokenUpdated_AfterSuccessfulRefresh`

## Acceptance criteria

- [ ] 403s no longer trigger logout.
- [ ] Successful refresh transparently replays the original request; the caller sees the expected data instead of a 401.
- [ ] Multiple concurrent 401s during a refresh produce exactly one call to `/auth/refresh-token`.
- [ ] Infinite loop is impossible (guard against `_retry`-flag-missing and refresh-endpoint-self-call).
- [ ] All new Vitest tests pass.
- [ ] Manual smoke: open the app, wait until the 15-minute access token expires, click any link. The request should succeed transparently; the user stays logged in. Leave it overnight — still logged in when the refresh rotates at the 7-day boundary; logged out only after that.

## Risk / rollback

Low on the frontend side. The interceptor is one file; rollback is a revert. Bigger risk is shipping this before todo [04](04-refresh-token-persistence.md) — without a working refresh endpoint, this interceptor's "successful refresh" branch will never execute and the improvement is limited to "no more logout on 403," which is still valuable but not the full fix.

## Ordering

Ship **after** todo [04](04-refresh-token-persistence.md) merges. If shipping separately is needed (e.g., to deliver partial value), the 403-no-logout change is safe to ship first on its own.
