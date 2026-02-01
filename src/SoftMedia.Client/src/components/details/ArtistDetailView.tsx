import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import MediaCard from '../items/MediaCard';

interface ArtistDetailViewProps {
    item: MediaItem;
}

export default function ArtistDetailView({ item }: ArtistDetailViewProps) {
    const { data: albums, isLoading } = useQuery({
        queryKey: ['artist', item.id, 'albums'],
        queryFn: async () => {
            const response = await api.get<MediaItem[]>(`/libraries/artists/${item.id}/albums`);
            return response.data;
        }
    });

    if (isLoading) return <div className="text-gray-400">Loading albums...</div>;

    return (
        <div className="space-y-8">
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
