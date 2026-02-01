import { useNavigate } from 'react-router-dom';
import { useLibraries, useRecentMedia } from '../hooks/useLibrary';
import api from '../services/api';
import HeroSection from '../components/ui/HeroSection';
import MediaRow from '../components/ui/MediaRow';
import { type Library } from '../types';

/**
 * Internal component to handle fetching and rendering a "Recently Added" row for a specific media type.
 * This keeps the main HomePage clean and modular.
 */
function RecentlyAddedRow({
    type,
    libraries
}: {
    type: Library['type'],
    libraries: Library[]
}) {
    const { data: recentItems, isLoading } = useRecentMedia(10, type === 'TV' ? 'TV' : type);

    if (isLoading || !recentItems || recentItems.length === 0) return null;

    // Find the primary library of this type for the "View All" link
    const libraryId = libraries.find(l => l.type === type)?.id;

    // Customize title based on type
    const title = type === 'TV' ? 'Recently Added TV Shows' : `Recently Added ${type}s`;

    return (
        <MediaRow
            title={title}
            items={recentItems}
            libraryType={type}
            viewAllLink={libraryId ? `/libraries/${libraryId}` : undefined}
        />
    );
}

const MEDIA_TYPE_ORDER: Library['type'][] = ['Movie', 'TV', 'Music', 'Book', 'Game'];

export default function HomePage() {
    const { data: libraries } = useLibraries();
    const { data: recentMovies } = useRecentMedia(10, 'Movie');

    // Hero content: Use first recent movie as the primary highlight
    const heroItem = recentMovies?.[0];

    // Determine which media types we have libraries for (excluding Photos)
    const availableTypes = Array.from(new Set(
        libraries?.map(l => l.type).filter(type => type !== 'Photo' && MEDIA_TYPE_ORDER.includes(type))
    )).sort((a, b) => MEDIA_TYPE_ORDER.indexOf(a) - MEDIA_TYPE_ORDER.indexOf(b));

    const navigate = useNavigate();

    const handlePlay = async () => {
        if (!heroItem) return;

        if (heroItem.type === 1) { // 1 is Series in the MediaType enum from index.ts
            try {
                const response = await api.get(`/series/${heroItem.id}/next-episode`);
                const nextEpisode = response.data;
                navigate(`/play/${nextEpisode.episodeId}`);
            } catch (error) {
                console.error('[HomePage] Failed to fetch next episode for hero item:', error);
                navigate(`/media/${heroItem.id}`);
            }
        } else {
            navigate(`/play/${heroItem.id}`);
        }
    };

    const handleMoreInfo = () => {
        if (!heroItem) return;
        navigate(`/media/${heroItem.id}`);
    };

    return (
        <div className="pb-20">
            {/* Hero Section */}
            {heroItem && (
                <HeroSection
                    title={heroItem.title}
                    description={heroItem.description || ''}
                    imageUrl={heroItem.backdropPath || ''}
                    posterUrl={heroItem.posterPath || ''}
                    year={heroItem.year}
                    rating={heroItem.rating}
                    duration={heroItem.duration}
                    communityRating={heroItem.communityRating}
                    userRating={heroItem.userRating}
                    onPlay={handlePlay}
                    onMoreInfo={handleMoreInfo}
                />
            )}

            {/* Dynamic Recently Added Rows */}
            {libraries && availableTypes.map(type => (
                <RecentlyAddedRow
                    key={type}
                    type={type}
                    libraries={libraries}
                />
            ))}
        </div>
    );
}
