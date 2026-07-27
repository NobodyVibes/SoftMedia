import { describe, it, expect } from 'vitest';
import { playlistsIndexTarget, LIBRARY_VIEW_PARAM, PLAYLIST_ORIGIN_PARAM } from './backNavigation';

const lib = (id: string, type: 'Movie' | 'Music' | 'Photo') => ({ id, type });
const tab = (id: string) => `/libraries/${id}?${LIBRARY_VIEW_PARAM}=playlists`;

describe('playlistsIndexTarget', () => {
    // The bug: PlaylistDetailPage linked to "/playlists", which App.tsx never
    // registers (only "/playlists/:id"), so the catch-all <Navigate to="/">
    // dumped the user on the home page.
    it('never resolves to the bare /playlists route', () => {
        expect(playlistsIndexTarget([lib('m1', 'Music')])).not.toBe('/playlists');
    });

    it('deep-links the Music library playlists tab', () => {
        expect(playlistsIndexTarget([lib('movies', 'Movie'), lib('tunes', 'Music')]))
            .toBe(`/libraries/tunes?${LIBRARY_VIEW_PARAM}=playlists`);
    });

    it('picks the first Music library when several exist', () => {
        expect(playlistsIndexTarget([lib('m1', 'Music'), lib('m2', 'Music')]))
            .toBe(`/libraries/m1?${LIBRARY_VIEW_PARAM}=playlists`);
    });

    // Only genuinely-nowhere-to-go cases fall back to home.
    it('falls back to home when no Music library exists', () => {
        expect(playlistsIndexTarget([lib('movies', 'Movie')])).toBe('/');
    });

    it('falls back to home while the library list is still loading', () => {
        expect(playlistsIndexTarget(undefined)).toBe('/');
    });

    describe('remembering the library the user came from', () => {
        const twoMusicLibraries = [lib('vinyl', 'Music'), lib('movies', 'Movie'), lib('flac', 'Music')];

        it('returns to the origin library rather than the first Music one', () => {
            expect(playlistsIndexTarget(twoMusicLibraries, 'flac')).toBe(tab('flac'));
        });

        it('still honours the origin when it IS the first Music library', () => {
            expect(playlistsIndexTarget(twoMusicLibraries, 'vinyl')).toBe(tab('vinyl'));
        });

        it('ignores an origin that is not a Music library', () => {
            expect(playlistsIndexTarget(twoMusicLibraries, 'movies')).toBe(tab('vinyl'));
        });

        // A deleted library, or a hand-edited URL, must not strand the user on a
        // dead library page.
        it('ignores an origin that no longer exists', () => {
            expect(playlistsIndexTarget(twoMusicLibraries, 'deleted-lib')).toBe(tab('vinyl'));
        });

        it('ignores an empty origin', () => {
            expect(playlistsIndexTarget(twoMusicLibraries, '')).toBe(tab('vinyl'));
            expect(playlistsIndexTarget(twoMusicLibraries, null)).toBe(tab('vinyl'));
        });

        // Nothing to validate against yet; the id came from our own link, and a
        // fallback here would make the href flicker as the query resolves.
        it('trusts the origin while the library list is loading', () => {
            expect(playlistsIndexTarget(undefined, 'flac')).toBe(tab('flac'));
        });

        it('honours the origin even when it is the only Music library', () => {
            expect(playlistsIndexTarget([lib('flac', 'Music')], 'flac')).toBe(tab('flac'));
        });

        it('uses the origin param name the links are built with', () => {
            expect(PLAYLIST_ORIGIN_PARAM).toBe('from');
        });
    });
});
