import { describe, it, expect } from 'vitest';
import { buildSearchSections, nameMatchTier } from './searchRanking';
import type { GlobalSearchResult } from '../services/searchService';
import type { PlaylistSummary } from '../services/playlistService';
import type { Library } from '../types';

const mediaGroup = (overrides: Partial<GlobalSearchResult> = {}): GlobalSearchResult => ({
    libraryId: 'lib-1',
    libraryName: 'Movies',
    libraryType: 'Movie',
    items: [],
    bestMatchTier: 0,
    matchReasons: {},
    ...overrides,
});

const playlist = (name: string, description: string | null = null): PlaylistSummary => ({
    id: 'p1', name, description, isPublic: false, isOwner: true, ownerUsername: 'me',
    itemCount: 1, createdAt: '2026-01-01', updatedAt: '2026-01-01',
    coverImagePaths: [], kind: 'Manual', rules: null, coverImagePath: null,
});

const library = (name: string, order = 0): Library => ({
    id: `lib-${name}`, name, type: 'Movie', paths: [], order,
});

describe('nameMatchTier', () => {
    it('ranks prefix over contains over description-only over none', () => {
        expect(nameMatchTier('test', 'Test Anthem')).toBe(0);
        expect(nameMatchTier('test', 'Contest Night')).toBe(1);
        expect(nameMatchTier('test', 'Mix', 'a test of songs')).toBe(2);
        expect(nameMatchTier('test', 'Unrelated')).toBe(3);
    });

    it('is case-insensitive, like the server ranking it mirrors', () => {
        expect(nameMatchTier('TEST', 'test anthem')).toBe(0);
    });

    it('treats an empty query as matching nothing', () => {
        expect(nameMatchTier('  ', 'Anything')).toBe(3);
    });
});

describe('buildSearchSections', () => {
    it('a strong media hit outranks a weak playlist hit — the old pinning bug', () => {
        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [mediaGroup({ bestMatchTier: 0 })],
            // Description-only match: tier 2.
            playlists: [playlist('Road Mix', 'my test songs')],
            libraries: [],
        });

        expect(sections.map(s => s.kind)).toEqual(['media', 'playlists']);
    });

    it('a strong playlist hit outranks a weak media hit', () => {
        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [mediaGroup({ bestMatchTier: 2 })],
            playlists: [playlist('Test Mix')],
            libraries: [],
        });

        expect(sections.map(s => s.kind)).toEqual(['playlists', 'media']);
    });

    // Being someone's own construct wins ties — it no longer wins contests.
    it('breaks equal-quality ties personal-first: playlists, libraries, media', () => {
        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [mediaGroup({ bestMatchTier: 0 })],
            playlists: [playlist('Test Mix')],
            libraries: [library('Test')],
        });

        expect(sections.map(s => s.kind)).toEqual(['playlists', 'libraries', 'media']);
    });

    it('matches libraries by NAME, which the server never searched', () => {
        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [],
            playlists: [],
            libraries: [library('Test'), library('Music')],
        });

        expect(sections).toHaveLength(1);
        const libSection = sections[0];
        if (libSection.kind !== 'libraries') throw new Error('expected a libraries section');
        expect(libSection.libraries.map(l => l.name)).toEqual(['Test']);
    });

    // Genre/description hits on library names would be nonsense; only real name
    // matches (prefix or contains) qualify.
    it('does not surface a library on a description-tier match', () => {
        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [],
            playlists: [],
            libraries: [library('Unrelated')],
        });

        expect(sections).toHaveLength(0);
    });

    it('preserves the server-decided order of equal-tier media groups', () => {
        const first = mediaGroup({ libraryId: 'a', bestMatchTier: 1 });
        const second = mediaGroup({ libraryId: 'b', bestMatchTier: 1 });

        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [first, second],
            playlists: [],
            libraries: [],
        });

        const ids = sections.map(s => (s.kind === 'media' ? s.group.libraryId : ''));
        expect(ids).toEqual(['a', 'b']);
    });

    it('lets a strong media group split weak and strong groups around a playlist', () => {
        const strong = mediaGroup({ libraryId: 'strong', bestMatchTier: 0 });
        const weak = mediaGroup({ libraryId: 'weak', bestMatchTier: 2 });

        const sections = buildSearchSections({
            query: 'test',
            mediaGroups: [strong, weak],
            playlists: [playlist('Contest Hits')], // tier 1
            libraries: [],
        });

        expect(sections.map(s => (s.kind === 'media' ? s.group.libraryId : s.kind)))
            .toEqual(['strong', 'playlists', 'weak']);
    });

    it('returns nothing when no source has hits', () => {
        expect(buildSearchSections({
            query: 'test', mediaGroups: [], playlists: [], libraries: [library('Music')],
        })).toHaveLength(0);
    });
});
