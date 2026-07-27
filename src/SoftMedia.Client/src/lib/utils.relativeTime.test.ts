import { describe, it, expect, afterEach, vi } from 'vitest';
import { formatRelativeTime } from './utils';

/**
 * Playlist cards show when a list was last touched. The label has to survive
 * the inputs a server can actually produce — including a timestamp slightly in
 * the future, which happens whenever the server clock leads the browser's.
 */
describe('formatRelativeTime', () => {
    const NOW = new Date('2026-07-26T12:00:00Z');

    const at = (iso: string) => {
        vi.useFakeTimers();
        vi.setSystemTime(NOW);
        return formatRelativeTime(iso);
    };

    afterEach(() => vi.useRealTimers());

    it('returns null for missing input', () => {
        expect(formatRelativeTime(null)).toBeNull();
        expect(formatRelativeTime(undefined)).toBeNull();
        expect(formatRelativeTime('')).toBeNull();
    });

    it('returns null rather than "Invalid Date" for unparsable input', () => {
        expect(formatRelativeTime('not-a-date')).toBeNull();
    });

    it('reads a future timestamp as just now instead of a negative interval', () => {
        expect(at('2026-07-26T12:05:00Z')).toBe('just now');
    });

    it('collapses the last minute to just now', () => {
        expect(at('2026-07-26T11:59:30Z')).toBe('just now');
    });

    it('reports minutes within the hour', () => {
        expect(at('2026-07-26T11:15:00Z')).toBe('45m ago');
    });

    it('reports hours within the day', () => {
        expect(at('2026-07-26T07:00:00Z')).toBe('5h ago');
    });

    it('names the previous day', () => {
        expect(at('2026-07-25T10:00:00Z')).toBe('yesterday');
    });

    it('reports days within the week', () => {
        expect(at('2026-07-23T12:00:00Z')).toBe('3 days ago');
    });

    it('reports weeks up to a month', () => {
        expect(at('2026-07-19T12:00:00Z')).toBe('last week');
        expect(at('2026-07-05T12:00:00Z')).toBe('3 weeks ago');
    });

    // Past a month "N weeks ago" is harder to place than the date itself.
    it('falls back to a calendar date beyond a month', () => {
        const label = at('2026-01-15T12:00:00Z');
        expect(label).toContain('2026');
        expect(label).not.toContain('weeks');
    });
});
