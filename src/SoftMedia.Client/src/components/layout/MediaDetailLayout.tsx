import { type ReactNode } from 'react';
import { ArrowLeft, Play, Heart, Share2, Eye, Star } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { motion } from 'framer-motion';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../../services/api';
import { type MediaItem, MediaType } from '../../types';
import QualityBadge from '../ui/QualityBadge';
import MediaQualityInfo from '../ui/MediaQualityInfo';
import { StarRating } from '../ui/StarRating';
import { cn } from '../../lib/utils';
import { getGenreColors } from '../../lib/genreColors';
import { resolveHeroPosterUrl, resolveBackdropUrl } from '../../lib/mediaImageUrl';
import { WatchlistButton } from '../details/WatchlistButton';

interface MediaDetailLayoutProps {
    item: MediaItem;
    children: ReactNode;
    onPlay?: () => void;
    qualityItem?: MediaItem | null;
    backdropOverride?: string | null;
    customMetadata?: React.ReactNode;
}

export default function MediaDetailLayout({ item, children, onPlay, qualityItem, backdropOverride, customMetadata }: MediaDetailLayoutProps) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    const rateMutation = useMutation({
        mutationFn: (rating: number) => api.post(`/interaction/${item.id}/rate`, { rating }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['media', item.id] });
            queryClient.invalidateQueries({ queryKey: ['library'] });
        }
    });

    const favoriteMutation = useMutation({
        mutationFn: (isFavorite: boolean) => api.post(`/interaction/${item.id}/favorite`, { isFavorite }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['media', item.id] });
            queryClient.invalidateQueries({ queryKey: ['library'] });
        }
    });

    const watchedMutation = useMutation({
        mutationFn: (watched: boolean) => api.post(`/interaction/${item.id}/watched`, { watched }),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['media', item.id] });
        }
    });

    // Use override if provided, otherwise default to item backdrop.
    // Request smaller thumbnails for both backdrop (blurred, doesn't need full res)
    // and hero poster — avoids tying up browser connection slots while album art loads.
    const effectiveBackdrop = resolveBackdropUrl(backdropOverride ?? item.backdropPath);
    const heroPoster = resolveHeroPosterUrl(item.posterPath);

    return (
        <div className="min-h-screen bg-background relative overflow-x-hidden">
            {/* Backdrop */}
            <div className="fixed inset-0 w-full h-full overflow-hidden pointer-events-none">
                {effectiveBackdrop ? (
                    <>
                        <img
                            src={effectiveBackdrop}
                            alt=""
                            referrerPolicy="no-referrer"
                            className="w-full h-full object-cover object-top opacity-30 blur-xl scale-110"
                        />
                        <div className="absolute inset-0 bg-background/60" />
                        <div className="absolute inset-0 bg-gradient-to-t from-background via-background/40 to-transparent" />
                    </>
                ) : (
                    <div className="w-full h-full bg-gradient-to-b from-primary/10 to-background" />
                )}
            </div>

            {/* Content */}
            <div className="relative z-10 w-full px-4 lg:px-6 pt-4 lg:pt-6 pb-12">
                <button
                    onClick={() => navigate(-1)}
                    className="mb-8 flex items-center gap-2 text-gray-300 hover:text-white transition-colors group"
                >
                    <div className="p-2 rounded-full bg-black/20 group-hover:bg-black/40 transition-colors">
                        <ArrowLeft className="w-5 h-5" />
                    </div>
                    <span className="font-medium">Back</span>
                </button>

                <div className="flex flex-col lg:flex-row gap-8 lg:gap-12">
                    <div className="flex-shrink-0 w-full sm:w-64 md:w-72 lg:w-80 mx-auto lg:mx-0">
                        <motion.div
                            initial={{ opacity: 0, y: 20 }}
                            animate={{ opacity: 1, y: 0 }}
                            className={cn(
                                "rounded-xl overflow-hidden shadow-2xl ring-1 ring-white/10",
                                (item.type === MediaType.Album || item.type === MediaType.Artist || item.type === MediaType.Audio || item.type === MediaType.Track)
                                    ? "aspect-square"
                                    : "aspect-[2/3]"
                            )}
                        >
                            {heroPoster ? (
                                <img
                                    src={heroPoster}
                                    alt={item.title}
                                    referrerPolicy="no-referrer"
                                    className="w-full h-full object-cover"
                                />
                            ) : (
                                <div className="w-full h-full bg-slate-800 flex items-center justify-center text-slate-600">
                                    <span className="text-6xl">?</span>
                                </div>
                            )}
                        </motion.div>

                        {/* Actions Sidebar */}
                        <motion.div
                            initial={{ opacity: 0, y: 20 }}
                            animate={{ opacity: 1, y: 0 }}
                            transition={{ delay: 0.2 }}
                            className="mt-6 flex flex-col gap-4"
                        >
                            {item.type !== MediaType.Artist && item.type !== MediaType.Album && (
                                <button
                                    onClick={onPlay}
                                    className="relative z-50 w-full flex items-center justify-center gap-2 px-8 py-4 bg-gradient-to-r from-blue-600 to-violet-600 text-white rounded-xl font-bold shadow-lg shadow-violet-500/40 hover:scale-[1.02] active:scale-95 text-lg opacity-100"
                                >
                                    <Play className="w-6 h-6 fill-current" />
                                    Play
                                </button>
                            )}

                            <div className="flex items-center justify-between px-2">
                                {item.type !== MediaType.Artist && item.type !== MediaType.Album && (
                                    <button
                                        onClick={() => favoriteMutation.mutate(!item.isFavorite)}
                                        className="group"
                                        title="Favorite"
                                    >
                                        <div className={cn(
                                            "p-3 rounded-full transition-all group-hover:scale-110 active:scale-95",
                                            item.isFavorite
                                                ? "bg-red-500/20 text-red-500"
                                                : "bg-white/5 hover:bg-white/10 text-white"
                                        )}>
                                            <Heart className={cn("w-5 h-5", item.isFavorite && "fill-current")} />
                                        </div>
                                    </button>
                                )}

                                {item.type !== MediaType.Artist &&
                                    item.type !== MediaType.Album &&
                                    item.type !== MediaType.Audio &&
                                    item.type !== MediaType.Track && (
                                        <button
                                            onClick={() => watchedMutation.mutate(!item.watched)}
                                            className="group"
                                            title={item.watched ? "Mark as unwatched" : "Mark as watched"}
                                        >
                                            <div className={cn(
                                                "p-3 rounded-full transition-all group-hover:scale-110 active:scale-95",
                                                item.watched
                                                    ? "bg-green-500/20 text-green-500"
                                                    : "bg-white/5 hover:bg-white/10 text-white"
                                            )}>
                                                <Eye className="w-5 h-5" />
                                            </div>
                                        </button>
                                    )}

                                {item.type !== MediaType.Artist && item.type !== MediaType.Album && (
                                    <button className="group" title="Share">
                                        <div className="p-3 rounded-full bg-white/5 hover:bg-white/10 text-white transition-all group-hover:scale-110 active:scale-95">
                                            <Share2 className="w-5 h-5" />
                                        </div>
                                    </button>
                                )}
                            </div>
                        </motion.div>
                    </div>

                    {/* Info Column - Title, Rating, Actions, Description */}
                    <div className="flex-grow">
                        <motion.div
                            initial={{ opacity: 0, y: 20 }}
                            animate={{ opacity: 1, y: 0 }}
                            transition={{ delay: 0.1 }}
                        >
                            <h1 className="text-4xl md:text-5xl lg:text-6xl font-bold text-white mb-4 leading-tight">
                                {item.title}
                            </h1>

                            {/* Secondary Metadata Row */}
                            <div className="flex flex-wrap items-center gap-6 mb-6 font-medium text-gray-200">
                                {item.year && (
                                    <span className="text-lg">{item.year}</span>
                                )}
                                {item.year && customMetadata && (
                                    <span className="text-gray-600">•</span>
                                )}
                                {customMetadata}
                                {item.type === MediaType.Series && item.rating && (
                                    <span className="px-2 py-0.5 border border-gray-500/30 bg-gray-500/10 rounded text-xs font-bold text-gray-400 uppercase tracking-wider">
                                        {item.rating}
                                    </span>
                                )}
                                {item.duration && (
                                    <span className="text-lg">{item.duration}</span>
                                )}
                                {item.type !== MediaType.Series && item.rating && (
                                    <span className="px-2 py-0.5 border border-gray-500/30 bg-gray-500/10 rounded text-xs font-bold text-gray-400 uppercase tracking-wider">
                                        {item.rating}
                                    </span>
                                )}
                                {item.quality && (
                                    <QualityBadge quality={item.quality} />
                                )}
                            </div>

                            {/* Consolidated Ratings Section */}
                            <div className="flex flex-wrap items-center gap-4 mb-8">
                                {/* IMDb Rating */}
                                {item.communityRating && (
                                    <div className="flex items-center gap-2 px-3 py-2 rounded-xl bg-yellow-400/10 border border-yellow-400/20">
                                        <Star className="w-5 h-5 text-yellow-400 fill-current" />
                                        <div className="flex flex-col leading-none">
                                            <span className="text-lg font-bold text-yellow-400">{item.communityRating.toFixed(1)}</span>
                                            <span className="text-[10px] uppercase tracking-wider text-yellow-400/70 font-bold">IMDb</span>
                                        </div>
                                    </div>
                                )}

                                {/* SoftMedia Average Rating */}
                                {item.userRating && (
                                    <div className="flex items-center gap-2 px-3 py-2 rounded-xl bg-violet-500/10 border border-violet-500/20">
                                        <Star className="w-5 h-5 text-violet-500 fill-current" />
                                        <div className="flex flex-col leading-none">
                                            <span className="text-lg font-bold text-violet-500">{item.userRating.toFixed(1)}</span>
                                            <span className="text-[10px] uppercase tracking-wider text-violet-500/70 font-bold">SoftMedia</span>
                                        </div>
                                    </div>
                                )}

                                {/* Personal Rating */}
                                <div className="flex flex-col gap-1 p-2 rounded-xl bg-white/5 border border-white/10">
                                    <span className="text-[10px] uppercase tracking-wider text-gray-500 font-bold ml-1">Your Rating</span>
                                    <StarRating
                                        rating={item.personalRating ?? 0}
                                        onChange={(r) => rateMutation.mutate(r)}
                                        size={20}
                                        max={10}
                                    />
                                </div>
                            </div>

                            {/* Genres */}
                            {item.genres && item.genres.length > 0 && (
                                <div className="flex flex-wrap gap-2 mb-6">
                                    {item.genres.map(genre => {
                                        const colors = getGenreColors(genre);
                                        return (
                                            <span
                                                key={genre}
                                                className={`px-3 py-1 rounded-full text-sm font-medium transition-colors cursor-default border ${colors.bg} ${colors.text} ${colors.hoverBg} ${colors.border || 'border-transparent'}`}
                                            >
                                                {genre}
                                            </span>
                                        );
                                    })}
                                </div>
                            )}

                            {/* Extended Quality Info */}
                            <MediaQualityInfo item={qualityItem || item} className="mb-8" />

                            {/* Actions — watchlist is for "I'll come back to this later"
                                content (movies, series, books, comics, games). Music uses
                                playlists for the same purpose, so the button is hidden for
                                Artist/Album/Audio/Track to match the backend rejection. */}
                            {item.type !== MediaType.Artist
                                && item.type !== MediaType.Album
                                && item.type !== MediaType.Audio
                                && item.type !== MediaType.Track && (
                                <div className="flex flex-wrap items-center gap-2 mb-6">
                                    <WatchlistButton
                                        mediaId={item.id}
                                        isWatchlisted={!!item.isWatchlisted}
                                        title={item.title}
                                    />
                                </div>
                            )}

                            {/* Description */}
                            {item.description && (
                                <div className="prose prose-invert max-w-3xl">
                                    <p className="text-lg text-gray-300 leading-relaxed">
                                        {item.description}
                                    </p>
                                </div>
                            )}
                        </motion.div>
                    </div>
                </div>

                {/* Type Specific Content - Full Width Below */}
                <div className="mt-12">
                    {children}
                </div>
            </div>
        </div>
    );
}
