import type { SmartPlaylistRules, SmartPlaylistSort } from '../services/playlistService';

/**
 * Ready-made smart playlists.
 *
 * Presets rather than a rule builder for the first cut: these five are what
 * people actually ask for, and each is one tap instead of a form. The rules
 * they produce are the same shape the API accepts, so a builder can be layered
 * on later without changing anything below it.
 */
export interface SmartPlaylistPreset {
    id: string;
    label: string;
    /** Shown under the label — says what the playlist will contain, in plain words. */
    hint: string;
    /** Seeded into the name field; the user can overwrite it. */
    suggestedName: string;
    rules: SmartPlaylistRules;
}

export const SMART_PLAYLIST_PRESETS: SmartPlaylistPreset[] = [
    {
        id: 'recently-added',
        label: 'Recently Added',
        hint: 'Tracks added to your library in the last 30 days.',
        suggestedName: 'Recently Added',
        rules: { addedWithinDays: 30, sort: 'RecentlyAdded', limit: 100 },
    },
    {
        id: 'most-played',
        label: 'Most Played',
        hint: 'Your most-played tracks, counted from your own listening.',
        suggestedName: 'Most Played',
        rules: { sort: 'MostPlayed', limit: 100 },
    },
    {
        id: 'favourites',
        label: 'Favourites',
        hint: 'Every track you have marked as a favourite.',
        suggestedName: 'Favourites',
        rules: { favoritesOnly: true, sort: 'RecentlyAdded', limit: 200 },
    },
    {
        id: 'never-played',
        label: 'Never Played',
        hint: "Tracks you haven't listened to yet.",
        suggestedName: 'Never Played',
        rules: { unplayedOnly: true, sort: 'RecentlyAdded', limit: 100 },
    },
    {
        id: 'recently-played',
        label: 'Recently Played',
        hint: 'What you listened to most recently.',
        suggestedName: 'Recently Played',
        rules: { sort: 'RecentlyPlayed', limit: 50 },
    },
];

const SORT_LABELS: Record<SmartPlaylistSort, string> = {
    RecentlyAdded: 'newest first',
    MostPlayed: 'most played first',
    RecentlyPlayed: 'recently played first',
    Title: 'by title',
    Artist: 'by artist',
};

/**
 * A one-line, human description of what a smart playlist is currently doing.
 *
 * The detail page shows this instead of the raw rules: a user who returns to a
 * playlist months later needs to know why these tracks and not others, and
 * "Favourites, newest first, up to 200 tracks" answers that where a JSON blob
 * does not.
 */
export function describeSmartRules(rules: SmartPlaylistRules): string {
    const parts: string[] = [];

    if (rules.favoritesOnly) parts.push('Favourites');
    if (rules.unplayedOnly) parts.push('Never played');
    if (rules.addedWithinDays) {
        parts.push(rules.addedWithinDays === 1
            ? 'Added in the last day'
            : `Added in the last ${rules.addedWithinDays} days`);
    }
    if (rules.genre) parts.push(`Genre: ${rules.genre}`);

    // With nothing narrowing the library, the sort IS the definition ("the top 50
    // by play count"), so leading with "All tracks" keeps the sentence honest.
    if (parts.length === 0) parts.push('All tracks');

    parts.push(SORT_LABELS[rules.sort] ?? SORT_LABELS.RecentlyAdded);
    parts.push(`up to ${rules.limit} ${rules.limit === 1 ? 'track' : 'tracks'}`);

    return parts.join(' · ');
}
