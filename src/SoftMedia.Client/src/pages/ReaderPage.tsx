import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../services/api';
import type { MediaItem } from '../types';
import BookReader from '../components/reader/BookReader';

export default function ReaderPage() {
    const { id } = useParams<{ id: string }>();

    const { data: item, isLoading, error } = useQuery({
        queryKey: ['media', id],
        queryFn: async () => {
            const res = await api.get<MediaItem>(`/libraries/items/${id}`);
            return res.data;
        },
        enabled: !!id
    });

    if (isLoading) {
        return (
            <div className="h-screen w-screen flex items-center justify-center bg-gray-900 text-white">
                <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-white"></div>
            </div>
        );
    }

    if (error || !item) {
        return (
            <div className="h-screen w-screen flex items-center justify-center bg-gray-900 text-red-500">
                Failed to load book.
            </div>
        );
    }

    return <BookReader item={item} />;
}
