import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { Play, Shuffle, Clock, User } from 'lucide-react';
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
    const highlightTrackId = searchParams.get('highlight');

    const { data: tracks, isLoading } = useQuery({
        queryKey: ['album', item.id, 'tracks'],
        queryFn: async () => {
            const response = await api.get<MediaItem[]>(`/libraries/albums/${item.id}/tracks`);
            return response.data;
        }
    });

    // Get artist name from metadata or first track
    const artistName = (item.metadata?.artist as string) || tracks?.[0]?.metadata?.artist as string;

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

    // Calculate total duration using durationSeconds (raw seconds)
    const totalDuration = tracks?.reduce((acc, track) => acc + (track.durationSeconds || 0), 0) || 0;

    if (isLoading) return <div className="text-gray-400">Loading tracks...</div>;

    return (
        <div className="space-y-8">
            {/* Album Info Row */}
            <div className="flex flex-wrap items-center gap-4">
                {/* Artist Link */}
                {item.artistId && artistName && (
                    <Link
                        to={`/media/${item.artistId}`}
                        className="flex items-center gap-2 text-gray-400 hover:text-white transition-colors group"
                    >
                        <User className="w-4 h-4 group-hover:text-primary" />
                        <span className="font-medium group-hover:underline">{artistName}</span>
                    </Link>
                )}

                {/* Track Count & Duration */}
                <div className="text-sm text-gray-500">
                    {tracks?.length || 0} tracks • {formatDuration(totalDuration)}
                </div>
            </div>

            {/* Action Buttons */}
            <div className="flex items-center gap-3">
                <button
                    onClick={handlePlayAlbum}
                    className="bg-primary hover:bg-primary/80 text-white px-6 py-3 rounded-full font-medium flex items-center gap-2 transition-colors"
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
                        {tracks?.map((track) => {
                            const isHighlighted = track.id === highlightTrackId;
                            return (
                                <tr
                                    key={track.id}
                                    className={cn(
                                        "hover:bg-white/5 transition-colors cursor-pointer group",
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
                            );
                        })}
                    </tbody>
                </table>
            </div>
        </div>
    );
}
