import { useLibraries, useRecentMedia } from '../hooks/useLibrary';
import HeroSection from '../components/ui/HeroSection';
import MediaRow from '../components/ui/MediaRow';
import { Link } from 'react-router-dom';

export default function HomePage() {
    const { data: libraries } = useLibraries();
    const { data: recentMovies } = useRecentMedia(10, 'Movie');
    const { data: recentTV } = useRecentMedia(10, 'TV');

    // Hero content: Use first recent movie, or fallback to first library item if needed
    const heroItem = recentMovies?.[0];

    // Helper to find library ID for "View All" links
    const getLibraryIdByType = (type: string) => libraries?.find(l => l.type === type)?.id;
    const movieLibraryId = getLibraryIdByType('Movie');
    const tvLibraryId = getLibraryIdByType('TV');

    return (
        <div className="pb-20 -mt-6">
            {/* Hero Section */}
            {heroItem && (
                <HeroSection
                    title={heroItem.title}
                    description={heroItem.description || ''}
                    imageUrl={heroItem.backdropPath || heroItem.posterPath || ''}
                    year={heroItem.year}
                    rating={heroItem.rating}
                    duration={heroItem.duration ? `${Math.floor(Number(heroItem.duration) / 60)}m` : undefined}
                    onPlay={() => window.location.href = `/media/${heroItem.id}`}
                    onMoreInfo={() => console.log('More Info clicked')}
                />
            )}

            {/* Recently Added Movies */}
            {recentMovies && recentMovies.length > 0 && (
                <MediaRow
                    title="Recently Added Movies"
                    items={recentMovies}
                    viewAllLink={movieLibraryId ? `/libraries/${movieLibraryId}` : undefined}
                />
            )}

            {/* Recently Added TV Shows */}
            {recentTV && recentTV.length > 0 && (
                <MediaRow
                    title="Recently Added TV Shows"
                    items={recentTV}
                    viewAllLink={tvLibraryId ? `/libraries/${tvLibraryId}` : undefined}
                />
            )}

            {/* Libraries Section */}
            {libraries && libraries.length > 0 && (
                <div className="px-6 mt-12">
                    <h2 className="text-2xl font-bold text-white mb-4">Your Libraries</h2>
                    <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 gap-4">
                        {libraries.map((lib) => (
                            <Link
                                key={lib.id}
                                to={`/libraries/${lib.id}`}
                                className="group p-6 rounded-xl bg-white/5 hover:bg-white/10 border border-white/10 hover:border-primary/50 transition-all"
                            >
                                <h3 className="text-lg font-bold text-white group-hover:text-primary transition-colors">{lib.name}</h3>
                                <p className="text-sm text-gray-400 mt-1">{lib.type}</p>
                            </Link>
                        ))}
                    </div>
                </div>
            )}
        </div>
    );
}
