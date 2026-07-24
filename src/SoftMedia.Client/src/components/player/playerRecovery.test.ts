import { describe, it, expect } from 'vitest';
import { MAX_NETWORK_RETRIES, networkRetryDelayMs, parseRetryAfterSeconds } from './playerRecovery';

/**
 * SR-WI-026 — retry policy for the player's error/recovery paths. (There is no
 * mounted-VideoPlayer test harness in this repo, so the policy lives in pure
 * helpers and is pinned here.)
 */

describe('networkRetryDelayMs', () => {
    it('backs off exponentially and caps at 8s', () => {
        expect([1, 2, 3, 4, 5, 6].map(networkRetryDelayMs))
            .toEqual([1000, 2000, 4000, 8000, 8000, 8000]);
    });

    it('spends the full retry budget over roughly 30 seconds', () => {
        let total = 0;
        for (let attempt = 1; attempt <= MAX_NETWORK_RETRIES; attempt++) {
            total += networkRetryDelayMs(attempt);
        }
        expect(total).toBeGreaterThanOrEqual(25_000);
        expect(total).toBeLessThanOrEqual(35_000);
    });

    it('treats out-of-range attempts sanely', () => {
        expect(networkRetryDelayMs(0)).toBe(1000);
        expect(networkRetryDelayMs(-5)).toBe(1000);
        expect(networkRetryDelayMs(100)).toBe(8000);
    });
});

describe('parseRetryAfterSeconds', () => {
    it('parses a delta-seconds header', () => {
        expect(parseRetryAfterSeconds('5')).toBe(5);
        expect(parseRetryAfterSeconds('30')).toBe(30);
    });

    it('falls back when the header is missing or unparsable', () => {
        expect(parseRetryAfterSeconds(null)).toBe(10);
        expect(parseRetryAfterSeconds(undefined)).toBe(10);
        expect(parseRetryAfterSeconds('')).toBe(10);
        expect(parseRetryAfterSeconds('soon')).toBe(10);
        // HTTP-date form is not supported → fallback, not NaN.
        expect(parseRetryAfterSeconds('Wed, 21 Oct 2026 07:28:00 GMT')).toBe(10);
    });

    it('rejects non-positive values and clamps huge ones', () => {
        expect(parseRetryAfterSeconds('0')).toBe(10);
        expect(parseRetryAfterSeconds('-3')).toBe(10);
        expect(parseRetryAfterSeconds('600')).toBe(60);
    });

    it('honors a custom fallback', () => {
        expect(parseRetryAfterSeconds(null, 7)).toBe(7);
    });
});
