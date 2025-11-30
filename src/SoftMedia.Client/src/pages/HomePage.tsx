import { useLibraries } from '../hooks/useLibrary';
import HeroSection from '../components/ui/HeroSection';
import MediaRow from '../components/ui/MediaRow';
import { Link } from 'react-router-dom';
import { sampleMovies, sampleTVShows } from '../lib/sampleData';

export default function HomePage() {
    const { data: libraries, isLoading } = useLibraries();

    // Hero content using first item from sample data
    const heroItem = sampleMovies[0];

    return (
        <div className="pb-20 -mt-6">
            {/* Hero Section */}
            <HeroSection
                title={heroItem.title}
                description={heroItem.description || ''}
                imageUrl={heroItem.backdropPath || heroItem.posterPath || ''}
                year={heroItem.year}
                rating={heroItem.rating}
                duration={heroItem.duration}
                onPlay={() => window.location.href = `/media/${heroItem.id}`}
                onMoreInfo={() => console.log('More Info clicked')}
            />

            {/* Continue Watching */}
            <MediaRow
                title="Continue Watching"
                items={[sampleMovies[0], sampleTVShows[0], sampleMovies[2]]}
            />

            {/* Recently Added Movies */}
            <MediaRow
                title="Recently Added Movies"
                items={sampleMovies}
                viewAllLink="/movies"
            />

            {/* Recently Added TV Shows */}
            <MediaRow
                title="Recently Added TV Shows"
                items={sampleTVShows}
                viewAllLink="/tv"
            />

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
