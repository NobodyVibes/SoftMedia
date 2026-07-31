import { type MediaItem } from '../../types';
import { Trophy, DollarSign, Film, Pen } from 'lucide-react';
import { useUIStore } from '../../store/uiStore';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';
import CastStripItem from './CastStripItem';
import CollectionStripSection from './CollectionStripSection';
import { ExtrasSection } from './ExtrasSection';

interface MovieDetailViewProps {
    item: MediaItem;
}

export default function MovieDetailView({ item }: MovieDetailViewProps) {
    const metadata = item.metadata || {};
    const { isSidebarCollapsed } = useUIStore();
    // The background poster URL embeds the media token (AA-WI-001) — re-render on rotation.
    useMediaTokenRefresh();

    const director = metadata.director as string;
    const writer = metadata.writer as string;
    // metadata is provider-supplied Record<string, unknown> — narrow before render.
    const studioRaw = metadata.studio ?? metadata.production;
    const studio = typeof studioRaw === 'string' ? studioRaw : null;
    const awards = metadata.awards as string;
    const boxOffice = metadata.boxOffice as string;

    // Get background poster (token-gated /cache path — attach the media token)
    const backgroundPosterRaw = item.posterPath || item.backdropPath || null;
    const backgroundPoster = backgroundPosterRaw ? attachAuthToApiUrl(backgroundPosterRaw) : null;

    return (
        <>
            {/* Full-page background poster: decorative, must stay BEHIND the page content.
                It renders inside the layout's `relative z-10` container, and a POSITIONED
                element with z-index:0 paints above its STATIC siblings — so at z-0 this
                "background" (and its two gradients) was painted over the hero poster and
                text, visibly washing them out. A negative z-index puts it back behind the
                in-flow content while staying above the page background. */}
            {backgroundPoster && (
                <div
                    className={`fixed top-16 right-0 bottom-0 z-[-1] pointer-events-none transition-all duration-300 ${isSidebarCollapsed ? 'left-20' : 'left-64'
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
                {item.cast && item.cast.length > 0 && (
                    <div>
                        <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                            <Film className="w-5 h-5 text-violet-400" />
                            Cast
                        </h2>
                        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-3 justify-items-center">
                            {item.cast.slice(0, 12).map((member) => (
                                <CastStripItem key={member.id} member={member} />
                            ))}
                        </div>
                    </div>
                )}

                {/* Wave E2 — "More from this collection" strip. The component
                    self-suppresses (renders nothing) when the API returns 204,
                    so the section only appears when there are ≥2 visible
                    siblings in the same franchise. */}
                <CollectionStripSection movieId={item.id} />

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

                {/* NR-WI-014 — bonus content beside the movie file (the trailer itself
                    is promoted to the Trailer button next to Play) */}
                <ExtrasSection mediaId={item.id} itemType={item.type} />
            </div>
        </>
    );
}

