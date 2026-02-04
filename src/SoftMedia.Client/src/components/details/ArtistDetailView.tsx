import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Play, Shuffle, Heart, Share2 } from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import MediaCard from '../items/MediaCard';
import { useAudioStore } from '../../store/audioStore';
import { cn } from '../../lib/utils';

interface ArtistDetailViewProps {
    item: MediaItem;
}

export default function ArtistDetailView({ item }: ArtistDetailViewProps) {
    const { playPlaylist } = useAudioStore();
    const queryClient = useQueryClient();

    const favoriteMutation = useMutation({
        mutationFn: (isFavorite: boolean) => api.post(`/interaction/${item.id}/favorite`, { isFavorite }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['media', item.id] });
            queryClient.invalidateQueries({ queryKey: ['library'] });
        }
    });

    // Fetch albums
    const { data: albums, isLoading: albumsLoading } = useQuery({
        queryKey: ['artist', item.id, 'albums'],
        queryFn: async () => {
            const response = await api.get<MediaItem[]>(`/libraries/artists/${item.id}/albums`);
            return response.data;
        }
    });

    // Fetch all tracks for Play All / Shuffle
    const { data: allTracks } = useQuery({
        queryKey: ['artist', item.id, 'all-tracks'],
        queryFn: async () => {
            // Fetch tracks from all albums
            if (!albums || albums.length === 0) return [];

            const allTrackPromises = albums.map(album =>
                api.get<MediaItem[]>(`/libraries/albums/${album.id}/tracks`)
            );
            const responses = await Promise.all(allTrackPromises);
            return responses.flatMap(r => r.data);
        },
        enabled: !!albums && albums.length > 0
    });

    const handlePlayAll = () => {
        if (allTracks && allTracks.length > 0) {
            playPlaylist(allTracks);
        }
    };

    const handleShuffle = () => {
        if (allTracks && allTracks.length > 0) {
            const shuffled = [...allTracks].sort(() => Math.random() - 0.5);
            playPlaylist(shuffled);
        }
    };

    if (albumsLoading) return <div className="text-gray-400">Loading albums...</div>;

    return (
        <div className="space-y-8">
            {/* Action Buttons */}
            <div className="flex items-center gap-3">
                <button
                    onClick={handlePlayAll}
                    disabled={!allTracks || allTracks.length === 0}
                    className="bg-gradient-to-r from-blue-600 to-violet-600 disabled:from-gray-600 disabled:to-gray-700 disabled:opacity-50 disabled:cursor-not-allowed text-white px-6 py-3 rounded-full font-bold flex items-center gap-2 transition-all hover:scale-[1.02] active:scale-95 shadow-lg shadow-violet-500/30"
                >
                    <Play className="w-5 h-5 fill-current" />
                    Play All
                </button>
                <button
                    onClick={handleShuffle}
                    disabled={!allTracks || allTracks.length === 0}
                    className="bg-white/10 hover:bg-white/20 disabled:opacity-50 disabled:cursor-not-allowed text-white px-6 py-3 rounded-full font-medium flex items-center gap-2 transition-colors border border-white/10"
                >
                    <Shuffle className="w-5 h-5" />
                    Shuffle
                </button>

                <div className="flex items-center gap-2 ml-2">
                    <button
                        onClick={() => favoriteMutation.mutate(!item.isFavorite)}
                        className="group"
                        title="Favorite"
                    >
                        <div className={cn(
                            "p-3 rounded-full transition-all group-hover:scale-110 active:scale-95",
                            item.isFavorite
                                ? "bg-red-500/20 text-red-500"
                                : "bg-white/5 hover:bg-white/10 text-white"
                        )}>
                            <Heart className={cn("w-5 h-5", item.isFavorite && "fill-current")} />
                        </div>
                    </button>

                    <button className="group" title="Share">
                        <div className="p-3 rounded-full bg-white/5 hover:bg-white/10 text-white transition-all group-hover:scale-110 active:scale-95">
                            <Share2 className="w-5 h-5" />
                        </div>
                    </button>
                </div>
            </div>

            {/* Albums Section */}
            <div>
                <h2 className="text-2xl font-bold text-white mb-4">Albums</h2>
                {albums && albums.length > 0 ? (
                    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-6">
                        {albums.map((album) => (
                            <MediaCard key={album.id} item={album} libraryType="Music" />
                        ))}
                    </div>
                ) : (
                    <p className="text-gray-500">No albums found.</p>
                )}
            </div>
        </div>
    );
}
