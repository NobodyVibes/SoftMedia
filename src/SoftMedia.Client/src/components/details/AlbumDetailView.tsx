import { Fragment } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { Play, Shuffle, Clock, Heart, Share2, Disc3 } from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { useAudioStore } from '../../store/audioStore';
import { formatDuration, cn } from '../../lib/utils';

interface AlbumDetailViewProps {
    item: MediaItem;
}

export default function AlbumDetailView({ item }: AlbumDetailViewProps) {
    const { playPlaylist } = useAudioStore();
    const [searchParams] = useSearchParams();
    const queryClient = useQueryClient();
    const highlightTrackId = searchParams.get('highlight');

    const favoriteMutation = useMutation({
        mutationFn: (isFavorite: boolean) => api.post(`/interaction/${item.id}/favorite`, { isFavorite }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['media', item.id] });
            queryClient.invalidateQueries({ queryKey: ['library'] });
        }
    });

    const { data: tracks, isLoading } = useQuery({
        queryKey: ['album', item.id, 'tracks'],
        queryFn: async () => {
            const response = await api.get<MediaItem[]>(`/libraries/albums/${item.id}/tracks`);
            return response.data;
        }
    });

    const handlePlayAlbum = () => {
        if (tracks && tracks.length > 0) {
            playPlaylist(tracks);
        }
    };

    const handleShuffleAlbum = () => {
        if (tracks && tracks.length > 0) {
            // Create shuffled copy
            const shuffled = [...tracks].sort(() => Math.random() - 0.5);
            playPlaylist(shuffled);
        }
    };

    const handlePlayTrack = (track: MediaItem) => {
        if (tracks) {
            playPlaylist(tracks, track);
        }
    };

    if (isLoading) return <div className="text-gray-400">Loading tracks...</div>;

    return (
        <div className="space-y-8">
            {/* Action Buttons */}
            <div className="flex items-center gap-3">
                <button
                    onClick={handlePlayAlbum}
                    disabled={!tracks || tracks.length === 0}
                    className="bg-gradient-to-r from-blue-600 to-violet-600 disabled:from-gray-600 disabled:to-gray-700 disabled:opacity-50 disabled:cursor-not-allowed text-white px-6 py-3 rounded-full font-bold flex items-center gap-2 transition-all hover:scale-[1.02] active:scale-95 shadow-lg shadow-violet-500/30"
                >
                    <Play className="w-5 h-5 fill-current" />
                    Play All
                </button>
                <button
                    onClick={handleShuffleAlbum}
                    className="bg-white/10 hover:bg-white/20 text-white px-6 py-3 rounded-full font-medium flex items-center gap-2 transition-colors border border-white/10"
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

            {/* Track List */}
            <div className="bg-white/5 border border-white/10 rounded-xl overflow-hidden">
                <table className="w-full text-left text-sm text-gray-400">
                    <thead className="bg-white/5 text-gray-200">
                        <tr>
                            <th className="px-6 py-3 font-medium w-12">#</th>
                            <th className="px-6 py-3 font-medium">Title</th>
                            <th className="px-6 py-3 font-medium text-right w-24"><Clock className="w-4 h-4 ml-auto" /></th>
                        </tr>
                    </thead>
                    <tbody className="divide-y divide-white/5">
                        {(() => {
                            const distinctDiscs = new Set(
                                (tracks ?? []).map(t => t.discNumber ?? 1)
                            );
                            const showDiscHeaders = distinctDiscs.size > 1;
                            let lastDisc: number | null = null;

                            return tracks?.map((track) => {
                                const isHighlighted = track.id === highlightTrackId;
                                const disc = track.discNumber ?? 1;
                                const showHeader = showDiscHeaders && disc !== lastDisc;
                                lastDisc = disc;
                                return (
                                    <Fragment key={track.id}>
                                        {showHeader && (
                                            <tr className="bg-white/[0.07] border-t border-white/10">
                                                <td
                                                    colSpan={3}
                                                    className="px-6 py-3 text-xs font-semibold uppercase tracking-wider"
                                                >
                                                    <span className="inline-flex items-center gap-2 bg-gradient-to-r from-blue-400 to-violet-400 bg-clip-text text-transparent">
                                                        <Disc3 className="w-4 h-4 text-violet-400" />
                                                        Disc {disc}
                                                    </span>
                                                </td>
                                            </tr>
                                        )}
                                        <tr
                                            className={cn(
                                                "hover:bg-white/5 focus-visible:bg-white/5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-400 transition-colors cursor-pointer group",
                                                isHighlighted && "bg-primary/10 border-l-2 border-primary"
                                            )}
                                            onClick={() => handlePlayTrack(track)}
                                        >
                                            <td className={cn(
                                                "px-6 py-4 group-hover:text-primary transition-colors",
                                                isHighlighted && "text-primary"
                                            )}>
                                                <span className="group-hover:hidden">{track.trackNumber}</span>
                                                <Play className="w-4 h-4 hidden group-hover:block fill-current" />
                                            </td>
                                            <td className={cn(
                                                "px-6 py-4 font-medium",
                                                isHighlighted ? "text-primary" : "text-white"
                                            )}>{track.title}</td>
                                            <td className="px-6 py-4 text-right">{formatDuration(track.durationSeconds || 0)}</td>
                                        </tr>
                                    </Fragment>
                                );
                            });
                        })()}
                    </tbody>
                </table>
            </div>
        </div>
    );
}
