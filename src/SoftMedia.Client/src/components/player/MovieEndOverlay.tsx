import { useEffect, useState, useCallback } from 'react';
import { Film, Home, Pause, RotateCcw, X } from 'lucide-react';
import { StarRating } from '../ui/StarRating';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import type { MediaItem } from '../../types';
import type { PostPlayInfo } from '../../services/postPlayService';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

interface MovieEndOverlayProps {
    /** Title of the movie that just finished (already marked watched by the player). */
    movieTitle: string;
    /** Recommendations: same-collection films first, then genre matches. Null while loading. */
    postPlay: PostPlayInfo | null;
    /** Current user rating for the movie (if any). */
    currentRating?: number;
    /** Called when the user rates the finished movie. */
    onRateCurrent: (rating: number) => void;
    /** Called when the user dismisses the overlay to watch the credits. */
    onWatchCredits: () => void;
    /**
     * Called for every way OFF the player (countdown expiry, Back to Library, a recommendation
     * card). The player cleans up the transcode session and navigates to the given path.
     */
    onLeave: (path: string) => void;
    /** Called to pause/unpause the video (countdown pause also pauses playback, like the TV overlay). */
    onPauseVideo: (paused: boolean) => void;
    /** Library the finished movie belongs to — the countdown's destination. */
    libraryId: string;
}

/** Same cadence as the TV "Play Next" overlay. */
const COUNTDOWN_SECONDS = 10;
/** Max recommendation cards shown (collection items take precedence). */
const MAX_CARDS = 4;

/**
 * The movie counterpart of <see>NextEpisodeOverlay</see>, deliberately built to the same compact
 * card (same width, header-with-countdown, rating panel, and action grid) so movies and episodes
 * end the same way. Instead of a single next episode it offers post-play recommendations —
 * same-collection films first — and its countdown returns to the movie's library.
 */
export function MovieEndOverlay({
    movieTitle,
    postPlay,
    currentRating,
    onRateCurrent,
    onWatchCredits,
    onLeave,
    onPauseVideo,
    libraryId,
}: MovieEndOverlayProps) {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const [countdown, setCountdown] = useState(COUNTDOWN_SECONDS);
    const [isPaused, setIsPaused] = useState(false);
    const [rating, setRating] = useState<number | null>(currentRating ?? null);
    const [isVisible, setIsVisible] = useState(false);

    // Trigger fade-in on mount
    useEffect(() => {
        const timer = setTimeout(() => setIsVisible(true), 10);
        return () => clearTimeout(timer);
    }, []);

    // Handle countdown timer
    useEffect(() => {
        if (isPaused || countdown <= 0) return;
        const timer = setInterval(() => {
            setCountdown(prev => (prev <= 1 ? 0 : prev - 1));
        }, 1000);
        return () => clearInterval(timer);
    }, [isPaused, countdown]);

    // Countdown expiry returns to the movie's library (the default action).
    useEffect(() => {
        if (countdown === 0 && !isPaused) {
            onLeave(`/libraries/${libraryId}`);
        }
    }, [countdown, isPaused, onLeave, libraryId]);

    const handleRating = useCallback((newRating: number) => {
        setRating(newRating);
        onRateCurrent(newRating);
    }, [onRateCurrent]);

    // Handle restart countdown (mirrors the TV overlay)
    const handleRestart = useCallback(() => {
        setCountdown(COUNTDOWN_SECONDS);
        setIsPaused(false);
    }, []);

    // Collection films lead; genre matches fill the remaining card slots.
    const collectionCount = Math.min(postPlay?.collectionItems.length ?? 0, MAX_CARDS);
    const cards: { item: MediaItem; fromCollection: boolean }[] = [
        ...(postPlay?.collectionItems ?? []).slice(0, MAX_CARDS).map(item => ({ item, fromCollection: true })),
        ...(postPlay?.similarItems ?? []).slice(0, Math.max(0, MAX_CARDS - collectionCount)).map(item => ({ item, fromCollection: false })),
    ];

    // Same glassy gradient card as the TV overlay
    const cardStyle = "relative bg-gradient-to-r from-blue-600/20 via-violet-600/20 to-purple-600/20 backdrop-blur-2xl rounded-2xl p-6 max-w-xl mx-4 border border-white/20 shadow-2xl overflow-hidden";

    return (
        <div
            className={`absolute inset-0 bg-black/75 flex items-center justify-center z-50 transition-opacity duration-500 ease-out ${isVisible ? 'opacity-100' : 'opacity-0'}`}
        >
            <div className={cardStyle}>
                {/* Gradient glow effect behind */}
                <div className="absolute inset-0 bg-gradient-to-r from-blue-500/10 via-violet-500/10 to-purple-500/10 blur-xl" />

                <div className="relative z-10">
                    {/* Header with countdown and X dismiss button */}
                    <div className="flex items-center justify-between mb-4">
                        <h3 className="text-lg font-semibold text-white">
                            {postPlay?.collectionName && collectionCount > 0
                                ? `Next in ${postPlay.collectionName}`
                                : 'You might also like'}
                        </h3>
                        <div className="flex items-center gap-3">
                            {/* X button to dismiss (same as Watch Credits) */}
                            <button
                                type="button"
                                onClick={onWatchCredits}
                                aria-label="Dismiss and watch credits"
                                className="text-gray-400 hover:text-white focus-visible:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
                                title="Dismiss and watch credits"
                            >
                                <X size={20} />
                            </button>

                            {isPaused ? (
                                <button
                                    type="button"
                                    onClick={handleRestart}
                                    aria-label="Resume countdown"
                                    className="text-gray-400 hover:text-white focus-visible:text-white transition-colors p-1 min-w-[44px] min-h-[44px] flex items-center justify-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
                                    title="Resume countdown"
                                >
                                    <RotateCcw size={18} />
                                </button>
                            ) : (
                                <button
                                    type="button"
                                    onClick={() => {
                                        setIsPaused(true);
                                        onPauseVideo(true);
                                    }}
                                    aria-label="Pause countdown and video"
                                    className="relative w-10 h-10 group cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-full"
                                    title="Pause countdown and video"
                                >
                                    <svg className="w-10 h-10 -rotate-90">
                                        <circle cx="20" cy="20" r="16" fill="none" stroke="currentColor" strokeWidth="3" className="text-white/20" />
                                        <circle
                                            cx="20" cy="20" r="16" fill="none"
                                            stroke="url(#movieEndCountdownGradient)"
                                            strokeWidth="3"
                                            strokeDasharray={`${(countdown / COUNTDOWN_SECONDS) * 100.53} 100.53`}
                                            className="transition-all duration-1000"
                                        />
                                        <defs>
                                            <linearGradient id="movieEndCountdownGradient" x1="0%" y1="0%" x2="100%" y2="0%">
                                                <stop offset="0%" stopColor="#3b82f6" />
                                                <stop offset="100%" stopColor="#8b5cf6" />
                                            </linearGradient>
                                        </defs>
                                    </svg>
                                    <span className="absolute inset-0 flex items-center justify-center text-sm font-bold text-white group-hover:hidden">
                                        {countdown}
                                    </span>
                                    <span className="absolute inset-0 flex items-center justify-center text-white hidden group-hover:flex">
                                        <Pause size={16} />
                                    </span>
                                </button>
                            )}
                        </div>
                    </div>

                    {/* Recommendation cards — compact strip, sized to keep the card episode-overlay small */}
                    {cards.length > 0 && (
                        <div className="grid grid-cols-4 gap-2 mb-5">
                            {cards.map(({ item, fromCollection }) => (
                                <button
                                    key={item.id}
                                    type="button"
                                    onClick={() => onLeave(`/play/${item.id}`)}
                                    className="group text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
                                    title={`Play ${item.title}`}
                                >
                                    <div className="aspect-[2/3] bg-white/10 rounded-lg overflow-hidden border border-white/10 group-hover:border-blue-400/60 transition-colors">
                                        {item.posterPath ? (
                                            <img
                                                src={attachAuthToApiUrl(item.posterPath)}
                                                alt={item.title}
                                                referrerPolicy="no-referrer"
                                                className="w-full h-full object-cover"
                                            />
                                        ) : (
                                            <div className="flex h-full w-full items-center justify-center bg-slate-800 text-slate-500">
                                                <Film size={22} className="opacity-50" />
                                            </div>
                                        )}
                                    </div>
                                    <p className="text-white text-xs font-medium mt-1 truncate">{item.title}</p>
                                    <p className="text-gray-400 text-[11px] truncate">
                                        {fromCollection && postPlay?.collectionName ? postPlay.collectionName : item.year ?? ''}
                                    </p>
                                </button>
                            ))}
                        </div>
                    )}

                    {/* Rate the finished movie (same panel as the TV overlay) */}
                    <div className="mb-5 p-3 bg-white/5 backdrop-blur rounded-xl border border-white/10">
                        <p className="text-sm text-gray-400 mb-2 text-center">Rate "{movieTitle}"</p>
                        <div className="flex justify-center">
                            <StarRating rating={rating} onChange={handleRating} size={24} max={10} />
                        </div>
                    </div>

                    {/* Action Buttons (same grid style as the TV overlay) */}
                    <div className="grid grid-cols-2 gap-3">
                        <button
                            type="button"
                            onClick={onWatchCredits}
                            className="py-3 px-3 bg-white/10 hover:bg-white/20 focus-visible:bg-white/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 backdrop-blur text-white font-medium rounded-xl transition-all flex items-center justify-center gap-2 border border-white/10 text-sm"
                        >
                            <Film size={16} />
                            Watch Credits
                        </button>
                        <button
                            type="button"
                            onClick={() => onLeave(`/libraries/${libraryId}`)}
                            className="py-3 px-3 bg-gradient-to-r from-blue-600 to-violet-600 hover:from-blue-500 hover:to-violet-500 focus-visible:from-blue-500 focus-visible:to-violet-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-300 text-white font-semibold rounded-xl transition-all flex items-center justify-center gap-2 shadow-lg shadow-violet-500/25 text-sm"
                        >
                            <Home size={16} />
                            Back to Library
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}
