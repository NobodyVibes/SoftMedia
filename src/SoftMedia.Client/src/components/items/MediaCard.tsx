import { useMemo, memo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Play, ListMusic, ListPlus, Heart, Check, Clock, Star } from 'lucide-react';
import { type MediaItem, MediaType } from '../../types';
import QualityBadge from '../ui/QualityBadge';
import { useAudioStore } from '../../store/audioStore';
import api from '../../services/api';
import { getGenreGradient, getGenreColors } from '../../lib/genreColors';
import LoadingImage from '../ui/LoadingImage';
import { resolveCardPosterUrl } from '../../lib/mediaImageUrl';
import { AddToPlaylistMenu } from '../playlists/AddToPlaylistMenu';

interface MediaCardProps {
    item: MediaItem;
    libraryType?: string;
    enableHoverScale?: boolean;
    /** Optional batch/cascade coordination — forwarded to the inner LoadingImage. */
    groupReady?: boolean;
    onImageLoad?: () => void;
    onImageError?: () => void;
}

export default memo(function MediaCard({ item, libraryType, groupReady, onImageLoad, onImageError }: MediaCardProps) {
    const navigate = useNavigate();
    const { playTrack, addToQueue } = useAudioStore();
    // Track-card-only: anchors the AddToPlaylistMenu popover. We render it as
    // a sibling of the card poster so the menu survives losing :hover on the
    // card (the play overlay fades out, but the open menu must stay visible).
    const [showPlaylistMenu, setShowPlaylistMenu] = useState(false);
    const isTrackCard = item.type === MediaType.Audio || item.type === MediaType.Track;

    // Memoize constant property calculations
    const primaryGenre = useMemo(() => item.genres?.[0] || 'Drama', [item.genres]);
    const glowGradient = useMemo(() => getGenreGradient(primaryGenre), [primaryGenre]);

    const isAudio = useMemo(() =>
        libraryType === 'Music' ||
        item.type === MediaType.Audio ||
        item.type === MediaType.Artist ||
        item.type === MediaType.Album,
        [libraryType, item.type]);

    // Request thumbnail-sized images for card display (300px wide)
    const cardPosterSrc = useMemo(() => resolveCardPosterUrl(item.posterPath), [item.posterPath]);

    const isMovie = libraryType === 'Movie' || item.type === MediaType.Movie;
    // For TV: if it has an episodeNumber, it's an episode; otherwise treat as a series
    const isTVEpisode = (libraryType === 'TV' && !!item.episodeNumber) || item.type === MediaType.Episode;
    const isTVSeries = (libraryType === 'TV' && !item.episodeNumber) || item.type === MediaType.Series;

    // Logic for "New" Badge (14 days threshold)
    // Memoize derived flags
    const isNew = useMemo(() => {
        if (!item.dateAdded) return false;
        const added = new Date(item.dateAdded);
        const now = new Date();
        const diffTime = Math.abs(now.getTime() - added.getTime());
        const diffDays = Math.ceil(diffTime / (1000 * 60 * 60 * 24));
        return diffDays <= 14;
    }, [item.dateAdded]);

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
                <div className={`relative ${isAudio ? 'aspect-square' : 'aspect-[2/3]'} w-full bg-gray-900 overflow-hidden`}>
                    {/* Poster Image */}
                    <LoadingImage
                        src={cardPosterSrc}
                        alt={item.title}
                        className="h-full w-full object-cover transition-transform duration-700 ease-out group-hover/card:scale-105"
                        groupReady={groupReady}
                        onLoad={onImageLoad}
                        onError={onImageError}
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
                        <div className="flex flex-col items-end gap-1.5 translate-y-2 group-hover/card:translate-y-0 group-focus-within/card:translate-y-0 opacity-0 group-hover/card:opacity-100 group-focus-within/card:opacity-100 transition-all duration-300">
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

                    {/* Play Overlay - Centered. The overlay also opens on
                        focus-within so keyboard users tabbing into the card
                        can see the play button instead of hitting an invisible
                        focused element (SDD §8.3 universal-client a11y). */}
                    <div className="absolute inset-0 flex items-center justify-center z-20 opacity-0 group-hover/card:opacity-100 group-focus-within/card:opacity-100 transition-opacity duration-300 pointer-events-none">
                        {/* Play Button wrapped in pointer-events-auto to capture clicks */}
                        <div className="relative group/play pointer-events-auto">
                            <div className={`absolute inset-0 bg-gradient-to-br ${glowGradient} blur-lg opacity-50 rounded-full scale-75 group-hover/play:scale-110 transition-transform duration-500`} />
                            <button
                                type="button"
                                aria-label={`Play ${item.title ?? ''}`.trim()}
                                className="relative bg-white/10 backdrop-blur-md min-w-[44px] min-h-[44px] p-4 rounded-full border border-white/20 shadow-2xl hover:bg-white/20 hover:scale-110 focus-visible:bg-white/20 focus-visible:ring-2 focus-visible:ring-white focus-visible:outline-none active:scale-95 transition-all duration-300 text-white flex items-center justify-center"
                                onClick={handlePlay}
                            >
                                <Play className="w-8 h-8 fill-white ml-1" />
                            </button>

                            {/* Add to Queue Button (Audio Only) attached near play button */}
                            {isAudio && (
                                <button
                                    type="button"
                                    aria-label={`Add ${item.title ?? 'track'} to queue`}
                                    className="absolute -right-12 top-1/2 -translate-y-1/2 bg-black/60 backdrop-blur-md min-w-[44px] min-h-[44px] p-2 rounded-full border border-white/10 shadow-xl hover:bg-white/20 focus-visible:bg-white/20 focus-visible:ring-2 focus-visible:ring-white focus-visible:outline-none text-gray-200 hover:text-white transition-all duration-200 flex items-center justify-center"
                                    onClick={handleAddToQueue}
                                    title="Add to Queue"
                                >
                                    <ListMusic className="w-4 h-4" />
                                </button>
                            )}

                            {/* Add to Playlist (track cards only). Mirrors the queue
                                button on the opposite side. The popover lives on
                                the outer card wrapper (below) so it isn't hidden
                                when the play overlay fades on mouse-out. */}
                            {isTrackCard && (
                                <button
                                    type="button"
                                    aria-label={`Add ${item.title ?? 'track'} to playlist`}
                                    aria-haspopup="menu"
                                    aria-expanded={showPlaylistMenu}
                                    className="absolute -left-12 top-1/2 -translate-y-1/2 bg-black/60 backdrop-blur-md min-w-[44px] min-h-[44px] p-2 rounded-full border border-white/10 shadow-xl hover:bg-white/20 focus-visible:bg-white/20 focus-visible:ring-2 focus-visible:ring-white focus-visible:outline-none text-gray-200 hover:text-white transition-all duration-200 flex items-center justify-center"
                                    onClick={(e) => {
                                        e.preventDefault();
                                        e.stopPropagation();
                                        setShowPlaylistMenu(v => !v);
                                    }}
                                    title="Add to Playlist"
                                >
                                    <ListPlus className="w-4 h-4" />
                                </button>
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
                    <div className="flex flex-col gap-1.5">
                        <h3 className="text-gray-100 font-bold text-[1rem] leading-tight line-clamp-2 group-hover/card:text-white transition-colors tracking-tight" title={item.title}>
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

                            {(isMovie || isTVSeries || isTVEpisode || libraryType === 'Game' || libraryType === 'Book') ? (
                                <div className="flex items-center gap-1.5">
                                    {/* Personal Rating (Yellow Star) - Individual User's Rating */}
                                    {item.personalRating && item.personalRating > 0 && (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] bg-yellow-500/10 border border-yellow-500/30 rounded-[4px] text-yellow-500" title="Your Rating">
                                            <Star className="w-2.5 h-2.5 fill-current" />
                                            <span className="text-[10px] font-bold tracking-tight">
                                                {item.personalRating}
                                            </span>
                                        </div>
                                    )}

                                    {/* Community Rating (Yellow Star) - If no personal rating exists */}
                                    {!item.personalRating && item.communityRating && item.communityRating > 0 && (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] bg-yellow-500/10 border border-yellow-500/30 rounded-[4px] text-yellow-500" title="Community Rating">
                                            <Star className="w-2.5 h-2.5 fill-current" />
                                            <span className="text-[10px] font-bold tracking-tight">
                                                {item.communityRating.toFixed(1)}
                                            </span>
                                        </div>
                                    )}

                                    {/* SoftMedia Average (Violet Star) */}
                                    {item.userRating && item.userRating > 0 && (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] bg-violet-500/10 border border-violet-500/30 rounded-[4px] text-violet-400" title="SoftMedia Average">
                                            <Star className="w-2.5 h-2.5 fill-current" />
                                            <span className="text-[10px] font-bold tracking-tight">
                                                {item.userRating % 1 === 0 ? item.userRating : item.userRating.toFixed(1)}
                                            </span>
                                        </div>
                                    )}

                                    {!item.communityRating && !item.userRating && !item.personalRating && (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] border border-white/5 bg-white/5 rounded-[4px]">
                                            <Star className="w-2.5 h-2.5 text-gray-600" />
                                            <span className="text-[10px] text-gray-500 font-semibold tracking-wide">
                                                N/A
                                            </span>
                                        </div>
                                    )}
                                </div>
                            ) : (
                                // Music types or fallback
                                <>
                                    {item.personalRating ? (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] border border-yellow-500/30 bg-yellow-500/10 rounded-[4px] text-yellow-500" title="Your Rating">
                                            <Star className="w-2.5 h-2.5 fill-current" />
                                            <span className="text-[10px] font-bold tracking-wide">
                                                {item.personalRating}
                                            </span>
                                        </div>
                                    ) : item.userRating ? (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] border border-violet-500/30 bg-violet-500/10 rounded-[4px] text-violet-400" title="SoftMedia Average">
                                            <Star className="w-2.5 h-2.5 fill-current" />
                                            <span className="text-[10px] font-bold tracking-wide">
                                                {item.userRating % 1 === 0 ? item.userRating : item.userRating.toFixed(1)}
                                            </span>
                                        </div>
                                    ) : item.communityRating ? (
                                        <div className="flex items-center gap-1 px-1.5 py-[1px] border border-yellow-500/30 bg-yellow-500/10 rounded-[4px] text-yellow-500" title="Community Rating">
                                            <Star className="w-2.5 h-2.5 fill-current" />
                                            <span className="text-[10px] font-bold tracking-wide">
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
                                </>
                            )}
                        </div>
                    </div>

                    {item.genres && item.genres.length > 0 && (
                        <div className="mt-2 flex flex-wrap gap-1 opacity-70 group-hover/card:opacity-100 transition-opacity">
                            {item.genres.slice(0, 2).map(genre => {
                                const colors = getGenreColors(genre);
                                return (
                                    <span
                                        key={genre}
                                        className={`px-1.5 py-0.5 rounded text-[9px] font-semibold uppercase tracking-wider ${colors.bg} ${colors.text}`}
                                    >
                                        {genre}
                                    </span>
                                );
                            })}
                        </div>
                    )}
                </div>
            </div>

            {/* Track-card playlist popover. Sits OUTSIDE Main Container's
                overflow-hidden so the dropdown can extend past card bounds.
                The menu component itself stops click propagation, so selecting
                a playlist row doesn't bubble to the card-as-button playback
                handler. */}
            {isTrackCard && showPlaylistMenu && (
                <AddToPlaylistMenu
                    mediaItemIds={[item.id]}
                    onClose={() => setShowPlaylistMenu(false)}
                />
            )}
        </div>
    );

    // Albums and Artists should navigate to detail page
    if (item.type === MediaType.Album || item.type === MediaType.Artist) {
        return (
            <Link to={`/media/${item.id}`} className="block group/card relative hover:z-50 h-full">
                {CardContent}
            </Link>
        );
    }

    // Tracks: click anywhere on card to play (play button also works).
    // Using role="button" + tabIndex + keyboard handler rather than a real
    // <button> because the inner play-overlay IS a <button> and HTML forbids
    // nested interactive elements.
    if (isAudio) {
        // Ignore clicks that originate inside an open popover menu (e.g.
        // the AddToPlaylistMenu hosted on this card). Without this, picking
        // a playlist would also start playback because the click bubbles up.
        const activateFromClick = (e: React.MouseEvent<HTMLDivElement>) => {
            if ((e.target as HTMLElement).closest('[role="menu"]')) return;
            playTrack(item);
        };
        // Force the card to its hover z-index while the playlist popover is
        // open so the dropdown stacks above neighbouring grid cells, even
        // after the cursor has left the card.
        const zClass = showPlaylistMenu ? 'z-50' : 'hover:z-50';
        return (
            <div
                role="button"
                tabIndex={0}
                aria-label={`Play ${item.title ?? 'track'}`}
                className={`block group/card cursor-pointer relative ${zClass} h-full focus-visible:ring-2 focus-visible:ring-white focus-visible:outline-none rounded-lg`}
                onClick={activateFromClick}
                onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        playTrack(item);
                    }
                }}
            >
                {CardContent}
            </div>
        );
    }

    const linkTarget = `/media/${item.id}`;

    return (
        <Link to={linkTarget} className="block group/card relative hover:z-50 h-full">
            {CardContent}
        </Link>
    );
});
