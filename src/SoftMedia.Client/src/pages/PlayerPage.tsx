import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import api from '../services/api';
import VideoPlayer from '../components/player/VideoPlayer';
import { type MediaItem } from '../types';

export default function PlayerPage() {
    const { id } = useParams<{ id: string }>();

    const { data: item, isLoading, error } = useQuery({
        queryKey: ['media', id],
        queryFn: async () => {
            const response = await api.get<MediaItem>(`/media/${id}`);
            return response.data;
        },
        enabled: !!id,
    });

    if (isLoading) {
        return <div className="flex justify-center items-center h-screen text-white">Loading...</div>;
    }

    if (error || !item) {
        return <div className="flex justify-center items-center h-screen text-red-500">Error loading media</div>;
    }

    const streamUrl = `/api/v1/stream/${id}`;

    return (
        <div className="min-h-screen bg-black flex flex-col items-center justify-center p-4">
            <div className="w-full mb-4">
                <h1 className="text-2xl font-bold text-white">{item.title}</h1>
                {item.year && <p className="text-gray-400">{item.year}</p>}
            </div>

            <VideoPlayer item={item} src={streamUrl} />
        </div>
    );
}
