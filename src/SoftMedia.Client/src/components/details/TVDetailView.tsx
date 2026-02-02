import { useMemo, useState, useRef, useEffect } from 'react';
import { type MediaItem } from '../../types';
import { Play, Star, Check, LayoutGrid, List } from 'lucide-react';
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
    selectedEpisodeId?: string | null;
    onEpisodeSelect?: (episode: MediaItem) => void;
    onDefaultQualityItemFound?: (episode: MediaItem) => void;
}

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
    const prevSrcRef = useRef<string | null | undefined>(src);
    const [displaySrc, setDisplaySrc] = useState(src);

    // Only reset state when src actually changes to a different value
    useEffect(() => {
        if (src !== prevSrcRef.current) {
            prevSrcRef.current = src;
            // Keep showing the old image until new one loads
            if (src) {
                setLoaded(false);
                setError(false);
                setDisplaySrc(src);
            } else {
                setDisplaySrc(src);
            }
        }
    }, [src]);

    if (!displaySrc || error) {
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
                src={displaySrc}
                alt={alt}
                className={`${className} transition-opacity duration-300 ${loaded ? 'opacity-100' : 'opacity-0'}`}
                onLoad={() => setLoaded(true)}
                onError={() => setError(true)}
                loading="lazy"
            />
        </div>
    );
}

import HorizontalScrollList from '../ui/HorizontalScrollList';

export default function TVDetailView({ item, selectedEpisodeId, onEpisodeSelect, onDefaultQualityItemFound }: TVDetailViewProps) {
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

    const { data: seasonsData, isLoading: seasonsLoading } = useQuery({
        queryKey: ['series', item.id, 'seasons'],
        queryFn: async () => {
            const res = await api.get<Season[]>(`/libraries/series/${item.id}/seasons`);
            return res.data;
        }
    });

    const seasons = useMemo(() => {
        if (!episodes) return {};
        const grouped = episodes.reduce((acc, ep) => {
            const season = ep.seasonNumber ?? 1;
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

    // Prefetch episode images when season changes
    useEffect(() => {
        if (currentEpisodes.length === 0) return;

        // Prefetch images in the background by creating Image objects
        currentEpisodes.forEach((ep) => {
            const epMeta = ep.metadata || {};
            const stillUrl = epMeta.still;

            let imgUrl: string | null = null;
            if (stillUrl && stillUrl.startsWith('/cache/')) {
                imgUrl = stillUrl;
            } else if (stillUrl && stillUrl.startsWith('http')) {
                imgUrl = `/api/v1/image/proxy?url=${encodeURIComponent(stillUrl)}`;
            } else {
                imgUrl = epMeta.thumbnail || ep.posterPath || item.posterPath || null;
            }

            if (imgUrl) {
                const img = new Image();
                img.src = imgUrl;
            }
        });
    }, [currentEpisodes, item.posterPath]);

    // Find a default quality item (representative episode) if none is selected
    useEffect(() => {
        if (!episodes || episodes.length === 0 || !onDefaultQualityItemFound) return;

        // Use the first episode of the first season as the default representative
        // (assuming episodes are sorted or we find the lowest season/episode)
        const firstSeasonNum = seasonNumbers[0];
        if (firstSeasonNum === undefined) return;

        const firstSeasonEpisodes = seasons[firstSeasonNum];
        if (firstSeasonEpisodes && firstSeasonEpisodes.length > 0) {
            const representativeEp = firstSeasonEpisodes[0];
            onDefaultQualityItemFound(representativeEp);
        }
    }, [episodes, seasonNumbers, seasons, onDefaultQualityItemFound]);

    const getEpisodePoster = (ep: MediaItem) => {
        const epMeta = ep.metadata || {};
        const stillUrl = epMeta.still;

        // If still URL is already a local cache path, use it directly
        if (stillUrl && stillUrl.startsWith('/cache/')) {
            return stillUrl;
        }

        // Use image proxy for remote stills (proxy will serve cached version if available)
        if (stillUrl && stillUrl.startsWith('http')) {
            return `/api/v1/image/proxy?url=${encodeURIComponent(stillUrl)}`;
        }

        // Fallback to thumbnail or poster
        return epMeta.thumbnail || ep.posterPath || item.posterPath;
    };

    const getSeasonPoster = (seasonNum: number): string | null => {
        // Return null while loading to show skeleton, not fallback poster
        if (!seasonsData) return null;
        const season = seasonsData.find(s => s.number === seasonNum);
        // Fall back to series poster if season data was loaded but has no poster
        return season?.poster || item.posterPath || null;
    };

    const getResolutionBadge = (ep: MediaItem) => {
        const resolution = ep.resolution || ep.metadata?.resolution;

        // Use explicit resolution string if available as primary source
        if (resolution) {
            const res = resolution.toLowerCase();
            if (res.includes('2160') || res.includes('4k') || res.includes('uhd')) return '4K';
            if (res.includes('1080') || res.includes('fhd')) return 'FHD';
            if (res.includes('720') || res.includes('hd')) return 'HD';
            if (res.includes('480') || res.includes('sd')) return 'SD';
        }

        // Fallback to dimensions check for irregular aspect ratios (e.g. 3840x1600)
        const h = ep.height || ep.metadata?.height || 0;
        const w = ep.width || ep.metadata?.width || 0;

        if (h >= 4300 || w >= 7600) return '8K';
        if (h >= 2100 || w >= 3800) return '4K';
        if (h >= 1400 || w >= 2500) return '1440p';
        if (h >= 1000 || w >= 1900) return 'FHD';
        if (h >= 700 || w >= 1260) return 'HD';
        if (h >= 480 || w >= 840) return '480p';
        if (h >= 360) return '360p';
        if (h >= 240) return '240p';
        return 'SD';

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
        const isSelected = selectedEpisodeId === ep.id;

        return (
            <div
                onClick={() => onEpisodeSelect?.(ep)}
                className={`group flex-shrink-0 w-72 cursor-pointer transition-all rounded-xl border ${isSelected ? 'border-violet-500 bg-white/10 shadow-lg shadow-violet-500/20' : 'bg-white/5 border-white/10 hover:border-violet-500/50 hover:bg-white/10 hover:shadow-lg hover:shadow-violet-500/10'}`}
            >
                <div className="relative rounded-xl overflow-hidden aspect-video bg-gradient-to-br from-gray-800 to-gray-900 mx-1 mt-1">
                    <LoadingImage
                        src={getEpisodePoster(ep)}
                        alt={ep.title}
                        className="w-full h-full object-cover"
                        fallback={
                            <div className="w-full h-full flex items-center justify-center bg-gradient-to-br from-gray-800 to-gray-900">
                                <span className="text-4xl text-gray-600">{ep.episodeNumber}</span>
                            </div>
                        }
                    />
                    <div className="absolute inset-0 bg-black/50 opacity-0 group-hover:opacity-100 transition-opacity flex items-center justify-center pointer-events-none">
                        <div className="pointer-events-auto">
                            <Link
                                to={`/play/${ep.id}`}
                                onClick={(e) => e.stopPropagation()}
                                className="flex items-center justify-center w-14 h-14 rounded-full bg-white/20 backdrop-blur-sm hover:bg-white/30 transition-colors"
                            >
                                <Play className="w-7 h-7 text-white fill-current" />
                            </Link>
                        </div>
                    </div>
                    {hasProgress && (
                        <div className="absolute bottom-0 left-0 right-0 h-1 bg-black/50">
                            <div className="h-full bg-gradient-to-r from-blue-500 to-violet-500" style={{ width: `${progressPercent}%` }} />
                        </div>
                    )}
                </div>
                <div className="p-3">
                    <h4 className={`text-sm font-medium line-clamp-1 transition-colors ${isSelected ? 'text-violet-400' : 'text-white group-hover:text-violet-400'}`}>
                        {ep.title}
                    </h4>
                    <div className="flex items-center gap-2 mt-2 flex-wrap">
                        <span className="px-2 py-0.5 rounded bg-white/10 text-xs font-bold text-white">E{ep.episodeNumber}</span>
                        {resBadge && (
                            <span className={`px-2 py-0.5 rounded text-xs font-bold ${resBadge === '8K' ? 'bg-gradient-to-r from-pink-500 to-rose-500 text-white' :
                                resBadge === '4K' ? 'bg-gradient-to-r from-amber-500 to-orange-500 text-white' :
                                    resBadge === '1440p' ? 'bg-cyan-600 text-white' :
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
        );
    };

    // Episode List Row Component
    const EpisodeListRow = ({ ep }: { ep: MediaItem }) => {
        const resBadge = getResolutionBadge(ep);
        const { resumeSeconds, progressPercent } = getEpisodeProgress(ep);
        const hasProgress = progressPercent > 0 && progressPercent < 100;
        const isSelected = selectedEpisodeId === ep.id;

        return (
            <div
                onClick={() => onEpisodeSelect?.(ep)}
                className={`group flex items-center gap-4 p-3 rounded-xl border transition-all cursor-pointer ${isSelected ? 'border-violet-500 bg-white/10' : 'bg-white/5 border-white/10 hover:border-violet-500/50 hover:bg-white/10'}`}
            >
                {/* Thumbnail */}
                <div className="relative w-40 aspect-video rounded-lg overflow-hidden flex-shrink-0">
                    <LoadingImage
                        src={getEpisodePoster(ep)}
                        alt={ep.title}
                        className="w-full h-full object-cover"
                        fallback={
                            <div className="w-full h-full bg-gray-800 flex items-center justify-center">
                                <span className="text-2xl text-gray-600">{ep.episodeNumber}</span>
                            </div>
                        }
                    />
                    {hasProgress && (
                        <div className="absolute bottom-0 left-0 right-0 h-1 bg-black/50">
                            <div className="h-full bg-gradient-to-r from-blue-500 to-violet-500" style={{ width: `${progressPercent}%` }} />
                        </div>
                    )}
                </div>

                {/* Info */}
                <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                        <span className="px-2 py-0.5 rounded bg-white/10 text-xs font-bold text-white">E{ep.episodeNumber}</span>
                        <h4 className={`font-medium text-sm line-clamp-1 transition-colors ${isSelected ? 'text-violet-400' : 'text-white group-hover:text-violet-400'}`}>
                            {ep.title}
                        </h4>
                    </div>
                    <div className="flex items-center gap-3 mt-1.5">
                        {resBadge && (
                            <span className={`px-2 py-0.5 rounded text-xs font-bold ${resBadge === '8K' ? 'bg-gradient-to-r from-pink-500 to-rose-500 text-white' :
                                resBadge === '4K' ? 'bg-gradient-to-r from-amber-500 to-orange-500 text-white' :
                                    resBadge === '1440p' ? 'bg-cyan-600 text-white' :
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
                <Link
                    to={`/play/${ep.id}`}
                    onClick={(e) => e.stopPropagation()}
                    className="w-10 h-10 rounded-full bg-violet-600/20 flex items-center justify-center group-hover:bg-violet-600 transition-colors"
                >
                    <Play className="w-5 h-5 text-violet-400 group-hover:text-white fill-current" />
                </Link>
            </div>
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
                    <HorizontalScrollList className="py-3 px-20 -mx-14 -my-3">
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
                                            {seasonsLoading ? (
                                                <div className="w-full h-full animate-pulse bg-gradient-to-br from-gray-700 via-gray-600 to-gray-700" />
                                            ) : seasonPoster ? (
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
                                    <div className="w-20 h-20 mx-auto rounded-full bg-gradient-to-br from-blue-600/30 to-violet-600/30 border-2 border-white/10 group-hover:border-violet-500/50 transition-all overflow-hidden flex items-center justify-center">
                                        {actor.image ? (
                                            <img
                                                src={actor.image.startsWith('/cache/') ? actor.image : `/api/v1/image/proxy?url=${encodeURIComponent(actor.image)}`}
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
