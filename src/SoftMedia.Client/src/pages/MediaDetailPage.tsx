import { useParams, useNavigate } from 'react-router-dom';
import { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import api from '../services/api';
import { type MediaItem } from '../types';
import MediaDetailLayout from '../components/layout/MediaDetailLayout';
import MovieDetailView from '../components/details/MovieDetailView';
import TVDetailView from '../components/details/TVDetailView';
import MusicDetailView from '../components/details/MusicDetailView';
import ArtistDetailView from '../components/details/ArtistDetailView';
import AlbumDetailView from '../components/details/AlbumDetailView';
import BookDetailView from '../components/details/BookDetailView';
import GameDetailView from '../components/details/GameDetailView';
import PhotoDetailView from '../components/details/PhotoDetailView';
import { useAudioStore } from '../store/audioStore';
import { MediaType } from '../types';
import { useMediaHub } from '../hooks/useMediaHub';

export default function MediaDetailPage() {
    const { id } = useParams<{ id: string }>();

    // SignalR real-time updates - refreshes when images are cached
    useMediaHub({ mediaId: id });

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

    // Determine Library Type (Need to fetch library or infer from item if available)
    // The backend MediaItem doesn't strictly have 'type' on it directly, but it has LibraryId.
    // Ideally, the backend should return the type or we fetch the library.
    // However, for now, let's assume we can infer it or the backend adds it.
    // Wait, the MediaItem interface in frontend has `libraryId`.
    // We might need to fetch the library to know the type, OR update the backend to send `LibraryType` in MediaItemDto.
    // Let's check MediaItemDto again. It doesn't have Type.
    // BUT, we can use the `Container` or `Metadata` to guess, or better yet, fetch the library.

    // Actually, for this task, I'll fetch the library details if needed, but that's an extra call.
    // A better approach is to add `LibraryType` to MediaItemDto.
    // But I can't change backend right now easily without restarting.
    // Let's see if I can infer it.
    // Movies have 'video', TV has 'video'.
    // Music has 'audio'.
    // Books have 'book'.
    // Games have 'game'.
    // Photos have 'image'.

    // Let's use a helper to guess type or fetch library.
    // Actually, `MediaItem` has `libraryId`. I can use `useQuery` to fetch library.

    return <MediaDetailPageContent item={item} />;
}

function MediaDetailPageContent({ item }: { item: MediaItem }) {
    const navigate = useNavigate();
    const { playTrack } = useAudioStore();

    // Fetch library to get type
    const { data: library } = useQuery({
        queryKey: ['library', item.libraryId],
        queryFn: async () => {
            const response = await api.get(`/libraries/${item.libraryId}`);
            return response.data;
        },
        enabled: !!item.libraryId
    });

    const type = library?.type;

    const handlePlay = async () => {
        if (type === 'Music') {
            playTrack(item);
        } else if (type === 'Book') {
            navigate(`/read/${item.id}`);
        } else if (type === 'TV') {
            // For TV shows, fetch the next episode to watch based on watch history
            try {
                const response = await api.get<{
                    episodeId: string;
                    resumePosition: number;
                    isSeriesComplete: boolean;
                }>(`/series/${item.id}/next-episode`);

                const { episodeId, resumePosition } = response.data;

                // Navigate to the episode with resume position
                // If resumePosition is 0, no need to add query param
                if (resumePosition > 0) {
                    navigate(`/play/${episodeId}?start=${resumePosition}`);
                } else {
                    navigate(`/play/${episodeId}`);
                }
            } catch (error) {
                console.error('Failed to fetch next episode:', error);
                // Fallback: just navigate to the series page (though this won't work)
                // In a production app, we'd show a toast notification
                navigate(`/play/${item.id}`);
            }
        } else {
            navigate(`/play/${item.id}`);
        }
    };


    // State for overriding quality info (e.g. for specific episodes)
    const [qualityItem, setQualityItem] = useState<MediaItem | null>(null);
    // Separate state for visual selection of episode card
    const [selectedEpisodeId, setSelectedEpisodeId] = useState<string | null>(null);

    // Reset quality item and selection when main item changes
    useEffect(() => {
        setQualityItem(null);
        setSelectedEpisodeId(null);
    }, [item.id]);

    const handleEpisodeSelect = (episode: MediaItem) => {
        setQualityItem(episode);
        setSelectedEpisodeId(episode.id);
    };

    const handleDefaultQualityFound = (episode: MediaItem) => {
        // Only set the quality item if one isn't already selected
        // ensuring we don't override user selection if they somehow selected faster
        // AND don't set selectedEpisodeId so no card is highlighted
        setQualityItem(prev => prev ? prev : episode);
    };

    const renderContent = () => {
        if (!type) return null;

        // Use item.type directly if available (it should be from backend)
        // If item.type is defined, use it. Otherwise fallback to library type.
        // Note: item.type is an enum number in backend, mapped to number in frontend.

        // Map Library Type string to MediaType enum for fallback
        // Actually, let's just use item.type if it matches our expectations.

        if (item.type === MediaType.Artist) return <ArtistDetailView item={item} />;
        if (item.type === MediaType.Album) return <AlbumDetailView item={item} />;

        // Fallback or other types
        switch (type) {
            case 'Movie': return <MovieDetailView item={item} />;
            case 'TV':
                return (
                    <TVDetailView
                        item={item}
                        selectedEpisodeId={selectedEpisodeId}
                        onEpisodeSelect={handleEpisodeSelect}
                        onDefaultQualityItemFound={handleDefaultQualityFound}
                    />
                );
            case 'Music':
                // If it's a track (Audio), show MusicDetailView (or maybe AlbumDetailView?)
                // MusicDetailView was likely for individual tracks or generic music.
                return <MusicDetailView item={item} />;
            case 'Book': return <BookDetailView item={item} />;
            case 'Game': return <GameDetailView item={item} />;
            case 'Photo': return <PhotoDetailView item={item} />;
            default: return null;
        }
    };

    return (
        <MediaDetailLayout item={item} onPlay={handlePlay} qualityItem={qualityItem}>
            {renderContent()}
        </MediaDetailLayout>
    );
}
