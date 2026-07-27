import type { GlobalSearchResult } from '../services/searchService';
import type { PlaylistSummary } from '../services/playlistService';
import type { Library } from '../types';

/**
 * Unified ordering for the global search dropdown.
 *
 * Three result sources feed the dropdown — media groups (server-ranked),
 * playlist hits, and library-name hits — and they used to be concatenated in a
 * fixed order, playlists always first. That encoded "what kind of thing" as
 * rank: a playlist whose description weakly contained the query sat above a
 * movie whose title matched exactly. This module puts every section on the one
 * scale the server already uses:
 *
 *   tier 0 — name/title starts with the query
 *   tier 1 — name/title contains the query
 *   tier 2 — matched via a secondary field (description, genre, cast, …)
 *
 * Sections order by tier; ties break personal-first (playlists, then
 * libraries, then media groups by their configured position) — being someone's
 * own construct wins ties, it no longer wins contests.
 */

export type SearchSection =
    | { kind: 'playlists'; tier: number; playlists: PlaylistSummary[] }
    | { kind: 'libraries'; tier: number; libraries: Library[] }
    | { kind: 'media'; tier: number; group: GlobalSearchResult };

/** Mirrors the server's TitleMatchTier; lower is better, 3 = no match at all. */
export function nameMatchTier(query: string, name: string, description?: string | null): number {
    const q = query.trim().toLowerCase();
    if (q.length === 0) return 3;
    const n = name.toLowerCase();
    if (n.startsWith(q)) return 0;
    if (n.includes(q)) return 1;
    if ((description ?? '').toLowerCase().includes(q)) return 2;
    return 3;
}

/** Tie order: personal constructs, then places, then contents. */
const KIND_PRIORITY: Record<SearchSection['kind'], number> = {
    playlists: 0,
    libraries: 1,
    media: 2,
};

export function buildSearchSections(input: {
    query: string;
    mediaGroups: GlobalSearchResult[];
    playlists: PlaylistSummary[];
    /** The full (ACL-filtered) library list; name matching happens here. */
    libraries: Library[];
}): SearchSection[] {
    const { query, mediaGroups, playlists, libraries } = input;
    const sections: SearchSection[] = [];

    if (playlists.length > 0) {
        // The endpoint already matched these; the tier is recomputable from the
        // fields it matched on (name, description), so no API change was needed.
        sections.push({
            kind: 'playlists',
            tier: Math.min(...playlists.map(p => nameMatchTier(query, p.name, p.description))),
            playlists,
        });
    }

    // Library names were never searched at all — a library called "Test" showed
    // up for "test" only by the coincidence of containing matching items.
    const libraryHits = libraries
        .filter(l => nameMatchTier(query, l.name) <= 1)
        .sort((a, b) => a.order - b.order);
    if (libraryHits.length > 0) {
        sections.push({
            kind: 'libraries',
            tier: Math.min(...libraryHits.map(l => nameMatchTier(query, l.name))),
            libraries: libraryHits,
        });
    }

    for (const group of mediaGroups) {
        sections.push({ kind: 'media', tier: group.bestMatchTier ?? 2, group });
    }

    // Media groups arrive server-ordered (tier, then library position); a stable
    // sort by tier + kind preserves that relative order within ties.
    return sections
        .map((section, index) => ({ section, index }))
        .sort((a, b) =>
            a.section.tier - b.section.tier
            || KIND_PRIORITY[a.section.kind] - KIND_PRIORITY[b.section.kind]
            || a.index - b.index)
        .map(({ section }) => section);
}
