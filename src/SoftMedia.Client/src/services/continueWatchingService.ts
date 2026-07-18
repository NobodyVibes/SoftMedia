import api from './api';
import type { MediaItem } from '../types';

/**
 * Continue Watching — the calling user's in-progress Movies and TV shows, newest-first.
 *
 * A TV show appears as a single show card (not individual episodes); playing it resumes the
 * correct episode via the existing next-episode resolver. Finished movies and fully-watched
 * series are excluded server-side. Per-user, ACL- + rating-filtered. Each item carries
 * `progress`/`playbackPosition` so the card renders a resume bar.
 */
export const continueWatchingService = {
    list: async (limit: number = 20): Promise<MediaItem[]> => {
        const { data } = await api.get<MediaItem[]>('/continue-watching', { params: { limit } });
        return data;
    },
};
