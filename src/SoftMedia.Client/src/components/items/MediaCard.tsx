import { useState, useEffect } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Play, ListMusic, Heart, Check, Clock, Star } from 'lucide-react';
import { type MediaItem } from '../../types';
import QualityBadge from '../ui/QualityBadge';
import { useAudioStore } from '../../store/audioStore';
import api from '../../services/api';

// Loading image component with skeleton placeholder and fade-in transition
function LoadingImage({
    src,
    alt,
    className = '',
    fallback
}: {
    src: string | null | undefined;
    alt: string;
    className?: string;
    fallback?: React.ReactNode;
}) {
    const [loaded, setLoaded] = useState(false);
    const [error, setError] = useState(false);

    // Reset state when src changes
    useEffect(() => {
        setLoaded(false);
        setError(false);
    }, [src]);

    if (!src || error) {
        return fallback ? <>{fallback}</> : null;
    }

    return (
        <div className="relative w-full h-full">
            {/* Skeleton placeholder - visible while loading */}
            {!loaded && (
                <div className="absolute inset-0 bg-gradient-to-br from-gray-800 via-gray-700 to-gray-800 animate-pulse" />
            )}
            {/* Actual image with fade-in */}
            <img
                src={src}
                alt={alt}
                className={`${className} transition-opacity duration-300 ${loaded ? 'opacity-100' : 'opacity-0'}`}
                onLoad={() => setLoaded(true)}
                onError={() => setError(true)}
                loading="lazy"
            />
        </div>
    );
}

interface MediaCardProps {
    item: MediaItem;
    libraryType?: string;
    enableHoverScale?: boolean;
}

const genreColors: Record<string, string> = {
    'Fantasy': 'from-purple-600 to-pink-600',
    'Action': 'from-red-600 to-orange-600',
    'Horror': 'from-red-900 to-black',
    'Comedy': 'from-yellow-400 to-orange-500',
    'Drama': 'from-blue-600 to-indigo-600',
    'Sci-Fi': 'from-cyan-500 to-blue-600',
    'Thriller': 'from-emerald-600 to-teal-700',
    'Animation': 'from-pink-500 to-rose-500',
    'Mystery': 'from-violet-600 to-indigo-700',
    'Adventure': 'from-green-500 to-teal-500',
    'Crime': 'from-slate-700 to-red-800',
    'Romance': 'from-rose-400 to-pink-500',
};

export default function MediaCard({ item, libraryType }: MediaCardProps) {
    const navigate = useNavigate();
    const { playTrack, addToQueue } = useAudioStore();
    const primaryGenre = item.genres?.[0] || 'Drama';
    // Use the genre color or a default pleasing gradient
    const glowGradient = genreColors[primaryGenre] || 'from-blue-600 to-violet-600';

    const isAudio = libraryType === 'Music';
    const isMovie = libraryType === 'Movie';
    // For TV: if it has an episodeNumber, it's an episode; otherwise treat as a series
    const isTVEpisode = libraryType === 'TV' && !!item.episodeNumber;
    const isTVSeries = libraryType === 'TV' && !item.episodeNumber;

    // Logic for "New" Badge (14 days threshold)
    const isNew = (() => {
        if (!item.dateAdded) return false;
        const added = new Date(item.dateAdded);
        const now = new Date();
        const diffTime = Math.abs(now.getTime() - added.getTime());
        const diffDays = Math.ceil(diffTime / (1000 * 60 * 60 * 24));
        return diffDays <= 14;
    })();

    const handlePlay = async (e: React.MouseEvent) => {
        e.preventDefault();
        e.stopPropagation();

        if (isAudio) {
            playTrack(item);
        } else if (isMovie || isTVEpisode) {
            // Navigate directly to player
            navigate(`/play/${item.id}`);
        } else if (isTVSeries) {
            // Fetch next episode to play using smart continue logic
            console.log('[MediaCard] TV Series detected, fetching next episode for:', item.id, item.title);
            try {
                const response = await api.get(`/series/${item.id}/next-episode`);
                console.log('[MediaCard] Next episode response:', response.data);
                const nextEpisode = response.data;
                navigate(`/play/${nextEpisode.episodeId}`);
            } catch (error) {
                // Fallback: navigate to series detail page
                console.error('[MediaCard] Failed to fetch next episode:', error);
                navigate(`/media/${item.id}`);
            }
        }
    };

    const handleAddToQueue = (e: React.MouseEvent) => {
        e.preventDefault();
        e.stopPropagation();
        addToQueue(item);
    };

    const subtitle = (item.seasonNumber !== undefined && item.seasonNumber !== null && item.episodeNumber)
        ? `S${item.seasonNumber} • E${item.episodeNumber}`
        : (item.year ? String(item.year) : '');

    const CardContent = (
        <div className="relative w-full h-full group/inner">
            {/* Ambient Glow - Backlight Effect */}
            <div
                className={`absolute -inset-3 bg-gradient-to-br ${glowGradient} rounded-xl blur-xl opacity-0 group-hover/card:opacity-40 transition-opacity duration-500 -z-10 will-change-[opacity]`}
            />

            {/* Main Container */}
            <div className="flex flex-col h-full bg-[#1a1d21]/95 backdrop-blur-sm rounded-xl border border-white/5 overflow-hidden group-hover/card:border-white/20 transition-all duration-300 shadow-lg group-hover/card:shadow-2xl ring-1 ring-black/50">

                {/* Poster Section relative wrapper */}
                <div className="relative aspect-[2/3] w-full bg-gray-900 overflow-hidden">
                    {/* Poster Image */}
                    <LoadingImage
                        src={item.posterPath}
                        alt={item.title}
                        className="h-full w-full object-cover transition-transform duration-700 ease-out group-hover/card:scale-105"
                        fallback={
                            <div className="flex h-full w-full items-center justify-center bg-slate-800 text-slate-500">
                                <span className="text-4xl font-thin opacity-50">?</span>
                            </div>
                        }
                    />

                    {/* Top Indicators Row */}
                    <div className="absolute top-2 left-2 right-2 flex justify-between items-start z-10 pointer-events-none">
                        {/* Left Side: New Badge */}
                        <div className="flex gap-1">
                            {isNew && (
                                <span className="px-1.5 py-0.5 bg-blue-600/90 backdrop-blur-sm rounded text-[9px] font-bold text-white shadow-lg tracking-wider border border-blue-400/20">
                                    NEW
                                </span>
                            )}
                        </div>

                        {/* Right Side: Quality, Favorite, Watched */}
                        <div className="flex flex-col items-end gap-1.5 translate-y-2 group-hover/card:translate-y-0 opacity-0 group-hover/card:opacity-100 transition-all duration-300">
                            <QualityBadge quality={item.quality} />

                            {item.isFavorite && (
                                <div className="p-1 bg-black/60 backdrop-blur-sm rounded-full border border-white/10 shadow-sm">
                                    <Heart className="w-3.5 h-3.5 text-red-500 fill-red-500" />
                                </div>
                            )}
                            {item.watched && (
                                <div className="p-1 bg-green-900/80 backdrop-blur-sm rounded-full border border-green-500/30 shadow-sm">
                                    <Check className="w-3.5 h-3.5 text-green-400" />
                                </div>
                            )}
                        </div>
                    </div>

                    {/* Duration Pill - Bottom Right */}
                    {item.duration && (
                        <div className="absolute bottom-2 right-2 z-20">
                            <div className="flex items-center gap-1 bg-black/70 backdrop-blur-md px-1.5 py-0.5 rounded text-[10px] font-bold text-gray-200 border border-white/5 shadow-lg">
                                <Clock className="w-2.5 h-2.5 text-gray-400" />
                                <span>{item.duration}</span>
                            </div>
                        </div>
                    )}

                    {/* Play Overlay - Centered */}
                    <div className="absolute inset-0 flex items-center justify-center z-20 opacity-0 group-hover/card:opacity-100 transition-opacity duration-300 pointer-events-none">
                        {/* Play Button wrapped in pointer-events-auto to capture clicks */}
                        <div className="relative group/play pointer-events-auto">
                            <div className={`absolute inset-0 bg-gradient-to-br ${glowGradient} blur-lg opacity-50 rounded-full scale-75 group-hover/play:scale-110 transition-transform duration-500`} />
                            <div
                                className="relative bg-white/10 backdrop-blur-md p-4 rounded-full border border-white/20 shadow-2xl hover:bg-white/20 hover:scale-110 active:scale-95 transition-all duration-300 cursor-pointer text-white flex items-center justify-center"
                                onClick={handlePlay}
                            >
                                <Play className="w-8 h-8 fill-white ml-1" />
                            </div>

                            {/* Add to Queue Button (Audio Only) attached near play button */}
                            {isAudio && (
                                <div
                                    className="absolute -right-12 top-1/2 -translate-y-1/2 bg-black/60 backdrop-blur-md p-2 rounded-full border border-white/10 shadow-xl hover:bg-white/20 cursor-pointer text-gray-200 hover:text-white transition-all duration-200"
                                    onClick={handleAddToQueue}
                                    title="Add to Queue"
                                >
                                    <ListMusic className="w-4 h-4" />
                                </div>
                            )}
                        </div>
                    </div>

                    {/* Progress Bar */}
                    {item.progress !== undefined && item.progress > 0 && (
                        <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-gray-800/50 z-30">
                            <div
                                className={`h-full bg-gradient-to-r ${glowGradient} shadow-[0_0_8px_rgba(255,255,255,0.4)]`}
                                style={{ width: `${item.progress}%` }}
                            />
                        </div>
                    )}
                </div>

                {/* Info Section */}
                <div className="flex-1 p-3 flex flex-col justify-between bg-[#1a1d21] relative z-30 group-hover/card:bg-[#202328] transition-colors duration-300">
                    <div className="flex flex-col gap-1">
                        <h3 className="text-gray-100 font-bold text-[0.95rem] leading-tight line-clamp-2 group-hover/card:text-white transition-colors" title={item.title}>
                            {item.title}
                        </h3>

                        <div className="flex items-center gap-2 mt-0.5">
                            <p className="text-xs text-gray-500 font-medium whitespace-nowrap group-hover/card:text-gray-400 transition-colors">
                                {subtitle}
                            </p>
                            {item.rating && (
                                <span className="px-1.5 py-[1px] border border-white/10 bg-blue-500/20 rounded-[4px] text-[10px] text-blue-200 font-semibold tracking-wide">
                                    {item.rating}
                                </span>
                            )}

                            {item.userRating ? (
                                <div className="flex items-center gap-1 px-1.5 py-[1px] border border-yellow-500/30 bg-yellow-500/10 rounded-[4px]">
                                    <Star className="w-2.5 h-2.5 text-yellow-500 fill-current" />
                                    <span className="text-[10px] text-yellow-500 font-bold tracking-wide">
                                        {item.userRating}
                                    </span>
                                </div>
                            ) : item.communityRating ? (
                                <div className="flex items-center gap-1 px-1.5 py-[1px] border border-white/10 bg-white/5 rounded-[4px]">
                                    <Star className="w-2.5 h-2.5 text-gray-400 fill-current" />
                                    <span className="text-[10px] text-gray-400 font-semibold tracking-wide">
                                        {item.communityRating.toFixed(1)}
                                    </span>
                                </div>
                            ) : (
                                <div className="flex items-center gap-1 px-1.5 py-[1px] border border-white/5 bg-white/5 rounded-[4px]">
                                    <Star className="w-2.5 h-2.5 text-gray-600" />
                                    <span className="text-[10px] text-gray-500 font-semibold tracking-wide">
                                        N/A
                                    </span>
                                </div>
                            )}
                        </div>
                    </div>

                    {item.genres && item.genres.length > 0 && (
                        <div className="mt-2 text-[10px] font-semibold text-gray-500 uppercase tracking-wider truncate opacity-70 group-hover/card:opacity-100 transition-opacity">
                            {item.genres.slice(0, 2).join(' / ')}
                        </div>
                    )}
                </div>
            </div>
        </div>
    );

    if (isAudio) {
        return (
            <div className="block group/card cursor-pointer relative hover:z-50 h-full">
                {CardContent}
            </div>
        );
    }

    const isBook = libraryType === 'Book';
    const linkTarget = isBook ? `/read/${item.id}` : `/media/${item.id}`;

    return (
        <Link to={linkTarget} className="block group/card relative hover:z-50 h-full">
            {CardContent}
        </Link>
    );
}
