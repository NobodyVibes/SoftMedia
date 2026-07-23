import { useQuery } from '@tanstack/react-query';
import api from '../services/api';
import { MediaType } from '../types';

export interface MediaExtra {
    index: number;
    title: string;
    kind: string;
    fileName: string;
    sizeBytes: number;
}

/**
 * NR-WI-014 — companion clips for a Movie/Series item, server-probed from the
 * filesystem. Shared by MediaDetailLayout (the promoted Trailer button) and
 * ExtrasSection (the bonus-content row); react-query dedupes the request.
 */
export function useExtras(mediaId: string, itemType: string | undefined) {
    return useQuery<MediaExtra[]>({
        queryKey: ['extras', mediaId],
        queryFn: async () => (await api.get<MediaExtra[]>(`/stream/${mediaId}/extras`)).data,
        staleTime: 60_000,
        enabled: itemType === MediaType.Movie || itemType === MediaType.Series,
    });
}

/**
 * The extra promoted to the detail page's Trailer button: the first trailer-kind
 * clip. Both the button and the Extras row derive it from the SAME list, so the
 * promoted clip never shows twice.
 */
export function findTrailer(extras: MediaExtra[] | undefined): MediaExtra | null {
    return extras?.find((e) => e.kind === 'trailer') ?? null;
}
