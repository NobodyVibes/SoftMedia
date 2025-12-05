import { useQuery } from '@tanstack/react-query';
import { Play, Clock } from 'lucide-react';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { useAudioStore } from '../../store/audioStore';
import { formatDuration } from '../../lib/utils';

interface AlbumDetailViewProps {
    item: MediaItem;
}

export default function AlbumDetailView({ item }: AlbumDetailViewProps) {
    const { playPlaylist } = useAudioStore();

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

    const handlePlayTrack = (track: MediaItem) => {
        if (tracks) {
            playPlaylist(tracks, track);
        }
    };

    if (isLoading) return <div className="text-gray-400">Loading tracks...</div>;

    return (
        <div className="space-y-8">
            <div className="flex items-center gap-4">
                <button
                    onClick={handlePlayAlbum}
                    className="bg-primary hover:bg-primary/80 text-white px-6 py-3 rounded-full font-medium flex items-center gap-2 transition-colors"
                >
                    <Play className="w-5 h-5 fill-current" />
                    Play Album
                </button>
            </div>

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
                        {tracks?.map((track) => (
                            <tr
                                key={track.id}
                                className="hover:bg-white/5 transition-colors cursor-pointer group"
                                onClick={() => handlePlayTrack(track)}
                            >
                                <td className="px-6 py-4 group-hover:text-primary transition-colors">
                                    <span className="group-hover:hidden">{track.trackNumber}</span>
                                    <Play className="w-4 h-4 hidden group-hover:block fill-current" />
                                </td>
                                <td className="px-6 py-4 text-white font-medium">{track.title}</td>
                                <td className="px-6 py-4 text-right">{formatDuration(Number(track.duration) || 0)}</td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
        </div>
    );
}
