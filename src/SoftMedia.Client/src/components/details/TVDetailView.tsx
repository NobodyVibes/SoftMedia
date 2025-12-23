import { useMemo, useState, useRef, useEffect } from 'react';
import { type MediaItem } from '../../types';
import { Play, Star, Check, ChevronLeft, ChevronRight, LayoutGrid, List } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import api from '../../services/api';
import { Link } from 'react-router-dom';
import { useUIStore } from '../../store/uiStore';

interface Season {
    number: number;
    poster: string | null;
    episodeCount: number | null;
    premiereDate: string | null;
}

interface TVDetailViewProps {
    item: MediaItem;
}

// Reusable horizontal scroll container with arrows and slider
function HorizontalScrollList({
    children,
    className = ''
}: {
    children: React.ReactNode;
    className?: string
}) {
    const scrollRef = useRef<HTMLDivElement>(null);
    const sliderRef = useRef<HTMLDivElement>(null);
    const thumbRef = useRef<HTMLDivElement>(null);
    const [canScrollLeft, setCanScrollLeft] = useState(false);
    const [canScrollRight, setCanScrollRight] = useState(true);

    // Update thumb position directly via DOM for smooth performance
    const updateThumbPosition = () => {
        if (!scrollRef.current || !thumbRef.current) return;
        const { scrollLeft, scrollWidth, clientWidth } = scrollRef.current;
        const maxScroll = scrollWidth - clientWidth;
        const progress = maxScroll > 0 ? scrollLeft / maxScroll : 0;
        const thumbWidth = Math.max(15, (clientWidth / scrollWidth) * 100);
        const thumbPosition = progress * (100 - thumbWidth);

        thumbRef.current.style.width = `${thumbWidth}%`;
        thumbRef.current.style.marginLeft = `${thumbPosition}%`;
    };

    // Initialize thumb size on mount and when children change
    useEffect(() => {
        // Small delay to ensure DOM is measured correctly
        const timer = setTimeout(() => {
            updateThumbPosition();
            updateScrollState();
        }, 50);
        return () => clearTimeout(timer);
    }, [children]);

    const updateScrollState = () => {
        if (!scrollRef.current) return;
        const { scrollLeft, scrollWidth, clientWidth } = scrollRef.current;
        setCanScrollLeft(scrollLeft > 0);
        setCanScrollRight(scrollLeft < scrollWidth - clientWidth - 10);
        updateThumbPosition();
    };

    const scroll = (direction: 'left' | 'right') => {
        if (!scrollRef.current) return;
        const scrollAmount = scrollRef.current.clientWidth * 0.8;
        scrollRef.current.scrollBy({
            left: direction === 'left' ? -scrollAmount : scrollAmount,
            behavior: 'smooth'
        });
    };

    // Handle slider track click to scroll
    const handleSliderClick = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!scrollRef.current || !sliderRef.current || e.target === thumbRef.current) return;
        const rect = sliderRef.current.getBoundingClientRect();
        const clickPosition = (e.clientX - rect.left) / rect.width;
        const maxScroll = scrollRef.current.scrollWidth - scrollRef.current.clientWidth;
        scrollRef.current.scrollTo({
            left: clickPosition * maxScroll,
            behavior: 'smooth'
        });
    };

    // Optimized thumb drag - uses requestAnimationFrame for smooth updates
    const handleThumbDrag = (e: React.MouseEvent) => {
        if (!scrollRef.current || !sliderRef.current) return;
        e.preventDefault();
        e.stopPropagation();

        if (thumbRef.current) {
            thumbRef.current.style.cursor = 'grabbing';
            thumbRef.current.style.transition = 'none';
        }

        const onMove = (moveEvent: MouseEvent) => {
            if (!scrollRef.current || !sliderRef.current) return;
            const rect = sliderRef.current.getBoundingClientRect();
            const position = Math.max(0, Math.min(1, (moveEvent.clientX - rect.left) / rect.width));
            const maxScroll = scrollRef.current.scrollWidth - scrollRef.current.clientWidth;
            scrollRef.current.scrollLeft = position * maxScroll;
        };

        const onUp = () => {
            if (thumbRef.current) {
                thumbRef.current.style.cursor = 'grab';
                thumbRef.current.style.transition = '';
            }
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        };

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    };

    return (
        <div className="relative group/scroll">
            {/* Container with arrow buttons outside */}
            <div className="flex items-center gap-2">
                {/* Left Arrow */}
                <button
                    onClick={() => scroll('left')}
                    className={`flex-shrink-0 w-10 h-10 rounded-full bg-white/10 border border-white/20 flex items-center justify-center text-white transition-all hover:bg-violet-600 hover:border-violet-400 opacity-0 group-hover/scroll:opacity-100 ${canScrollLeft ? '' : 'pointer-events-none !opacity-0'
                        }`}
                >
                    <ChevronLeft className="w-5 h-5" />
                </button>

                {/* Scrollable Content */}
                <div
                    ref={scrollRef}
                    onScroll={updateScrollState}
                    className={`flex-1 flex gap-4 overflow-x-auto pb-2 scrollbar-none ${className}`}
                    style={{ scrollbarWidth: 'none', msOverflowStyle: 'none' }}
                >
                    {children}
                </div>

                {/* Right Arrow */}
                <button
                    onClick={() => scroll('right')}
                    className={`flex-shrink-0 w-10 h-10 rounded-full bg-white/10 border border-white/20 flex items-center justify-center text-white transition-all hover:bg-violet-600 hover:border-violet-400 opacity-0 group-hover/scroll:opacity-100 ${canScrollRight ? '' : 'pointer-events-none !opacity-0'
                        }`}
                >
                    <ChevronRight className="w-5 h-5" />
                </button>
            </div>

            {/* Interactive Slider Bar - Only visible on hover */}
            <div
                ref={sliderRef}
                onClick={handleSliderClick}
                className="mt-3 mx-12 h-2 bg-white/10 rounded-full cursor-pointer hover:bg-white/20 transition-all opacity-0 group-hover/scroll:opacity-100"
            >
                <div
                    ref={thumbRef}
                    className="h-full bg-gradient-to-r from-violet-500 to-blue-500 rounded-full"
                    style={{ width: '20%', cursor: 'grab' }}
                    onMouseDown={handleThumbDrag}
                />
            </div>
        </div>
    );
}

export default function TVDetailView({ item }: TVDetailViewProps) {
    const metadata = item.metadata || {};
    const [selectedSeason, setSelectedSeason] = useState<number | null>(null);
    const [viewMode, setViewMode] = useState<'cards' | 'list'>('cards');
    const { isSidebarCollapsed } = useUIStore();

    const { data: episodes, isLoading } = useQuery({
        queryKey: ['series', item.id, 'episodes'],
        queryFn: async () => {
            const res = await api.get<MediaItem[]>(`/libraries/series/${item.id}/episodes`);
            return res.data;
        }
    });

    const { data: seasonsData } = useQuery({
        queryKey: ['series', item.id, 'seasons'],
        queryFn: async () => {
            const res = await api.get<Season[]>(`/libraries/series/${item.id}/seasons`);
            return res.data;
        }
    });

    const seasons = useMemo(() => {
        if (!episodes) return {};
        const grouped = episodes.reduce((acc, ep) => {
            const season = ep.seasonNumber || 1;
            if (!acc[season]) acc[season] = [];
            acc[season].push(ep);
            return acc;
        }, {} as Record<number, MediaItem[]>);

        Object.keys(grouped).forEach(key => {
            const k = parseInt(key);
            grouped[k].sort((a, b) => (a.episodeNumber || 0) - (b.episodeNumber || 0));
        });

        return grouped;
    }, [episodes]);

    const seasonNumbers = useMemo(() =>
        Object.keys(seasons).map(k => parseInt(k)).sort((a, b) => a - b),
        [seasons]
    );

    useMemo(() => {
        if (seasonNumbers.length > 0 && selectedSeason === null) {
            setSelectedSeason(seasonNumbers[0]);
        }
    }, [seasonNumbers, selectedSeason]);

    const currentEpisodes = selectedSeason !== null ? seasons[selectedSeason] || [] : [];

    const getEpisodePoster = (ep: MediaItem) => {
        const epMeta = ep.metadata || {};
        const stillUrl = epMeta.still;
        if (stillUrl) {
            return `/api/v1/image/proxy?url=${encodeURIComponent(stillUrl)}`;
        }
        return epMeta.thumbnail || ep.posterPath || item.posterPath;
    };

    const getSeasonPoster = (seasonNum: number): string | null => {
        if (!seasonsData) return item.posterPath || null;
        const season = seasonsData.find(s => s.number === seasonNum);
        return season?.poster || item.posterPath || null;
    };

    const getResolutionBadge = (ep: MediaItem) => {
        const resolution = ep.resolution || ep.metadata?.resolution;
        if (!resolution) return null;
        const res = resolution.toLowerCase();
        if (res.includes('2160') || res.includes('4k') || res.includes('uhd')) return '4K';
        if (res.includes('1080')) return 'FHD';
        if (res.includes('720')) return 'HD';
        if (res.includes('480') || res.includes('sd')) return 'SD';
        return null;
    };

    const parseDurationToSeconds = (duration: string | number | undefined): number => {
        if (!duration) return 0;
        if (typeof duration === 'number') return duration;
        const hours = duration.match(/(\d+)h/);
        const minutes = duration.match(/(\d+)m/);
        const seconds = duration.match(/(\d+)s/);
        return (hours ? parseInt(hours[1]) * 3600 : 0) +
            (minutes ? parseInt(minutes[1]) * 60 : 0) +
            (seconds ? parseInt(seconds[1]) : 0);
    };

    const formatTime = (seconds: number): string => {
        if (!seconds || seconds <= 0) return '0:00';
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        if (h > 0) {
            return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        }
        return `${m}:${s.toString().padStart(2, '0')}`;
    };

    const getEpisodeProgress = (ep: MediaItem): { resumeSeconds: number; progressPercent: number } => {
        const durationSeconds = parseDurationToSeconds(ep.duration);
        const progressPercent = ep.progress || 0;
        const resumeSeconds = durationSeconds > 0 ? (progressPercent / 100) * durationSeconds : 0;
        return { resumeSeconds, progressPercent };
    };

    // Episode Card Component
    const EpisodeCard = ({ ep }: { ep: MediaItem }) => {
        const resBadge = getResolutionBadge(ep);
        const { resumeSeconds, progressPercent } = getEpisodeProgress(ep);
        const hasProgress = progressPercent > 0 && progressPercent < 100;

        return (
            <Link to={`/play/${ep.id}`} className="group flex-shrink-0 w-72">
                <div className="relative rounded-xl overflow-hidden bg-white/5 border border-white/10 hover:border-violet-500/50 transition-all hover:shadow-lg hover:shadow-violet-500/10">
                    <div className="relative aspect-video bg-gradient-to-br from-gray-800 to-gray-900">
                        {getEpisodePoster(ep) ? (
                            <img src={getEpisodePoster(ep) || ''} alt={ep.title} className="w-full h-full object-cover" />
                        ) : (
                            <div className="w-full h-full flex items-center justify-center">
                                <span className="text-4xl text-gray-600">{ep.episodeNumber}</span>
                            </div>
                        )}
                        <div className="absolute inset-0 bg-black/50 opacity-0 group-hover:opacity-100 transition-opacity flex items-center justify-center">
                            <div className="w-14 h-14 rounded-full bg-white/20 backdrop-blur-sm flex items-center justify-center">
                                <Play className="w-7 h-7 text-white fill-current" />
                            </div>
                        </div>
                        {hasProgress && (
                            <div className="absolute bottom-0 left-0 right-0 h-1 bg-black/50">
                                <div className="h-full bg-gradient-to-r from-violet-500 to-blue-500" style={{ width: `${progressPercent}%` }} />
                            </div>
                        )}
                    </div>
                    <div className="p-3">
                        <h4 className="text-white font-medium text-sm line-clamp-1 group-hover:text-violet-400 transition-colors">
                            {ep.title}
                        </h4>
                        <div className="flex items-center gap-2 mt-2 flex-wrap">
                            <span className="px-2 py-0.5 rounded bg-white/10 text-xs font-bold text-white">E{ep.episodeNumber}</span>
                            {resBadge && (
                                <span className={`px-2 py-0.5 rounded text-xs font-bold ${resBadge === '4K' ? 'bg-gradient-to-r from-amber-500 to-orange-500 text-white' :
                                    resBadge === 'FHD' ? 'bg-blue-600 text-white' :
                                        resBadge === 'HD' ? 'bg-green-600 text-white' : 'bg-gray-600 text-white'
                                    }`}>{resBadge}</span>
                            )}
                            {ep.watched && (
                                <span className="flex items-center gap-1 px-2 py-0.5 rounded bg-green-600 text-xs font-bold text-white">
                                    <Check className="w-3 h-3" />Watched
                                </span>
                            )}
                        </div>
                        <div className="flex items-center gap-2 mt-1.5 text-xs text-gray-400">
                            {ep.duration && (
                                <span>
                                    {hasProgress && <span className="text-violet-400">{formatTime(resumeSeconds)}</span>}
                                    {hasProgress ? ' / ' : ''}{ep.duration}
                                </span>
                            )}
                            {ep.userRating && (
                                <span className="flex items-center gap-1 text-yellow-500">
                                    <Star className="w-3 h-3 fill-current" />{ep.userRating}
                                </span>
                            )}
                        </div>
                    </div>
                </div>
            </Link>
        );
    };

    // Episode List Row Component
    const EpisodeListRow = ({ ep }: { ep: MediaItem }) => {
        const resBadge = getResolutionBadge(ep);
        const { resumeSeconds, progressPercent } = getEpisodeProgress(ep);
        const hasProgress = progressPercent > 0 && progressPercent < 100;

        return (
            <Link to={`/play/${ep.id}`} className="group flex items-center gap-4 p-3 rounded-xl bg-white/5 border border-white/10 hover:border-violet-500/50 hover:bg-white/10 transition-all">
                {/* Thumbnail */}
                <div className="relative w-40 aspect-video rounded-lg overflow-hidden flex-shrink-0">
                    {getEpisodePoster(ep) ? (
                        <img src={getEpisodePoster(ep) || ''} alt={ep.title} className="w-full h-full object-cover" />
                    ) : (
                        <div className="w-full h-full bg-gray-800 flex items-center justify-center">
                            <span className="text-2xl text-gray-600">{ep.episodeNumber}</span>
                        </div>
                    )}
                    {hasProgress && (
                        <div className="absolute bottom-0 left-0 right-0 h-1 bg-black/50">
                            <div className="h-full bg-gradient-to-r from-violet-500 to-blue-500" style={{ width: `${progressPercent}%` }} />
                        </div>
                    )}
                </div>

                {/* Info */}
                <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                        <span className="px-2 py-0.5 rounded bg-white/10 text-xs font-bold text-white">E{ep.episodeNumber}</span>
                        <h4 className="text-white font-medium text-sm line-clamp-1 group-hover:text-violet-400 transition-colors">
                            {ep.title}
                        </h4>
                    </div>
                    <div className="flex items-center gap-3 mt-1.5">
                        {resBadge && (
                            <span className={`px-2 py-0.5 rounded text-xs font-bold ${resBadge === '4K' ? 'bg-gradient-to-r from-amber-500 to-orange-500 text-white' :
                                resBadge === 'FHD' ? 'bg-blue-600 text-white' :
                                    resBadge === 'HD' ? 'bg-green-600 text-white' : 'bg-gray-600 text-white'
                                }`}>{resBadge}</span>
                        )}
                        {ep.watched && (
                            <span className="flex items-center gap-1 px-2 py-0.5 rounded bg-green-600 text-xs font-bold text-white">
                                <Check className="w-3 h-3" />Watched
                            </span>
                        )}
                        {ep.duration && (
                            <span className="text-xs text-gray-400">
                                {hasProgress && <span className="text-violet-400">{formatTime(resumeSeconds)} / </span>}
                                {ep.duration}
                            </span>
                        )}
                        {ep.userRating && (
                            <span className="flex items-center gap-1 text-xs text-yellow-500">
                                <Star className="w-3 h-3 fill-current" />{ep.userRating}
                            </span>
                        )}
                    </div>
                </div>

                {/* Play Button */}
                <div className="w-10 h-10 rounded-full bg-violet-600/20 flex items-center justify-center group-hover:bg-violet-600 transition-colors">
                    <Play className="w-5 h-5 text-violet-400 group-hover:text-white fill-current" />
                </div>
            </Link>
        );
    };

    // Get the current background poster (season poster or fallback to show poster)
    const currentBackgroundPoster = useMemo(() => {
        if (selectedSeason !== null) {
            const seasonPoster = getSeasonPoster(selectedSeason);
            if (seasonPoster) return seasonPoster;
        }
        return item.posterPath || item.backdropPath || null;
    }, [selectedSeason, seasonsData, item.posterPath, item.backdropPath]);

    return (
        <>
            {/* Full-page background poster overlay */}
            {currentBackgroundPoster && (
                <div
                    className={`fixed top-16 right-0 bottom-0 z-0 pointer-events-none transition-all duration-300 ${isSidebarCollapsed ? 'left-20' : 'left-64'
                        }`}
                >
                    <img
                        src={currentBackgroundPoster}
                        alt=""
                        className="w-full h-full object-cover opacity-20 blur-sm transition-all duration-500"
                    />
                    <div className="absolute inset-0 bg-gradient-to-b from-transparent via-background/80 to-background" />
                    <div className="absolute inset-0 bg-gradient-to-r from-background/60 via-transparent to-background/60" />
                </div>
            )}

            <div className="space-y-6 relative z-10">
                {/* Seasons */}
                <div>
                    <h3 className="text-lg font-bold text-white mb-4">Seasons</h3>
                    <HorizontalScrollList className="py-3 px-2 -mx-2 -my-3">
                        {seasonNumbers.map(seasonNum => {
                            const seasonPoster = getSeasonPoster(seasonNum);
                            const episodeCount = seasons[seasonNum]?.length || 0;
                            const isSelected = selectedSeason === seasonNum;

                            return (
                                <button
                                    key={seasonNum}
                                    onClick={() => setSelectedSeason(seasonNum)}
                                    className={`flex-shrink-0 w-36 group transition-all ${isSelected ? 'scale-105' : 'opacity-80 hover:opacity-100'}`}
                                >
                                    <div className={`relative rounded-xl overflow-hidden border-2 transition-all ${isSelected ? 'border-violet-500 shadow-lg shadow-violet-500/30' : 'border-transparent hover:border-white/30'}`}>
                                        <div className="aspect-[2/3] bg-gradient-to-br from-gray-800 to-gray-900">
                                            {seasonPoster ? (
                                                <img src={seasonPoster} alt={`Season ${seasonNum}`} className="w-full h-full object-cover" />
                                            ) : (
                                                <div className="w-full h-full flex items-center justify-center">
                                                    <span className="text-4xl text-gray-600">{seasonNum}</span>
                                                </div>
                                            )}
                                        </div>
                                        {isSelected && <div className="absolute inset-0 bg-gradient-to-t from-violet-600/50 to-transparent" />}
                                    </div>
                                    <div className="mt-2 text-center">
                                        <p className={`font-semibold text-sm ${isSelected ? 'text-violet-400' : 'text-white'}`}>Season {seasonNum}</p>
                                        <p className="text-xs text-gray-400">{episodeCount} episodes</p>
                                    </div>
                                </button>
                            );
                        })}
                    </HorizontalScrollList>
                </div>

                {/* Episodes */}
                <div>
                    <div className="flex items-center justify-between mb-4">
                        <h3 className="text-lg font-bold text-white flex items-center gap-2">
                            <span>Episodes</span>
                            {selectedSeason && (
                                <span className="text-sm font-normal text-gray-400">
                                    ({seasons[selectedSeason]?.length || 0} episodes)
                                </span>
                            )}
                        </h3>

                        {/* View Toggle */}
                        <div className="flex items-center gap-1 p-1 rounded-lg bg-white/10">
                            <button
                                onClick={() => setViewMode('cards')}
                                className={`p-2 rounded-md transition-all ${viewMode === 'cards' ? 'bg-violet-600 text-white' : 'text-gray-400 hover:text-white'}`}
                                title="Card View"
                            >
                                <LayoutGrid className="w-4 h-4" />
                            </button>
                            <button
                                onClick={() => setViewMode('list')}
                                className={`p-2 rounded-md transition-all ${viewMode === 'list' ? 'bg-violet-600 text-white' : 'text-gray-400 hover:text-white'}`}
                                title="List View"
                            >
                                <List className="w-4 h-4" />
                            </button>
                        </div>
                    </div>

                    {isLoading ? (
                        <div className="flex gap-4 overflow-x-auto pb-4">
                            {[1, 2, 3, 4, 5].map(i => (
                                <div key={i} className="w-64 h-44 rounded-xl bg-white/5 animate-pulse flex-shrink-0" />
                            ))}
                        </div>
                    ) : currentEpisodes.length === 0 ? (
                        <div className="bg-white/5 rounded-xl p-8 text-center border border-white/10 border-dashed">
                            <p className="text-gray-400">No episodes found.</p>
                        </div>
                    ) : viewMode === 'cards' ? (
                        <HorizontalScrollList>
                            {currentEpisodes.map(ep => <EpisodeCard key={ep.id} ep={ep} />)}
                        </HorizontalScrollList>
                    ) : (
                        <div className="space-y-2 max-h-[600px] overflow-y-auto pr-2 scrollbar-thin scrollbar-thumb-white/20">
                            {currentEpisodes.map(ep => <EpisodeListRow key={ep.id} ep={ep} />)}
                        </div>
                    )}
                </div>

                {/* Cast */}
                {metadata.cast && Array.isArray(metadata.cast) && metadata.cast.length > 0 && (
                    <div>
                        <h3 className="text-lg font-bold text-white mb-4">Cast</h3>
                        <HorizontalScrollList>
                            {metadata.cast.slice(0, 15).map((actor: { name?: string; character?: string; image?: string }, i: number) => (
                                <div key={i} className="flex-shrink-0 w-28 text-center group">
                                    <div className="w-20 h-20 mx-auto rounded-full bg-gradient-to-br from-violet-600/30 to-blue-600/30 border-2 border-white/10 group-hover:border-violet-500/50 transition-all overflow-hidden flex items-center justify-center">
                                        {actor.image ? (
                                            <img
                                                src={`/api/v1/image/proxy?url=${encodeURIComponent(actor.image)}`}
                                                alt={actor.name}
                                                className="w-full h-full object-cover"
                                            />
                                        ) : (
                                            <span className="text-2xl text-gray-400">{actor.name?.charAt(0) || '?'}</span>
                                        )}
                                    </div>
                                    <p className="mt-2 text-sm text-white font-medium line-clamp-1">{actor.name}</p>
                                    <p className="text-xs text-gray-400 line-clamp-1">{actor.character}</p>
                                </div>
                            ))}
                        </HorizontalScrollList>
                    </div>
                )}
            </div>
        </>
    );
}
