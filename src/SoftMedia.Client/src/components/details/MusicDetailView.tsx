import { type MediaItem } from '../../types';
import { Disc } from 'lucide-react';

interface MusicDetailViewProps {
    item: MediaItem;
}

export default function MusicDetailView({ item }: MusicDetailViewProps) {
    const metadata = item.metadata || {};
    const artist = metadata.artist as string;
    const album = metadata.album as string;
    const track = metadata.track as number;

    return (
        <div className="space-y-8">
            <div className="grid grid-cols-1 md:grid-cols-2 gap-8">
                {artist && (
                    <div className="flex items-center gap-4">
                        <div className="p-4 bg-purple-500/20 rounded-full">
                            <Disc className="w-8 h-8 text-purple-400" />
                        </div>
                        <div>
                            <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Artist</h3>
                            <p className="text-white text-xl font-bold">{artist}</p>
                        </div>
                    </div>
                )}
                {album && (
                    <div>
                        <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Album</h3>
                        <p className="text-white text-lg">{album}</p>
                    </div>
                )}
            </div>

            {track && (
                <div className="flex items-center gap-2 text-gray-400">
                    <span>Track {track}</span>
                </div>
            )}
        </div>
    );
}
