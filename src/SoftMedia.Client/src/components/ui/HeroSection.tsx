import { Play, Info, Star } from 'lucide-react';
import { motion, AnimatePresence } from 'framer-motion';
import { useState, useEffect, useMemo } from 'react';
import { type MediaItem, MediaType } from '../../types';
import { formatRuntime } from '../../lib/utils';
import { resolveBackdropUrl, resolveHeroPosterUrl } from '../../lib/mediaImageUrl';
import { useAuthStore } from '../../store/authStore';

interface HeroSectionProps {
    items: MediaItem[];
    isLoading?: boolean;
    onPlay?: (item: MediaItem) => void;
    onMoreInfo?: (item: MediaItem) => void;
}

export default function HeroSection({
    items,
    isLoading,
    onPlay,
    onMoreInfo
}: HeroSectionProps) {
    // Hooks must run on EVERY render, before any early return — the loading and
    // empty branches below used to sit above them, so the first render after
    // `isLoading` flipped changed the hook count and React threw.
    const [currentIndex, setCurrentIndex] = useState(0);

    // The media token is what `resolve*Url` embeds in `/api/v1/*` image URLs
    // (browsers can't send an Authorization header on a background-image or
    // <img> load). Subscribe so a token rotation re-renders and rebuilds the
    // URLs — otherwise the hero keeps a stale token and 401s until reload.
    const mediaToken = useAuthStore((s) => s.mediaToken);

    const count = items?.length ?? 0;

    useEffect(() => {
        if (count <= 1) return;

        const interval = setInterval(() => {
            setCurrentIndex((prev) => (prev + 1) % count);
        }, 10000); // Cycle every 10 seconds

        return () => clearInterval(interval);
    }, [count]); // Remove currentIndex from dependencies

    // A shorter items array (library removed, hero cache refreshed) would otherwise
    // leave the index pointing past the end and blank the whole section.
    const currentIdx = count > 0 ? currentIndex % count : 0;
    const currentItem: MediaItem | undefined = count > 0 ? items[currentIdx] : undefined;

    const rawBackdrop = currentItem?.backdropPath;
    const rawPoster = currentItem?.posterPath;

    // Backdrops stay remote (only posters are downloaded to /cache), so they
    // resolve to the authenticated /api/v1/image/proxy route; album covers and
    // photos resolve to their own /api/v1 endpoints. Every one of those needs the
    // query-string token attached — which is why only series, whose local /cache
    // poster needs no auth, used to render here.
    const { imageUrl, posterUrl, hasBackdrop } = useMemo(() => {
        const backdropOk = !!rawBackdrop && !rawBackdrop.includes('poster');
        const poster = resolveHeroPosterUrl(rawPoster) || '';
        return {
            imageUrl: (backdropOk ? resolveBackdropUrl(rawBackdrop) : poster) || poster,
            posterUrl: poster,
            hasBackdrop: backdropOk,
        };
        // mediaToken is a real dependency: it is baked into the URLs above.
    }, [rawBackdrop, rawPoster, mediaToken]);

    if (isLoading) {
        return (
            <div className="relative w-full h-[500px] mb-12 overflow-hidden bg-background/50 animate-pulse border-b border-white/5 flex items-center justify-center">
                <div className="w-full max-w-7xl px-12 flex flex-col gap-6">
                    <div className="h-6 w-48 bg-white/5 rounded-md" />
                    <div className="h-20 w-3/4 bg-white/10 rounded-xl" />
                    <div className="h-24 w-1/2 bg-white/5 rounded-lg" />
                    <div className="flex gap-4">
                        <div className="h-12 w-40 bg-white/20 rounded-lg" />
                        <div className="h-12 w-40 bg-white/10 rounded-lg" />
                    </div>
                </div>
            </div>
        );
    }

    if (!items || items.length === 0) {
        return (
            <div className="relative w-full h-[500px] mb-12 overflow-hidden bg-gradient-to-br from-violet-950/40 via-background to-background flex items-center justify-center border-b border-white/5">
                <div className="absolute inset-0 bg-[radial-gradient(circle_at_center,_var(--tw-gradient-stops))] from-violet-500/10 via-transparent to-transparent opacity-50" />
                <div className="relative z-10 flex flex-col items-center text-center gap-6 px-4">
                    <div className="w-24 h-24 rounded-3xl bg-white/5 border border-white/10 backdrop-blur-xl flex items-center justify-center shadow-2xl rotate-3 transform-gpu">
                        <Play className="text-violet-400 opacity-40 translate-x-1" size={40} fill="currentColor" />
                    </div>
                    <div className="space-y-3">
                        <h2 className="text-3xl font-black text-white/40 tracking-tight">Your Media Universe</h2>
                        <p className="text-white/20 max-w-md mx-auto leading-relaxed">
                            Connect your libraries and run a scan to see your collections featured here in stunning detail.
                        </p>
                    </div>
                </div>
            </div>
        );
    }

    if (!currentItem) return null;

    const showPosterCard = !hasBackdrop && !!posterUrl;

    // Music art is a square cover, not a 2:3 poster — cropping an album sleeve to
    // poster shape lops off the top and bottom of the artwork. Same type set as
    // the detail-page sidebar (MediaDetailLayout) and MediaCard.
    const isSquareArt =
        currentItem.type === MediaType.Album ||
        currentItem.type === MediaType.Artist ||
        currentItem.type === MediaType.Audio ||
        currentItem.type === MediaType.Track;

    const title = currentItem.title;
    const description = currentItem.description || '';
    const year = currentItem.year;
    const rating = currentItem.rating;
    const duration = formatRuntime(currentItem.durationSeconds);
    const communityRating = currentItem.communityRating;
    const userRating = currentItem.userRating;

    return (
        <div className="relative w-full h-[500px] mb-12 overflow-visible group">
            <AnimatePresence mode="wait">
                <motion.div
                    key={currentItem.id}
                    className="absolute inset-0 overflow-hidden"
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    exit={{ opacity: 0 }}
                    transition={{ duration: 1 }}
                    style={{ willChange: 'opacity' }}
                >
                    {/* Background Layer */}
                    <motion.div
                        className={`absolute inset-0 bg-cover bg-center scale-105 ${!hasBackdrop ? 'blur-3xl opacity-40' : ''}`}
                        style={imageUrl ? { backgroundImage: `url("${imageUrl}")` } : undefined}
                        initial={{ scale: 1.05 }}
                        animate={{ scale: 1.08 }}
                        transition={{ duration: 10, repeat: Infinity, repeatType: "reverse" }}
                    />

                    {/* Multi-layer Gradient Overlays */}
                    <div className="absolute inset-0 bg-gradient-to-t from-background via-background/80 to-transparent pointer-events-none" />
                    <div className="absolute inset-0 bg-gradient-to-r from-background via-background/40 to-transparent pointer-events-none" />

                    {/* Content Container */}
                    <div className="absolute inset-0 flex items-center px-12 pb-8">
                        <div className="flex gap-12 items-end w-full max-w-7xl">
                            {/* Sharp Poster Card Overlay */}
                            {showPosterCard && (
                                <motion.div
                                    className={`hidden md:block w-64 ${isSquareArt ? 'aspect-square' : 'aspect-[2/3]'} shrink-0 rounded-2xl overflow-hidden shadow-2xl border border-white/10 relative z-10`}
                                    initial={{ opacity: 0, x: -20 }}
                                    animate={{ opacity: 1, x: 0 }}
                                    transition={{ delay: 0.2 }}
                                >
                                    <img src={posterUrl} alt={title} className="w-full h-full object-cover" />
                                    <div className="absolute inset-0 ring-1 ring-inset ring-white/20 rounded-2xl" />
                                </motion.div>
                            )}

                            <div className="flex flex-col justify-end flex-1 pb-4 min-w-0">
                                <div className="max-w-2xl">
                                    {/* Metadata Pills - Standardized Height */}
                                    <motion.div
                                        className="flex items-center gap-4 h-8 mb-2 pt-8"
                                        initial={{ opacity: 0, y: 20 }}
                                        animate={{ opacity: 1, y: 0 }}
                                        transition={{ delay: 0.2 }}
                                        style={{ willChange: 'transform, opacity', transform: 'translateZ(0)' }}
                                    >
                                        {year && (
                                            <span className="text-white/80 text-lg font-semibold tracking-wide">
                                                {year}
                                            </span>
                                        )}

                                        <div className="flex items-center gap-3">
                                            {communityRating && communityRating > 0 && (
                                                <div className="flex items-center gap-1.5 px-2 py-1 bg-black/30 backdrop-blur-md border border-white/10 rounded-lg text-yellow-400">
                                                    <Star size={16} fill="currentColor" />
                                                    <span className="text-white text-sm font-bold">
                                                        <span className="text-gray-400 font-medium mr-1.5 uppercase tracking-tighter text-xs">
                                                            {currentItem.type === MediaType.Series ||
                                                                currentItem.type === MediaType.Episode ||
                                                                currentItem.type === MediaType.Season
                                                                ? 'TVMaze'
                                                                : 'IMDB'}
                                                        </span>
                                                        {communityRating.toFixed(1)}
                                                    </span>
                                                </div>
                                            )}

                                            {userRating && userRating > 0 && (
                                                <div className="flex items-center gap-1.5 px-2 py-1 bg-violet-500/10 backdrop-blur-md border border-violet-500/30 rounded-lg text-violet-400 shadow-[0_0_15px_rgba(139,92,246,0.3)]">
                                                    <Star size={16} fill="currentColor" className="text-violet-500 shadow-sm" />
                                                    <span className="text-white text-sm font-bold">
                                                        <span className="text-violet-300 font-medium mr-1.5 uppercase tracking-tighter text-xs">SoftMedia</span>
                                                        {userRating % 1 === 0 ? userRating : userRating.toFixed(1)}
                                                    </span>
                                                </div>
                                            )}
                                        </div>

                                        {rating && isNaN(Number(rating)) && (
                                            <span className="px-3 py-1 bg-white/10 backdrop-blur-md border border-white/20 rounded-md text-sm font-bold text-white">
                                                {rating}
                                            </span>
                                        )}
                                        {duration && (
                                            <span className="text-white/80 text-lg">
                                                {duration}
                                            </span>
                                        )}
                                    </motion.div>

                                    {/* Unified Text Container - Fixed Total Height to stabilize buttons, dynamic internal split */}
                                    <div className="h-[240px] lg:h-[300px] flex flex-col mb-8 overflow-hidden">
                                        {/* Title area - Flexible but limited to 2 lines. Wrapper allows shadows to bleed. */}
                                        <div className="shrink-0 mb-2 overflow-visible p-4 -m-4">
                                            <motion.h1
                                                className="text-5xl lg:text-7xl font-black text-white leading-[1.2] px-4 -mx-4 pb-2 drop-shadow-2xl line-clamp-2"
                                                style={{
                                                    textShadow: '0 4px 20px rgba(0,0,0,0.8), 0 0 40px rgba(99,102,241,0.2)',
                                                    willChange: 'transform, opacity',
                                                    transform: 'translateZ(0)'
                                                }}
                                                initial={{ opacity: 0, y: 30 }}
                                                animate={{ opacity: 1, y: 0 }}
                                                transition={{ delay: 0.3 }}
                                            >
                                                {title}
                                            </motion.h1>
                                        </div>

                                        {/* Description area - Fills the remaining space. Limited to 4 lines to guarantee fit. */}
                                        <motion.p
                                            className="text-gray-200 text-lg leading-relaxed drop-shadow-lg font-medium line-clamp-4"
                                            initial={{ opacity: 0, y: 20 }}
                                            animate={{ opacity: 1, y: 0 }}
                                            transition={{ delay: 0.4 }}
                                            style={{ willChange: 'transform, opacity', transform: 'translateZ(0)' }}
                                        >
                                            {description}
                                        </motion.p>
                                    </div>

                                    {/* Action Buttons */}
                                    <motion.div
                                        className="flex items-center gap-4 h-12"
                                        initial={{ opacity: 0, y: 20 }}
                                        animate={{ opacity: 1, y: 0 }}
                                        transition={{ delay: 0.5 }}
                                        style={{ willChange: 'transform, opacity', transform: 'translateZ(0)' }}
                                    >
                                        <motion.button
                                            onClick={() => onPlay?.(currentItem)}
                                            className="group/btn relative px-8 py-3 bg-white rounded-lg font-bold text-lg text-black overflow-hidden shadow-2xl"
                                            whileHover={{ scale: 1.05 }}
                                            whileTap={{ scale: 0.95 }}
                                        >
                                            <div className="absolute inset-0 bg-gradient-to-r from-white to-gray-100" />
                                            <div className="absolute inset-0 bg-white opacity-0 group-hover/btn:opacity-20 transition-opacity" />
                                            <div className="relative flex items-center gap-3">
                                                <Play fill="currentColor" size={20} />
                                                <span>Play Now</span>
                                            </div>
                                        </motion.button>

                                        <motion.button
                                            onClick={() => onMoreInfo?.(currentItem)}
                                            className="group/btn relative px-8 py-3 bg-white/10 backdrop-blur-xl border border-white/20 rounded-lg font-bold text-lg text-white overflow-hidden shadow-xl"
                                            whileHover={{ scale: 1.05, backgroundColor: 'rgba(255,255,255,0.15)' }}
                                            whileTap={{ scale: 0.95 }}
                                        >
                                            <div className="relative flex items-center gap-3">
                                                <Info size={20} />
                                                <span>More Info</span>
                                            </div>
                                        </motion.button>
                                    </motion.div>
                                </div>
                            </div>
                        </div>
                    </div>
                </motion.div>
            </AnimatePresence>

            {/* Pagination Indicators */}
            <div className="absolute bottom-6 right-12 flex items-center gap-2 z-20">
                {items.map((_, idx) => (
                    <button
                        key={idx}
                        onClick={() => setCurrentIndex(idx)}
                        className={`h-1.5 transition-all duration-500 rounded-full ${idx === currentIdx ? 'w-8 bg-white' : 'w-2 bg-white/30'
                            }`}
                    />
                ))}
            </div>

            {/* Bottom Fade */}
            <div className="absolute bottom-0 left-0 right-0 h-32 bg-gradient-to-t from-background to-transparent pointer-events-none" />
        </div>
    );
}
