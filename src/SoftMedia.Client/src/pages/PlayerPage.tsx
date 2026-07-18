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

    // Immersive playback: the player owns the whole window (fixed inset-0 tracks the viewport
    // through resizes without viewport-unit quirks). The title/year live in the player's own
    // top overlay bar, fading with the controls, so nothing competes with the video.
    return (
        <div className="fixed inset-0 bg-black">
            <VideoPlayer item={item} src={streamUrl} />
        </div>
    );
}
