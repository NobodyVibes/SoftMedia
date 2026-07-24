import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import api from '../services/api';
import VideoPlayer from '../components/player/VideoPlayer';
import { type MediaItem } from '../types';

export default function PlayerPage() {
    const { id } = useParams<{ id: string }>();

    const { data: item, isLoading, error, refetch, isRefetching } = useQuery({
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
        // SR-WI-052: "Error loading media" told the user nothing. A 404 means the item is
        // gone (removed by a scan, bad link) — retrying can't help, so offer a way out
        // instead. Anything else (server hiccup, network) gets a Retry.
        const notFound = axios.isAxiosError(error) && error.response?.status === 404;

        return (
            <div className="fixed inset-0 bg-black flex flex-col justify-center items-center text-center px-6">
                {notFound ? (
                    <>
                        <p className="text-white text-xl font-semibold mb-2">This item could not be found</p>
                        <p className="text-gray-400 text-sm mb-6">It may have been removed from the library.</p>
                        <Link
                            to="/"
                            className="px-4 py-2 rounded-lg bg-white/10 hover:bg-white/20 text-white text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            Go Home
                        </Link>
                    </>
                ) : (
                    <>
                        <p className="text-white text-xl font-semibold mb-2">Couldn't load this video</p>
                        <p className="text-gray-400 text-sm mb-6">Something went wrong on the way to the server. Check your connection, then try again.</p>
                        <div className="flex items-center gap-3">
                            <button
                                type="button"
                                onClick={() => refetch()}
                                disabled={isRefetching}
                                className="px-4 py-2 rounded-lg bg-white text-black text-sm font-medium hover:bg-gray-200 disabled:opacity-60 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                {isRefetching ? 'Retrying…' : 'Retry'}
                            </button>
                            <Link
                                to="/"
                                className="px-4 py-2 rounded-lg bg-white/10 hover:bg-white/20 text-white text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                Go Home
                            </Link>
                        </div>
                    </>
                )}
            </div>
        );
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
