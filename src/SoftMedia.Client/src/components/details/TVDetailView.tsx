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
import { ExtrasSection } from './ExtrasSection';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { formatRuntime } from '../../lib/utils';

interface ScrollToIndexHandle {
    scrollToIndex: (index: number) => void;
}

// Above this many episodes per season we switch to a virtualized render path
// to keep the DOM small for very long runs (e.g. One Piece, Naruto, daily soaps).
const VIRTUALIZATION_THRESHOLD = 50;
const VIRTUAL_CARD_WIDTH_PX = 304;   // w-72 (288px) + gap-4 (16px)
const VIRTUAL_CARD_HEIGHT_PX = 290;
const VIRTUAL_LIST_ROW_HEIGHT_PX = 122;
// Frames the resume reveal keeps retrying for (~10 frames ≈ 160ms), and how long
// it keeps re-issuing a virtualized scroll while the virtualizer measures itself.
const REVEAL_MAX_FRAMES = 10;
const VIRTUAL_SCROLL_SETTLE_FRAMES = 3;
// How long the episode list waits for the resume lookup before giving up and
// showing season 1. Long enough to avoid the season-1-then-jump flicker, short
// enough that a stalled request can't leave the list stuck on a skeleton.
const RESUME_WAIT_MS = 1500;

interface Season {
    number: number;
    poster: string | null;
    episodeCount: number | null;
    premiereDate: string | null;
}

interface TVDetailViewProps {
    item: MediaItem;
    selectedEpisodeId?: string | null;
    /** Episode the Play button would resume (server next-episode resolver), or null for none. */
    resumeEpisodeId?: string | null;
    /** True while that lookup is still in flight, so the strips can hold off on defaulting to season 1. */
    resumeEpisodePending?: boolean;
    /** Whether that episode has a saved playback position (as opposed to being simply next up). */
    resumeHasPosition?: boolean;
    onEpisodeSelect?: (episode: MediaItem) => void;
    onDefaultQualityItemFound?: (episode: MediaItem) => void;
}

// --- Pure utility functions (module-scope to avoid re-creation) ---

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
    // SR-WI-063: the formatted `duration` string is gone from the DTO; the raw
    // seconds field is the single source of truth.
    const durationSeconds = ep.durationSeconds ?? 0;
    const progressPercent = ep.progress || 0;
    const resumeSeconds = durationSeconds > 0 ? (progressPercent / 100) * durationSeconds : 0;
    return { resumeSeconds, progressPercent };
}

/**
 * Brings an auto-selected season/episode into view by nudging the scroller it
 * lives in — and nothing else. Deliberately not `scrollIntoView`: the strips
 * usually sit below the fold on load, and a resume selection must never yank
 * the page around on arrival. `boundary` (the section wrapper) stops the walk
 * short of the page scroller, so a strip that doesn't overflow simply does
 * nothing instead of scrolling the document to it.
 */
export function scrollSelectionIntoView(el: HTMLElement, boundary: HTMLElement) {
    for (let c = el.parentElement; c && c !== boundary; c = c.parentElement) {
        const horizontal = c.scrollWidth > c.clientWidth + 1;
        const vertical = c.scrollHeight > c.clientHeight + 1;
        if (!horizontal && !vertical) continue;

        const er = el.getBoundingClientRect();
        const cr = c.getBoundingClientRect();
        if (horizontal) {
            c.scrollLeft += (er.left - cr.left) - (cr.width - er.width) / 2;
        } else {
            c.scrollTop += (er.top - cr.top) - (cr.height - er.height) / 2;
        }
        return;
    }
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
            data-episode-id={ep.id}
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
                    {formatRuntime(ep.durationSeconds) && (
                        <span>
                            {hasProgress && <span className="text-violet-400">{formatTime(resumeSeconds)}</span>}
                            {hasProgress ? ' / ' : ''}{formatRuntime(ep.durationSeconds)}
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
            data-episode-id={ep.id}
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
                    {formatRuntime(ep.durationSeconds) && (
                        <span className="text-xs text-gray-400">
                            {hasProgress && <span className="text-violet-400">{formatTime(resumeSeconds)} / </span>}
                            {formatRuntime(ep.durationSeconds)}
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

export default function TVDetailView({ item, selectedEpisodeId, resumeEpisodeId, resumeEpisodePending, resumeHasPosition, onEpisodeSelect, onDefaultQualityItemFound }: TVDetailViewProps) {
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

    // --- Resume-aware initial selection ---
    //
    // A show you're partway through should open on the season and episode the
    // Play button would resume, not on season 1 episode 1. The target is the
    // server's next-episode resolver (the same one Play uses), so the highlight
    // and the button can never point at different episodes.

    // A manual click pins the selection: a late-arriving next-episode response
    // must not yank the strips out from under the user.
    const [userPinnedSelection, setUserPinnedSelection] = useState(false);
    const appliedResumeIdRef = useRef<string | null>(null);
    // What the auto-selection still needs to scroll into view. State, not a ref:
    // the reveal below has to re-run when a target is requested, and the resume
    // season is often the one already selected (single-season shows), so a
    // selectedSeason change can't be the trigger.
    const [revealRequest, setRevealRequest] = useState<{ season: number; episodeId: string } | null>(null);
    const [resumeWaitExpired, setResumeWaitExpired] = useState(false);

    useEffect(() => {
        if (!resumeEpisodePending) return;
        const timer = setTimeout(() => setResumeWaitExpired(true), RESUME_WAIT_MS);
        return () => clearTimeout(timer);
    }, [resumeEpisodePending]);

    // Reset when the page swaps to a different series (this component stays
    // mounted). Change only, never the mount run — see the matching guard in
    // MediaDetailPage for why a reset that also fires on mount is a hazard here.
    const previousItemIdRef = useRef(item.id);
    useEffect(() => {
        if (previousItemIdRef.current === item.id) return;
        previousItemIdRef.current = item.id;
        setSelectedSeason(null);
        setUserPinnedSelection(false);
        setRevealRequest(null);
        setResumeWaitExpired(false);
        appliedResumeIdRef.current = null;
    }, [item.id]);

    const resumeEpisode = useMemo(
        () => (resumeEpisodeId ? episodes?.find(e => e.id === resumeEpisodeId) ?? null : null),
        [episodes, resumeEpisodeId]
    );

    // The one case to skip is the untouched series: the resolver hands back
    // episode 1 of season 1 for a show nobody has started, and highlighting it
    // there would be a phantom "you were here". Everything else — a saved
    // position, a target past the first episode, or progress on the target
    // itself — is a genuine resume.
    //
    // Deliberately decided from the resolver's own answer rather than inferred
    // from the episode list's progress fields: the server only fills Progress in
    // when it knows the episode's duration, so a library whose durations never
    // probed reports no progress anywhere and would silently lose the selection
    // while the Resume button (which reads the raw position) still worked.
    const shouldAutoSelect = useMemo(() => {
        if (!resumeEpisode) return false;
        if (resumeHasPosition || resumeEpisode.watched || (resumeEpisode.progress ?? 0) > 0) return true;
        const firstEpisodeId = seasonNumbers.length > 0 ? seasons[seasonNumbers[0]]?.[0]?.id : undefined;
        return resumeEpisode.id !== firstEpisodeId;
    }, [resumeEpisode, resumeHasPosition, seasons, seasonNumbers]);

    useEffect(() => {
        if (userPinnedSelection || seasonNumbers.length === 0) return;

        if (shouldAutoSelect && resumeEpisode && appliedResumeIdRef.current !== resumeEpisode.id) {
            appliedResumeIdRef.current = resumeEpisode.id;
            const season = resumeEpisode.seasonNumber ?? seasonNumbers[0];
            const target = seasonNumbers.includes(season) ? season : seasonNumbers[0];
            setSelectedSeason(target);
            setRevealRequest({ season: target, episodeId: resumeEpisode.id });
            onEpisodeSelect?.(resumeEpisode);
            return;
        }

        // Fall back to the first season — but wait for the resume lookup to
        // settle first, so the strips don't snap to season 1 and then jump to
        // season 4 a moment later. (The parent reports pending only for a real
        // series query, so this can never wait forever.)
        //
        // The applied-ref check is what keeps this branch off the resume's back.
        // `selectedSeason` here is the value from the render this effect closed
        // over, so it still reads null on any re-run that happens before the
        // state lands — StrictMode's double-invoked mount effect being the case
        // that bit: run 1 selects season 4, run 2 sees a stale null and would
        // reset the strip to season 1. The ref updates synchronously, so it is
        // the only honest record of "a resume selection has been made".
        if (selectedSeason === null && appliedResumeIdRef.current === null && (!resumeEpisodePending || resumeWaitExpired)) {
            setSelectedSeason(seasonNumbers[0]);
        }
    }, [seasonNumbers, selectedSeason, resumeEpisode, resumeEpisodePending, resumeWaitExpired, shouldAutoSelect, userPinnedSelection, onEpisodeSelect]);

    // A manual click also cancels any reveal still in flight, so the strips
    // never scroll out from under the hand that just moved them.
    const handleSeasonSelect = (seasonNum: number) => {
        setUserPinnedSelection(true);
        setRevealRequest(null);
        setSelectedSeason(seasonNum);

        // Season posters are tall, so the row you just filtered usually sits
        // below the fold — bring it up. Click only: the resume auto-selection
        // deliberately leaves the page where it is on arrival.
        episodeAreaRef.current?.scrollIntoView?.({
            block: 'start',
            behavior: window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
        });
    };

    const handleEpisodeSelect = useCallback((ep: MediaItem) => {
        setUserPinnedSelection(true);
        setRevealRequest(null);
        onEpisodeSelect?.(ep);
    }, [onEpisodeSelect]);

    const seasonStripRef = useRef<HTMLDivElement>(null);
    const episodeAreaRef = useRef<HTMLDivElement>(null);

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

    // Bring the auto-selected season and episode into view. This runs on frame
    // boundaries rather than at commit time because neither target is reliably
    // scrollable the moment it renders: a virtualized episode scroller drops a
    // scrollToIndex issued before it has measured its viewport, and the strips
    // are still being laid out on the commit that first reveals them. Each
    // target retries until it lands, then stops — so a manual click, which
    // leaves nothing pending, never moves the strips.
    useEffect(() => {
        if (!revealRequest) return;

        let frame = 0;
        let attempts = 0;
        let seasonDone = false;
        let episodeDone = false;

        const tick = () => {
            attempts += 1;

            const strip = seasonStripRef.current;
            if (!seasonDone && strip) {
                const el = strip.querySelector<HTMLElement>(`[data-season="${revealRequest.season}"]`);
                if (el) {
                    seasonDone = true;
                    scrollSelectionIntoView(el, strip);
                }
            }

            if (!episodeDone) {
                const idx = currentEpisodes.findIndex(e => e.id === revealRequest.episodeId);
                if (shouldVirtualize) {
                    if (idx !== -1) {
                        const scroller = viewMode === 'cards' ? cardScrollerRef.current : listScrollerRef.current;
                        scroller?.scrollToIndex(idx);
                        // Re-issued for a few frames: the first call lands before the
                        // virtualizer has measured anything and is simply ignored.
                        episodeDone = attempts >= VIRTUAL_SCROLL_SETTLE_FRAMES;
                    }
                } else if (episodeAreaRef.current) {
                    const area = episodeAreaRef.current;
                    const el = area.querySelector<HTMLElement>(`[data-episode-id="${revealRequest.episodeId}"]`);
                    if (el) {
                        episodeDone = true;
                        scrollSelectionIntoView(el, area);
                    }
                }
            }

            if (seasonDone && episodeDone) {
                setRevealRequest(null);
            } else if (attempts < REVEAL_MAX_FRAMES) {
                frame = requestAnimationFrame(tick);
            } else {
                // Out of retries — drop the request rather than let it fire late
                // (e.g. on a view-mode toggle minutes from now).
                setRevealRequest(null);
            }
        };

        frame = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(frame);
    }, [revealRequest, currentEpisodes, shouldVirtualize, viewMode]);

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
            {/* Decorative page background — must stay BEHIND the content. See MovieDetailView:
                at z-0 this positioned layer painted OVER its static siblings (poster + text). */}
            {currentBackgroundPoster && (
                <div
                    className={`fixed top-16 right-0 bottom-0 z-[-1] pointer-events-none transition-all duration-300 ${isSidebarCollapsed ? 'left-20' : 'left-64'
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
                <div ref={seasonStripRef}>
                    <h3 className="text-lg font-bold text-white mb-4">Seasons</h3>
                    <HorizontalScrollList className="py-3 px-20 -mx-14 -my-3">
                        {seasonNumbers.map((seasonNum, idx) => {
                            const seasonPoster = getSeasonPoster(seasonNum);
                            const episodeCount = seasons[seasonNum]?.length || 0;
                            const isSelected = selectedSeason === seasonNum;

                            return (
                                <button
                                    key={seasonNum}
                                    data-season={seasonNum}
                                    onClick={() => handleSeasonSelect(seasonNum)}
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

                {/* Episodes — scroll-mt keeps a sliver of the season row visible
                    when a click scrolls this section to the top. */}
                <div ref={episodeAreaRef} className="scroll-mt-4">
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

                    {/* `selectedSeason === null` covers the window where the episodes have
                        loaded but the resume lookup hasn't landed yet — keep the skeleton
                        rather than flashing "No episodes found." */}
                    {isLoading || selectedSeason === null ? (
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
                                            onSelect={() => handleEpisodeSelect(ep)}
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
                                        onSelect={() => handleEpisodeSelect(ep)}
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
                            onEpisodeSelect={handleEpisodeSelect}
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
                                    onSelect={() => handleEpisodeSelect(ep)}
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

                {/* NR-WI-014 — bonus content in the series folder (the trailer itself
                    is promoted to the Trailer button next to Play) */}
                <ExtrasSection mediaId={item.id} itemType={item.type} />
            </div>
        </>
    );
}
