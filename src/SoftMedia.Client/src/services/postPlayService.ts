import api from './api';
import type { MediaItem } from '../types';

/**
 * End-of-movie recommendations for the player's post-play overlay.
 *
 * `collectionItems` are unfinished movies from the same collection, ordered so the film
 * released after the finished one leads (marathon-friendly); `similarItems` are genre
 * matches from the user's visible libraries. Both exclude movies already finished.
 */
export interface PostPlayInfo {
    collectionName?: string | null;
    collectionItems: MediaItem[];
    similarItems: MediaItem[];
}

export const postPlayService = {
    forMovie: async (movieId: string): Promise<PostPlayInfo> => {
        const { data } = await api.get<PostPlayInfo>(`/movie/${movieId}/post-play`);
        return data;
    },
};
