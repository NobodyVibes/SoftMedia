import { type MediaItem } from '../../types';

interface MovieDetailViewProps {
    item: MediaItem;
}

export default function MovieDetailView({ item }: MovieDetailViewProps) {
    const metadata = item.metadata || {};
    const cast = (metadata.cast as string[]) || [];
    const director = metadata.director as string;
    const studio = metadata.studio as string;

    return (
        <div className="space-y-8">
            {/* Cast Grid */}
            {cast.length > 0 && (
                <div>
                    <h2 className="text-2xl font-bold text-white mb-4">Cast</h2>
                    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4">
                        {cast.map((actor, index) => (
                            <div key={index} className="bg-white/5 p-3 rounded-lg hover:bg-white/10 transition-colors">
                                <div className="font-medium text-gray-200">{actor}</div>
                            </div>
                        ))}
                    </div>
                </div>
            )}

            {/* Details Grid */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-8">
                {director && (
                    <div>
                        <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Director</h3>
                        <p className="text-white text-lg">{director}</p>
                    </div>
                )}
                {studio && (
                    <div>
                        <h3 className="text-gray-400 text-sm uppercase tracking-wider mb-1">Studio</h3>
                        <p className="text-white text-lg">{studio}</p>
                    </div>
                )}
            </div>
        </div>
    );
}
