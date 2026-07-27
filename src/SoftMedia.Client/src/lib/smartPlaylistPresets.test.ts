import { describe, it, expect } from 'vitest';
import { SMART_PLAYLIST_PRESETS, describeSmartRules } from './smartPlaylistPresets';
import { MAX_SMART_PLAYLIST_LIMIT, type SmartPlaylistRules } from '../services/playlistService';

const rules = (overrides: Partial<SmartPlaylistRules> = {}): SmartPlaylistRules => ({
    sort: 'RecentlyAdded',
    limit: 100,
    ...overrides,
});

describe('smart playlist presets', () => {
    it('offers presets with distinct ids', () => {
        const ids = SMART_PLAYLIST_PRESETS.map(p => p.id);
        expect(new Set(ids).size).toBe(ids.length);
    });

    // These mirror the server's SmartPlaylistRules.Validate. A preset that trips a
    // server rule would fail on submit with an error the user cannot act on, so the
    // shipped presets have to satisfy the same constraints.
    it('ships no preset the server would reject', () => {
        for (const preset of SMART_PLAYLIST_PRESETS) {
            const r = preset.rules;

            expect(r.favoritesOnly && r.unplayedOnly, `${preset.id}: contradictory filters`).toBeFalsy();
            expect(r.limit, `${preset.id}: limit too high`).toBeLessThanOrEqual(MAX_SMART_PLAYLIST_LIMIT);
            expect(r.limit, `${preset.id}: limit must be positive`).toBeGreaterThan(0);

            if (r.unplayedOnly) {
                // Every match has zero plays, so a play-activity sort has nothing
                // to order by — the server rejects the combination outright.
                expect(['MostPlayed', 'RecentlyPlayed'], `${preset.id}: unplayed cannot sort by plays`)
                    .not.toContain(r.sort);
            }
            if (r.addedWithinDays != null) {
                expect(r.addedWithinDays, `${preset.id}: window must be >= 1`).toBeGreaterThanOrEqual(1);
            }
        }
    });

    it('gives every preset a name suggestion', () => {
        for (const preset of SMART_PLAYLIST_PRESETS) {
            expect(preset.suggestedName.trim().length).toBeGreaterThan(0);
        }
    });
});

describe('describeSmartRules', () => {
    it('describes a filtered playlist in plain words', () => {
        expect(describeSmartRules(rules({ favoritesOnly: true })))
            .toBe('Favourites · newest first · up to 100 tracks');
    });

    it('spells out the added-within window', () => {
        expect(describeSmartRules(rules({ addedWithinDays: 30 })))
            .toContain('Added in the last 30 days');
    });

    it('uses the singular for a one-day window', () => {
        expect(describeSmartRules(rules({ addedWithinDays: 1 })))
            .toContain('Added in the last day');
    });

    // With nothing narrowing the library the sort IS the definition, so the
    // sentence has to say what the population is rather than start with an order.
    it('says "All tracks" when nothing narrows the library', () => {
        expect(describeSmartRules(rules({ sort: 'MostPlayed' })))
            .toBe('All tracks · most played first · up to 100 tracks');
    });

    it('combines multiple filters', () => {
        const text = describeSmartRules(rules({ unplayedOnly: true, genre: 'Rock' }));

        expect(text).toContain('Never played');
        expect(text).toContain('Genre: Rock');
    });

    it('uses the singular for a single-track limit', () => {
        expect(describeSmartRules(rules({ limit: 1 }))).toContain('up to 1 track');
    });

    it('describes every preset without leaving a placeholder', () => {
        for (const preset of SMART_PLAYLIST_PRESETS) {
            const text = describeSmartRules(preset.rules);
            expect(text.length).toBeGreaterThan(0);
            expect(text).not.toContain('undefined');
        }
    });
});
