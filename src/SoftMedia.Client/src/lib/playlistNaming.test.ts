import { describe, it, expect } from 'vitest';
import { copyPlaylistName, MAX_PLAYLIST_NAME_LENGTH } from './playlistNaming';

/**
 * The server rejects a name over 120 characters outright, so "Save a copy" on a
 * long-named shared playlist would fail the save entirely if the suffix were
 * appended blindly. These pin the boundary.
 */
describe('copyPlaylistName', () => {
    it('appends the copy suffix to a short name', () => {
        expect(copyPlaylistName('Road Trip')).toBe('Road Trip (copy)');
    });

    it('trims surrounding whitespace before appending', () => {
        expect(copyPlaylistName('  Road Trip  ')).toBe('Road Trip (copy)');
    });

    it('never exceeds the server limit', () => {
        const result = copyPlaylistName('x'.repeat(200));
        expect(result.length).toBeLessThanOrEqual(MAX_PLAYLIST_NAME_LENGTH);
        expect(result.endsWith(' (copy)')).toBe(true);
    });

    it('keeps a name that lands exactly on the limit intact', () => {
        const exact = 'x'.repeat(MAX_PLAYLIST_NAME_LENGTH - ' (copy)'.length);
        const result = copyPlaylistName(exact);

        expect(result).toBe(`${exact} (copy)`);
        expect(result.length).toBe(MAX_PLAYLIST_NAME_LENGTH);
    });

    it('truncates the tail by one character when a name is one over', () => {
        const oneOver = 'x'.repeat(MAX_PLAYLIST_NAME_LENGTH - ' (copy)'.length + 1);
        const result = copyPlaylistName(oneOver);

        expect(result.length).toBe(MAX_PLAYLIST_NAME_LENGTH);
        expect(result).toBe(`${'x'.repeat(MAX_PLAYLIST_NAME_LENGTH - ' (copy)'.length)} (copy)`);
    });

    // Truncation can land mid-space; the result shouldn't read "Long name  (copy)".
    it('does not leave a dangling space before the suffix', () => {
        const name = `${'y'.repeat(MAX_PLAYLIST_NAME_LENGTH - 8)} tail`;
        expect(copyPlaylistName(name)).not.toContain('  (copy)');
    });
});
