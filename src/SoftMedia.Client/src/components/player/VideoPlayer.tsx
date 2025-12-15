import { useEffect, useRef, useState, useCallback } from 'react';
import Hls from 'hls.js';
import { type MediaItem } from '../../types';
import { useAuthStore } from '../../store/authStore';

interface VideoPlayerProps {
    item: MediaItem;
    src: string;
}

interface TrackInfo {
    index: number;
    type: string;
    language?: string;
    title?: string;
    codec?: string;
    isDefault: boolean;
}

interface TracksResponse {
    audioTracks: TrackInfo[];
    subtitleTracks: TrackInfo[];
}

const PLAYBACK_SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];

/**
 * VideoPlayer component with custom controls, keyboard shortcuts, playback speed, and PiP support.
 * Uses native HTML5 video with hls.js for HLS support, but with custom UI controls.
 */
export default function VideoPlayer({ item, src: initialSrc }: VideoPlayerProps) {
    const videoRef = useRef<HTMLVideoElement>(null);
    const hlsRef = useRef<Hls | null>(null);
    const progressRef = useRef<HTMLDivElement>(null);
    const containerRef = useRef<HTMLDivElement>(null);
    const controlsTimeoutRef = useRef<NodeJS.Timeout | null>(null);
    const seekAfterLoadRef = useRef<number>(0); // Position to seek to after HLS reloads

    const [src, setSrc] = useState<string>('');
    const [isTranscoding, setIsTranscoding] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    const [isBuffering, setIsBuffering] = useState(false);

    // Playback state
    const [isPlaying, setIsPlaying] = useState(false);
    const [currentTime, setCurrentTime] = useState(0);
    const [seekOffset, setSeekOffset] = useState(0); // Offset when transcoding starts from non-zero position
    const [bufferedTime, setBufferedTime] = useState(0);
    const [volume, setVolume] = useState(1);
    const [isMuted, setIsMuted] = useState(false);
    const [isFullscreen, setIsFullscreen] = useState(false);
    const [showControls, setShowControls] = useState(true);
    const [playbackSpeed, setPlaybackSpeed] = useState(1);
    const [showSpeedMenu, setShowSpeedMenu] = useState(false);
    const [isPiP, setIsPiP] = useState(false);

    // Track selection state
    const [audioTracks, setAudioTracks] = useState<TrackInfo[]>([]);
    const [subtitleTracks, setSubtitleTracks] = useState<TrackInfo[]>([]);
    const [selectedAudioTrack, setSelectedAudioTrack] = useState<number | null>(null);
    const [selectedSubtitleTrack, setSelectedSubtitleTrack] = useState<number | null>(null);
    const [showTrackMenu, setShowTrackMenu] = useState(false);

    // Duration from FFprobe (when not in metadata)
    const [probedDuration, setProbedDuration] = useState<number>(0);

    const token = useAuthStore((state) => state.token);

    // Get actual duration from media item metadata (in seconds)
    const getActualDuration = useCallback((): number => {
        if (!item.duration) return 0;
        if (typeof item.duration === 'number') return item.duration;
        // Parse formats like "1h 45m" or "1h 45m 30s"
        const match = item.duration.match(/(?:(\d+)h)?\s*(?:(\d+)m)?\s*(?:(\d+)s)?/);
        if (match) {
            const hours = parseInt(match[1] || '0', 10);
            const minutes = parseInt(match[2] || '0', 10);
            const seconds = parseInt(match[3] || '0', 10);
            const totalSeconds = hours * 3600 + minutes * 60 + seconds;
            return totalSeconds;
        }
        return 0;
    }, [item.duration]);

    const actualDuration = getActualDuration();

    // Fetch duration from FFprobe if not in metadata
    useEffect(() => {
        if (actualDuration > 0 || !token || !item.id) return; // Already have duration

        const fetchDuration = async () => {
            try {
                const response = await fetch(`/api/media/${item.id}/duration?token=${token}`);
                if (response.ok) {
                    const duration = await response.json();
                    if (duration > 0) {
                        setProbedDuration(duration);
                    }
                }
            } catch {
                // Silently fail - will fall back to video element duration
            }
        };
        fetchDuration();
    }, [actualDuration, token, item.id]);

    // Use metadata duration, probed duration, or video element duration as fallback
    const displayDuration = actualDuration > 0 ? actualDuration : (probedDuration > 0 ? probedDuration : (videoRef.current?.duration || 0));
    const formatTime = (seconds: number): string => {
        if (!seconds || isNaN(seconds) || seconds < 0) return '0:00';
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        if (h > 0) {
            return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        }
        return `${m}:${s.toString().padStart(2, '0')}`;
    };

    // Auto-hide controls after inactivity
    const resetControlsTimeout = useCallback(() => {
        if (controlsTimeoutRef.current) {
            clearTimeout(controlsTimeoutRef.current);
        }
        setShowControls(true);
        if (isPlaying) {
            controlsTimeoutRef.current = setTimeout(() => {
                setShowControls(false);
                setShowSpeedMenu(false);
                setShowTrackMenu(false);
            }, 3000);
        }
    }, [isPlaying]);

    // Prevent auto-hide when interacting with menus
    const handleMenuInteraction = (isEntering: boolean) => {
        if (isEntering) {
            if (controlsTimeoutRef.current) clearTimeout(controlsTimeoutRef.current);
        } else {
            resetControlsTimeout();
        }
    };

    // Keyboard shortcuts
    useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            // Don't handle if typing in an input
            if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;

            const video = videoRef.current;
            if (!video) return;

            switch (e.key.toLowerCase()) {
                case ' ':
                case 'k':
                    e.preventDefault();
                    togglePlay();
                    break;
                case 'arrowleft':
                case 'j':
                    e.preventDefault();
                    skip(-10);
                    break;
                case 'arrowright':
                case 'l':
                    e.preventDefault();
                    skip(10);
                    break;
                case 'arrowup':
                    e.preventDefault();
                    video.volume = Math.min(1, video.volume + 0.1);
                    break;
                case 'arrowdown':
                    e.preventDefault();
                    video.volume = Math.max(0, video.volume - 0.1);
                    break;
                case 'm':
                    e.preventDefault();
                    toggleMute();
                    break;
                case 'f':
                    e.preventDefault();
                    toggleFullscreen();
                    break;
                case 'p':
                    e.preventDefault();
                    togglePiP();
                    break;
                case 'escape':
                    setShowSpeedMenu(false);
                    break;
            }
            resetControlsTimeout();
        };

        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [resetControlsTimeout]);

    // Determine playback strategy based on container/codec
    useEffect(() => {
        if (!token) return;

        const container = item.container?.toLowerCase() || '';
        const videoCodec = item.videoCodec?.toLowerCase() || '';

        const directPlayContainers = ['mp4', 'webm', 'ogg', 'mov', 'm4v'];
        const directPlayCodecs = ['h264', 'avc', 'avc1', 'vp8', 'vp9', 'av1'];

        const isContainerSupported = directPlayContainers.includes(container);
        const isCodecSupported = !videoCodec || directPlayCodecs.some(c => videoCodec.includes(c));
        const needsTranscode = !isContainerSupported || !isCodecSupported;

        if (needsTranscode) {
            console.log(`Format ${container}/${videoCodec} not supported, switching to HLS transcoding.`);

            // Save current playback position before switching subtitles
            const currentPosition = videoRef.current?.currentTime || 0;
            const effectivePosition = currentPosition + seekOffset; // Include any existing offset

            if (effectivePosition > 5) {
                // Starting from a non-zero position
                seekAfterLoadRef.current = effectivePosition;
                setSeekOffset(Math.floor(effectivePosition));
            } else {
                // Starting from the beginning
                seekAfterLoadRef.current = 0;
                setSeekOffset(0);
            }

            // Cleanup all previous transcodes for this media before starting new one
            fetch(`/api/transcode/${item.id}?all=true&token=${token}`, { method: 'DELETE' })
                .catch(() => { });

            // Include subtitle track and seek position in the HLS URL
            let hlsUrl = `/api/transcode/${item.id}/master.m3u8?token=${token}`;
            if (selectedSubtitleTrack !== null) {
                hlsUrl += `&sub=${selectedSubtitleTrack}`;
            }
            // Pass seek position to start transcoding from current position
            if (effectivePosition > 5) {
                hlsUrl += `&seek=${Math.floor(effectivePosition)}`;
            }
            setSrc(hlsUrl);
            setIsTranscoding(true);
        } else {
            console.log(`Direct play supported for ${container}/${videoCodec}`);
            const directUrl = `${initialSrc}${initialSrc.includes('?') ? '&' : '?'}token=${token}`;
            setSrc(directUrl);
            setIsTranscoding(false);
            setSeekOffset(0); // Reset offset for direct play
        }
    }, [item, initialSrc, token, selectedSubtitleTrack]); // Re-run when subtitle changes to update HLS URL

    // Fetch audio and subtitle tracks
    useEffect(() => {
        if (!token || !item.id) return;

        const fetchTracks = async () => {
            try {
                const response = await fetch(`/api/media/${item.id}/tracks`, {
                    headers: { Authorization: `Bearer ${token}` }
                });
                if (response.ok) {
                    const data: TracksResponse = await response.json();
                    setAudioTracks(data.audioTracks || []);
                    setSubtitleTracks(data.subtitleTracks || []);

                    // Set defaults
                    const defaultAudio = data.audioTracks?.find(t => t.isDefault);
                    if (defaultAudio) setSelectedAudioTrack(defaultAudio.index);

                    const defaultSub = data.subtitleTracks?.find(t => t.isDefault);
                    if (defaultSub) setSelectedSubtitleTrack(defaultSub.index);
                }
            } catch (err) {
                console.error('Failed to fetch tracks:', err);
            }
        };

        fetchTracks();
    }, [item.id, token]);

    // Setup HLS.js or native playback
    useEffect(() => {
        if (!src || !videoRef.current) return;

        const video = videoRef.current;
        setIsLoading(true);
        setError(null);

        if (hlsRef.current) {
            hlsRef.current.destroy();
            hlsRef.current = null;
        }

        if (isTranscoding && src.includes('.m3u8')) {
            if (Hls.isSupported()) {
                const hls = new Hls({
                    debug: false,
                    enableWorker: true,
                    lowLatencyMode: false,
                    startPosition: 0,
                    liveSyncDurationCount: 0,
                    liveBackBufferLength: Infinity,
                    manifestLoadingMaxRetry: 10,
                    manifestLoadingRetryDelay: 2000,
                    levelLoadingMaxRetry: 6,
                    fragLoadingMaxRetry: 6,
                });

                hls.loadSource(src);
                hls.attachMedia(video);

                hls.on(Hls.Events.MANIFEST_PARSED, () => {
                    console.log('HLS manifest parsed, ready to play');
                    setIsLoading(false);

                    // Auto-play if we were switching subtitles (seekAfterLoadRef > 0)
                    // The seek position is handled by FFmpeg's -ss flag, so we just need to play
                    if (seekAfterLoadRef.current > 0) {
                        video.play().catch(() => { });
                        seekAfterLoadRef.current = 0; // Reset after use
                    }
                });

                hls.on(Hls.Events.ERROR, (_, data) => {
                    if (data.fatal) {
                        switch (data.type) {
                            case Hls.ErrorTypes.NETWORK_ERROR:
                                hls.startLoad();
                                break;
                            case Hls.ErrorTypes.MEDIA_ERROR:
                                hls.recoverMediaError();
                                break;
                            default:
                                setError(`Playback error: ${data.type}`);
                                hls.destroy();
                                break;
                        }
                    }
                });

                hlsRef.current = hls;
            } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
                video.src = src;
                video.addEventListener('loadedmetadata', () => setIsLoading(false));
            } else {
                setError('HLS is not supported in this browser');
                setIsLoading(false);
            }
        } else {
            video.src = src;
            video.addEventListener('loadeddata', () => setIsLoading(false));
            video.load();
        }

        return () => {
            if (hlsRef.current) {
                hlsRef.current.destroy();
                hlsRef.current = null;
            }
            // Stop the transcode on the server when leaving the page
            if (isTranscoding && token && item.id) {
                fetch(`/api/transcode/${item.id}?all=true&token=${token}`, { method: 'DELETE' })
                    .catch(() => { });
            }
        };
    }, [src, isTranscoding, token, item.id]);

    // Video event handlers
    useEffect(() => {
        const video = videoRef.current;
        if (!video) return;

        const handleTimeUpdate = () => setCurrentTime(video.currentTime);
        const handlePlay = () => {
            setIsPlaying(true);
            // Signal backend to resume transcoding (throttle control)
            if (isTranscoding && token && item.id) {
                fetch(`/api/transcode/${item.id}/resume?token=${token}${selectedSubtitleTrack !== null ? `&sub=${selectedSubtitleTrack}` : ''}`, {
                    method: 'POST'
                }).catch(() => { });
            }
        };
        const handlePause = () => {
            setIsPlaying(false);
            // Signal backend to pause transcoding (throttle control)
            if (isTranscoding && token && item.id) {
                fetch(`/api/transcode/${item.id}/pause?token=${token}${selectedSubtitleTrack !== null ? `&sub=${selectedSubtitleTrack}` : ''}`, {
                    method: 'POST'
                }).catch(() => { });
            }
        };
        const handlePlaying = () => {
            setIsPlaying(true);
            setIsBuffering(false);
        };
        const handleWaiting = () => setIsBuffering(true);
        const handleProgress = () => {
            if (video.buffered.length > 0) {
                setBufferedTime(video.buffered.end(video.buffered.length - 1));
            }
        };
        const handleVolumeChange = () => {
            setVolume(video.volume);
            setIsMuted(video.muted);
        };
        const handleEnterPiP = () => setIsPiP(true);
        const handleLeavePiP = () => setIsPiP(false);
        const handleEnded = () => {
            // Signal backend to clean up transcode session when video ends
            if (isTranscoding && token && item.id) {
                fetch(`/api/transcode/${item.id}?all=true&token=${token}`, {
                    method: 'DELETE'
                }).catch(() => { });
            }
        };

        video.addEventListener('timeupdate', handleTimeUpdate);
        video.addEventListener('play', handlePlay);
        video.addEventListener('pause', handlePause);
        video.addEventListener('playing', handlePlaying);
        video.addEventListener('waiting', handleWaiting);
        video.addEventListener('progress', handleProgress);
        video.addEventListener('volumechange', handleVolumeChange);
        video.addEventListener('enterpictureinpicture', handleEnterPiP);
        video.addEventListener('leavepictureinpicture', handleLeavePiP);
        video.addEventListener('ended', handleEnded);

        return () => {
            video.removeEventListener('timeupdate', handleTimeUpdate);
            video.removeEventListener('play', handlePlay);
            video.removeEventListener('pause', handlePause);
            video.removeEventListener('playing', handlePlaying);
            video.removeEventListener('waiting', handleWaiting);
            video.removeEventListener('progress', handleProgress);
            video.removeEventListener('volumechange', handleVolumeChange);
            video.removeEventListener('enterpictureinpicture', handleEnterPiP);
            video.removeEventListener('leavepictureinpicture', handleLeavePiP);
            video.removeEventListener('ended', handleEnded);
        };
    }, [src, isTranscoding, token, item.id, selectedSubtitleTrack]);

    // Fullscreen change handler
    useEffect(() => {
        const handleFullscreenChange = () => {
            setIsFullscreen(!!document.fullscreenElement);
        };
        document.addEventListener('fullscreenchange', handleFullscreenChange);
        return () => document.removeEventListener('fullscreenchange', handleFullscreenChange);
    }, []);

    // Note: Burn-in subtitles are handled via the HLS URL - no need for text tracks
    // The selectedSubtitleTrack state triggers HLS URL change (via playback strategy useEffect)
    // which restarts the transcode with the subtitle burned in

    // Control actions
    const togglePlay = () => {
        if (!videoRef.current) return;
        if (isPlaying) {
            videoRef.current.pause();
        } else {
            videoRef.current.play();
        }
    };

    const handleSeek = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!progressRef.current || !videoRef.current || displayDuration <= 0) return;
        const rect = progressRef.current.getBoundingClientRect();
        const percent = (e.clientX - rect.left) / rect.width;
        const seekTime = Math.max(0, percent * displayDuration);

        // For transcoding: check if seeking beyond the current video duration (transcoded portion)
        // If so, restart transcode from the seek position
        const currentTranscodedDuration = videoRef.current.duration || 0;
        const effectiveCurrentTime = currentTime + seekOffset;

        if (isTranscoding && token && seekTime > effectiveCurrentTime + currentTranscodedDuration + 5) {
            // Seeking beyond transcoded portion - restart transcode at this position
            console.log(`Seeking to ${seekTime}s - beyond transcoded range, restarting transcode`);

            // Store seek position for auto-play after restart and set offset
            seekAfterLoadRef.current = seekTime;
            setSeekOffset(Math.floor(seekTime)); // Track the offset for time display

            // Cleanup current transcode
            fetch(`/api/transcode/${item.id}?all=true&token=${token}`, { method: 'DELETE' })
                .catch(() => { });

            // Build new URL with seek position
            let hlsUrl = `/api/transcode/${item.id}/master.m3u8?token=${token}&seek=${Math.floor(seekTime)}`;
            if (selectedSubtitleTrack !== null) {
                hlsUrl += `&sub=${selectedSubtitleTrack}`;
            }

            // Update src which triggers HLS reinit
            setSrc(hlsUrl);
        } else {
            // Seeking within current range - calculate target time relative to current offset
            const targetInStream = seekTime - seekOffset;
            if (targetInStream >= 0 && targetInStream <= currentTranscodedDuration) {
                videoRef.current.currentTime = targetInStream;
            } else if (isTranscoding && token && targetInStream < 0) {
                // Seeking before current offset - need to restart from new position
                console.log(`Seeking to ${seekTime}s - before current offset, restarting transcode`);
                seekAfterLoadRef.current = seekTime;
                setSeekOffset(Math.floor(seekTime));

                fetch(`/api/transcode/${item.id}?all=true&token=${token}`, { method: 'DELETE' })
                    .catch(() => { });

                let hlsUrl = `/api/transcode/${item.id}/master.m3u8?token=${token}&seek=${Math.floor(seekTime)}`;
                if (selectedSubtitleTrack !== null) {
                    hlsUrl += `&sub=${selectedSubtitleTrack}`;
                }
                setSrc(hlsUrl);
            } else {
                // Non-transcoding direct play
                videoRef.current.currentTime = Math.min(seekTime, videoRef.current.duration || Infinity);
            }
        }
    };

    const handleVolumeChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (!videoRef.current) return;
        const newVolume = parseFloat(e.target.value);
        videoRef.current.volume = newVolume;
        videoRef.current.muted = newVolume === 0;
    };

    const toggleMute = () => {
        if (!videoRef.current) return;
        videoRef.current.muted = !videoRef.current.muted;
    };

    const toggleFullscreen = () => {
        if (!containerRef.current) return;
        if (!document.fullscreenElement) {
            containerRef.current.requestFullscreen();
        } else {
            document.exitFullscreen();
        }
    };

    const skip = (seconds: number) => {
        if (!videoRef.current) return;
        videoRef.current.currentTime = Math.max(0, videoRef.current.currentTime + seconds);
    };

    const changePlaybackSpeed = (speed: number) => {
        if (!videoRef.current) return;
        videoRef.current.playbackRate = speed;
        setPlaybackSpeed(speed);
        setShowSpeedMenu(false);
    };

    const togglePiP = async () => {
        if (!videoRef.current) return;
        try {
            if (document.pictureInPictureElement) {
                await document.exitPictureInPicture();
            } else if (document.pictureInPictureEnabled) {
                await videoRef.current.requestPictureInPicture();
            }
        } catch (err) {
            console.error('PiP error:', err);
        }
    };

    // Progress bar percentages
    // Calculate displayed time including seek offset (for when transcoding starts from non-zero position)
    const displayedTime = currentTime + seekOffset;
    const progressPercent = displayDuration > 0 ? (displayedTime / displayDuration) * 100 : 0;

    // For HLS streams, buffer is relative to current stream position, need to add seekOffset
    const displayedBuffered = isTranscoding ? bufferedTime + seekOffset : bufferedTime;
    const bufferedPercent = displayDuration > 0 ? (displayedBuffered / displayDuration) * 100 : 0;

    if (!token || !src) {
        return (
            <div className="w-full max-w-5xl mx-auto aspect-video bg-black rounded-xl flex items-center justify-center">
                <div className="text-white/50 animate-pulse">Loading player...</div>
            </div>
        );
    }

    if (error) {
        return (
            <div className="w-full max-w-5xl mx-auto aspect-video bg-black rounded-xl flex items-center justify-center flex-col gap-4">
                <div className="text-red-400">{error}</div>
                <button
                    onClick={() => window.location.reload()}
                    className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700"
                >
                    Retry
                </button>
            </div>
        );
    }

    return (
        <div className="w-full max-w-5xl mx-auto">
            <div
                ref={containerRef}
                className="relative aspect-video bg-black rounded-xl overflow-hidden shadow-2xl group"
                onMouseMove={resetControlsTimeout}
                onMouseLeave={() => {
                    if (isPlaying) {
                        setShowControls(false);
                        setShowSpeedMenu(false);
                    }
                }}
            >
                {/* Loading overlay */}
                {(isLoading || isBuffering) && (
                    <div className="absolute inset-0 flex items-center justify-center bg-black/50 z-20">
                        <div className="flex flex-col items-center gap-3">
                            {/* Spinner */}
                            <div className="w-12 h-12 border-4 border-white/30 border-t-blue-500 rounded-full animate-spin" />
                            <div className="text-white text-sm">
                                {isLoading ? (isTranscoding ? 'Starting transcoding...' : 'Loading video...') : 'Buffering...'}
                            </div>
                        </div>
                    </div>
                )}

                {/* Video element */}
                <video
                    ref={videoRef}
                    className="w-full h-full cursor-pointer"
                    playsInline
                    poster={item.posterPath || undefined}
                    onClick={togglePlay}
                    onDoubleClick={toggleFullscreen}
                    crossOrigin="anonymous"
                >
                    {/* Subtitles are added programmatically via useEffect */}
                </video>

                {/* Center play button (when paused) */}
                {!isPlaying && !isLoading && !isBuffering && (
                    <div
                        className="absolute inset-0 flex items-center justify-center cursor-pointer"
                        onClick={togglePlay}
                    >
                        <div className="w-20 h-20 bg-white/20 backdrop-blur-sm rounded-full flex items-center justify-center hover:bg-white/30 transition-colors">
                            <svg className="w-10 h-10 text-white ml-1" fill="currentColor" viewBox="0 0 24 24">
                                <path d="M8 5v14l11-7z" />
                            </svg>
                        </div>
                    </div>
                )}

                {/* Custom Controls Bar */}
                <div
                    className={`absolute bottom-0 left-0 right-0 bg-gradient-to-t from-black/90 via-black/60 to-transparent pt-12 pb-3 px-4 transition-opacity duration-300 ${showControls || !isPlaying ? 'opacity-100' : 'opacity-0 pointer-events-none'
                        }`}
                >
                    {/* Progress Bar */}
                    <div
                        ref={progressRef}
                        className="relative w-full h-1.5 bg-white/20 rounded-full cursor-pointer mb-3 group/progress hover:h-2.5 transition-all overflow-hidden"
                        onClick={handleSeek}
                    >
                        <div
                            className="absolute top-0 left-0 h-full bg-white/50 rounded-full pointer-events-none"
                            style={{ width: `${Math.min(bufferedPercent, 100)}%` }}
                        />
                        <div
                            className="absolute top-0 left-0 h-full bg-blue-500 rounded-full pointer-events-none"
                            style={{ width: `${Math.min(progressPercent, 100)}%` }}
                        />
                        <div
                            className="absolute top-1/2 -translate-y-1/2 w-3.5 h-3.5 bg-blue-500 rounded-full opacity-0 group-hover/progress:opacity-100 transition-opacity shadow-lg pointer-events-none"
                            style={{ left: `calc(${Math.min(progressPercent, 100)}% - 7px)` }}
                        />
                    </div>

                    {/* Controls row */}
                    <div className="flex items-center gap-3">
                        {/* Play/Pause */}
                        <button onClick={togglePlay} className="text-white hover:text-blue-400 transition-colors" title={isPlaying ? 'Pause (K)' : 'Play (K)'}>
                            {isPlaying ? (
                                <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z" />
                                </svg>
                            ) : (
                                <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M8 5v14l11-7z" />
                                </svg>
                            )}
                        </button>

                        {/* Skip backward */}
                        <button onClick={() => skip(-10)} className="text-white/70 hover:text-white transition-colors" title="Back 10s (J)">
                            <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                <path d="M11 18V6l-8.5 6 8.5 6zm.5-6l8.5 6V6l-8.5 6z" />
                            </svg>
                        </button>

                        {/* Skip forward */}
                        <button onClick={() => skip(10)} className="text-white/70 hover:text-white transition-colors" title="Forward 10s (L)">
                            <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                <path d="M4 18l8.5-6L4 6v12zm9-12v12l8.5-6L13 6z" />
                            </svg>
                        </button>

                        {/* Volume */}
                        <div className="flex items-center gap-1 group/volume">
                            <button onClick={toggleMute} className="text-white/70 hover:text-white transition-colors" title="Mute (M)">
                                {isMuted || volume === 0 ? (
                                    <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z" />
                                    </svg>
                                ) : (
                                    <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z" />
                                    </svg>
                                )}
                            </button>
                            <div className="overflow-hidden w-0 group-hover/volume:w-20 transition-all duration-200">
                                <input
                                    type="range"
                                    min="0"
                                    max="1"
                                    step="0.05"
                                    value={isMuted ? 0 : volume}
                                    onChange={handleVolumeChange}
                                    className="w-20 accent-blue-500 cursor-pointer"
                                />
                            </div>
                        </div>

                        {/* Time display */}
                        <div className="text-white text-sm font-mono ml-2">
                            {formatTime(displayedTime)} / {formatTime(displayDuration)}
                        </div>

                        {/* Spacer */}
                        <div className="flex-1" />

                        {/* Playback Speed */}
                        <div className="relative">
                            <button
                                onClick={() => setShowSpeedMenu(!showSpeedMenu)}
                                className="text-white/70 hover:text-white transition-colors text-sm font-medium px-2"
                                title="Playback Speed"
                            >
                                {playbackSpeed}x
                            </button>
                            {showSpeedMenu && (
                                <div
                                    className="absolute bottom-full right-0 mb-2 bg-black/90 rounded-lg py-1 min-w-[80px] shadow-xl"
                                    onMouseEnter={() => handleMenuInteraction(true)}
                                    onMouseLeave={() => handleMenuInteraction(false)}
                                >
                                    {PLAYBACK_SPEEDS.map(speed => (
                                        <button
                                            key={speed}
                                            onClick={() => changePlaybackSpeed(speed)}
                                            className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${playbackSpeed === speed ? 'text-blue-400' : 'text-white'
                                                }`}
                                        >
                                            {speed}x
                                        </button>
                                    ))}
                                </div>
                            )}
                        </div>

                        {/* Subtitle/Audio Track Selection */}
                        {(subtitleTracks.length > 0 || audioTracks.length > 0) && (
                            <div className="relative">
                                <button
                                    onClick={() => setShowTrackMenu(!showTrackMenu)}
                                    className="text-white/70 hover:text-white transition-colors"
                                    title="Subtitle & Audio Tracks"
                                >
                                    <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M20 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V6h16v12zM6 10h2v2H6zm0 4h8v2H6zm10 0h2v2h-2zm-6-4h8v2h-8z" />
                                    </svg>
                                </button>
                                {showTrackMenu && (
                                    <div
                                        className="absolute bottom-full right-0 mb-2 bg-black/95 rounded-lg py-2 min-w-[200px] shadow-xl max-h-80 overflow-y-auto"
                                        onMouseEnter={() => handleMenuInteraction(true)}
                                        onMouseLeave={() => handleMenuInteraction(false)}
                                    >
                                        {/* Subtitle Tracks */}
                                        {subtitleTracks.length > 0 && (
                                            <>
                                                <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold">Subtitles</div>
                                                <button
                                                    onClick={() => {
                                                        setSelectedSubtitleTrack(null);
                                                        setShowTrackMenu(false);
                                                    }}
                                                    className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedSubtitleTrack === null ? 'text-blue-400' : 'text-white'
                                                        }`}
                                                >
                                                    Off
                                                </button>
                                                {subtitleTracks.map(track => (
                                                    <button
                                                        key={track.index}
                                                        onClick={() => {
                                                            setSelectedSubtitleTrack(track.index);
                                                            setShowTrackMenu(false);
                                                        }}
                                                        className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedSubtitleTrack === track.index ? 'text-blue-400' : 'text-white'
                                                            }`}
                                                    >
                                                        {track.language ? track.language.toUpperCase() : 'Unknown'}
                                                        {track.title && <span className="text-white/50 ml-1">({track.title})</span>}
                                                    </button>
                                                ))}
                                            </>
                                        )}

                                        {/* Audio Tracks */}
                                        {audioTracks.length > 1 && (
                                            <>
                                                <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold mt-2 border-t border-white/10 pt-2">Audio</div>
                                                {audioTracks.map(track => (
                                                    <button
                                                        key={track.index}
                                                        onClick={() => {
                                                            setSelectedAudioTrack(track.index);
                                                            setShowTrackMenu(false);
                                                            // Note: Switching audio tracks mid-playback requires re-transcoding with the new audio track
                                                            // For now, just store the selection - full implementation would restart transcode with -map 0:v -map 0:[audioIndex]
                                                        }}
                                                        className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedAudioTrack === track.index ? 'text-blue-400' : 'text-white'
                                                            }`}
                                                    >
                                                        {track.language ? track.language.toUpperCase() : 'Track ' + (track.index + 1)}
                                                        {track.title && <span className="text-white/50 ml-1">({track.title})</span>}
                                                    </button>
                                                ))}
                                            </>
                                        )}
                                    </div>
                                )}
                            </div>
                        )}

                        {/* Picture-in-Picture */}
                        {document.pictureInPictureEnabled && (
                            <button
                                onClick={togglePiP}
                                className={`transition-colors ${isPiP ? 'text-blue-400' : 'text-white/70 hover:text-white'}`}
                                title="Picture-in-Picture (P)"
                            >
                                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M19 7h-8v6h8V7zm2-4H3c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H3V5h18v14z" />
                                </svg>
                            </button>
                        )}

                        {/* Fullscreen */}
                        <button onClick={toggleFullscreen} className="text-white/70 hover:text-white transition-colors" title="Fullscreen (F)">
                            {isFullscreen ? (
                                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z" />
                                </svg>
                            ) : (
                                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z" />
                                </svg>
                            )}
                        </button>
                    </div>
                </div>
            </div>

            {/* Keyboard shortcuts hint */}
            <div className="text-xs text-white/40 text-center mt-2 space-x-4">
                <span>Space: Play/Pause</span>
                <span>←/→: Seek ±10s</span>
                <span>↑/↓: Volume</span>
                <span>M: Mute</span>
                <span>F: Fullscreen</span>
            </div>
        </div>
    );
}
