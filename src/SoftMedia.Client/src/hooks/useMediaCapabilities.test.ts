import { renderHook, act } from '@testing-library/react';
import { describe, it, expect, afterEach, vi } from 'vitest';
import { useMediaCapabilities, detectHdrDetails } from './useMediaCapabilities';

/**
 * SR-WI-027 — HDR display detection must require `(video-dynamic-range: high)`
 * and NOT fall back to wide-gamut queries: nearly every modern SDR laptop and
 * phone has a P3 panel, and the old `(color-gamut: p3)` fallback made them all
 * claim HDR displays — the server then direct-played HDR onto SDR screens
 * (washed-out picture) instead of tone-mapping.
 */

const HDR_CODEC_MIMES = [
    'video/mp4; codecs="hvc1.2.4.L153.B0"', // HEVC Main 10
    'video/mp4; codecs="av01.0.09M.10"',    // AV1 10-bit
];

function stubMatchMedia(matchingQueries: string[]) {
    vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
        matches: matchingQueries.includes(query),
        media: query,
    } as MediaQueryList)));
}

function stubMediaSource(supportedMimes: string[]) {
    vi.stubGlobal('MediaSource', {
        isTypeSupported: (mime: string) => supportedMimes.includes(mime),
    });
}

afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
});

describe('detectHdrDetails — display gating (SR-WI-027)', () => {
    it('reports an HDR display when (video-dynamic-range: high) matches', () => {
        stubMatchMedia(['(video-dynamic-range: high)']);
        expect(detectHdrDetails().displaySupportsHdr).toBe(true);
    });

    it('does NOT report HDR for a wide-gamut (P3) SDR display', () => {
        // The classic false positive: modern SDR laptop/phone with a P3 panel.
        stubMatchMedia(['(color-gamut: p3)']);
        expect(detectHdrDetails().displaySupportsHdr).toBe(false);
    });

    it('does NOT report HDR for a rec2020-gamut display without high dynamic range', () => {
        stubMatchMedia(['(color-gamut: p3)', '(color-gamut: rec2020)']);
        expect(detectHdrDetails().displaySupportsHdr).toBe(false);
    });

    it('reports no HDR display when matchMedia is unavailable, without throwing', () => {
        vi.stubGlobal('matchMedia', undefined);
        expect(detectHdrDetails().displaySupportsHdr).toBe(false);
    });
});

describe('detectHdrDetails — codec check', () => {
    it('detects HDR codec support when the browser decodes a 10-bit codec', () => {
        stubMatchMedia([]);
        stubMediaSource(HDR_CODEC_MIMES);
        expect(detectHdrDetails().codecSupportsHdr).toBe(true);
    });

    it('reports no HDR codec support when only 8-bit codecs decode', () => {
        stubMatchMedia([]);
        stubMediaSource(['video/mp4; codecs="avc1.42E01E"']);
        expect(detectHdrDetails().codecSupportsHdr).toBe(false);
    });
});

describe('useMediaCapabilities — supportsHdr end to end', () => {
    async function detectWith(matchingQueries: string[]) {
        vi.useFakeTimers();
        stubMatchMedia(matchingQueries);
        stubMediaSource(HDR_CODEC_MIMES);
        const { result } = renderHook(() => useMediaCapabilities());
        // Detection runs on a 100ms timer after mount.
        await act(async () => {
            vi.advanceTimersByTime(150);
        });
        return result.current;
    }

    it('P3-gamut SDR screen with HDR-capable codecs → supportsHdr false', async () => {
        const { capabilities, isDetecting } = await detectWith(['(color-gamut: p3)']);
        expect(isDetecting).toBe(false);
        expect(capabilities.displaySupportsHdr).toBe(false);
        expect(capabilities.supportsHdr).toBe(false);
        // The codec half is still reported truthfully.
        expect(capabilities.codecSupportsHdr).toBe(true);
    });

    it('true HDR screen with HDR-capable codecs → supportsHdr true', async () => {
        const { capabilities } = await detectWith(['(video-dynamic-range: high)', '(color-gamut: p3)']);
        expect(capabilities.displaySupportsHdr).toBe(true);
        expect(capabilities.supportsHdr).toBe(true);
    });
});
