import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { useLibraries, useLibraryRecent, useHeroItems } from '../hooks/useLibrary';
import api from '../services/api';
import HeroSection from '../components/ui/HeroSection';
import MediaRow from '../components/ui/MediaRow';
import { type Library, type MediaItem, MediaType } from '../types';

/**
 * Component to handle fetching and rendering a "Recently Added" row for a specific library.
 */
function LibraryRecentRow({
    library
}: {
    library: Library
}) {
    const { data: recentItems, isLoading } = useLibraryRecent(library.id);

    if (isLoading || !recentItems || recentItems.length === 0) return null;

    return (
        <MediaRow
            key={library.id}
            title={`Recently Added ${library.name}`}
            items={recentItems || []}
            viewAllLink={`/library/${library.id}`}
            libraryType={library.type}
        />
    );
}

const MEDIA_TYPE_ORDER: Library['type'][] = ['Movie', 'TV', 'Music', 'Book', 'Game'];

export default function HomePage() {
    const { data: libraries } = useLibraries();
    const { data: heroItems, isLoading: heroLoading } = useHeroItems();
    const navigate = useNavigate();

    // Determine which libraries to show (excluding Photos and unknown types)
    const sortedLibraries = useMemo(() => {
        return libraries
            ?.filter(l => l.type !== 'Photo' && MEDIA_TYPE_ORDER.includes(l.type))
            .sort((a, b) => {
                const typeDiff = MEDIA_TYPE_ORDER.indexOf(a.type) - MEDIA_TYPE_ORDER.indexOf(b.type);
                if (typeDiff !== 0) return typeDiff;
                return a.order - b.order;
            });
    }, [libraries]);

    const handlePlay = async (item: MediaItem) => {
        if (!item) return;

        if (item.type === MediaType.Series) { // Use MediaType enum
            try {
                const response = await api.get(`/series/${item.id}/next-episode`);
                const nextEpisode = response.data;
                navigate(`/play/${nextEpisode.episodeId}`);
            } catch (error) {
                console.error('[HomePage] Failed to fetch next episode for hero item:', error);
                navigate(`/media/${item.id}`);
            }
        } else {
            navigate(`/play/${item.id}`);
        }
    };

    const handleMoreInfo = (item: MediaItem) => {
        if (!item) return;
        navigate(`/media/${item.id}`);
    };

    return (
        <div className="pb-20">
            {/* Hero Section */}
            <HeroSection
                items={heroItems || []}
                isLoading={heroLoading}
                onPlay={handlePlay}
                onMoreInfo={handleMoreInfo}
            />

            {/* Dynamic Recently Added Rows per Library */}
            <div className="flex flex-col gap-8">
                {sortedLibraries?.map(library => (
                    <LibraryRecentRow
                        key={library.id}
                        library={library}
                    />
                ))}
            </div>
        </div>
    );
}
