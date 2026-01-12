import api from './api';

export interface GlobalSearchResult {
    libraryId: string;
    libraryName: string;
    libraryType: 'Movie' | 'TV' | 'Music' | 'Book' | 'Game' | 'Photo';
    items: import('../types').MediaItem[];
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
