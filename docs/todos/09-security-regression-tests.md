# 09 · Security regression test suite

**Severity:** P1 · **Layer:** Tests · **Est. size:** M (1–3 days)
**Depends on:** Todos 01 and 07 should land first so the tests exercise the hardened code.

## Problem

The backend has **35 test files** covering services, metadata providers, and helpers, but has **zero controller tests** for the four file-serving endpoints, and **no dedicated tests** for the path-jail check that everything else relies on. The audit found:

- No tests for `StreamController`.
- No tests for `AudioStreamController`.
- No tests for `ImageController` (proxy + caching).
- No tests for `StreamSecurityService.IsPathAuthorized` — mocked in one book-controller test but never exercised directly.
- `BookController.GetPage` **is** tested (`BookControllerTests.cs`) — covered.

For a self-hosted product whose main security claim is "path-jailed file access," the absence of regression tests on the jail itself is the single most dangerous gap.

## Target state

- Dedicated unit tests for `StreamSecurityService.IsPathAuthorized` covering every interesting input shape.
- Integration tests (via `WebApplicationFactory<Program>`) for the three untested file-serving controllers, covering: authentication gate, path-traversal payloads, range-request semantics, and content-type correctness.
- A reusable `AuthenticatedTestClient` helper so future controller tests don't each re-invent token issuance.
- CI runs the full suite on every PR.

## Scope

**In scope:**
- New test files under `src/SoftMedia.Server.Tests/`.
- A test-only helper for issuing valid JWTs against the test `WebApplicationFactory`.
- Fixture setup: temp directories, minimal DB seeding, sample media files (tiny synthetic payloads — do not commit real media).

**Out of scope:**
- Frontend tests (covered by todo [06](06-universal-client-a11y.md) for a11y; general frontend coverage is a separate epic).
- Performance / load tests.
- Fuzz testing (worth doing later, but out of scope here).
- Rewriting existing provider tests.

## Test plan

### `StreamSecurityServiceTests.cs` (new, unit)

Tests target: `src/SoftMedia.Server/Services/Security/StreamSecurityService.cs:15-51`.

- `IsPathAuthorized_PathInsideLibrary_ReturnsTrue`
- `IsPathAuthorized_PathOutsideLibrary_ReturnsFalse`
- `IsPathAuthorized_PathWithTraversalDots_Canonicalises_ThenDenies` — `"C:\Media\Movies\..\..\Windows\win.ini"`.
- `IsPathAuthorized_LibraryPathWithoutTrailingSeparator_DoesNotMatchSiblingDirectory` — **critical regression test**. `C:\Media\Movies` library should not authorise `C:\Media\Movies-secret\file.mkv`. Verifies the `DirectorySeparatorChar` append logic on line 32–35.
- `IsPathAuthorized_CaseInsensitiveMatch_ReturnsTrue` (Windows) / `CaseSensitiveMatch_ReturnsFalse` (Linux, conditional on `RuntimeInformation.IsOSPlatform`).
- `IsPathAuthorized_EmptyLibraryPaths_ReturnsFalse`
- `IsPathAuthorized_NullFilePath_ReturnsFalse`
- `IsPathAuthorized_MalformedFilePath_ReturnsFalseAndLogs` — uses an invalid-char path to exercise the `catch` at line 46.
- `ValidateMediaAccess_NullItem_ReturnsFileNotFound`
- `ValidateMediaAccess_ItemWithoutLibrary_ReturnsFileNotFound`
- `ValidateMediaAccess_FileMissingOnDisk_ReturnsFileNotFound`
- `ValidateMediaAccess_FilePresent_ButOutsideLibraryPaths_ReturnsUnauthorized`
- `ValidateMediaAccess_AllGood_ReturnsAllowed`

### `StreamControllerTests.cs` (new, integration)

- `Stream_WithoutToken_Returns401`
- `Stream_WithValidToken_UnknownId_Returns404`
- `Stream_WithValidToken_ItemPathOutsideLibrary_Returns404` (path-traversal defence)
- `Stream_RangeRequestFirstBytes_Returns206_WithContentRange`
- `Stream_FullRequest_Returns200_WithContentLength`

### `AudioStreamControllerTests.cs` (new, integration)

- Mirror the same pattern: auth gate, out-of-library path, range requests, unsupported format.

### `ImageControllerTests.cs` (new, integration)

- `Proxy_WithoutToken_Returns401` (relies on todo [01](01-controller-authorization.md))
- `Proxy_AllowedHost_Returns200_AndCachesResponse`
- `Proxy_DisallowedHost_Returns400`
- `Proxy_OversizedResponse_Returns400OrAborts`
- `Proxy_NonImageContentType_Returns400`
- `Proxy_Follows_OrDoesNotFollow_Redirects_AsDocumented` — document the behaviour either way; test pins it.

### `AuthenticatedTestClient.cs` (new, helper)

A single class exposing:

```
HttpClient CreateClientAsUser(string username = "tester", string role = "User");
HttpClient CreateClientAsAdmin();
HttpClient CreateClientUnauthenticated();
```

Internally seeds a user in the test DB, issues a JWT via the same `IJwtService` used in prod, and attaches the `Authorization: Bearer` header to a `WebApplicationFactory.CreateClient()` instance.

## Implementation steps

1. Build `AuthenticatedTestClient` first — every controller test depends on it.
2. Write `StreamSecurityServiceTests` — no web host required, pure unit tests. Knock them out fast; they're the highest ROI.
3. Write `StreamControllerTests` using `WebApplicationFactory`. Seed a minimal `MediaItem` + `Library` in a temp SQLite DB. Place a small synthetic file on disk under the test's temp directory.
4. Repeat the pattern for `AudioStreamControllerTests` and `ImageControllerTests`.
5. For `ImageController`, stub outbound HTTP calls with `HttpMessageHandler` mocks — do not hit real TVMaze/Wikimedia in tests.
6. Wire a CI job (or extend existing) to run `dotnet test` on every PR.

## Files to touch

- `src/SoftMedia.Server.Tests/Helpers/AuthenticatedTestClient.cs` (new)
- `src/SoftMedia.Server.Tests/Services/Security/StreamSecurityServiceTests.cs` (new)
- `src/SoftMedia.Server.Tests/Controllers/StreamControllerTests.cs` (new)
- `src/SoftMedia.Server.Tests/Controllers/AudioStreamControllerTests.cs` (new — this is the streaming controller, distinct from `AudioController` which is tested via the file created by todo [01](01-controller-authorization.md))
- `src/SoftMedia.Server.Tests/Controllers/ImageControllerTests.cs` — **extends** the file created by todo [01](01-controller-authorization.md); add path-traversal / range / content-type cases here rather than duplicating the auth-gate test
- `src/SoftMedia.Server.Tests/Controllers/AudioControllerTests.cs` — **extends** the file created by todo [01](01-controller-authorization.md); add the traversal test promised in todo [07](07-audio-cover-path-jailing.md) if not already landed
- `src/SoftMedia.Server.Tests/Fixtures/*.cs` (new, if shared setup grows)
- `.github/workflows/ci.yml` (or equivalent) — confirm `dotnet test` is already wired; add if not. Coordinate with todo [06](06-universal-client-a11y.md) which also touches CI config; whichever PR merges second should rebase.

## Test data and fixtures

- **Do not commit real media files.** Generate synthetic payloads in `Setup()`: a tiny 1 KB file written to a per-test temp directory, deleted in `Dispose()`.
- **Use in-memory SQLite** for the test DB (already a pattern in existing tests — reuse the setup).
- **Isolate each test's file system** using `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())`.

## Acceptance criteria

- [ ] `StreamSecurityServiceTests` exists and covers all 13 cases listed, including the critical `without-trailing-separator` regression.
- [ ] `StreamControllerTests`, `AudioStreamControllerTests`, and `ImageControllerTests` exist with auth-gate + traversal + range tests.
- [ ] `AuthenticatedTestClient` helper exists and is used by at least the four new test files.
- [ ] All tests pass locally (`dotnet test`) and in CI.
- [ ] Coverage on `StreamSecurityService` and the three controllers is ≥80% line coverage (if coverage reporting is enabled — otherwise assert "every branch exercised" by reviewing the test list against the code).
- [ ] A PR template note (or the repo CONTRIBUTING) mentions: "Any new file-serving endpoint requires a path-traversal test."

## Risk / rollback

None — adding tests cannot break production. The only risk is flaky tests; mitigate by:

- No network calls in unit tests (HTTP mocks for `ImageController`).
- Deterministic temp paths (fresh per test; cleaned in `Dispose`).
- No reliance on wall-clock timing; use fake `TimeProvider` if rate-limit tests are added later.

## Follow-up (not in this todo)

- Frontend test coverage is currently 11 files and mostly reader/player — a separate epic should add auth-flow tests, admin-workflow tests, and per-page smoke tests.
- Fuzz the path-jail check with something like FsCheck — powerful but out of scope here.
- Consider adding a `testcontainers`-based integration test tier that runs against a real SQLite file and FFmpeg binary for transcoding paths.
