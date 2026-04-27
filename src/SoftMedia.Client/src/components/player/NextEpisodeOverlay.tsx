import { useEffect, useState, useCallback } from 'react';
import { Play, X, RotateCcw, Eye, Home, SkipForward, Pause } from 'lucide-react';
import { StarRating } from '../ui/StarRating';
import { useNavigate } from 'react-router-dom';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';

/** Info about the next episode from the API */
export interface NextEpisodeInfo {
    episodeId: string;
    seriesId: string;
    seasonNumber: number;
    episodeNumber: number;
    title: string;
    resumePosition: number;
    posterPath?: string;
    backdropPath?: string;
    isSeriesComplete?: boolean;
}

interface NextEpisodeOverlayProps {
    /** Current episode being watched */
    currentEpisodeId: string;
    currentEpisodeTitle: string;
    /** Info about the next episode */
    nextEpisode: NextEpisodeInfo;
    /** Current user rating for the episode (if any) */
    currentRating?: number;
    /** Called when user plays next episode from resume position */
    onPlayNextResume: () => void;
    /** Called when user plays next episode from start */
    onPlayNextFromStart: () => void;
    /** Called when user wants to continue watching current episode */
    onContinueWatching: () => void;
    /** Called when user returns to library */
    onReturnToLibrary: () => void;
    /** Called when user rates the current episode */
    onRateCurrent: (rating: number) => void;
    /** Called to pause/unpause the video */
    onPauseVideo: (paused: boolean) => void;
    /** Optional: library ID for navigation */
    libraryId?: string;
}

const COUNTDOWN_SECONDS = 10;

export function NextEpisodeOverlay({
    currentEpisodeTitle,
    nextEpisode,
    currentRating,
    onPlayNextResume,
    onPlayNextFromStart,
    onContinueWatching,
    onReturnToLibrary,
    onRateCurrent,
    onPauseVideo,
    libraryId
}: NextEpisodeOverlayProps) {
    const navigate = useNavigate();
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
            setCountdown(prev => {
                if (prev <= 1) {
                    clearInterval(timer);
                    return 0;
                }
                return prev - 1;
            });
        }, 1000);

        return () => clearInterval(timer);
    }, [isPaused, countdown]);

    // Auto-play from resume when countdown reaches 0 (default action)
    useEffect(() => {
        if (countdown === 0 && !isPaused) {
            onPlayNextResume();
        }
    }, [countdown, isPaused, onPlayNextResume]);

    // Handle rating change
    const handleRating = useCallback((newRating: number) => {
        setRating(newRating);
        onRateCurrent(newRating);
    }, [onRateCurrent]);

    // Handle return to library
    const handleReturnToLibrary = useCallback(() => {
        onReturnToLibrary();
        if (libraryId) {
            navigate(`/library/${libraryId}`);
        } else {
            navigate('/');
        }
    }, [onReturnToLibrary, libraryId, navigate]);

    // Handle restart countdown
    const handleRestart = useCallback(() => {
        setCountdown(COUNTDOWN_SECONDS);
        setIsPaused(false);
    }, []);

    // Glassy gradient style for the card
    const cardStyle = "relative bg-gradient-to-r from-blue-600/20 via-violet-600/20 to-purple-600/20 backdrop-blur-2xl rounded-2xl p-6 max-w-xl mx-4 border border-white/20 shadow-2xl overflow-hidden";

    // Is series complete?
    if (nextEpisode.isSeriesComplete) {
        return (
            <div
                className={`absolute inset-0 bg-black/80 flex items-center justify-center z-50 transition-opacity duration-500 ease-out ${isVisible ? 'opacity-100' : 'opacity-0'}`}
            >
                <div className={cardStyle}>
                    {/* Gradient glow effect behind */}
                    <div className="absolute inset-0 bg-gradient-to-r from-blue-500/10 via-violet-500/10 to-purple-500/10 blur-xl" />

                    <div className="relative z-10">
                        <h2 className="text-2xl font-bold text-white mb-4 text-center">
                            🎉 Series Complete!
                        </h2>
                        <p className="text-gray-300 text-center mb-6">
                            You've finished watching this series.
                        </p>

                        {/* Rate the episode you just watched */}
                        <div className="mb-6">
                            <p className="text-sm text-gray-400 text-center mb-2">
                                Rate "{currentEpisodeTitle}"
                            </p>
                            <div className="flex justify-center">
                                <StarRating
                                    rating={rating}
                                    onChange={handleRating}
                                    size={28}
                                    max={10}
                                />
                            </div>
                        </div>

                        <button
                            type="button"
                            onClick={handleReturnToLibrary}
                            className="w-full py-3 px-6 bg-gradient-to-r from-blue-600 to-violet-600 hover:from-blue-500 hover:to-violet-500 focus-visible:from-blue-500 focus-visible:to-violet-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-300 text-white font-semibold rounded-xl transition-all flex items-center justify-center gap-2"
                        >
                            Return to Library
                        </button>
                    </div>
                </div>
            </div>
        );
    }

    const hasResumePosition = nextEpisode.resumePosition > 0;

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
                        <h3 className="text-lg font-semibold text-white">Up Next</h3>
                        <div className="flex items-center gap-3">
                            {/* X button to dismiss (same as Keep Watching) */}
                            <button
                                type="button"
                                onClick={onContinueWatching}
                                aria-label="Dismiss and continue watching"
                                className="text-gray-400 hover:text-white focus-visible:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
                                title="Dismiss and continue watching"
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
                                        <circle
                                            cx="20"
                                            cy="20"
                                            r="16"
                                            fill="none"
                                            stroke="currentColor"
                                            strokeWidth="3"
                                            className="text-white/20"
                                        />
                                        <circle
                                            cx="20"
                                            cy="20"
                                            r="16"
                                            fill="none"
                                            stroke="url(#countdownGradient)"
                                            strokeWidth="3"
                                            strokeDasharray={`${(countdown / COUNTDOWN_SECONDS) * 100.53} 100.53`}
                                            className="transition-all duration-1000"
                                        />
                                        <defs>
                                            <linearGradient id="countdownGradient" x1="0%" y1="0%" x2="100%" y2="0%">
                                                <stop offset="0%" stopColor="#3b82f6" />
                                                <stop offset="100%" stopColor="#8b5cf6" />
                                            </linearGradient>
                                        </defs>
                                    </svg>
                                    {/* Countdown number - shows pause icon on hover */}
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

                    {/* Next Episode Card */}
                    <div className="flex gap-4 mb-5">
                        {/* Thumbnail */}
                        <div className="w-32 h-20 bg-white/10 backdrop-blur rounded-lg overflow-hidden flex-shrink-0 border border-white/10">
                            {nextEpisode.posterPath || nextEpisode.backdropPath ? (
                                <img
                                    src={attachAuthToApiUrl(nextEpisode.posterPath || nextEpisode.backdropPath || '')}
                                    alt={nextEpisode.title}
                                    referrerPolicy="no-referrer"
                                    className="w-full h-full object-cover"
                                />
                            ) : (
                                <div className="flex h-full w-full items-center justify-center bg-slate-800 text-slate-500">
                                    <span className="text-2xl font-thin opacity-50">?</span>
                                </div>
                            )}
                        </div>

                        {/* Episode Info */}
                        <div className="flex-1 min-w-0">
                            <p className="text-transparent bg-clip-text bg-gradient-to-r from-blue-400 to-violet-400 text-sm font-medium">
                                S{String(nextEpisode.seasonNumber).padStart(2, '0')}E{String(nextEpisode.episodeNumber).padStart(2, '0')}
                            </p>
                            <h4 className="text-white font-semibold text-lg truncate">
                                {nextEpisode.title}
                            </h4>
                            {hasResumePosition && (
                                <p className="text-gray-400 text-sm mt-1">
                                    Can resume from {formatTime(nextEpisode.resumePosition)}
                                </p>
                            )}
                        </div>
                    </div>

                    {/* Rate Current Episode */}
                    <div className="mb-5 p-3 bg-white/5 backdrop-blur rounded-xl border border-white/10">
                        <p className="text-sm text-gray-400 mb-2 text-center">
                            Rate "{currentEpisodeTitle}"
                        </p>
                        <div className="flex justify-center">
                            <StarRating
                                rating={rating}
                                onChange={handleRating}
                                size={24}
                                max={10}
                            />
                        </div>
                    </div>

                    {/* Action Buttons - 2x2 Grid */}
                    <div className="grid grid-cols-2 gap-3">
                        {/* Play Next from Beginning */}
                        <button
                            type="button"
                            onClick={onPlayNextFromStart}
                            className="py-3 px-3 bg-white/10 hover:bg-white/20 focus-visible:bg-white/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 backdrop-blur text-white font-medium rounded-xl transition-all flex items-center justify-center gap-2 border border-white/10 text-sm"
                        >
                            <Play size={16} />
                            Play Next: Beginning
                        </button>

                        {/* Play Next from Resume (highlighted as default) */}
                        <button
                            type="button"
                            onClick={onPlayNextResume}
                            className="py-3 px-3 bg-gradient-to-r from-blue-600 to-violet-600 hover:from-blue-500 hover:to-violet-500 focus-visible:from-blue-500 focus-visible:to-violet-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-300 text-white font-semibold rounded-xl transition-all flex items-center justify-center gap-2 shadow-lg shadow-violet-500/25 text-sm"
                        >
                            <SkipForward size={16} />
                            Play Next: Resume
                        </button>

                        {/* Keep Watching Current */}
                        <button
                            type="button"
                            onClick={onContinueWatching}
                            className="py-3 px-3 bg-white/10 hover:bg-white/20 focus-visible:bg-white/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 backdrop-blur text-white font-medium rounded-xl transition-all flex items-center justify-center gap-2 border border-white/10 text-sm"
                        >
                            <Eye size={16} />
                            Keep Watching
                        </button>

                        {/* Back to Library */}
                        <button
                            type="button"
                            onClick={handleReturnToLibrary}
                            className="py-3 px-3 bg-white/10 hover:bg-white/20 focus-visible:bg-white/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 backdrop-blur text-white font-medium rounded-xl transition-all flex items-center justify-center gap-2 border border-white/10 text-sm"
                        >
                            <Home size={16} />
                            Library
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}

/** Format seconds to mm:ss or hh:mm:ss */
function formatTime(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);

    if (h > 0) {
        return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
    }
    return `${m}:${String(s).padStart(2, '0')}`;
}
