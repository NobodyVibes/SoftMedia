import { describe, it, expect } from 'vitest';
import { computeCueShift, applyCueShift, clampUserOffset } from './subtitleSync';
import { buildCueCss, SUBTITLE_COLORS } from './subtitleStyle';

/** Minimal mutable cue + track doubles (jsdom has no VTTCue). */
function makeCue(start: number, end: number) {
    return { startTime: start, endTime: end } as VTTCue;
}
function makeTrack(cues: VTTCue[]): TextTrack {
    return { cues: cues as unknown as TextTrackCueList } as TextTrack;
}

/** R-WI-018 — the load-bearing sync rule: the SERVER serves stream-aligned cues
 *  (it offsets the VTT on far-seek restarts — OffsetWebVttTimestamps), so the
 *  client shift is the USER offset alone. Subtracting seekOffset here was the
 *  live-found bug: it double-shifted against the server's correction (cues at
 *  −476s instead of +2s after a far seek to 478s). */
describe('computeCueShift', () => {
    it('is exactly the user offset — never seek-offset compensated', () => {
        expect(computeCueShift(2)).toBe(2);
        expect(computeCueShift(-1.5)).toBe(-1.5);
        expect(computeCueShift(0)).toBe(0);
    });
});

describe('applyCueShift', () => {
    it('shifts every cue by the delta and reports the applied shift', () => {
        const cues = [makeCue(10, 12), makeCue(50, 53)];
        const applied = applyCueShift(makeTrack(cues), -5);

        expect(applied).toBe(-5);
        expect(cues[0].startTime).toBe(5);
        expect(cues[0].endTime).toBe(7);
        expect(cues[1].startTime).toBe(45);
    });

    it('re-applying the SAME target is a no-op — each cue anchors its served times', () => {
        // Regression for the live-found compounding: hls.js restarts recreate
        // track elements and cue objects can be observed across generations, so
        // caller-side delta bookkeeping desynced and stacked shifts. Per-cue
        // anchors make repeat calls idempotent no matter the call pattern.
        const cues = [makeCue(2, 10)]; // server-aligned cue after a far seek
        const track = makeTrack(cues);
        applyCueShift(track, 1.5);
        applyCueShift(track, 1.5);
        applyCueShift(track, 1.5);

        expect(cues[0].startTime).toBe(3.5); // shifted once, not three times
    });

    it('a FRESH track generation anchors its own served times — no reset call needed', () => {
        const oldCues = [makeCue(480, 488)]; // pre-seek generation: absolute times
        applyCueShift(makeTrack(oldCues), 2);

        // Far-seek restart: the server serves RE-ALIGNED cues (2 = 480 − seek 478).
        const newCues = [makeCue(2, 10)];
        applyCueShift(makeTrack(newCues), 2);

        expect(newCues[0].startTime).toBe(4);   // served time + user offset, exactly once
        expect(oldCues[0].startTime).toBe(482); // stale generation untouched by the new apply
    });

    it('moving between targets re-anchors from originals, not from mutated times', () => {
        const cues = [makeCue(100, 103)];
        const track = makeTrack(cues);
        applyCueShift(track, -3);
        applyCueShift(track, 2); // user flips the nudge direction

        expect(cues[0].startTime).toBe(102);
    });

    it('keeps end > start when a shift would produce an invalid cue', () => {
        const cues = [makeCue(1, 2)];
        applyCueShift(makeTrack(cues), -30);
        expect(cues[0].endTime).toBeGreaterThan(cues[0].startTime);
    });

    it('tolerates a track whose cues have not loaded yet (no bookkeeping written)', () => {
        const track = { cues: null } as unknown as TextTrack;
        expect(applyCueShift(track, 5)).toBe(0);
        // Once cues exist, the full shift still applies.
        const cues = [makeCue(10, 12)];
        const loaded = { ...track, cues: cues as unknown as TextTrackCueList } as TextTrack;
        applyCueShift(loaded, 5);
        expect(cues[0].startTime).toBe(15);
    });
});

describe('clampUserOffset', () => {
    it('clamps to ±30s and rounds to 0.1s', () => {
        expect(clampUserOffset(31)).toBe(30);
        expect(clampUserOffset(-99)).toBe(-30);
        expect(clampUserOffset(1.25)).toBe(1.3);
    });
});

describe('buildCueCss', () => {
    it('renders the selected appearance into scoped ::cue rules', () => {
        const css = buildCueCss({ fontSize: '125', color: 'yellow', bgOpacity: '0.5', edgeStyle: 'outline' });
        expect(css).toContain('video.sm-player-video::cue');
        expect(css).toContain(`color: ${SUBTITLE_COLORS.yellow}`);
        expect(css).toContain('background-color: rgba(0, 0, 0, 0.5)');
        expect(css).toContain('font-size: 125%');
        expect(css).toContain('-1px -1px 0 #000'); // outline via 4-way shadow
    });

    it('falls back to safe defaults on unknown values (stale/corrupt localStorage)', () => {
        const css = buildCueCss({ fontSize: '999', color: 'plaid', bgOpacity: '7', edgeStyle: 'sparkles' });
        expect(css).toContain(`color: ${SUBTITLE_COLORS.white}`);
        expect(css).toContain('font-size: 100%');
        expect(css).toContain('rgba(0, 0, 0, 0.75)');
        expect(css).toContain('text-shadow: none');
    });
});
