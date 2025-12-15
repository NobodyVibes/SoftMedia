# Transcode Throttling Implementation Plan

**Version:** 1.5
**Date:** 2025-12-15
**Status:** Approved

---

## 1. Overview

This document defines the implementation plan for transcode throttling in SoftMedia. The goal is to prevent wasteful transcoding by dynamically controlling FFmpeg's input read rate based on the client's playback position, while ensuring robust cleanup policies.

**Chosen Strategy:** FFmpeg `-readrate` flag manipulation combined with disk space and time-based retention policies.

---

## 2. State Machine

The transcoder operates in one of five states:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                             TRANSCODE STATES                                 │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌─────────┐    Buffer ≥ 30s    ┌──────────┐    Buffer ≥ 120s              │
│   │  BURST  │ ─────────────────► │ CATCHING │ ──────────────────┐           │
│   │ (None)  │                    │  (2.0x)  │                   │           │
│   └────┬────┘                    └────┬─────┘                   │           │
│        │                              │                         ▼           │
│        │                              │                    ┌────────┐       │
│        │                              │ Buffer < 90s      │CRUISING│       │
│        │                              │◄───────────────────│ (1.0x) │       │
│        │                              │                    └────┬───┘       │
│        │                              │                         │           │
│        │                              │    User Paused +        │           │
│        │                  ┌───────────┴───── Buffer ≥ 120s ─────┘           │
│        │                  │                                                  │
│        │                  ▼                                                  │
│        │             ┌─────────┐      User Resumes + Buffer ≥ 120s          │
│        │             │ DORMANT │ ──────────────────────────────────►┐       │
│        │             │ (Stop)  │                                    │       │
│        │             └────┬────┘      User Resumes + Buffer < 120s  │       │
│        │                  │ ────────────────────────────────────────┼──►┐   │
│        │                  │                                         │   │   │
│        │                  │ Cleanup Policy                          │   │   │
│        │                  ▼                                         │   │   │
│        │           ┌───────────┐◄────────────────────────────────────   │   │
│        │           │ COMPLETED │◄───────────────────────────────────────┘   │
│        └──────────►│ (Cleanup) │◄─── Video Ends (DELETE) from ANY state     │
│                    └───────────┘                                            │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
```

> [!NOTE]
> The COMPLETED state is reachable from ANY active state when the frontend calls `DELETE` (video ended). This is Idea 3.

---

## 3. Configuration & Constants

| Constant | Value | Description |
|:---|:---:|:---|
| `BurstThresholdSeconds` | `30` | Seconds of buffer before leaving BURST. |
| `ThrottleThresholdSeconds` | `120` | Target buffer size for CRUISING/DORMANT. |
| `ResumeBoostThresholdSeconds` | `90` | If buffer drops below this, boost back to CATCHING. |
| `HlsSegmentDurationSeconds` | `6` | Duration of each HLS `.ts` segment. |
| `MinDiskSpaceThresholdMB` | `500` | **Idea 1:** Evict dormant sessions if disk < 500MB. |
| `MaxDormantAgeHours` | `24` | **Idea 2:** Delete sessions dormant > 24 hours. |
| `CleanupCheckIntervalSeconds` | `30` | Frequency of disk space check. |
| `StaleSessionCheckIntervalMinutes` | `60` | Frequency of stale session cleanup. |
| `StateCheckIntervalSeconds` | `5` | Frequency of buffer/state evaluation. |

---

## 4. Cleanup & Eviction Policies

### 4.1 Disk Pressure Policy (Idea 1)
*   **Trigger:** Background task checks every 30 seconds.
*   **Condition:** `DriveInfo.AvailableFreeSpace < MinDiskSpaceThresholdMB`.
*   **Action:**
    1.  Get all sessions where `State == DORMANT`.
    2.  Sort by `LastClientRequestTime` ascending (oldest first).
    3.  Delete sessions until free space > 500MB or no dormant sessions remain.
    4.  Log warning if active sessions remain and disk still low.

### 4.2 Stale Session Policy (Idea 2)
*   **Trigger:** Scheduled task every 60 minutes.
*   **Condition:** `State == DORMANT` AND `(UtcNow - LastClientRequestTime) > 24 hours`.
*   **Action:** Delete session files and remove from `_activeSessions` dictionary.

### 4.3 Completion Policy (Idea 3)
*   **Trigger:** Frontend calls `DELETE /api/transcode/{id}` when video ends.
*   **Condition:** None. Works in ANY state (BURST, CATCHING, CRUISING, DORMANT).
*   **Action:** Immediate cleanup—kill FFmpeg if running, delete all files, remove session.

---

## 5. Detailed State Definitions

### 5.1 BURST (Initial Ramp-Up)
*   **Readrate:** `None` (Full CPU/GPU speed).
*   **Entry:** Session start.
*   **Exit:** `Buffer >= 30s`.
*   **Next:** `CATCHING`.

### 5.2 CATCHING (Controlled Catch-Up)
*   **Readrate:** `2.0`.
*   **Entry:** From BURST, from CRUISING (buffer dropped), or user resumed from DORMANT with `Buffer < 120s`.
*   **Exit:** `Buffer >= 120s`.
*   **Next:** `CRUISING`.

### 5.3 CRUISING (Steady State)
*   **Readrate:** `1.0`.
*   **Entry:** `Buffer >= 120s` (from CATCHING, or resume from DORMANT with buffer still full).
*   **Exit:**
    *   `Buffer < 90s` → `CATCHING`.
    *   User paused AND `Buffer >= 120s` → `DORMANT`.

### 5.4 DORMANT (Paused/Inactive)
*   **Readrate:** N/A (FFmpeg process terminated, segments retained).
*   **Entry:** User paused AND `Buffer >= 120s`.
*   **Exit:**
    *   User resumes AND `Buffer >= 120s` → `CRUISING`.
    *   User resumes AND `Buffer < 120s` → `CATCHING`.
    *   Video ends (DELETE) → `COMPLETED`.
    *   Cleanup policy triggers → Delete session.

> [!IMPORTANT]
> **Pause with Low Buffer:** If user pauses while `Buffer < 120s`, remain in current state (BURST/CATCHING) and continue transcoding until `Buffer >= 120s`, THEN transition to DORMANT.

### 5.5 COMPLETED (Terminal State)
*   **Entry:** Frontend signals video ended (`DELETE` request) from ANY state.
*   **Action:** Kill FFmpeg (if running), delete all session files, remove from memory.
*   **Next:** None (session removed).

---

## 6. Buffer Calculation

**Formula:**
```csharp
int bufferSegments = LatestSegmentIndex - ClientSegmentIndex;
int bufferSeconds = Math.Max(0, bufferSegments) * HlsSegmentDurationSeconds;
```

> [!NOTE]
> `Math.Max(0, ...)` prevents negative buffer values if client somehow requests a segment beyond what's been transcoded.

**Segment Index Extraction:**
```csharp
private static readonly Regex SegmentPattern = new(@"^seg_(\d+)\.ts$", RegexOptions.Compiled);

private int ExtractSegmentIndex(string segmentName)
{
    var match = SegmentPattern.Match(segmentName);
    return match.Success ? int.Parse(match.Groups[1].Value) : -1;
}
```

**Latest Segment Detection:**
```csharp
private int GetLatestSegmentIndex(string sessionDir)
{
    if (!Directory.Exists(sessionDir)) return 0;
    var files = Directory.GetFiles(sessionDir, "seg_*.ts");
    return files
        .Select(f => ExtractSegmentIndex(Path.GetFileName(f)))
        .Where(i => i >= 0)
        .DefaultIfEmpty(0)
        .Max();
}
```

---

## 7. Seek Handling

| Scenario | Detection | Action |
|:---|:---|:---|
| **Seek Forward (within buffer)** | Client requests segment > current but <= latest | Update `ClientSegmentIndex`. Buffer shrinks. May trigger CATCHING. |
| **Seek Forward (beyond buffer)** | Client requests segment > `LatestSegmentIndex` | Return `404 Not Found`. Client waits. Optional: boost to CATCHING. |
| **Seek Backward** | Client requests segment < current | Update `ClientSegmentIndex`. Buffer grows. No state change. |
| **Seek to new position (major jump)** | User drags scrubber past buffer | Frontend restarts transcode with `?seek={seconds}` parameter. |

**On Seek Restart:**
When `?seek=` is provided, the existing session is replaced:
1.  Stop current FFmpeg process.
2.  Delete existing segments.
3.  Start new FFmpeg with `-ss {seekSeconds}`.
4.  Reset `ClientSegmentIndex = 0`, `LatestSegmentIndex = 0`, `CrashRetryCount = 0`.
5.  Reset state to BURST.

---

## 8. Backend Implementation Plan

### 8.1 `TranscodeSession` Model
```csharp
internal class TranscodeSession
{
    public TranscodeSessionKey Key { get; init; }
    public Guid UserId { get; init; }  // Session owner for authorization
    public Process? Process { get; set; }
    public TranscodeState State { get; set; } = TranscodeState.Burst;
    public double? CurrentReadRate { get; set; } = null;  // null = BURST (full speed)
    public int LatestSegmentIndex { get; set; } = 0;
    public int ClientSegmentIndex { get; set; } = 0;
    public DateTime LastClientRequestTime { get; set; } = DateTime.UtcNow;
    public DateTime SessionStartTime { get; init; } = DateTime.UtcNow;
    public bool IsPaused { get; set; } = false;
    public int CrashRetryCount { get; set; } = 0;  // Reset on successful segment generation
    public string SessionDirectory { get; init; } = string.Empty;
}

internal enum TranscodeState { Burst, Catching, Cruising, Dormant, Completed }
```

### 8.2 `ThrottleMonitorService` (Background Service)
*   **5-second loop:**
    *   Recalculate `LatestSegmentIndex` from disk.
    *   Compute buffer.
    *   Evaluate state transitions (restart FFmpeg with new `-readrate` if needed).
*   **30-second loop:** Disk space check, evict dormant if low.
*   **60-minute loop:** Stale session cleanup (> 24h dormant).

### 8.3 `TranscodeController` Endpoints

| Endpoint | Auth | Rate Limit | Purpose |
|:---|:---:|:---:|:---|
| `GET /{id}/master.m3u8` | ✅ | — | Start transcode, return playlist. |
| `GET /{id}/{segment}` | ✅ | — | Serve segment, update `ClientSegmentIndex`. |
| `POST /{id}/pause` | ✅ | 20/min | Set `IsPaused = true`. |
| `POST /{id}/resume` | ✅ | 20/min | Set `IsPaused = false`. |
| `DELETE /{id}` | ✅ | — | Trigger COMPLETED state and cleanup. |

> [!CAUTION]
> **Authorization:** Pause/resume/delete endpoints MUST verify `User.Id == session.UserId`. Return `403 Forbidden` if mismatch.

---

## 9. Frontend Implementation Plan

### 9.1 Player Component (`VideoPlayer.tsx`)

```typescript
// On video end (Idea 3) - triggers COMPLETED from any state
const onEnded = () => {
    fetch(`/api/transcode/${mediaId}?all=true`, { 
        method: 'DELETE',
        headers: { 'Authorization': `Bearer ${token}` }
    });
};

// On pause
const onPause = () => {
    fetch(`/api/transcode/${mediaId}/pause`, {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${token}` }
    });
};

// On play/resume
const onPlay = () => {
    fetch(`/api/transcode/${mediaId}/resume`, {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${token}` }
    });
};
```

---

## 10. Security Considerations

| Risk | Mitigation |
|:---|:---|
| **DoS via fake segment requests** | Validate `RequestedSegmentIndex <= LatestSegmentIndex`. Return 404 for out-of-range. |
| **Session hijacking** | Store `UserId` in session. Verify on pause/resume/delete. Return 403 on mismatch. |
| **Disk exhaustion attack** | Disk pressure policy evicts dormant sessions. Log warning for active sessions. |
| **Path traversal** | Validate segment filename matches `^seg_\d+\.ts$`. Reject all other patterns. |
| **Pause/resume spam** | Rate limit to 20 requests per minute per session. |

---

## 11. Crash Recovery

**Scenario:** FFmpeg process crashes or exits unexpectedly mid-transcode.

**Detection:**
*   `ThrottleMonitorService` checks `Process.HasExited` during each 5-second loop.
*   If `HasExited == true` AND `State != Dormant` AND `State != Completed`, a crash occurred.

**Recovery Action:**
1.  Log error with exit code: `Process.ExitCode`.
2.  Restart FFmpeg with `-ss {LatestSegmentIndex * HlsSegmentDurationSeconds}` to resume from last segment.
3.  Remain in current state (BURST/CATCHING/CRUISING).
4.  If restart fails 3 times consecutively, transition to COMPLETED (cleanup) and log critical error.

---

## 12. Verification Cases

| Scenario | Expected Behavior |
|:---|:---|
| **Burst → Catching** | After 30s buffer, FFmpeg restarts with `-readrate 2.0`. |
| **Catching → Cruising** | After 120s buffer, FFmpeg restarts with `-readrate 1.0`. |
| **Cruising → Catching** | Buffer drops below 90s, FFmpeg restarts with `-readrate 2.0`. |
| **Seek forward (in buffer)** | Buffer shrinks, may trigger CATCHING. |
| **Seek forward (beyond buffer)** | 404 returned. Client waits. |
| **Pause with full buffer** | FFmpeg stops, files kept, state = DORMANT. |
| **Pause with low buffer** | Transcode continues until buffer >= 120s, then DORMANT. |
| **Resume from DORMANT (buffer full)** | FFmpeg restarts at 1.0x (CRUISING). |
| **Resume from DORMANT (buffer depleted)** | FFmpeg restarts at 2.0x (CATCHING). |
| **Video ends (any state)** | DELETE called, FFmpeg killed, all files removed. |
| **Disk < 500MB** | Oldest dormant session deleted. |
| **Session dormant > 24h** | Hourly task deletes it. |
| **Tab close (no DELETE)** | Files remain. Cleaned up by Idea 1 or Idea 2. |
| **Wrong user calls pause** | 403 Forbidden returned. |
| **FFmpeg crashes** | Auto-restart from last segment. After 3 failures, cleanup. |

---

## 13. Version History

| Version | Date | Changes |
|:---|:---|:---|
| 1.0 | 2025-12-15 | Initial draft with state machine and cleanup policies. |
| 1.1 | 2025-12-15 | Incorporated 3 user ideas (disk pressure, 24h cleanup, video-end cleanup). |
| 1.2 | 2025-12-15 | Added COMPLETED state, buffer formula, seek handling, security section, pause-with-low-buffer logic. |
| 1.3 | 2025-12-15 | Fixed state diagram (COMPLETED from any state), added UserId, conditional resume logic, rate limits. |
| 1.4 | 2025-12-15 | Increased rate limit to 20/min, added CurrentReadRate field, added crash recovery section. |
| 1.5 | 2025-12-15 | Added CrashRetryCount field, clarified seek restart resets indexes. |

