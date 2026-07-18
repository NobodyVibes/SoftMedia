/**
 * R-WI-018 — subtitle timing math for the HLS sidecar-VTT path.
 *
 * The SERVER already aligns the VTT with the stream timeline: after a far-seek
 * restart it re-extracts the subtitles and shifts them by the seek position
 * (`OffsetWebVttTimestamps` in TranscodeService), so the cues the client loads
 * are ALWAYS stream-relative. The only client-side adjustment is therefore the
 * USER's sync offset — do NOT compensate for `seekOffset` here (doing so
 * double-shifts against the server's correction; found live: cues landed at
 * −476s instead of +2s after a far seek to 478s).
 *
 *   cue fires when: streamTime == servedCueTime + userOffset
 */

/** Total shift (seconds) to apply to served cue times. */
export function computeCueShift(userOffsetSeconds: number): number {
    return userOffsetSeconds;
}

/** Expando keys: a cue's ORIGINAL (as-served) times, captured on first touch. */
const ORIG_START = '__smOrigStart';
const ORIG_END = '__smOrigEnd';

type AnchoredCue = VTTCue & { [ORIG_START]?: number; [ORIG_END]?: number };

/**
 * Set every cue's times to `asServed + targetShift`. Each cue remembers its
 * original times via expandos on first touch, and every application computes
 * absolute targets from those anchors — fully idempotent per cue, whatever the
 * call pattern. This matters because track elements are recreated by hls.js
 * restarts (MANIFEST_PARSED can fire more than once per stream) and the same
 * parsed cue objects can be observed across generations — delta-based
 * bookkeeping kept in the caller desyncs and compounds; anchors cannot.
 *
 * Returns the shift applied to the track's cues (0 when no cues were loaded —
 * the loaded-track callbacks re-apply once cues exist).
 */
export function applyCueShift(track: TextTrack, targetShift: number): number {
    if (!track.cues || track.cues.length === 0) return 0;

    // Snapshot: TextTrackCueList is live and re-sorts as times change.
    const cues = Array.from(track.cues) as AnchoredCue[];
    for (const cue of cues) {
        const origStart = cue[ORIG_START] ?? (cue[ORIG_START] = cue.startTime);
        const origEnd = cue[ORIG_END] ?? (cue[ORIG_END] = cue.endTime);
        cue.startTime = origStart + targetShift;
        // Keep end > start so a shift can't produce an invalid negative-duration
        // cue; cues pushed before t=0 are unreachable and harmless.
        cue.endTime = Math.max(origEnd + targetShift, cue.startTime + 0.001);
    }
    return targetShift;
}

/** Clamp the user's sync offset to a sane range (±30s covers real-world drift). */
export function clampUserOffset(seconds: number): number {
    return Math.max(-30, Math.min(30, Math.round(seconds * 10) / 10));
}
