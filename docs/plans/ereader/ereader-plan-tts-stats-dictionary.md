# SoftMedia eReader — TTS, Reading Stats, Offline Dictionary

**Status:** Complete — 2026-04-19
**Scope reference:** [ereader-roadmap.md](ereader-roadmap.md), tasks ER-050, ER-052, ER-051
**Predecessors:** Milestones 1–5 (all shipped)

## 1. Scope & rationale

Milestone 6 closes the remaining Phase 5 items — the three Phase 5 tasks
that weren't already absorbed into M5's polish pass. These are all
"power feature" additions with modest user impact individually, but together
they round out the eReader's feature set and retire the roadmap.

**In scope:**

| ID | Title | Phase | Priority | Effort | Surface |
|---|---|---|---|---|---|
| ER-050 | Text-to-speech for EPUB | 5 | P3 | M | Frontend |
| ER-052 | Reading stats and session tracking | 5 | P3 | M | Full-stack |
| ER-051 | Offline dictionary lookup | 5 | P3 | L | Full-stack |

After this milestone, every task on the roadmap is Done.

## 2. Ordering

1. **ER-050** — frontend-only, depends on nothing else.
2. **ER-052** — full-stack; introduces the `ReadingSession` entity + summary endpoint before the stats widget that consumes it.
3. **ER-051** — full-stack; bundles the dictionary-source-file contract + endpoint + selection-popover UI.

## 3. Workstreams — acceptance summary

- **ER-050:** Uses the browser `speechSynthesis` API — no network, no external service. "Listen" header button for EPUB reads the current page's plain text; speech auto-advances to the next page on utterance end. Play / pause / stop controls. Voice + rate selectable in the Settings panel, persisted via the existing `readerStore` (`ttsVoice` field already reserved; `ttsRate` added). Disabled for PDF and CBZ (no OCR in this milestone).
- **ER-052:** New `ReadingSession` entity keyed on `(Id, UserId, MediaItemId, StartedAt, EndedAt, PagesRead)`. Endpoints: `POST /api/v1/interaction/{mediaId}/sessions/start` → returns new session id, `POST /api/v1/interaction/{mediaId}/sessions/{sid}/end` → finalises with `PagesRead`, `GET /api/v1/interaction/{mediaId}/sessions/summary` → totals for this book. Client instruments session start on reader mount, end on unmount *or* 5-minute idle timeout (so leaving the tab open overnight doesn't show a 10-hour session). A compact stats row in the Settings panel shows total read time + pages/min for the current book.
- **ER-051:** New endpoint `GET /api/v1/dictionary/{word}` returns `{ word, entries: [{partOfSpeech?, definition}] }`. Data source is a JSON file at a conventional path (`data/dictionary.json`) — when the file is missing the endpoint returns `501 Not Implemented` with a clear message ("Dictionary dataset not installed"). Frontend: text selection in EPUB (or PDF once ER-003 re-enabled the text layer) surfaces a "Define" button in the existing highlight-toolbar area; clicking opens a popover with the result or an explanatory empty state.

## 4. Risks & mitigations

| Risk | Mitigation |
|---|---|
| speechSynthesis voice list loads asynchronously — first render often shows zero voices | Listen for `voiceschanged` and re-populate; fall back to the default voice when `ttsVoice` can't be resolved. |
| speechSynthesis APIs vary across browsers (especially Safari's pause quirks) | Keep MVP to play/stop only — omit fine-grained pause/resume if flaky. Document. |
| Idle-timeout sessions accumulate to "0 pages read in 30 minutes" on a user who walked away | Don't persist a session with zero activity; finalise but discard on end if `PagesRead === 0 && duration > idle timeout`. |
| Dictionary JSON file in a misbehaving format blocks server startup | Lazy-load on first query; cache the parsed map in-process; failure to parse → 501 with a warning log, not a crash. |
| Large dictionary files (WordNet compressed JSON can be 30–80 MB) | Stream-parse once into an in-memory dictionary; no per-request disk I/O. Memory cost is documented — users who skip the dataset pay zero. |

## 5. Acceptance checklist

- [x] **ER-050** Listen button toggles TTS for EPUB; voice + rate persist; auto-advance across pages via `rendition.next()` on utterance end; disabled for PDF/CBZ.
- [x] **ER-052** Session start + end endpoints + summary endpoint; client instruments mount/unmount + 5-minute idle timeout; stats row visible in Settings when session count > 0.
- [x] **ER-051** Dictionary endpoint returns 501 when dataset missing with a typed `{ available: false }` body; returns definitions when present; selection → Define → popover with result, empty state, or "install" guidance.

## 6. Change log

| Date | Author | Change |
|---|---|---|
| 2026-04-19 | — | Initial draft. |
| 2026-04-19 | — | All three workstreams shipped. Frontend: 57/57 reader + hook + store tests pass. Server: 238/238 tests pass (1 skipped CBR fixture request). One new migration (`AddReadingSessions`); dictionary ships with no schema change (file-based). |
