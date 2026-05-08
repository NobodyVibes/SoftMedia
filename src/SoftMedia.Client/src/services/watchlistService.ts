import api from './api';
import type { MediaItem } from '../types';

/**
 * Wave E3 — watchlist API.
 *
 * The watchlist is a single boolean flag on the user-media interaction row,
 * deliberately works across every MediaType (movies, series, books, comics,
 * albums). Add/remove via the toggle endpoint; list via GET.
 *
 * Per-library ACL is honoured server-side: items the user can no longer see
 * are stripped from the GET response without affecting the underlying flag.
 */
export const watchlistService = {
    /** Toggle (add or remove) the calling user's watchlist flag for a media item. */
    toggle: async (mediaId: string, isWatchlisted: boolean): Promise<void> => {
        await api.post(`/interaction/${mediaId}/watchlist`, { isWatchlisted });
    },

    /** Returns the user's watchlist, newest-first, ACL-filtered. */
    list: async (limit: number = 50): Promise<MediaItem[]> => {
        const { data } = await api.get<MediaItem[]>('/watchlist', {
            params: { limit },
        });
        return data;
    },
};
