import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../services/api';
import type { MediaItem } from '../types';
import BookReader from '../components/reader/BookReader';

export default function ReaderPage() {
    const { id } = useParams<{ id: string }>();

    // The media item is stable for the duration of a reading session. Disable
    // background refetches so a concurrent library rescan (which can briefly
    // orphan-delete-and-reinsert the row) can't yank the reader into an error
    // state while the user is mid-page.
    const { data: item, isLoading, error } = useQuery({
        queryKey: ['media', id],
        queryFn: async () => {
            // Must match the endpoint used by MediaDetailPage so the shared cache
            // key resolves correctly on direct navigation (e.g. from a comic issue
            // list, where there's no pre-populated cache entry).
            const res = await api.get<MediaItem>(`/media/${id}`);
            return res.data;
        },
        enabled: !!id,
        staleTime: Infinity,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        retry: 1,
    });

    if (isLoading) {
        return (
            <div className="h-screen w-screen flex items-center justify-center bg-gray-900 text-white">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-white"></div>
            </div>
        );
    }

    // Once `item` has loaded, keep rendering it even if a later refetch errors
    // (shouldn't happen with the options above, but belt-and-braces).
    if (!item) {
        return (
            <div className="h-screen w-screen flex items-center justify-center bg-gray-900 text-red-500">
                {error ? 'Failed to load book.' : 'Book not found.'}
            </div>
        );
    }

    return <BookReader item={item} />;
}
