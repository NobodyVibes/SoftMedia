import { describe, it, expect } from 'vitest';
import { playerBackTarget, detailBackTarget } from './backNavigation';
import { MediaType } from '../types';

// Back buttons are hierarchical, never browser history — this matrix is the contract
// for every back control's destination.
describe('playerBackTarget', () => {
    it('movie → its own detail page', () => {
        expect(playerBackTarget({ id: 'movie-1' })).toBe('/media/movie-1');
    });

    it('episode → its SERIES detail page (no per-episode pages exist)', () => {
        expect(playerBackTarget({ id: 'ep-1', seriesId: 'series-1' })).toBe('/media/series-1');
    });
});

describe('detailBackTarget', () => {
    it('episode detail → its series page', () => {
        expect(detailBackTarget({ id: 'ep-1', type: MediaType.Episode, seriesId: 'series-1', libraryId: 'lib-1' }))
            .toBe('/media/series-1');
    });

    it('track → its album', () => {
        expect(detailBackTarget({ id: 't-1', type: MediaType.Track, albumId: 'album-1', artistId: 'artist-1', libraryId: 'lib-1' }))
            .toBe('/media/album-1');
    });

    it('album → its artist', () => {
        expect(detailBackTarget({ id: 'album-1', type: MediaType.Album, artistId: 'artist-1', libraryId: 'lib-1' }))
            .toBe('/media/artist-1');
    });

    it('top-level items (movie, series, artist, book) → their library', () => {
        expect(detailBackTarget({ id: 'm-1', type: MediaType.Movie, libraryId: 'lib-1' }))
            .toBe('/libraries/lib-1');
        expect(detailBackTarget({ id: 's-1', type: MediaType.Series, libraryId: 'lib-2' }))
            .toBe('/libraries/lib-2');
    });

    it('photo → its album in the photo library, key preserved and encoded', () => {
        expect(detailBackTarget({ id: 'p-1', type: MediaType.Photo, libraryId: 'lib-1' }, 'summer/2011'))
            .toBe('/libraries/lib-1?album=summer%2F2011');
        expect(detailBackTarget({ id: 'p-1', type: MediaType.Photo, libraryId: 'lib-1' }, null))
            .toBe('/libraries/lib-1');
    });

    it('falls back to home when nothing else is known', () => {
        expect(detailBackTarget({ id: 'x-1' })).toBe('/');
    });
});
