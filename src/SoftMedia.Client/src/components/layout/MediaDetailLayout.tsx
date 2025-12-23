import { type ReactNode } from 'react';
import { ArrowLeft, Play, Heart, Share2, Eye } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { motion } from 'framer-motion';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import QualityBadge from '../ui/QualityBadge';
import { StarRating } from '../ui/StarRating';
import { cn } from '../../lib/utils';

interface MediaDetailLayoutProps {
    item: MediaItem;
    children: ReactNode;
    onPlay?: () => void;
}

export default function MediaDetailLayout({ item, children, onPlay }: MediaDetailLayoutProps) {
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

    return (
        <div className="min-h-screen bg-background relative overflow-x-hidden">
            {/* Backdrop */}
            <div className="absolute inset-0 h-[70vh] w-full overflow-hidden">
                {item.backdropPath ? (
                    <>
                        <img
                            src={item.backdropPath}
                            alt=""
                            className="w-full h-full object-cover opacity-40 blur-sm scale-105"
                        />
                        <div className="absolute inset-0 bg-gradient-to-b from-background/20 via-background/60 to-background" />
                    </>
                ) : (
                    <div className="w-full h-full bg-gradient-to-b from-primary/20 to-background" />
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
                    {/* Poster Column */}
                    <div className="flex-shrink-0 w-full sm:w-64 md:w-72 lg:w-80 mx-auto lg:mx-0">
                        <motion.div
                            initial={{ opacity: 0, y: 20 }}
                            animate={{ opacity: 1, y: 0 }}
                            className="rounded-xl overflow-hidden shadow-2xl aspect-[2/3] ring-1 ring-white/10"
                        >
                            {item.posterPath ? (
                                <img
                                    src={item.posterPath}
                                    alt={item.title}
                                    className="w-full h-full object-cover"
                                />
                            ) : (
                                <div className="w-full h-full bg-slate-800 flex items-center justify-center text-slate-600">
                                    <span className="text-6xl">?</span>
                                </div>
                            )}
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

                            <div className="flex flex-wrap items-center gap-6 text-sm md:text-base text-gray-300 mb-6">
                                <div className="flex flex-col gap-1">
                                    <StarRating
                                        rating={item.userRating ?? 0}
                                        onChange={(r) => rateMutation.mutate(r)}
                                        size={20}
                                        max={10}
                                    />
                                    <span className="text-xs text-gray-500 font-medium ml-1">
                                        Community: {item.communityRating ? item.communityRating.toFixed(1) : 'N/A'}
                                    </span>
                                </div>

                                <div className="flex items-center gap-4">
                                    {item.year && (
                                        <span className="font-semibold text-white">{item.year}</span>
                                    )}
                                    {item.rating && (
                                        <span className="px-2 py-0.5 border border-blue-500/30 bg-blue-500/10 rounded text-xs font-bold text-blue-200">
                                            {item.rating}
                                        </span>
                                    )}
                                    {item.duration && (
                                        <span>{item.duration}</span>
                                    )}
                                    {item.quality && (
                                        <QualityBadge quality={item.quality} />
                                    )}
                                </div>
                            </div>

                            {/* Genres */}
                            {item.genres && item.genres.length > 0 && (
                                <div className="flex flex-wrap gap-2 mb-8">
                                    {item.genres.map(genre => (
                                        <span
                                            key={genre}
                                            className="px-3 py-1 rounded-full bg-white/10 hover:bg-white/20 text-sm text-gray-200 transition-colors cursor-default"
                                        >
                                            {genre}
                                        </span>
                                    ))}
                                </div>
                            )}

                            {/* Actions */}
                            <div className="flex flex-wrap gap-4 mb-8">
                                <button
                                    onClick={onPlay}
                                    className="flex items-center gap-2 px-8 py-3 bg-primary hover:bg-primary/90 text-white rounded-full font-bold transition-all shadow-lg shadow-primary/20 hover:scale-105 active:scale-95"
                                >
                                    <Play className="w-5 h-5 fill-current" />
                                    Play
                                </button>

                                <button
                                    onClick={() => favoriteMutation.mutate(!item.isFavorite)}
                                    className={cn(
                                        "p-3 rounded-full transition-all hover:scale-105 active:scale-95",
                                        item.isFavorite
                                            ? "bg-red-500/20 text-red-500 hover:bg-red-500/30"
                                            : "bg-white/10 hover:bg-white/20 text-white"
                                    )}
                                >
                                    <Heart className={cn("w-5 h-5", item.isFavorite && "fill-current")} />
                                </button>

                                <button
                                    onClick={() => watchedMutation.mutate(!item.watched)}
                                    className={cn(
                                        "p-3 rounded-full transition-all hover:scale-105 active:scale-95",
                                        item.watched
                                            ? "bg-green-500/20 text-green-500 hover:bg-green-500/30"
                                            : "bg-white/10 hover:bg-white/20 text-white"
                                    )}
                                    title={item.watched ? "Mark as unwatched" : "Mark as watched"}
                                >
                                    <Eye className="w-5 h-5" />
                                </button>

                                <button className="p-3 rounded-full bg-white/10 hover:bg-white/20 text-white transition-all hover:scale-105 active:scale-95">
                                    <Share2 className="w-5 h-5" />
                                </button>
                            </div>

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
