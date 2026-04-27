import { useMemo, useState, useEffect, useRef, useCallback, useImperativeHandle } from 'react';
import { type MediaItem } from '../../types';
import { Play, Star, Check, LayoutGrid, List } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import api from '../../services/api';
import { Link } from 'react-router-dom';
import { useUIStore } from '../../store/uiStore';
import LoadingImage from '../ui/LoadingImage';
import useSequentialReveal from '../../hooks/useSequentialReveal';
import HorizontalScrollList, { type HorizontalScrollListHandle } from '../ui/HorizontalScrollList';
import CastStripItem from './CastStripItem';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';

interface ScrollToIndexHandle {
    scrollToIndex: (index: number) => void;
}

// Above this many episodes per season we switch to a virtualized render path
// to keep the DOM small for very long runs (e.g. One Piece, Naruto, daily soaps).
const VIRTUALIZATION_THRESHOLD = 50;
const VIRTUAL_CARD_WIDTH_PX = 304;   // w-72 (288px) + gap-4 (16px)
const VIRTUAL_CARD_HEIGHT_PX = 290;
const VIRTUAL_LIST_ROW_HEIGHT_PX = 122;

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

// --- Pure utility functions (module-scope to avoid re-creation) ---

function parseDurationToSeconds(duration: string | number | undefined): number {
    if (!duration) return 0;
    if (typeof duration === 'number') return duration;
    const hours = duration.match(/(\d+)h/);
    const minutes = duration.match(/(\d+)m/);
    const seconds = duration.match(/(\d+)s/);
    return (hours ? parseInt(hours[1]) * 3600 : 0) +
        (minutes ? parseInt(minutes[1]) * 60 : 0) +
        (seconds ? parseInt(seconds[1]) : 0);
}

function formatTime(seconds: number): string {
    if (!seconds || seconds <= 0) return '0:00';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    if (h > 0) {
        return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
    }
    return `${m}:${s.toString().padStart(2, '0')}`;
}

function getResolutionBadge(ep: MediaItem): string | null {
    const resolution = ep.resolution || ep.metadata?.resolution;

    if (resolution) {
        const res = resolution.toLowerCase();
        if (res.includes('2160') || res.includes('4k') || res.includes('uhd')) return '4K';
        if (res.includes('1080') || res.includes('fhd')) return 'FHD';
        if (res.includes('720') || res.includes('hd')) return 'HD';
        if (res.includes('480') || res.includes('sd')) return 'SD';
    }

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
}

function getEpisodeProgress(ep: MediaItem): { resumeSeconds: number; progressPercent: number } {
    const durationSeconds = parseDurationToSeconds(ep.duration);
    const progressPercent = ep.progress || 0;
    const resumeSeconds = durationSeconds > 0 ? (progressPercent / 100) * durationSeconds : 0;
    return { resumeSeconds, progressPercent };
}

const resBadgeClass = (badge: string | null) => {
    if (!badge) return '';
    switch (badge) {
        case '8K': return 'bg-gradient-to-r from-pink-500 to-rose-500 text-white';
        case '4K': return 'bg-gradient-to-r from-amber-500 to-orange-500 text-white';
        case '1440p': return 'bg-cyan-600 text-white';
        case 'FHD': return 'bg-blue-600 text-white';
        case 'HD': return 'bg-green-600 text-white';
        default: return 'bg-gray-600 text-white';
    }
};

// --- Episode components (module-scope for stable React identity) ---

interface EpisodeCardProps {
    ep: MediaItem;
    posterSrc: string | null | undefined;
    isSelected: boolean;
    onSelect: () => void;
    groupReady: boolean;
    onImageLoad: () => void;
    onImageError: () => void;
}

function EpisodeCard({ ep, posterSrc, isSelected, onSelect, groupReady, onImageLoad, onImageError }: EpisodeCardProps) {
    const resBadge = getResolutionBadge(ep);
    const { resumeSeconds, progressPercent } = getEpisodeProgress(ep);
    const hasProgress = progressPercent > 0 && progressPercent < 100;

    return (
        <div
            role="button"
            tabIndex={0}
            aria-label={`Episode ${ep.episodeNumber}: ${ep.title}`}
            aria-pressed={isSelected}
            onClick={onSelect}
            onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onSelect();
                }
            }}
            className={`group flex-shrink-0 w-72 cursor-pointer transition-all rounded-xl border focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 focus-visible:ring-offset-2 focus-visible:ring-offset-background ${isSelected ? 'border-violet-500 bg-white/10 shadow-lg shadow-violet-500/20' : 'bg-white/5 border-white/10 hover:border-violet-500/50 hover:bg-white/10 hover:shadow-lg hover:shadow-violet-500/10'}`}
        >
            <div className="relative rounded-xl overflow-hidden aspect-video bg-gradient-to-br from-gray-800 to-gray-900 mx-1 mt-1">
                <LoadingImage
                    src={posterSrc}
                    alt={ep.title}
                    className="w-full h-full object-cover"
                    groupReady={groupReady}
                    onLoad={onImageLoad}
                    onError={onImageError}
                    fallback={
                        <div className="w-full h-full flex items-center justify-center bg-gradient-to-br from-gray-800 to-gray-900">
                            <span className="text-4xl text-gray-600">{ep.episodeNumber}</span>
                        </div>
                    }
                />
                <div className="absolute inset-0 bg-black/50 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity flex items-center justify-center pointer-events-none">
                    <div className="pointer-events-auto">
                        <Link
                            to={`/play/${ep.id}`}
                            onClick={(e) => e.stopPropagation()}
                            aria-label={`Play episode ${ep.episodeNumber}`}
                            className="flex items-center justify-center w-14 h-14 rounded-full bg-white/20 backdrop-blur-sm hover:bg-white/30 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500"
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
                        <span className={`px-2 py-0.5 rounded text-xs font-bold ${resBadgeClass(resBadge)}`}>{resBadge}</span>
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
}

function EpisodeListRow({ ep, posterSrc, isSelected, onSelect, groupReady, onImageLoad, onImageError }: EpisodeCardProps) {
    const resBadge = getResolutionBadge(ep);
    const { resumeSeconds, progressPercent } = getEpisodeProgress(ep);
    const hasProgress = progressPercent > 0 && progressPercent < 100;

    return (
        <div
            role="button"
            tabIndex={0}
            aria-label={`Episode ${ep.episodeNumber}: ${ep.title}`}
            aria-pressed={isSelected}
            onClick={onSelect}
            onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    onSelect();
                }
            }}
            className={`group flex items-center gap-4 p-3 rounded-xl border transition-all cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 focus-visible:ring-offset-2 focus-visible:ring-offset-background ${isSelected ? 'border-violet-500 bg-white/10' : 'bg-white/5 border-white/10 hover:border-violet-500/50 hover:bg-white/10'}`}
        >
            {/* Thumbnail */}
            <div className="relative w-40 aspect-video rounded-lg overflow-hidden flex-shrink-0">
                <LoadingImage
                    src={posterSrc}
                    alt={ep.title}
                    className="w-full h-full object-cover"
                    groupReady={groupReady}
                    onLoad={onImageLoad}
                    onError={onImageError}
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
                        <span className={`px-2 py-0.5 rounded text-xs font-bold ${resBadgeClass(resBadge)}`}>{resBadge}</span>
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
                aria-label={`Play episode ${ep.episodeNumber}`}
                className="w-10 h-10 rounded-full bg-violet-600/20 flex items-center justify-center group-hover:bg-violet-600 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500"
            >
                <Play className="w-5 h-5 text-violet-400 group-hover:text-white fill-current" />
            </Link>
        </div>
    );
}

interface VirtualizedEpisodeListProps {
    episodes: MediaItem[];
    selectedEpisodeId?: string | null;
    onEpisodeSelect?: (episode: MediaItem) => void;
    getPoster: (ep: MediaItem) => string | null | undefined;
    ref?: React.Ref<ScrollToIndexHandle>;
}

function VirtualizedEpisodeList({ episodes, selectedEpisodeId, onEpisodeSelect, getPoster, ref }: VirtualizedEpisodeListProps) {
    const scrollRef = useRef<HTMLDivElement>(null);
    const virtualizer = useVirtualizer({
        count: episodes.length,
        getScrollElement: () => scrollRef.current,
        estimateSize: () => VIRTUAL_LIST_ROW_HEIGHT_PX,
        overscan: 8,
    });

    useImperativeHandle(ref, () => ({
        scrollToIndex: (index: number) => {
            virtualizer.scrollToIndex(index, { align: 'start' });
        },
    }), [virtualizer]);

    return (
        <div
            ref={scrollRef}
            className="max-h-[600px] overflow-y-auto pr-2 scrollbar-thin scrollbar-thumb-white/20"
        >
            <div style={{ height: `${virtualizer.getTotalSize()}px`, width: '100%', position: 'relative' }}>
                {virtualizer.getVirtualItems().map(vItem => {
                    const ep = episodes[vItem.index];
                    return (
                        <div
                            key={ep.id}
                            style={{
                                position: 'absolute',
                                top: 0,
                                left: 0,
                                width: '100%',
                                height: `${vItem.size}px`,
                                transform: `translateY(${vItem.start}px)`,
                                paddingBottom: '8px',
                            }}
                        >
                            <EpisodeListRow
                                ep={ep}
                                posterSrc={getPoster(ep)}
                                isSelected={selectedEpisodeId === ep.id}
                                onSelect={() => onEpisodeSelect?.(ep)}
                                groupReady={true}
                                onImageLoad={() => {}}
                                onImageError={() => {}}
                            />
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

// --- Main component ---

export default function TVDetailView({ item, selectedEpisodeId, onEpisodeSelect, onDefaultQualityItemFound }: TVDetailViewProps) {
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

    useEffect(() => {
        if (seasonNumbers.length > 0 && selectedSeason === null) {
            setSelectedSeason(seasonNumbers[0]);
        }
    }, [seasonNumbers, selectedSeason]);

    const currentEpisodes = useMemo(
        () => (selectedSeason !== null ? seasons[selectedSeason] || [] : []),
        [seasons, selectedSeason]
    );

    // Find a default quality item (representative episode) if none is selected
    useEffect(() => {
        if (!episodes || episodes.length === 0 || !onDefaultQualityItemFound) return;

        const firstSeasonNum = seasonNumbers[0];
        if (firstSeasonNum === undefined) return;

        const firstSeasonEpisodes = seasons[firstSeasonNum];
        if (firstSeasonEpisodes && firstSeasonEpisodes.length > 0) {
            const representativeEp = firstSeasonEpisodes[0];
            onDefaultQualityItemFound(representativeEp);
        }
    }, [episodes, seasonNumbers, seasons, onDefaultQualityItemFound]);

    const getEpisodePoster = (ep: MediaItem, width = 400) => {
        if (ep.backdropPath) {
            if (ep.backdropPath.startsWith('/cache/')) return ep.backdropPath;
            if (ep.backdropPath.startsWith('http')) return attachAuthToApiUrl(`/api/v1/image/proxy?url=${encodeURIComponent(ep.backdropPath)}&width=${width}`);
            return ep.backdropPath;
        }

        const stillUrl = (ep.metadata || {}).still;
        if (stillUrl) {
            if (stillUrl.startsWith('/cache/')) return stillUrl;
            if (stillUrl.startsWith('http')) return attachAuthToApiUrl(`/api/v1/image/proxy?url=${encodeURIComponent(stillUrl)}&width=${width}`);
        }

        return ep.posterPath || item.posterPath;
    };

    const getSeasonPoster = useCallback((seasonNum: number): string | null => {
        if (!seasonsData) return null;
        const season = seasonsData.find(s => s.number === seasonNum);
        const poster = season?.poster || item.posterPath || null;
        if (!poster) return null;
        if (poster.includes('/image/proxy?')) return attachAuthToApiUrl(`${poster}&width=200`);
        return poster;
    }, [seasonsData, item.posterPath]);

    // Sequential left-to-right reveal for season posters and episode stills.
    // The browser parallel-loads images; the cascade's stuck-timeout handles
    // any out-of-order arrivals so the reveal never pauses.
    const seasonReveal = useSequentialReveal(seasonNumbers.length);
    const episodeReveal = useSequentialReveal(currentEpisodes.length);

    // Very long seasons (hundreds or thousands of episodes) use a virtualized
    // render path to keep the DOM small. Cascade reveal is skipped in that
    // path since a left-to-right wave across a 1000-item list is pointless.
    const shouldVirtualize = currentEpisodes.length > VIRTUALIZATION_THRESHOLD;

    // Reset the episode cascade on season change
    useEffect(() => {
        episodeReveal.reset();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [selectedSeason]);

    // Get the current background poster (season poster or fallback to show poster)
    const currentBackgroundPoster = useMemo(() => {
        if (selectedSeason !== null) {
            const seasonPoster = getSeasonPoster(selectedSeason);
            if (seasonPoster) return seasonPoster;
        }
        return item.posterPath || item.backdropPath || null;
    }, [selectedSeason, getSeasonPoster, item.posterPath, item.backdropPath]);

    // Jump-to-episode wiring (only meaningful in the virtualized path)
    const cardScrollerRef = useRef<HorizontalScrollListHandle>(null);
    const listScrollerRef = useRef<ScrollToIndexHandle>(null);
    const [jumpInput, setJumpInput] = useState('');

    useEffect(() => {
        setJumpInput('');
    }, [selectedSeason]);

    const handleJumpToEpisode = () => {
        const target = parseInt(jumpInput, 10);
        if (Number.isNaN(target)) return;
        const idx = currentEpisodes.findIndex(e => (e.episodeNumber ?? 0) === target);
        if (idx === -1) return;
        if (viewMode === 'cards') {
            cardScrollerRef.current?.scrollToIndex(idx);
        } else {
            listScrollerRef.current?.scrollToIndex(idx);
        }
    };

    const maxEpisodeNumber = useMemo(() => {
        if (currentEpisodes.length === 0) return 1;
        return currentEpisodes.reduce((max, e) => Math.max(max, e.episodeNumber ?? 0), 1);
    }, [currentEpisodes]);

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
                        className="w-full h-full object-cover opacity-20 blur-sm transition-all duration-200"
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
                        {seasonNumbers.map((seasonNum, idx) => {
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
                                            ) : (
                                                <LoadingImage
                                                    src={seasonPoster}
                                                    alt={`Season ${seasonNum}`}
                                                    className="w-full h-full object-cover"
                                                    groupReady={seasonReveal.isRevealed(idx)}
                                                    onLoad={() => seasonReveal.onImageLoad(idx)}
                                                    onError={() => seasonReveal.onImageError(idx)}
                                                    fallback={
                                                        <div className="w-full h-full flex items-center justify-center">
                                                            <span className="text-4xl text-gray-600">{seasonNum}</span>
                                                        </div>
                                                    }
                                                />
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

                        <div className="flex items-center gap-3">
                            {/* Jump to episode (only useful on very long seasons) */}
                            {shouldVirtualize && (
                                <form
                                    onSubmit={(e) => {
                                        e.preventDefault();
                                        handleJumpToEpisode();
                                    }}
                                    className="flex items-center gap-2"
                                >
                                    <label htmlFor="jump-episode" className="text-xs text-gray-400">
                                        Jump to ep.
                                    </label>
                                    <input
                                        id="jump-episode"
                                        type="number"
                                        min={1}
                                        max={maxEpisodeNumber}
                                        value={jumpInput}
                                        onChange={(e) => setJumpInput(e.target.value)}
                                        placeholder="#"
                                        className="bg-white/10 px-2 py-1 rounded text-sm w-20 text-white placeholder-gray-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500"
                                    />
                                    <button
                                        type="submit"
                                        className="px-2 py-1 rounded text-xs font-medium bg-violet-600 text-white hover:bg-violet-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500"
                                    >
                                        Go
                                    </button>
                                </form>
                            )}

                            {/* View Toggle */}
                            <div className="flex items-center gap-1 p-1 rounded-lg bg-white/10">
                                <button
                                    onClick={() => setViewMode('cards')}
                                    className={`p-2 rounded-md transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 ${viewMode === 'cards' ? 'bg-violet-600 text-white' : 'text-gray-400 hover:text-white'}`}
                                    title="Card View"
                                >
                                    <LayoutGrid className="w-4 h-4" />
                                </button>
                                <button
                                    onClick={() => setViewMode('list')}
                                    className={`p-2 rounded-md transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 ${viewMode === 'list' ? 'bg-violet-600 text-white' : 'text-gray-400 hover:text-white'}`}
                                    title="List View"
                                >
                                    <List className="w-4 h-4" />
                                </button>
                            </div>
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
                        shouldVirtualize ? (
                            <HorizontalScrollList
                                ref={cardScrollerRef}
                                virtualized
                                itemCount={currentEpisodes.length}
                                estimateItemSize={VIRTUAL_CARD_WIDTH_PX}
                                itemHeightPx={VIRTUAL_CARD_HEIGHT_PX}
                                renderItem={(i) => {
                                    const ep = currentEpisodes[i];
                                    return (
                                        <EpisodeCard
                                            ep={ep}
                                            posterSrc={getEpisodePoster(ep)}
                                            isSelected={selectedEpisodeId === ep.id}
                                            onSelect={() => onEpisodeSelect?.(ep)}
                                            groupReady={true}
                                            onImageLoad={() => {}}
                                            onImageError={() => {}}
                                        />
                                    );
                                }}
                            />
                        ) : (
                            <HorizontalScrollList>
                                {currentEpisodes.map((ep, i) => (
                                    <EpisodeCard
                                        key={ep.id}
                                        ep={ep}
                                        posterSrc={getEpisodePoster(ep)}
                                        isSelected={selectedEpisodeId === ep.id}
                                        onSelect={() => onEpisodeSelect?.(ep)}
                                        groupReady={episodeReveal.isRevealed(i)}
                                        onImageLoad={() => episodeReveal.onImageLoad(i)}
                                        onImageError={() => episodeReveal.onImageError(i)}
                                    />
                                ))}
                            </HorizontalScrollList>
                        )
                    ) : shouldVirtualize ? (
                        <VirtualizedEpisodeList
                            ref={listScrollerRef}
                            episodes={currentEpisodes}
                            selectedEpisodeId={selectedEpisodeId}
                            onEpisodeSelect={onEpisodeSelect}
                            getPoster={(ep) => getEpisodePoster(ep, 250)}
                        />
                    ) : (
                        <div className="space-y-2 max-h-[600px] overflow-y-auto pr-2 scrollbar-thin scrollbar-thumb-white/20">
                            {currentEpisodes.map((ep, i) => (
                                <EpisodeListRow
                                    key={ep.id}
                                    ep={ep}
                                    posterSrc={getEpisodePoster(ep, 250)}
                                    isSelected={selectedEpisodeId === ep.id}
                                    onSelect={() => onEpisodeSelect?.(ep)}
                                    groupReady={episodeReveal.isRevealed(i)}
                                    onImageLoad={() => episodeReveal.onImageLoad(i)}
                                    onImageError={() => episodeReveal.onImageError(i)}
                                />
                            ))}
                        </div>
                    )}
                </div>

                {/* Cast */}
                {item.cast && item.cast.length > 0 && (
                    <div>
                        <h3 className="text-lg font-bold text-white mb-4">Cast</h3>
                        <HorizontalScrollList>
                            {item.cast.slice(0, 10).map((member) => (
                                <CastStripItem key={member.id} member={member} />
                            ))}
                        </HorizontalScrollList>
                    </div>
                )}
            </div>
        </>
    );
}
