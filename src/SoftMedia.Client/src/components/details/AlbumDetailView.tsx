import { Fragment, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { Play, Shuffle, Clock, Heart, Share2, Disc3, ListPlus } from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { useAudioStore } from '../../store/audioStore';
import { formatDuration, cn } from '../../lib/utils';
import { AddToPlaylistMenu } from '../playlists/AddToPlaylistMenu';

interface AlbumDetailViewProps {
    item: MediaItem;
}

export default function AlbumDetailView({ item }: AlbumDetailViewProps) {
    const { playPlaylist } = useAudioStore();
    const [searchParams] = useSearchParams();
    const queryClient = useQueryClient();
    const highlightTrackId = searchParams.get('highlight');

    // Track-row overflow menu — only one open at a time, keyed by track id.
    const [openMenuTrackId, setOpenMenuTrackId] = useState<string | null>(null);

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

                    {/* "Add album to playlist" — saves all tracks at once. */}
                    <div className="relative">
                        <button
                            type="button"
                            onClick={() => setOpenMenuTrackId(openMenuTrackId === '__album__' ? null : '__album__')}
                            className="group"
                            title="Add album to playlist"
                            aria-haspopup="menu"
                            aria-expanded={openMenuTrackId === '__album__'}
                        >
                            <div className="p-3 rounded-full bg-white/5 hover:bg-white/10 text-white transition-all group-hover:scale-110 active:scale-95 min-w-[44px] min-h-[44px] flex items-center justify-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400">
                                <ListPlus className="w-5 h-5" />
                            </div>
                        </button>
                        {openMenuTrackId === '__album__' && tracks && tracks.length > 0 && (
                            <AddToPlaylistMenu
                                mediaItemIds={tracks.map(t => t.id)}
                                onClose={() => setOpenMenuTrackId(null)}
                            />
                        )}
                    </div>

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
                            <th className="px-2 py-3 font-medium w-12" aria-label="Actions" />
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
                                                    colSpan={4}
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
                                            {/* `onClick` on the cell stops propagation so clicks inside
                                                the overflow button or the absolute-positioned popover
                                                don't bubble up to <tr> and trigger handlePlayTrack. */}
                                            <td
                                                className="px-2 py-2 text-right relative"
                                                onClick={(e) => e.stopPropagation()}
                                            >
                                                <button
                                                    type="button"
                                                    onClick={() =>
                                                        setOpenMenuTrackId(openMenuTrackId === track.id ? null : track.id)
                                                    }
                                                    aria-label={`Add ${track.title} to a playlist`}
                                                    aria-haspopup="menu"
                                                    aria-expanded={openMenuTrackId === track.id}
                                                    title="Add to playlist"
                                                    // Visible at low opacity by default so the affordance is
                                                    // discoverable without hovering. Brightens on row hover /
                                                    // keyboard focus per the universal-client a11y rule.
                                                    className="p-2 rounded-md text-gray-400 hover:text-white hover:bg-white/10 focus-visible:text-white focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors min-w-[44px] min-h-[44px] inline-flex items-center justify-center opacity-60 group-hover:opacity-100 group-focus-within:opacity-100"
                                                >
                                                    <ListPlus className="w-4 h-4" />
                                                </button>
                                                {openMenuTrackId === track.id && (
                                                    <AddToPlaylistMenu
                                                        mediaItemIds={[track.id]}
                                                        onClose={() => setOpenMenuTrackId(null)}
                                                    />
                                                )}
                                            </td>
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
