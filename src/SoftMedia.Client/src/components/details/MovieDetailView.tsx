import { type MediaItem } from '../../types';
import { Trophy, DollarSign, Film, Pen } from 'lucide-react';
import { useUIStore } from '../../store/uiStore';

interface MovieDetailViewProps {
    item: MediaItem;
}

export default function MovieDetailView({ item }: MovieDetailViewProps) {
    const metadata = item.metadata || {};
    const { isSidebarCollapsed } = useUIStore();

    // Extract metadata fields
    const cast = (metadata.cast as any[]) || [];
    const director = metadata.director as string;
    const writer = metadata.writer as string;
    const studio = metadata.studio || metadata.production as string;
    const awards = metadata.awards as string;
    const boxOffice = metadata.boxOffice as string;

    // Get background poster
    const backgroundPoster = item.posterPath || item.backdropPath || null;

    return (
        <>
            {/* Full-page background poster overlay */}
            {backgroundPoster && (
                <div
                    className={`fixed top-16 right-0 bottom-0 z-0 pointer-events-none transition-all duration-300 ${isSidebarCollapsed ? 'left-20' : 'left-64'
                        }`}
                >
                    <img
                        src={backgroundPoster}
                        alt=""
                        className="w-full h-full object-cover opacity-15 blur-sm transition-all duration-500"
                    />
                    <div className="absolute inset-0 bg-gradient-to-b from-transparent via-background/80 to-background" />
                    <div className="absolute inset-0 bg-gradient-to-r from-background/60 via-transparent to-background/60" />
                </div>
            )}

            <div className="space-y-8 relative z-10">

                {/* Cast Grid */}
                {cast.length > 0 && (
                    <div>
                        <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                            <Film className="w-5 h-5 text-violet-400" />
                            Cast
                        </h2>
                        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-3">
                            {cast.slice(0, 12).map((actor: any, index: number) => {
                                const name = typeof actor === 'string' ? actor : actor.name;
                                const character = typeof actor === 'string' ? null : actor.character;
                                return (
                                    <div
                                        key={index}
                                        className="bg-white/5 p-3 rounded-xl border border-white/10 hover:border-violet-500/30 hover:bg-white/10 transition-all flex flex-col justify-center"
                                    >
                                        <div className="font-medium text-gray-200 text-sm line-clamp-1" title={name}>{name}</div>
                                        {character && <div className="text-xs text-gray-400 mt-1 line-clamp-1" title={character}>{character}</div>}
                                    </div>
                                );
                            })}
                        </div>
                    </div>
                )}

                {/* Crew & Details Grid */}
                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                    {director && (
                        <div className="bg-white/5 p-4 rounded-xl border border-white/10">
                            <h3 className="text-gray-400 text-xs uppercase tracking-wider mb-2 flex items-center gap-2">
                                <Film className="w-4 h-4" />
                                Director
                            </h3>
                            <p className="text-white text-lg font-medium">{director}</p>
                        </div>
                    )}
                    {writer && (
                        <div className="bg-white/5 p-4 rounded-xl border border-white/10">
                            <h3 className="text-gray-400 text-xs uppercase tracking-wider mb-2 flex items-center gap-2">
                                <Pen className="w-4 h-4" />
                                Writer
                            </h3>
                            <p className="text-white text-lg font-medium">{writer}</p>
                        </div>
                    )}
                    {studio && (
                        <div className="bg-white/5 p-4 rounded-xl border border-white/10">
                            <h3 className="text-gray-400 text-xs uppercase tracking-wider mb-2">Studio</h3>
                            <p className="text-white text-lg font-medium">{studio}</p>
                        </div>
                    )}
                </div>

                {/* Awards & Box Office */}
                {(awards || boxOffice) && (
                    <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                        {awards && awards !== 'N/A' && (
                            <div className="bg-gradient-to-br from-amber-500/10 to-orange-500/10 p-5 rounded-xl border border-amber-500/20">
                                <h3 className="text-amber-400 text-sm uppercase tracking-wider mb-2 flex items-center gap-2">
                                    <Trophy className="w-4 h-4" />
                                    Awards
                                </h3>
                                <p className="text-white text-base">{awards}</p>
                            </div>
                        )}
                        {boxOffice && boxOffice !== 'N/A' && (
                            <div className="bg-gradient-to-br from-green-500/10 to-emerald-500/10 p-5 rounded-xl border border-green-500/20">
                                <h3 className="text-green-400 text-sm uppercase tracking-wider mb-2 flex items-center gap-2">
                                    <DollarSign className="w-4 h-4" />
                                    Box Office
                                </h3>
                                <p className="text-white text-2xl font-bold">{boxOffice}</p>
                            </div>
                        )}
                    </div>
                )}
            </div>
        </>
    );
}

