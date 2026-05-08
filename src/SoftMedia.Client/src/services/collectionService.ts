import api from './api';
import type { MediaItem } from '../types';

/**
 * Wave E2 — collection service.
 *
 * Two flavours of collection share one type:
 *   - Auto-collections (isAuto: true) come from Wikidata via the OMDb IMDb-ID
 *     bridge. Read-only.
 *   - Manual collections (isAuto: false) are admin-curated.
 *
 * The by-movie endpoint is the data source for the "More from this collection"
 * strip on MovieDetailView. It returns 204 when the strip should not render
 * (no collection, or fewer than 2 visible siblings).
 */

export interface CollectionSummary {
    id: string;
    name: string;
    overview: string | null;
    posterUrl: string | null;
    isAuto: boolean;
    visibleItemCount: number;
}

export interface CollectionEntry {
    media: MediaItem;
    isCurrent: boolean;
}

export interface CollectionDetail {
    id: string;
    name: string;
    overview: string | null;
    posterUrl: string | null;
    isAuto: boolean;
    items: CollectionEntry[];
}

export const collectionService = {
    list: async (): Promise<CollectionSummary[]> => {
        const { data } = await api.get<CollectionSummary[]>('/collections');
        return data;
    },

    get: async (id: string): Promise<CollectionDetail> => {
        const { data } = await api.get<CollectionDetail>(`/collections/${id}`);
        return data;
    },

    /**
     * Returns the collection strip for the given movie. Returns null when
     * the API replies 204 (no collection, or strip threshold not met).
     */
    getByMovie: async (movieId: string): Promise<CollectionDetail | null> => {
        const response = await api.get<CollectionDetail>(`/collections/by-movie/${movieId}`, {
            // Treat 204 as "no strip" rather than an error.
            validateStatus: (s) => s === 200 || s === 204,
        });
        if (response.status === 204) return null;
        return response.data;
    },
};
