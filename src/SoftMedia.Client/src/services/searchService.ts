import api from './api';

export interface GlobalSearchResult {
    libraryId: string;
    libraryName: string;
    libraryType: 'Movie' | 'TV' | 'Music' | 'Book' | 'Game' | 'Photo';
    items: import('../types').MediaItem[];
    /**
     * The group's strongest match: 0 = a title starts with the query, 1 = a
     * title contains it, 2 = matched via another field. The dropdown merges
     * media groups, playlist hits and library-name hits on this one scale, so
     * placement is decided by match quality rather than by result type.
     */
    bestMatchTier: number;
    /**
     * For items whose title did NOT match, why they're in the results — keyed
     * by item id ("Matched genre: Rock", "Matched cast: Ted Testa").
     */
    matchReasons: Record<string, string>;
}

export const searchService = {
    /**
     * Search across all libraries for media items matching the query.
     * Returns results grouped by library.
     */
    globalSearch: async (query: string, limit: number = 5): Promise<GlobalSearchResult[]> => {
        if (!query || query.length < 2) {
            return [];
        }
        const params = new URLSearchParams({
            query,
            limit: limit.toString(),
        });
        const response = await api.get<GlobalSearchResult[]>('/media/search', { params });
        return response.data;
    },
};
