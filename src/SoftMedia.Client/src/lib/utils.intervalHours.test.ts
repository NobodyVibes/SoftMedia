import { describe, it, expect } from 'vitest';
import { isIntervalHoursEnabled } from './utils';

/**
 * R-WI-008 review regression guard. The server parses interval-hours settings with int.TryParse
 * (unparsable or <= 0 => schedule disabled). The UI hint must apply the IDENTICAL rule — the
 * original `parseInt(v) === 0` showed "hours" for "2.5" and "" while the server silently never
 * ran a scan.
 */
describe('isIntervalHoursEnabled', () => {
    it('enables for positive integers only', () => {
        expect(isIntervalHoursEnabled('1')).toBe(true);
        expect(isIntervalHoursEnabled('24')).toBe(true);
        expect(isIntervalHoursEnabled('8760')).toBe(true);
        expect(isIntervalHoursEnabled(' 12 ')).toBe(true); // trimmed like the server's parse
    });

    it('disables for zero and negatives (server: <= 0 = off)', () => {
        expect(isIntervalHoursEnabled('0')).toBe(false);
        expect(isIntervalHoursEnabled('-3')).toBe(false);
    });

    it('disables for values int.TryParse rejects (the reported mismatch)', () => {
        expect(isIntervalHoursEnabled('2.5')).toBe(false); // reported scenario: showed "hours", never ran
        expect(isIntervalHoursEnabled('')).toBe(false);    // cleared field
        expect(isIntervalHoursEnabled('2e5')).toBe(false); // scientific notation
        expect(isIntervalHoursEnabled('abc')).toBe(false);
        expect(isIntervalHoursEnabled('1.0')).toBe(false); // int.TryParse rejects this too
    });
});
