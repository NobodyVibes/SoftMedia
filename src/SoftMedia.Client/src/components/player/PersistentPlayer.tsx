import React, { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { QueueList } from './QueueList';
import { AnimatePresence, motion } from 'framer-motion';
import { useAudioStore } from '../../store/audioStore';
import {
    Play, Pause, SkipForward, SkipBack,
    Volume2, VolumeX, Shuffle, Repeat, Repeat1,
    List, X, RotateCcw, RotateCw, ChevronUp, ChevronDown,
    Maximize2, Minimize2, ListPlus
} from 'lucide-react';
import { AddToPlaylistMenu } from '../playlists/AddToPlaylistMenu';
import api, { API_URL } from '../../services/api';
import { getUrlToken } from '../../store/authStore';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { cn } from '../../lib/utils';
import type { MediaItem } from '../../types';
import { ScrollingText } from '../ui/ScrollingText';
import { AudioVisualizer, VisualizerSelector } from './visualizers';
import { useVisualizerStore } from '../../store/visualizerStore';
import { useAudioAnalyser } from '../../hooks/useAudioAnalyser';
import { useMediaSession } from '../../hooks/useMediaSession';
import { useMediaTokenRefresh } from '../../hooks/useMediaTokenRefresh';

// Preload threshold in seconds before track ends
const PRELOAD_THRESHOLD = 15;
// Start crossfade this many seconds before track ends (overlap duration)
const CROSSFADE_START = 0.15; // 150ms before end
// Crossfade duration in milliseconds
const CROSSFADE_DURATION_MS = 100;

// Helper to format time
const formatTime = (seconds: number) => {
    if (!seconds || isNaN(seconds)) return "0:00";
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins}:${secs.toString().padStart(2, '0')}`;
};

export const PersistentPlayer: React.FC = () => {
    // Media URLs below embed the media token; re-render when it rotates so a
    // stale token can't leave the artwork permanently broken.
    useMediaTokenRefresh();
    const {
        currentTrack, isPlaying, volume, isMuted,
        shuffleMode, repeatMode, queue,
        pause, resume, next, previous,
        setVolume, toggleMute,
        toggleShuffle, cycleRepeatMode,
        closePlayer
    } = useAudioStore();

    // Dual audio elements for true gapless playback
    const audioARef = useRef<HTMLAudioElement>(null);
    const audioBRef = useRef<HTMLAudioElement>(null);

    // Track which audio element is currently active (0 = A, 1 = B)
    const [activePlayer, setActivePlayer] = useState<0 | 1>(0);
    const activeAudioRef = activePlayer === 0 ? audioARef : audioBRef;
    const preloadAudioRef = activePlayer === 0 ? audioBRef : audioARef;

    const [progress, setProgress] = useState(0);
    const [duration, setDuration] = useState(0);
    // Audio Element State (for Visualizer Hook Reactivity)
    const [audioAElement, setAudioAElement] = useState<HTMLAudioElement | null>(null);
    const [audioBElement, setAudioBElement] = useState<HTMLAudioElement | null>(null);


    const [showQueue, setShowQueue] = useState(false);
    const [isExpanded, setIsExpanded] = useState(false);
    const [isFullScreen, setIsFullScreen] = useState(false); // Explicit Full Screen Mode
    // "Add to playlist" popover, anchored to the trigger in either view.
    // Single piece of state because only one player is visible at a time.
    const [showPlaylistMenu, setShowPlaylistMenu] = useState(false);
    // Close the menu when the playing track changes so the user can't
    // accidentally add the next track to a playlist after the queue advances.
    const currentTrackId = currentTrack?.id;
    useEffect(() => {
        setShowPlaylistMenu(false);
    }, [currentTrackId]);

    // R-WI-013: listen-history beats. Mirrors VideoPlayer's ~10s progress cadence so music
    // plays are recorded server-side (the server applies the play threshold + dedup window).
    // Beats are fire-and-forget — reporting must never break playback.
    const lastBeatRef = useRef<{ trackId: string | null; at: number }>({ trackId: null, at: 0 });

    const reportProgress = useCallback((trackId: string | undefined, position: number) => {
        if (!trackId || !(position > 0)) return;
        api.post(`/interaction/${trackId}/progress`, { position }).catch(() => { /* best effort */ });
    }, []);

    /// Final beat for a finishing/leaving track: report the element's actual position (an
    /// auto-advance sits at ~duration so the server marks the play complete; a manual skip
    /// reports the real partial position — never credit an unfinished listen as full).
    const reportFinalBeat = useCallback((el: HTMLAudioElement | null, trackId: string | undefined) => {
        if (!el) return;
        reportProgress(trackId, el.currentTime || el.duration || 0);
    }, [reportProgress]);

    // Visualizer state
    const { isEnabled: visualizerEnabled, toggle: toggleVisualizer } = useVisualizerStore();
    const { frequencyData, timeDomainData, isReady: visualizerReady, updateData, setGlobalVolume } = useAudioAnalyser(
        audioAElement,
        audioBElement,
        activePlayer
    );
    const [isPreloaded, setIsPreloaded] = useState(false);
    const [preloadedTrackId, setPreloadedTrackId] = useState<string | null>(null);

    const [showControls, setShowControls] = useState(true);

    // Idle timer for fullscreen controls
    useEffect(() => {
        if (!isFullScreen) {
            setShowControls(true);
            return;
        }

        let timeout: NodeJS.Timeout;
        const resetIdle = () => {
            setShowControls(true);
            clearTimeout(timeout);
            timeout = setTimeout(() => setShowControls(false), 3000);
        };

        window.addEventListener('mousemove', resetIdle);
        window.addEventListener('click', resetIdle);
        window.addEventListener('keydown', resetIdle);
        resetIdle();

        return () => {
            window.removeEventListener('mousemove', resetIdle);
            window.removeEventListener('click', resetIdle);
            window.removeEventListener('keydown', resetIdle);
            clearTimeout(timeout);
        };
    }, [isFullScreen]);

    // Prevent multiple rapid transitions
    const isTransitioningRef = useRef(false);
    const crossfadeTriggeredRef = useRef(false);

    // Get auth token for stream URL
    const getStreamUrl = useCallback((track: MediaItem | null) => {
        if (!track) return '';
        const token = getUrlToken();
        return `${API_URL}/stream/${track.id}${token ? `?token=${token}` : ''}`;
    }, []);

    // Image URL helper
    const getImageUrl = useCallback((path: string | undefined) => {
        if (!path) return '/placeholder-music.png';
        if (path.startsWith('/api/')) return attachAuthToApiUrl(path);
        if (path.startsWith('http')) return path;
        // Anything left is a static file served from wwwroot (e.g.
        // /cache/images/albums/x.jpg), NOT an API route — it needs no token, and
        // the `${API_URL}` prefix this used to add produced /api/v1/cache/… ,
        // which routes nowhere and 404s. (Supersedes B-09, which assumed this
        // branch landed on /api/v1.)
        return path;
    }, []);

    // Memoized stream URL for current track
    const currentStreamUrl = useMemo(() => getStreamUrl(currentTrack), [currentTrack, getStreamUrl]);

    // Preload the next track onto the inactive audio element
    const preloadNextTrack = useCallback(() => {
        const nextTrack = queue[0];
        if (!nextTrack || !preloadAudioRef.current) return;

        // Don't reload if already preloaded
        if (preloadedTrackId === nextTrack.id) return;

        const url = getStreamUrl(nextTrack);
        const preloadEl = preloadAudioRef.current;

        preloadEl.src = url;
        preloadEl.load();
        preloadEl.volume = 0; // Start at 0 for crossfade

        setPreloadedTrackId(nextTrack.id);
        setIsPreloaded(false);

        console.log('[Gapless] Preloading:', nextTrack.title);
    }, [queue, preloadedTrackId, getStreamUrl, preloadAudioRef]);

    // Handle preload ready
    const handlePreloadReady = useCallback(() => {
        setIsPreloaded(true);
        console.log('[Gapless] Next track ready for instant playback');
    }, []);

    // Perform overlapping crossfade transition
    const performCrossfadeTransition = useCallback(() => {
        if (isTransitioningRef.current) return;
        isTransitioningRef.current = true;

        const preloadEl = preloadAudioRef.current;
        const currentEl = activeAudioRef.current;

        // R-WI-013: credit the leaving track before the store advances (auto-advance is at
        // ~duration → server completes the play; a manual skip reports the partial position).
        reportFinalBeat(currentEl, currentTrackId);

        if (!preloadEl || !isPreloaded) {
            isTransitioningRef.current = false;
            crossfadeTriggeredRef.current = false;
            next();
            return;
        }

        console.log('[Gapless] Starting overlapping crossfade');

        const targetVolume = isMuted ? 0 : volume;

        // Start next track at 0 volume immediately
        preloadEl.currentTime = 0;
        preloadEl.volume = 0;
        preloadEl.play().catch(e => console.error("Transition failed:", e));

        // Crossfade: fade out current, fade in next
        const fadeSteps = 10;
        const fadeInterval = CROSSFADE_DURATION_MS / fadeSteps;
        let step = 0;

        const crossfade = setInterval(() => {
            step++;
            const fadeProgress = step / fadeSteps;

            if (currentEl && !isMuted) {
                currentEl.volume = Math.max(0, targetVolume * (1 - fadeProgress));
            }
            if (preloadEl && !isMuted) {
                preloadEl.volume = Math.min(targetVolume, targetVolume * fadeProgress);
            }

            if (step >= fadeSteps) {
                clearInterval(crossfade);

                // Complete the transition
                if (currentEl) {
                    currentEl.pause();
                    currentEl.volume = targetVolume;
                }
                if (preloadEl) {
                    preloadEl.volume = targetVolume;
                }

                // Swap active player
                setActivePlayer(prev => prev === 0 ? 1 : 0);

                // Update store
                next();
                setIsPreloaded(false);
                setPreloadedTrackId(null);
                isTransitioningRef.current = false;
                crossfadeTriggeredRef.current = false;
            }
        }, fadeInterval);

    }, [isPreloaded, preloadAudioRef, activeAudioRef, next, volume, isMuted, currentTrackId, reportFinalBeat]);

    // Manual skip with gapless
    const handleSkipNext = useCallback(() => {
        if (isPreloaded && queue.length > 0 && !isTransitioningRef.current) {
            performCrossfadeTransition();
        } else {
            next();
        }
    }, [isPreloaded, queue, performCrossfadeTransition, next]);

    // Set up the active audio element when track changes
    useEffect(() => {
        const activeEl = activeAudioRef.current;
        if (!activeEl || !currentTrack) return;

        // Only update src if it doesn't match (prevents reload on swap)
        if (!activeEl.src.includes(currentTrack.id)) {
            activeEl.src = currentStreamUrl;
        }

        // Reset crossfade trigger for new track
        crossfadeTriggeredRef.current = false;

        // Force apply volume to the new active element to prevent full-volume blast
        activeEl.volume = isMuted ? 0 : volume;

        if (isPlaying) {
            activeEl.play().catch(e => console.error("Playback failed", e));
        }
    }, [currentTrack, currentStreamUrl, activeAudioRef, isPlaying, volume, isMuted]);

    // Play/pause control
    useEffect(() => {
        const activeEl = activeAudioRef.current;
        if (!activeEl) return;

        if (isPlaying) {
            activeEl.play().catch(e => console.error("Playback failed", e));
        } else {
            activeEl.pause();
        }
    }, [isPlaying, activeAudioRef]);

    // Volume control - now controls Master Gain (Post-Visualizer)
    useEffect(() => {
        const vol = isMuted ? 0 : volume;

        // Apply volume to Master Gain (Post-Visualizer) using Web Audio if ready
        if (visualizerReady && setGlobalVolume) {
            setGlobalVolume(vol);
        }

        // Keep local audio elements at full volume for visualizer input
        // unless we are NOT using Web Audio (fallback)
        const activeEl = activeAudioRef.current;
        if (activeEl && !isTransitioningRef.current) {
            if (visualizerReady) {
                activeEl.volume = 1.0; // Source always max for visualizer
            } else {
                activeEl.volume = vol; // Fallback to element volume
            }
        }
    }, [volume, isMuted, activeAudioRef, visualizerReady, isTransitioningRef, setGlobalVolume]);

    // Handle repeat one mode
    useEffect(() => {
        const activeEl = activeAudioRef.current;
        if (activeEl) {
            activeEl.loop = repeatMode === 'one';
        }
    }, [repeatMode, activeAudioRef]);

    // Preload when queue changes
    useEffect(() => {
        if (queue.length > 0 && repeatMode !== 'one') {
            preloadNextTrack();
        }
    }, [queue, repeatMode, preloadNextTrack]);

    // Time update handler - triggers crossfade BEFORE track ends
    const handleTimeUpdate = useCallback(() => {
        const activeEl = activeAudioRef.current;
        if (!activeEl) return;

        const currentTime = activeEl.currentTime;
        const audioDuration = activeEl.duration || 0;
        setProgress(currentTime);
        setDuration(audioDuration);

        // R-WI-013: throttled listen beat. A track change re-stamps without posting, so the
        // first beat lands after ~10s of actual listening (rapid skips post nothing).
        const nowMs = Date.now();
        if (lastBeatRef.current.trackId !== (currentTrackId ?? null)) {
            lastBeatRef.current = { trackId: currentTrackId ?? null, at: nowMs };
        } else if (isPlaying && currentTime > 0 && nowMs - lastBeatRef.current.at >= 10_000) {
            lastBeatRef.current = { trackId: currentTrackId ?? null, at: nowMs };
            reportProgress(currentTrackId, currentTime);
        }

        // Trigger preload when approaching end
        if (audioDuration > 0 &&
            audioDuration - currentTime < PRELOAD_THRESHOLD &&
            queue.length > 0 &&
            repeatMode !== 'one') {
            preloadNextTrack();
        }

        // Trigger crossfade BEFORE track ends for gapless overlap
        if (audioDuration > 0 &&
            audioDuration - currentTime <= CROSSFADE_START &&
            audioDuration - currentTime > 0 &&
            isPreloaded &&
            queue.length > 0 &&
            repeatMode !== 'one' &&
            !crossfadeTriggeredRef.current &&
            !isTransitioningRef.current) {

            crossfadeTriggeredRef.current = true;
            console.log('[Gapless] Auto-triggering crossfade at', currentTime.toFixed(2), 'of', audioDuration.toFixed(2));
            performCrossfadeTransition();
        }
    }, [activeAudioRef, queue, repeatMode, preloadNextTrack, isPreloaded, performCrossfadeTransition, currentTrackId, isPlaying, reportProgress]);

    // Seek handler
    const seekToTime = useCallback((time: number) => {
        const activeEl = activeAudioRef.current;
        if (activeEl) {
            activeEl.currentTime = time;
            setProgress(time);
            // Reset crossfade trigger if seeking backward
            if (time < (duration - CROSSFADE_START)) {
                crossfadeTriggeredRef.current = false;
            }
        }
    }, [activeAudioRef, duration]);

    const handleSeek = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
        seekToTime(parseFloat(e.target.value));
    }, [seekToTime]);

    // Track ended handler (fallback if crossfade didn't trigger)
    const handleEnded = useCallback(() => {
        if (repeatMode === 'one') {
            const activeEl = activeAudioRef.current;
            // R-WI-013: a full loop is a full listen — credit it before restarting.
            reportFinalBeat(activeEl, currentTrackId);
            if (activeEl) {
                activeEl.currentTime = 0;
                activeEl.play();
            }
            return;
        }

        // Fallback if crossfade didn't happen
        if (!isTransitioningRef.current) {
            console.log('[Gapless] Fallback: track ended without crossfade');
            reportFinalBeat(activeAudioRef.current, currentTrackId); // R-WI-013: at 'ended', position == duration
            next();
        }
    }, [repeatMode, next, activeAudioRef, currentTrackId, reportFinalBeat]);

    // Previous track handler
    const handlePrevious = useCallback(() => {
        const activeEl = activeAudioRef.current;
        if (activeEl && activeEl.currentTime > 3) {
            activeEl.currentTime = 0;
            crossfadeTriggeredRef.current = false;
        } else {
            previous();
        }
    }, [previous, activeAudioRef]);

    // Seek backward 30 seconds
    const handleSeekBackward = useCallback(() => {
        const activeEl = activeAudioRef.current;
        if (activeEl) {
            activeEl.currentTime = Math.max(0, activeEl.currentTime - 30);
            crossfadeTriggeredRef.current = false;
        }
    }, [activeAudioRef]);

    // Seek forward 30 seconds
    const handleSeekForward = useCallback(() => {
        const activeEl = activeAudioRef.current;
        if (activeEl) {
            const newTime = activeEl.currentTime + 30;
            activeEl.currentTime = Math.min(newTime, activeEl.duration || newTime);
            // Don't reset crossfade trigger for forward seek
        }
    }, [activeAudioRef]);

    // Toggle Full Screen with Browser API
    const toggleFullScreen = useCallback(async () => {
        if (!document.fullscreenElement) {
            try {
                await document.documentElement.requestFullscreen();
                setIsFullScreen(true);
                setIsExpanded(true); // Ensure expanded view is active
            } catch (err) {
                console.error("Error attempting to enable full-screen mode:", err);
            }
        } else {
            if (document.exitFullscreen) {
                await document.exitFullscreen();
                setIsFullScreen(false);
            }
        }
    }, []);

    // Sync state with browser fullscreen changes (e.g. user presses Esc)
    useEffect(() => {
        const handleFullScreenChange = () => {
            // If document.fullscreenElement is null, we are not in fullscreen
            const isFS = !!document.fullscreenElement;
            setIsFullScreen(isFS);
            // If we exit fullscreen, what should happen?
            // Should we exit expanded mode? Maybe not necessarily.
        };

        document.addEventListener('fullscreenchange', handleFullScreenChange);
        return () => document.removeEventListener('fullscreenchange', handleFullScreenChange);
    }, []);

    // Keyboard shortcuts
    useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;

            switch (e.code) {
                case 'Space':
                    e.preventDefault();
                    isPlaying ? pause() : resume();
                    break;
                case 'ArrowRight':
                    e.preventDefault();
                    if (e.ctrlKey || e.metaKey) {
                        handleSkipNext();
                    } else {
                        handleSeekForward();
                    }
                    break;
                case 'ArrowLeft':
                    e.preventDefault();
                    if (e.ctrlKey || e.metaKey) {
                        handlePrevious();
                    } else {
                        handleSeekBackward();
                    }
                    break;
                case 'KeyM':
                    e.preventDefault();
                    toggleMute();
                    break;
                case 'KeyS':
                    if (e.shiftKey) {
                        e.preventDefault();
                        toggleShuffle();
                    }
                    break;
                case 'KeyR':
                    if (e.shiftKey) {
                        e.preventDefault();
                        cycleRepeatMode();
                    }
                    break;
                case 'Escape':
                    if (isExpanded && !isFullScreen) {
                        e.preventDefault();
                        setIsExpanded(false);
                    }
                    // Browser handles Escape for exiting FullScreen naturally,
                    // which triggers fullscreenchange -> setIsFullScreen(false).
                    break;
                case 'KeyV':
                    e.preventDefault();
                    toggleVisualizer();
                    break;
            }
        };

        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [isPlaying, pause, resume, handleSkipNext, handlePrevious, handleSeekBackward, handleSeekForward, toggleMute, toggleShuffle, cycleRepeatMode, isExpanded, toggleVisualizer, isFullScreen]);

    // R-WI-015: OS media controls (lock screen / media keys). The shared hook
    // arbitrates with VideoPlayer — whichever most recently started playing owns
    // the session. Must run BEFORE the early return below (hooks are unconditional).
    useMediaSession({
        enabled: !!currentTrack,
        isPlaying,
        contentId: currentTrack?.id ?? null,
        metadata: currentTrack ? {
            title: currentTrack.title,
            artist: (currentTrack.metadata?.artist as string) || (currentTrack.metadata?.albumArtist as string) || 'Unknown Artist',
            album: (currentTrack.metadata?.album as string) || undefined,
            artworkUrl: getImageUrl(currentTrack.posterPath),
        } : null,
        handlers: {
            onPlay: resume,
            onPause: pause,
            onPreviousTrack: handlePrevious,
            onNextTrack: handleSkipNext,
            onSeekBackward: handleSeekBackward,
            onSeekForward: handleSeekForward,
            onSeekTo: seekToTime,
        },
        position: { duration, position: progress },
    });

    if (!currentTrack) return null;

    const imageUrl = getImageUrl(currentTrack.posterPath);
    const RepeatIcon = repeatMode === 'one' ? Repeat1 : Repeat;

    return (
        <>
            {/* Persistent Audio Elements - outside conditionals to prevent re-mounting */}
            <audio
                ref={(el) => {
                    // Update Ref for imperative logic
                    if (audioARef) (audioARef as any).current = el;
                    // Update State for hook reactivity
                    if (el !== audioAElement) setAudioAElement(el);
                }}
                crossOrigin="use-credentials"
                onTimeUpdate={activePlayer === 0 ? handleTimeUpdate : undefined}
                onEnded={activePlayer === 0 ? handleEnded : undefined}
                onCanPlayThrough={activePlayer === 1 ? handlePreloadReady : undefined}
                preload="auto"
            />
            <audio
                ref={(el) => {
                    // Update Ref for imperative logic
                    if (audioBRef) (audioBRef as any).current = el;
                    // Update State for hook reactivity
                    if (el !== audioBElement) setAudioBElement(el);
                }}
                crossOrigin="use-credentials"
                onTimeUpdate={activePlayer === 1 ? handleTimeUpdate : undefined}
                onEnded={activePlayer === 1 ? handleEnded : undefined}
                onCanPlayThrough={activePlayer === 0 ? handlePreloadReady : undefined}
                preload="auto"
            />

            {/* Expanded Fullscreen View */}
            <AnimatePresence>
                {isExpanded && (
                    <motion.div
                        initial={{ y: "100%" }}
                        animate={{ y: 0 }}
                        exit={{ y: "100%" }}
                        transition={{ type: "spring", damping: 25, stiffness: 200 }}
                        className={cn(
                            "fixed inset-0 z-[100] flex flex-col overflow-hidden",
                            isFullScreen ? "bg-black" : "bg-gradient-to-b from-gray-900 via-gray-900 to-black"
                        )}
                        style={{
                            cursor: (isFullScreen && !showControls) ? 'none' : 'default'
                        }}
                    >
                        {/* Visualizer Canvas (behind content) */}
                        <AudioVisualizer
                            frequencyData={frequencyData}
                            timeDomainData={timeDomainData}
                            isReady={visualizerReady}
                            updateData={updateData}
                            className="z-0"
                        />

                        {/* Controls Container - Fades out in FS */}
                        <motion.div
                            className="flex-1 flex flex-col w-full h-full relative z-10"
                            animate={{ opacity: (isFullScreen && !showControls) ? 0 : 1 }}
                            transition={{ duration: 0.5 }}
                        >
                            {/* Header */}
                            <div className="flex items-center justify-between p-4 relative z-10">
                                <button
                                    onClick={() => {
                                        if (isFullScreen) toggleFullScreen();
                                        else setIsExpanded(false);
                                    }}
                                    className="text-gray-400 hover:text-white transition p-2"
                                    title="Minimize (Escape)"
                                >
                                    <ChevronDown size={28} />
                                </button>
                                <div className="text-center">
                                    <p className="text-gray-400 text-sm">Now Playing</p>
                                </div>
                                <div className="flex items-center space-x-2">
                                    {/* Visualizer Selector */}
                                    <VisualizerSelector />

                                    {/* Full Screen Toggle */}
                                    <button
                                        type="button"
                                        onClick={toggleFullScreen}
                                        aria-label={isFullScreen ? 'Exit fullscreen player' : 'Enter fullscreen player'}
                                        aria-pressed={isFullScreen}
                                        className={cn(
                                            "p-2 transition min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                            isFullScreen ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                        )}
                                        title="Toggle Full Screen"
                                    >
                                        {isFullScreen ? <Minimize2 size={24} /> : <Maximize2 size={24} />}
                                    </button>

                                    {/* Add current track to a playlist. Anchored on the
                                        header bar of the expanded view, opens downward. */}
                                    <div className="relative">
                                        <button
                                            type="button"
                                            onClick={() => setShowPlaylistMenu(v => !v)}
                                            aria-label="Add current track to a playlist"
                                            aria-haspopup="menu"
                                            aria-expanded={showPlaylistMenu}
                                            className={cn(
                                                "p-2 transition min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                                showPlaylistMenu ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                            )}
                                            title="Add to playlist"
                                        >
                                            <ListPlus size={24} />
                                        </button>
                                        {showPlaylistMenu && (
                                            <AddToPlaylistMenu
                                                mediaItemIds={[currentTrack.id]}
                                                onClose={() => setShowPlaylistMenu(false)}
                                            />
                                        )}
                                    </div>

                                    <button
                                        type="button"
                                        onClick={() => setShowQueue(!showQueue)}
                                        aria-label="Queue"
                                        aria-pressed={showQueue}
                                        className={cn(
                                            "p-2 transition relative min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                            showQueue ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                        )}
                                        title="Queue"
                                    >
                                        <List size={24} />
                                        {queue.length > 0 && (
                                            <span className="absolute top-0 right-0 bg-primary text-white text-xs w-4 h-4 rounded-full flex items-center justify-center">
                                                {queue.length > 9 ? '9+' : queue.length}
                                            </span>
                                        )}
                                    </button>

                                    <button
                                        type="button"
                                        onClick={closePlayer}
                                        aria-label="Close player"
                                        className="text-gray-400 hover:text-red-500 focus-visible:text-red-500 transition-colors p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                        title="Close Player"
                                    >
                                        <X size={24} />
                                    </button>
                                </div>
                            </div>

                            {/* Main Content */}
                            <div className="flex-1 flex relative">
                                {/* Album Art & Controls */}
                                <div className={cn(
                                    "flex-1 flex flex-col items-center justify-center px-8 transition-all relative z-10",
                                    showQueue ? "w-1/2 xl:w-full" : "w-full"
                                )}>
                                    {/* Large Album Art - Fades out if FS+Visualizer */}
                                    <div className={cn(
                                        "relative mb-8 transition-opacity duration-1000",
                                        (isFullScreen && visualizerEnabled) ? "opacity-0" : "opacity-100"
                                    )}>
                                        <img
                                            src={imageUrl}
                                            alt={currentTrack.title}
                                            referrerPolicy="no-referrer"
                                            className="w-72 h-72 md:w-96 md:h-96 rounded-lg object-cover shadow-2xl"
                                        />
                                        {isPreloaded && (
                                            <span className="absolute top-2 right-2 bg-primary/80 text-white text-xs px-2 py-1 rounded">
                                                Next Ready
                                            </span>
                                        )}
                                    </div>

                                    {/* Track Info */}
                                    <div className="text-center mb-6 max-w-md">
                                        <ScrollingText text={currentTrack.title} className="text-white text-2xl font-bold" />
                                        <p className="text-gray-400 text-lg truncate">
                                            {(currentTrack.metadata?.artist as string) || (currentTrack.metadata?.albumArtist as string) || 'Unknown Artist'}
                                        </p>
                                    </div>

                                    {/* Progress Bar */}
                                    <div className="w-full max-w-lg mb-6">
                                        <input
                                            type="range"
                                            min="0"
                                            max={duration || 100}
                                            value={progress}
                                            onChange={handleSeek}
                                            className="w-full h-2 bg-gray-700 rounded-lg appearance-none cursor-pointer [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-4 [&::-webkit-slider-thumb]:h-4 [&::-webkit-slider-thumb]:bg-primary [&::-webkit-slider-thumb]:rounded-full"
                                        />
                                        <div className="flex justify-between text-sm text-gray-400 mt-1">
                                            <span>{formatTime(progress)}</span>
                                            <span>{formatTime(duration)}</span>
                                        </div>
                                    </div>

                                    {/* Controls */}
                                    <div className="grid grid-cols-[1fr_auto_1fr] items-center w-full max-w-4xl mx-auto">
                                        <div className="flex justify-end items-center gap-6 pr-10">
                                            <button
                                                type="button"
                                                onClick={toggleShuffle}
                                                aria-label="Shuffle"
                                                aria-pressed={shuffleMode}
                                                className={cn(
                                                    "transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                                    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                                    shuffleMode ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                                )}
                                                title="Shuffle (Shift+S)"
                                            >
                                                <Shuffle size={24} />
                                            </button>

                                            <button
                                                type="button"
                                                onClick={handlePrevious}
                                                aria-label="Previous track"
                                                className="text-gray-400 hover:text-white focus-visible:text-white transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                            >
                                                <SkipBack size={32} />
                                            </button>

                                            <button
                                                type="button"
                                                onClick={handleSeekBackward}
                                                aria-label="Seek backward 30 seconds"
                                                className="text-gray-400 hover:text-white focus-visible:text-white transition relative p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                                title="Seek backward 30s"
                                            >
                                                <RotateCcw size={24} />
                                                <span className="absolute text-[10px] font-bold" style={{ top: '50%', left: '50%', transform: 'translate(-50%, -50%)' }}>30</span>
                                            </button>
                                        </div>

                                        <div className="flex justify-center items-center">
                                            <button
                                                type="button"
                                                onClick={isPlaying ? pause : resume}
                                                aria-label={isPlaying ? 'Pause' : 'Play'}
                                                className="w-16 h-16 rounded-full bg-white text-black flex items-center justify-center hover:scale-105 focus-visible:scale-105 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-offset-2 transition shadow-lg"
                                            >
                                                {isPlaying ? <Pause size={32} fill="currentColor" /> : <Play size={32} fill="currentColor" className="ml-1" />}
                                            </button>
                                        </div>

                                        <div className="flex justify-start items-center gap-6 pl-10">
                                            <button
                                                type="button"
                                                onClick={handleSeekForward}
                                                aria-label="Seek forward 30 seconds"
                                                className="text-gray-400 hover:text-white focus-visible:text-white transition relative p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                                title="Seek forward 30s"
                                            >
                                                <RotateCw size={24} />
                                                <span className="absolute text-[10px] font-bold" style={{ top: '50%', left: '50%', transform: 'translate(-50%, -50%)' }}>30</span>
                                            </button>

                                            <button
                                                type="button"
                                                onClick={handleSkipNext}
                                                aria-label="Next track"
                                                className="text-gray-400 hover:text-white focus-visible:text-white transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                            >
                                                <SkipForward size={32} />
                                            </button>

                                            <button
                                                type="button"
                                                onClick={cycleRepeatMode}
                                                aria-label={`Repeat mode: ${repeatMode}`}
                                                className={cn(
                                                    "transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                                    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                                    repeatMode !== 'off' ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                                )}
                                                title={`Repeat: ${repeatMode} (Shift+R)`}
                                            >
                                                <RepeatIcon size={24} />
                                            </button>

                                            {/* Divider */}
                                            <div className="w-px h-6 bg-gray-700/50 mx-2" />

                                            {/* Volume Control Integrated */}
                                            <div className="relative flex items-center group/volume z-20">
                                                <button
                                                    type="button"
                                                    onClick={toggleMute}
                                                    aria-label={isMuted ? 'Unmute' : 'Mute'}
                                                    aria-pressed={isMuted}
                                                    className="text-gray-400 hover:text-white focus-visible:text-white p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors relative z-10"
                                                >
                                                    {isMuted ? <VolumeX size={24} /> : <Volume2 size={24} />}
                                                </button>

                                                <div className="absolute left-1/2 -translate-x-1/2 bottom-full flex flex-col items-center bg-gray-800/80 backdrop-blur-sm border border-white/5 rounded-lg px-2 py-4 opacity-0 scale-y-0 origin-bottom group-hover/volume:opacity-100 group-hover/volume:scale-y-100 transition-all duration-300 delay-150 group-hover/volume:delay-0 pointer-events-none group-hover/volume:pointer-events-auto shadow-xl w-11">
                                                    <input
                                                        type="range"
                                                        min="0"
                                                        max="1"
                                                        step="0.05"
                                                        value={isMuted ? 0 : volume}
                                                        onChange={(e) => {
                                                            const newVol = parseFloat(e.target.value);
                                                            if (isMuted && newVol > 0) toggleMute();
                                                            setVolume(newVol);
                                                        }}
                                                        className="h-24 accent-blue-500 cursor-pointer"
                                                        style={{ writingMode: 'vertical-lr', transform: 'rotate(180deg)' }}
                                                    />
                                                    <span className="text-white/70 text-xs mt-2">{Math.round((isMuted ? 0 : volume) * 100)}%</span>
                                                </div>
                                            </div>
                                        </div>
                                    </div>


                                </div>

                                {/* Inline Queue (when shown) */}
                                {showQueue && (
                                    <div className={cn(
                                        "w-80 bg-black/30 border-l border-gray-800 overflow-hidden flex flex-col scale-in-hor-right origin-right animate-in fade-in duration-300",
                                        "xl:absolute xl:right-0 xl:top-0 xl:bottom-0 xl:z-10 xl:bg-black/80 xl:backdrop-blur-md xl:border-l-gray-700"
                                    )}>
                                        <div className="flex items-center justify-between px-4 py-3 border-b border-gray-800">
                                            <h3 className="text-white font-semibold">Up Next</h3>
                                            <span className="text-gray-400 text-sm">{queue.length} tracks</span>
                                        </div>
                                        <div className="flex-1 overflow-y-auto flex flex-col">
                                            <QueueList />
                                        </div>
                                    </div>
                                )}
                            </div>
                        </motion.div>
                    </motion.div>
                )}
            </AnimatePresence>

            {/* Queue Drawer (collapsed mode only) */}
            <AnimatePresence>
                {showQueue && !isExpanded && (
                    <motion.div
                        initial={{ opacity: 0, y: 50, scale: 0.95 }}
                        animate={{ opacity: 1, y: 0, scale: 1 }}
                        exit={{ opacity: 0, y: 50, scale: 0.95 }}
                        transition={{ duration: 0.2 }}
                        className="fixed bottom-20 right-4 w-80 max-h-96 bg-gray-900 border border-gray-700 rounded-lg shadow-2xl z-50 overflow-hidden"
                    >
                        <div className="flex items-center justify-between px-4 py-3 border-b border-gray-700">
                            <h3 className="text-white font-semibold">Up Next</h3>
                            <button onClick={() => setShowQueue(false)} className="text-gray-400 hover:text-white">
                                <X size={18} />
                            </button>
                        </div>
                        <div className="flex-1 overflow-hidden flex flex-col">
                            <QueueList />
                        </div>
                    </motion.div>
                )}
            </AnimatePresence>

            {/* Player Bar (collapsed mode only) */}
            <AnimatePresence>
                {!isExpanded && (
                    <motion.div
                        initial={{ y: 100 }}
                        animate={{ y: 0 }}
                        exit={{ y: 100 }}
                        transition={{ duration: 0.3 }}
                        className="fixed bottom-0 left-0 w-screen h-20 bg-gray-900 border-t border-gray-800 flex items-center px-4 z-50 shadow-2xl"
                    >
                        {/* Visualizer Canvas (behind content) */}
                        <AudioVisualizer
                            frequencyData={frequencyData}
                            timeDomainData={timeDomainData}
                            isReady={visualizerReady}
                            updateData={updateData}
                            className="z-0"
                        />
                        {/* Track Info */}
                        <div className="flex items-center w-1/4 min-w-[200px] relative z-10 pointer-events-auto">
                            <img
                                src={imageUrl}
                                alt={currentTrack.title}
                                referrerPolicy="no-referrer"
                                className="w-14 h-14 rounded object-cover mr-4 bg-gray-800"
                            />
                            <div className="truncate">
                                <ScrollingText text={currentTrack.title} className="text-white font-medium" />
                                <p className="text-gray-400 text-sm truncate">
                                    {(currentTrack.metadata?.artist as string) || (currentTrack.metadata?.albumArtist as string) || 'Unknown Artist'}
                                </p>
                            </div>
                            {/* Expand Button */}
                            <button
                                type="button"
                                onClick={() => setIsExpanded(true)}
                                aria-label="Expand player"
                                className="text-gray-400 hover:text-white focus-visible:text-white transition ml-2 p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                title="Expand (Shift+F)"
                            >
                                <ChevronUp size={20} />
                            </button>
                        </div>

                        {/* Controls */}
                        <div className="flex-1 flex flex-col items-center justify-center relative z-10 pointer-events-auto">
                            <div className="flex items-center space-x-4 mb-1">
                                <button
                                    type="button"
                                    onClick={toggleShuffle}
                                    aria-label="Shuffle"
                                    aria-pressed={shuffleMode}
                                    className={cn(
                                        "transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                        shuffleMode ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                    )}
                                    title="Shuffle (Shift+S)"
                                >
                                    <Shuffle size={18} />
                                </button>

                                <button
                                    type="button"
                                    onClick={handlePrevious}
                                    aria-label="Previous track"
                                    className="text-gray-400 hover:text-white focus-visible:text-white transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                >
                                    <SkipBack size={22} />
                                </button>

                                {/* Seek Backward 30s */}
                                <button
                                    type="button"
                                    onClick={handleSeekBackward}
                                    aria-label="Seek backward 30 seconds"
                                    className="text-gray-400 hover:text-white focus-visible:text-white transition relative p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                    title="Seek backward 30s"
                                >
                                    <RotateCcw size={18} />
                                    <span className="absolute text-[8px] font-bold" style={{ top: '50%', left: '50%', transform: 'translate(-50%, -50%)' }}>30</span>
                                </button>

                                <button
                                    type="button"
                                    onClick={isPlaying ? pause : resume}
                                    aria-label={isPlaying ? 'Pause' : 'Play'}
                                    className="w-10 h-10 min-w-[44px] min-h-[44px] rounded-full bg-white text-black flex items-center justify-center hover:scale-105 focus-visible:scale-105 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-offset-2 focus-visible:ring-offset-gray-900 transition"
                                >
                                    {isPlaying ? <Pause size={20} fill="currentColor" /> : <Play size={20} fill="currentColor" className="ml-1" />}
                                </button>

                                {/* Seek Forward 30s */}
                                <button
                                    type="button"
                                    onClick={handleSeekForward}
                                    aria-label="Seek forward 30 seconds"
                                    className="text-gray-400 hover:text-white focus-visible:text-white transition relative p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                    title="Seek forward 30s"
                                >
                                    <RotateCw size={18} />
                                    <span className="absolute text-[8px] font-bold" style={{ top: '50%', left: '50%', transform: 'translate(-50%, -50%)' }}>30</span>
                                </button>

                                <button
                                    type="button"
                                    onClick={handleSkipNext}
                                    aria-label="Next track"
                                    className="text-gray-400 hover:text-white focus-visible:text-white transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                >
                                    <SkipForward size={22} />
                                </button>

                                <button
                                    type="button"
                                    onClick={cycleRepeatMode}
                                    aria-label={`Repeat mode: ${repeatMode}`}
                                    className={cn(
                                        "transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded",
                                        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                        repeatMode !== 'off' ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                    )}
                                    title={`Repeat: ${repeatMode} (Shift+R)`}
                                >
                                    <RepeatIcon size={18} />
                                </button>
                            </div>

                            {/* Progress Bar */}
                            <div className="w-full max-w-md flex items-center space-x-2 text-xs text-gray-400">
                                <span>{formatTime(progress)}</span>
                                <input
                                    type="range"
                                    min="0"
                                    max={duration || 100}
                                    value={progress}
                                    onChange={handleSeek}
                                    className="flex-1 h-1 bg-gray-700 rounded-lg appearance-none cursor-pointer [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-3 [&::-webkit-slider-thumb]:h-3 [&::-webkit-slider-thumb]:bg-white [&::-webkit-slider-thumb]:rounded-full"
                                />
                                <span>{formatTime(duration)}</span>
                            </div>
                        </div>

                        {/* Volume & Extras */}
                        <div className="w-1/4 flex items-center justify-end space-x-3 relative z-10 pointer-events-auto">
                            {/* Visualizer Toggle */}
                            <VisualizerSelector className="hidden md:block" direction="up" iconSize={20} />

                            <button
                                type="button"
                                onClick={toggleFullScreen}
                                aria-label={isFullScreen ? 'Exit fullscreen player' : 'Enter fullscreen player'}
                                aria-pressed={isFullScreen}
                                className={cn(
                                    "p-2 transition rounded-full min-w-[44px] min-h-[44px] flex items-center justify-center",
                                    "hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                    isFullScreen ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                )}
                                title="Toggle Full Screen"
                            >
                                <Maximize2 size={20} />
                            </button>

                            {/* Add current track to a playlist. Collapsed bar sits at
                                the bottom of the viewport, so the popover opens
                                upward to stay on-screen. */}
                            <div className="relative">
                                <button
                                    type="button"
                                    onClick={() => setShowPlaylistMenu(v => !v)}
                                    aria-label="Add current track to a playlist"
                                    aria-haspopup="menu"
                                    aria-expanded={showPlaylistMenu}
                                    className={cn(
                                        "p-2 transition rounded-full min-w-[44px] min-h-[44px] flex items-center justify-center",
                                        "hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                        showPlaylistMenu ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                    )}
                                    title="Add to playlist"
                                >
                                    <ListPlus size={20} />
                                </button>
                                {showPlaylistMenu && (
                                    <AddToPlaylistMenu
                                        mediaItemIds={[currentTrack.id]}
                                        onClose={() => setShowPlaylistMenu(false)}
                                        placement="up"
                                    />
                                )}
                            </div>

                            <button
                                type="button"
                                onClick={() => setShowQueue(!showQueue)}
                                aria-label="Queue"
                                aria-pressed={showQueue}
                                className={cn(
                                    "p-2 transition relative rounded-full min-w-[44px] min-h-[44px] flex items-center justify-center",
                                    "hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                    showQueue ? "text-primary" : "text-gray-400 hover:text-white focus-visible:text-white"
                                )}
                                title="Queue"
                            >
                                <List size={20} />
                                {queue.length > 0 && (
                                    <span className="absolute -top-2 -right-2 bg-primary text-white text-[10px] w-4 h-4 rounded-full flex items-center justify-center border-2 border-gray-900">
                                        {queue.length > 9 ? '9+' : queue.length}
                                    </span>
                                )}
                            </button>

                            <div className="relative flex items-center group/volume z-20">
                                <button
                                    type="button"
                                    onClick={toggleMute}
                                    aria-label={isMuted ? 'Unmute' : 'Mute'}
                                    aria-pressed={isMuted}
                                    className="text-gray-400 hover:text-white focus-visible:text-white p-2 min-w-[44px] min-h-[44px] flex items-center justify-center transition-colors relative z-10 rounded-full hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                >
                                    {isMuted ? <VolumeX size={20} /> : <Volume2 size={20} />}
                                </button>

                                <div className="absolute left-1/2 -translate-x-1/2 bottom-full flex flex-col items-center bg-gray-800/80 backdrop-blur-sm border border-white/5 rounded-lg px-2 py-4 opacity-0 scale-y-0 origin-bottom group-hover/volume:opacity-100 group-hover/volume:scale-y-100 transition-all duration-300 delay-150 group-hover/volume:delay-0 pointer-events-none group-hover/volume:pointer-events-auto shadow-xl w-11">
                                    <input
                                        type="range"
                                        min="0"
                                        max="1"
                                        step="0.05"
                                        value={isMuted ? 0 : volume}
                                        onChange={(e) => {
                                            const newVol = parseFloat(e.target.value);
                                            if (isMuted && newVol > 0) toggleMute();
                                            setVolume(newVol);
                                        }}
                                        className="h-24 accent-blue-500 cursor-pointer"
                                        style={{ writingMode: 'vertical-lr', transform: 'rotate(180deg)' }}
                                    />
                                    <span className="text-white/70 text-xs mt-2">{Math.round((isMuted ? 0 : volume) * 100)}%</span>
                                </div>
                            </div>

                            <button
                                type="button"
                                onClick={closePlayer}
                                aria-label="Close player"
                                className="text-gray-400 hover:text-red-500 focus-visible:text-red-500 transition p-2 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                title="Close Player"
                            >
                                <X size={20} />
                            </button>


                        </div>
                    </motion.div>
                )}
            </AnimatePresence>
        </>
    );
};
