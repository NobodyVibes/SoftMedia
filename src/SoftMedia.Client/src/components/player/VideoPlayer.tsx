import { useEffect, useRef, useState, useCallback } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import Hls from 'hls.js';
import { type MediaItem } from '../../types';
import { useTrackSelection } from '../../hooks/useTrackSelection';
import { useAuthStore } from '../../store/authStore';
import { NextEpisodeOverlay, type NextEpisodeInfo } from './NextEpisodeOverlay';
import { PlayerDebugPanel } from './PlayerDebugPanel';
import { ProgressBar } from './ProgressBar';
import { useMediaCapabilities, createCapabilitiesWithOverrides } from '../../hooks/useMediaCapabilities';
import { useLocalPreferences } from '../../hooks/useLocalPreferences';



interface VideoPlayerProps {
    item: MediaItem;
    src: string;
}



interface StreamPlan {
    method: 'DirectPlay' | 'Remux' | 'Transcode';
    url: string;
    displayProfile: string;
    videoCodec: string;
    audioCodec: string;
    container: string;
    isHdr: boolean;
    sourceIsHdr: boolean;
    resolution: string;
    audioChannels: number;
    reason: string;
}


const PLAYBACK_SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];

/**
 * VideoPlayer component with custom controls, keyboard shortcuts, playback speed, and PiP support.
 * Uses native HTML5 video with hls.js for HLS support, but with custom UI controls.
 */
export default function VideoPlayer({ item, src: initialSrc }: VideoPlayerProps) {
    const videoRef = useRef<HTMLVideoElement>(null);
    const hlsRef = useRef<Hls | null>(null);
    const seekTargetRef = useRef<number | null>(null); // Track where user drags to
    const containerRef = useRef<HTMLDivElement>(null);
    const controlsTimeoutRef = useRef<NodeJS.Timeout | null>(null);
    const seekAfterLoadRef = useRef<number>(0); // Position to seek to after HLS reloads
    const progressSaveIntervalRef = useRef<NodeJS.Timeout | null>(null);
    const lastSavedPositionRef = useRef<number>(0); // Track last saved position to avoid redundant saves
    const effectivePlaybackPositionRef = useRef<number>(0); // Track actual playback position for subtitle switching
    const pendingSeekPositionRef = useRef<number | null>(null); // Capture position at moment of subtitle/quality change
    const isInsideMenuRef = useRef<boolean>(false); // Track if mouse is inside a menu (for auto-hide logic)
    const lastLoadedItemIdRef = useRef<string>(''); // Track last loaded item ID to detect fresh episode loads

    // Resume position from server (loaded on mount)
    const [resumePosition, setResumePosition] = useState<number>(0);
    const [hasLoadedProgress, setHasLoadedProgress] = useState(false);
    const [isSubtitleChange, setIsSubtitleChange] = useState(false); // Track if subtitle just changed

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
    const [showQualityMenu, setShowQualityMenu] = useState(false);
    const [selectedQuality, setSelectedQuality] = useState<string>('auto');
    const [isPiP, setIsPiP] = useState(false);


    // Track selection state

    const [showTrackMenu, setShowTrackMenu] = useState(false);

    // Duration from FFprobe (when not in metadata)
    const [probedDuration, setProbedDuration] = useState<number>(0);

    // Frame preview for scrubber
    const [framePreviewUrl, setFramePreviewUrl] = useState<string | null>(null);
    const wasPlayingBeforeDragRef = useRef(false);
    const frameDebounceRef = useRef<NodeJS.Timeout | null>(null);

    // Next Episode Overlay state
    const [showNextEpisodeOverlay, setShowNextEpisodeOverlay] = useState(false);
    const [nextEpisodeInfo, setNextEpisodeInfo] = useState<NextEpisodeInfo | null>(null);
    // Track if we've reached thresholds (reset when seeking backward past them)
    const lastThresholdTimeRef = useRef<number>(0); // Last time we triggered the overlay
    const hasShownOverlayRef = useRef(false); // Whether overlay was shown for this threshold crossing

    // Adjacent episode navigation state (for prev/next buttons)
    const [previousEpisodeId, setPreviousEpisodeId] = useState<string | null>(null);
    const [nextEpisodeId, setNextEpisodeId] = useState<string | null>(null);

    // Debug panel state
    const [showDebugPanel, setShowDebugPanel] = useState(false);

    // HDR state tracking for toasts
    const [playerToast, setPlayerToast] = useState<{ message: string; type: 'info' | 'success' } | null>(null);
    const lastToastStatusRef = useRef<'hdr' | 'tonemapped' | null>(null);

    // Unique Stream ID to isolate transcode sessions per playback instance
    const [streamId] = useState(() => Math.random().toString(36).substring(2, 11));

    const handleDismissToast = useCallback(() => {
        setPlayerToast(null);
    }, []);

    const token = useAuthStore((state) => state.token);
    const navigate = useNavigate();
    const location = useLocation();

    // Check for ?start=0 query param to force starting from beginning
    const forceStartFromBeginning = new URLSearchParams(location.search).get('start') === '0';

    // Detect browser media capabilities for stream negotiation
    const { capabilities: mediaCapabilities, isDetecting: isDetectingCapabilities } = useMediaCapabilities();

    // Get user's local preferences (including default streaming quality)
    const { preferences: localPrefs } = useLocalPreferences();

    // Track selection state (managed by hook)
    const {
        audioTracks,
        subtitleTracks,
        selectedAudioTrack,
        selectedSubtitleTrack,
        setSelectedAudioTrack,
        setSelectedSubtitleTrack
    } = useTrackSelection({
        item,
        token,
        localPrefs
    });

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

    // Load saved playback position on mount or when item changes
    useEffect(() => {
        if (!token || !item.id) return;

        // COMPREHENSIVE RESET for new item - this is critical when navigating between episodes
        // Reset all playback-related state and refs to prevent old values bleeding into new video
        setResumePosition(0);
        setHasLoadedProgress(false);
        setCurrentTime(0);
        setSeekOffset(0);
        effectivePlaybackPositionRef.current = 0;
        lastSavedPositionRef.current = 0;
        hasShownOverlayRef.current = false;
        lastThresholdTimeRef.current = 0;
        setShowNextEpisodeOverlay(false);
        setNextEpisodeInfo(null);
        console.log(`[VideoPlayer] Reset all state for new item: ${item.id}, forceStartFromBeginning: ${forceStartFromBeginning}`);

        // If ?start=0 query param is present, skip fetching resume position and start from beginning
        if (forceStartFromBeginning) {
            console.log(`[VideoPlayer] Force start from beginning - skipping resume position fetch`);
            setHasLoadedProgress(true);
            return;
        }

        const fetchProgress = async () => {
            try {
                const response = await fetch(`/api/v1/interaction/${item.id}/progress`, {
                    headers: { Authorization: `Bearer ${token}` }
                });
                if (response.ok) {
                    const data = await response.json();
                    if (data.position > 0) {
                        // Validate: position must be less than duration (with 5s buffer)
                        // If position exceeds duration, it's corrupted - reset to beginning
                        // Duration may be a number (seconds) or a formatted string like "24m 46s" or "1h 30m 15s"
                        let durationSeconds = 0;
                        if (typeof item.duration === 'number') {
                            durationSeconds = item.duration;
                        } else if (typeof item.duration === 'string') {
                            // Parse formatted duration like "24m 46s" or "1h 30m 15s"
                            const hours = item.duration.match(/(\d+)h/);
                            const minutes = item.duration.match(/(\d+)m/);
                            const seconds = item.duration.match(/(\d+)s/);
                            durationSeconds =
                                (hours ? parseInt(hours[1]) * 3600 : 0) +
                                (minutes ? parseInt(minutes[1]) * 60 : 0) +
                                (seconds ? parseInt(seconds[1]) : 0);
                        }
                        const maxValidPosition = durationSeconds > 0 ? durationSeconds - 5 : Infinity;
                        console.log(`Resume validation for ${item.id}: position=${data.position}, duration=${item.duration}(parsed=${durationSeconds}s), maxValid=${maxValidPosition}`);
                        if (data.position < maxValidPosition) {
                            console.log(`Resuming from saved position: ${data.position}s`);
                            setResumePosition(data.position);
                        } else {
                            console.log(`Saved position ${data.position}s exceeds duration ${durationSeconds}s - starting from beginning`);
                            // Don't set resume position - will start from beginning
                        }
                    }
                }
            } catch {
                // Silently fail - will start from beginning
            } finally {
                // Mark progress as loaded so playback strategy can proceed
                setHasLoadedProgress(true);
            }
        };
        fetchProgress();
    }, [token, item.id, forceStartFromBeginning]);

    // Save playback position periodically (every 10 seconds) and on unmount
    useEffect(() => {
        if (!token || !item.id) return;

        const saveProgress = async () => {
            const effectivePosition = currentTime + seekOffset;
            // Only save if position changed significantly (> 5 seconds difference)
            if (Math.abs(effectivePosition - lastSavedPositionRef.current) > 5 && effectivePosition > 0) {
                try {
                    await fetch(`/api/v1/interaction/${item.id}/progress`, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            Authorization: `Bearer ${token}`
                        },
                        body: JSON.stringify({ position: effectivePosition })
                    });
                    lastSavedPositionRef.current = effectivePosition;
                } catch {
                    // Silently fail - position will be saved next interval
                }
            }
        };

        // Save every 10 seconds while playing
        progressSaveIntervalRef.current = setInterval(() => {
            if (isPlaying) {
                saveProgress();
            }
        }, 10000);

        return () => {
            // Save on unmount
            saveProgress();
            if (progressSaveIntervalRef.current) {
                clearInterval(progressSaveIntervalRef.current);
            }
        };
    }, [token, item.id, currentTime, seekOffset, isPlaying]);

    // Fetch adjacent episode IDs for navigation buttons (TV shows only)
    useEffect(() => {
        if (!item.seriesId || !token) {
            setPreviousEpisodeId(null);
            setNextEpisodeId(null);
            return;
        }

        const fetchAdjacentEpisodes = async () => {
            try {
                // Fetch previous episode
                const prevResponse = await fetch(`/api/v1/episode/${item.id}/previous`, {
                    headers: { Authorization: `Bearer ${token}` }
                });
                if (prevResponse.ok) {
                    const prevData = await prevResponse.json();
                    // Check if episodeId is not empty GUID
                    if (prevData.episodeId && prevData.episodeId !== '00000000-0000-0000-0000-000000000000') {
                        setPreviousEpisodeId(prevData.episodeId);
                    } else {
                        setPreviousEpisodeId(null);
                    }
                }

                // Fetch next episode
                const nextResponse = await fetch(`/api/v1/episode/${item.id}/next`, {
                    headers: { Authorization: `Bearer ${token}` }
                });
                if (nextResponse.ok) {
                    const nextData = await nextResponse.json();
                    // Check if episodeId is not empty GUID
                    if (nextData.episodeId && nextData.episodeId !== '00000000-0000-0000-0000-000000000000') {
                        setNextEpisodeId(nextData.episodeId);
                    } else {
                        setNextEpisodeId(null);
                    }
                }
            } catch (error) {
                console.error('[EpisodeNav] Error fetching adjacent episodes:', error);
            }
        };

        fetchAdjacentEpisodes();
    }, [item.id, item.seriesId, token]);

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
        // Don't set auto-hide timeout if mouse is inside a menu
        if (isPlaying && !isInsideMenuRef.current) {
            controlsTimeoutRef.current = setTimeout(() => {
                setShowControls(false);
                setShowSpeedMenu(false);
                setShowTrackMenu(false);
            }, 3000);
        }
    }, [isPlaying]);

    // Prevent auto-hide when interacting with menus
    const handleMenuInteraction = (isEntering: boolean) => {
        isInsideMenuRef.current = isEntering;
        if (isEntering) {
            if (controlsTimeoutRef.current) clearTimeout(controlsTimeoutRef.current);
        } else {
            resetControlsTimeout();
        }
    };

    // Helper to get storage key for "Last Used" track
    // MOVED usage to hook, but we still use LAST USED logic? 
    // Wait, the hook handles saving layout.
    // So saveLastUsedTrack and getLastUsedKey can be removed from here.

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
                    setShowDebugPanel(false);
                    break;
                case 'd':
                    // Toggle debug panel (don't prevent default for 'd' as it might be used elsewhere)
                    setShowDebugPanel(prev => !prev);
                    break;
            }
            resetControlsTimeout();
        };

        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [resetControlsTimeout]);

    // Determine playback strategy based on container/codec and detected capabilities
    // Wait for progress, subtitle preference, AND capability detection to be loaded before starting transcode
    // Determine playback strategy based on backend plan
    // Wait for progress, subtitle preference, AND capability detection to be loaded before starting
    useEffect(() => {
        if (!token || !hasLoadedProgress || isDetectingCapabilities) return;

        let isMounted = true;

        const fetchStreamPlan = async () => {
            // Use priority: media player selection > user's default quality > "auto"
            // This ensures user's "Default Quality" from My Account is respected
            const effectiveQuality = selectedQuality !== 'auto'
                ? selectedQuality
                : (localPrefs.defaultStreamingQuality && localPrefs.defaultStreamingQuality !== 'auto'
                    ? localPrefs.defaultStreamingQuality
                    : 'auto');

            // Calculate effective max bitrate
            const userMaxBitrate = parseInt(localPrefs.maxBitrate, 10) || 0;
            const isDataSaver = localPrefs.dataSaverMode === 'true';

            // Data Saver: max 2 Mbps, max 720p
            let effectiveMaxBitrate = userMaxBitrate;
            let effectiveMaxResolution = 0; // 0 = original

            if (isDataSaver) {
                const DATA_SAVER_BITRATE_LIMIT = 2000; // 2 Mbps
                if (effectiveMaxBitrate === 0 || effectiveMaxBitrate > DATA_SAVER_BITRATE_LIMIT) {
                    effectiveMaxBitrate = DATA_SAVER_BITRATE_LIMIT;
                }
                effectiveMaxResolution = 720;
            }

            // Create capabilities with quality override, bitrate override, and resolution override
            const capabilitiesToSend = createCapabilitiesWithOverrides(mediaCapabilities, {
                requestedQuality: effectiveQuality,
                maxBitrate: effectiveMaxBitrate,
                maxResolution: effectiveMaxResolution,
                subtitleTrackIndex: selectedSubtitleTrack,
                streamId: streamId
            });

            console.log('[StreamPlan] Quality - selected:', selectedQuality, 'default:', localPrefs.defaultStreamingQuality, 'effective:', effectiveQuality);
            console.log('[StreamPlan] Subtitle:', selectedSubtitleTrack);
            console.log('[StreamPlan] Data Saver:', isDataSaver, 'Bitrate:', effectiveMaxBitrate, 'Resolution:', effectiveMaxResolution);
            console.log('[StreamPlan] Requesting stream plan with capabilities:', capabilitiesToSend);

            try {
                const response = await fetch(`/api/transcode/${item.id}/plan`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': `Bearer ${token}`
                    },
                    body: JSON.stringify(capabilitiesToSend)
                });

                if (!response.ok || !isMounted) return;

                const plan: StreamPlan = await response.json();
                console.log('[StreamPlan] Received plan:', plan);

                // --- HDR TOAST LOGIC ---
                const isSourceHdr = plan.sourceIsHdr;
                const hasSubtitles = selectedSubtitleTrack !== null && selectedSubtitleTrack !== -1;

                if (isSourceHdr) {
                    if (!plan.isHdr && hasSubtitles) {
                        // Transition to Tonemapping
                        if (lastToastStatusRef.current !== 'tonemapped') {
                            setPlayerToast({
                                message: "HDR tone-mapping applied for subtitles. HDR will be disabled while subtitles are active.",
                                type: 'info'
                            });
                            lastToastStatusRef.current = 'tonemapped';
                        }
                    } else if (plan.isHdr && !hasSubtitles) {
                        // Transition to HDR Passthrough
                        if (lastToastStatusRef.current === 'tonemapped') {
                            setPlayerToast({
                                message: "Subtitles disabled. HDR passthrough re-enabled.",
                                type: 'success'
                            });
                        }
                        lastToastStatusRef.current = 'hdr';
                    } else if (plan.isHdr) {
                        lastToastStatusRef.current = 'hdr';
                    }
                }

                const needsTranscode = plan.method !== 'DirectPlay';
                const isFreshEpisodeLoad = lastLoadedItemIdRef.current !== item.id;

                // Determine starting position
                // IMPORTANT: For non-fresh loads, always prioritize the effective playback position
                // which tracks where the user actually is in the video (includes seekOffset)
                let startPosition = 0;

                // Check if we should force start from beginning (from ?start=0 query param)
                if (forceStartFromBeginning) {
                    startPosition = 0;
                    console.log(`Forced start from beginning via query param`);
                }
                // Priority 1: For non-fresh loads (subtitle change, quality change, etc)
                // Use pendingSeekPositionRef if set (captured at click time), otherwise use tracked position
                else if (!isFreshEpisodeLoad) {
                    if (pendingSeekPositionRef.current !== null && pendingSeekPositionRef.current > 0) {
                        // Use position captured at click time (most reliable)
                        startPosition = Math.floor(pendingSeekPositionRef.current);
                        console.log(`Using captured click position: ${startPosition}s`);
                        pendingSeekPositionRef.current = null; // Clear after use
                    } else if (effectivePlaybackPositionRef.current > 1) {
                        startPosition = Math.floor(effectivePlaybackPositionRef.current);
                        console.log(`Using tracked position: ${startPosition}s`);
                    } else if (seekOffset > 0 || (videoRef.current?.currentTime || 0) > 1) {
                        // Priority 3: Final fallback for non-fresh loads
                        const currentPosition = videoRef.current?.currentTime || 0;
                        startPosition = Math.max(0, seekOffset > 0 ? (currentPosition + seekOffset) : currentPosition);
                        console.log(`Continuing from fallback position: ${startPosition}s`);
                    }

                    // Delete previous transcode session to start fresh
                    if (isSubtitleChange && startPosition > 0) {
                        setIsSubtitleChange(false);
                        fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, { method: 'DELETE' }).catch(() => { });
                    }
                }
                // Priority 4: Resume position for fresh loads
                else if (isFreshEpisodeLoad && resumePosition > 0) {
                    startPosition = resumePosition;
                    console.log(`Using saved resume position: ${resumePosition}s`);
                }


                // Set offset for time display
                // IMPORTANT: Reset currentTime to 0 FIRST to prevent flicker (displayedTime = currentTime + seekOffset)
                if (startPosition > 0) {
                    setCurrentTime(0); // Reset video time to prevent momentary incorrect display
                    seekAfterLoadRef.current = startPosition;
                    setSeekOffset(Math.floor(startPosition));
                } else {
                    seekAfterLoadRef.current = 0;
                    setSeekOffset(0);
                }

                // Build Final URL
                let finalUrl = plan.url;

                // Append params for Transcode/Remux (HLS)
                // Append params for Transcode/Remux (HLS)
                if (needsTranscode) {
                    console.log(`[StreamPlan] Transcode. Sub: ${selectedSubtitleTrack}, Audio: ${selectedAudioTrack}`);
                    if (selectedSubtitleTrack !== null) {
                        finalUrl += `&sub=${selectedSubtitleTrack}`;
                    }
                    if (selectedAudioTrack !== null && selectedAudioTrack >= 0) {
                        finalUrl += `&audio=${selectedAudioTrack}`;
                    }
                    if (localPrefs.burnSubtitles === 'always') {
                        finalUrl += `&burnSubtitles=true`;
                    }
                    if (startPosition > 0) {
                        finalUrl += `&seek=${Math.floor(startPosition)}`;
                    }
                    console.log(`[StreamPlan] Final URL: ${finalUrl}`);
                } else {
                    setSeekOffset(0); // Reset offset for direct play
                }

                if (isMounted) {
                    setSrc(finalUrl);
                    setIsTranscoding(needsTranscode);
                    lastLoadedItemIdRef.current = item.id;
                }
            } catch (err) {
                console.error("Error fetching stream plan", err);
                // Fallback to direct play if plan negotiation fails
                if (isMounted) {
                    const directUrl = `${initialSrc}${initialSrc.includes('?') ? '&' : '?'}token=${token}`;
                    setSrc(directUrl);
                    setIsTranscoding(false);
                }
            }
        };

        fetchStreamPlan();

        return () => { isMounted = false; };

    }, [item, token, selectedSubtitleTrack, selectedAudioTrack, resumePosition, hasLoadedProgress, isSubtitleChange, forceStartFromBeginning, isDetectingCapabilities, mediaCapabilities, selectedQuality]);



    // Track fetching logic moved to useTrackSelection hook




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
                    // Enable native subtitle track rendering for WebVTT sidecar subtitles
                    renderTextTracksNatively: true,

                    // Buffer management for live transcoding
                    // These settings prevent unnecessary pausing when transcode is slower than real-time
                    maxBufferLength: 60,          // Target 60s of buffer (enough for CPU transcode)
                    maxMaxBufferLength: 120,      // Hard limit 120s of buffer
                    maxBufferSize: 100 * 1024 * 1024,  // 100MB max buffer size
                    maxBufferHole: 0.5,           // Skip gaps smaller than 0.5s

                    // Start playback more aggressively
                    startFragPrefetch: true,      // Start prefetching next fragment early
                    testBandwidth: false,         // Don't test bandwidth (we control transcode quality)
                });

                hls.loadSource(src);
                hls.attachMedia(video);

                hls.on(Hls.Events.MANIFEST_PARSED, () => {
                    console.log('HLS manifest parsed, ready to play');
                    setIsLoading(false);

                    // Add WebVTT subtitle track directly if subtitles are selected
                    // This approach works better than HLS manifest subtitles for single VTT files
                    if (selectedSubtitleTrack !== null && token) {
                        const subtitleUrl = `/api/transcode/${item.id}/subtitles.vtt?token=${token}&sub=${selectedSubtitleTrack}&sid=${streamId}`;
                        console.log(`Adding subtitle track: ${subtitleUrl}`);

                        // Remove any existing track elements
                        const existingTracks = video.querySelectorAll('track');
                        existingTracks.forEach(t => t.remove());

                        // Create and add new track element
                        const trackElement = document.createElement('track');
                        trackElement.kind = 'subtitles';
                        trackElement.label = 'Subtitles';
                        trackElement.srclang = 'en';
                        trackElement.src = subtitleUrl;
                        trackElement.default = true;
                        video.appendChild(trackElement);

                        // Enable the track reliably
                        trackElement.onload = () => {
                            if (video.textTracks && video.textTracks.length > 0) {
                                for (let i = 0; i < video.textTracks.length; i++) {
                                    const track = video.textTracks[i];
                                    if (track.label === 'Subtitles' && (track.kind === 'subtitles' || track.kind === 'captions')) {
                                        track.mode = 'showing';
                                        console.log(`Enabled text track (onload): ${track.label}`);
                                        break;
                                    }
                                }
                            }
                        };

                        // Fallback timeout in case onload doesn't fire (cached)
                        setTimeout(() => {
                            if (video.textTracks) {
                                for (let i = 0; i < video.textTracks.length; i++) {
                                    const track = video.textTracks[i];
                                    if (track.label === 'Subtitles' && track.mode !== 'showing') {
                                        track.mode = 'showing';
                                        console.log(`Enabled text track (timeout): ${track.label}`);
                                    }
                                }
                            }
                        }, 500);
                    }

                    // Auto-play when ready
                    video.play().catch(() => { });

                    // Reset seek ref if we were switching subtitles
                    if (seekAfterLoadRef.current > 0) {
                        seekAfterLoadRef.current = 0;
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
            // Explicitly stop transcode when leaving the player
            if (isTranscoding && token && item.id) {
                fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, {
                    method: 'DELETE'
                }).catch(() => { /* Ignore cleanup errors */ });
            }
        };
    }, [src, isTranscoding, token, item.id, selectedSubtitleTrack]);

    // Fetch next episode info for "Play Next" overlay
    const fetchNextEpisode = useCallback(async () => {
        if (!token || !item.id) return;

        try {
            const response = await fetch(`/api/v1/episode/${item.id}/next`, {
                headers: { Authorization: `Bearer ${token}` }
            });

            if (response.ok) {
                const data: NextEpisodeInfo = await response.json();
                console.log('[PlayNext] Fetched next episode:', data);
                setNextEpisodeInfo(data);
                setShowNextEpisodeOverlay(true);
            } else if (response.status === 404) {
                // No next episode - show series complete overlay
                setNextEpisodeInfo({
                    episodeId: '',
                    seriesId: item.seriesId || '',
                    seasonNumber: 0,
                    episodeNumber: 0,
                    title: '',
                    resumePosition: 0,
                    isSeriesComplete: true
                });
                setShowNextEpisodeOverlay(true);
            }
        } catch (error) {
            console.error('[PlayNext] Failed to fetch next episode:', error);
        }
    }, [token, item.id, item.seriesId]);

    // Handle playing the next episode from resume position (default)
    const handlePlayNextResume = useCallback(async () => {
        if (!nextEpisodeInfo || !nextEpisodeInfo.episodeId) return;

        // Mark current episode as watched
        if (token && item.id) {
            try {
                await fetch(`/api/v1/interaction/${item.id}/watched`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`
                    },
                    body: JSON.stringify({ watched: true })
                });
            } catch {
                // Silently fail
            }

            // Clean up transcode session before navigating
            if (isTranscoding) {
                fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, {
                    method: 'DELETE'
                }).catch(() => { });
            }
        }

        // Navigate to next episode (will resume from saved position)
        setShowNextEpisodeOverlay(false);
        navigate(`/play/${nextEpisodeInfo.episodeId}`);
    }, [nextEpisodeInfo, token, item.id, isTranscoding, navigate]);

    // Handle playing the next episode from start
    const handlePlayNextFromStart = useCallback(async () => {
        if (!nextEpisodeInfo || !nextEpisodeInfo.episodeId) return;

        // Mark current episode as watched
        if (token && item.id) {
            try {
                await fetch(`/api/v1/interaction/${item.id}/watched`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`
                    },
                    body: JSON.stringify({ watched: true })
                });
            } catch {
                // Silently fail
            }

            // Reset the next episode's playback position to 0 and WAIT for it to complete
            try {
                await fetch(`/api/v1/interaction/${nextEpisodeInfo.episodeId}/progress`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        Authorization: `Bearer ${token}`
                    },
                    body: JSON.stringify({ position: 0 })
                });
                console.log(`[PlayNext] Reset position to 0 for next episode: ${nextEpisodeInfo.episodeId}`);
            } catch {
                // Silently fail
            }

            // Clean up transcode session before navigating
            if (isTranscoding) {
                fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, {
                    method: 'DELETE'
                }).catch(() => { });
            }
        }

        // Navigate to next episode with ?start=0 to force starting from beginning
        setShowNextEpisodeOverlay(false);
        navigate(`/play/${nextEpisodeInfo.episodeId}?start=0`);
    }, [nextEpisodeInfo, token, item.id, isTranscoding, navigate]);

    // Handle return to library
    const handleReturnToLibrary = useCallback(() => {
        setShowNextEpisodeOverlay(false);
        // Mark current episode as watched
        if (token && item.id) {
            fetch(`/api/v1/interaction/${item.id}/watched`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Authorization: `Bearer ${token}`
                },
                body: JSON.stringify({ watched: true })
            }).catch(() => { });

            // Clean up transcode session
            if (isTranscoding) {
                fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, {
                    method: 'DELETE'
                }).catch(() => { });
            }
        }
    }, [token, item.id, isTranscoding]);

    // Handle rating the current episode from the overlay
    const handleRateCurrentEpisode = useCallback(async (rating: number) => {
        if (!token || !item.id) return;

        try {
            await fetch(`/api/v1/interaction/${item.id}/rate`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Authorization: `Bearer ${token}`
                },
                body: JSON.stringify({ rating })
            });
            console.log(`[PlayNext] Rated current episode: ${rating}`);
        } catch {
            // Silently fail
        }
    }, [token, item.id]);

    // Handle continue watching (dismiss overlay and resume playback)
    const handleContinueWatching = useCallback(() => {
        console.log('[PlayNext] Continue watching - dismissing overlay');
        setShowNextEpisodeOverlay(false);
        // Don't reset hasShownOverlayRef - once dismissed, don't show again this session
    }, []);

    // Handle pause/unpause video from overlay
    const handlePauseVideo = useCallback((paused: boolean) => {
        const video = videoRef.current;
        if (!video) return;

        if (paused) {
            video.pause();
        } else {
            video.play().catch(() => { });
        }
    }, []);

    // Video event handlers
    useEffect(() => {
        const video = videoRef.current;
        if (!video) return;

        // handleTimeUpdate is defined below with enhanced end detection
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
            console.log('Video ended event fired');
            // Signal backend to clean up transcode session when video ends
            if (isTranscoding && token && item.id) {
                fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, {
                    method: 'DELETE'
                }).catch(() => { });
            }
        };

        // For HLS streams, the 'ended' event may not fire if playlist lacks #EXT-X-ENDLIST
        // So we also check if we've reached the end based on time comparison
        const handleTimeUpdate = () => {
            setCurrentTime(video.currentTime);

            // Track effective playback position for subtitle switching
            const effectiveTime = video.currentTime + seekOffset;
            effectivePlaybackPositionRef.current = effectiveTime;

            // Next Episode Detection for TV Episodes only (has seriesId)
            // Trigger when reaching creditsStart or 98% of duration
            // IMPORTANT: Skip if this is a fresh episode load (state may be stale)
            const isFreshLoad = lastLoadedItemIdRef.current !== item.id;
            if (item.seriesId && displayDuration > 0 && !isFreshLoad && video.currentTime > 5) {
                const threshold98 = displayDuration * 0.98;
                const creditsStart = item.creditsStart;

                // Determine the earliest threshold (credits or 95%)
                const firstThreshold = (creditsStart && creditsStart > 0)
                    ? Math.min(creditsStart, threshold98)
                    : threshold98;

                // Check if we've reached any threshold
                const reachedCredits = creditsStart && creditsStart > 0 && effectiveTime >= creditsStart;
                const reached95Percent = effectiveTime >= threshold98;
                const reachedAnyThreshold = reachedCredits || reached95Percent;

                // Reset if we've seeked backward past all thresholds
                if (effectiveTime < firstThreshold - 5) {
                    // User seeked backward - allow overlay to show again
                    if (hasShownOverlayRef.current && !showNextEpisodeOverlay) {
                        console.log('[PlayNext] User seeked backward, resetting overlay trigger');
                        hasShownOverlayRef.current = false;
                        lastThresholdTimeRef.current = 0;
                    }
                }

                // Show overlay if we've crossed a threshold and haven't already shown it this pass
                if (reachedAnyThreshold && !hasShownOverlayRef.current && !showNextEpisodeOverlay) {
                    console.log(`[PlayNext] Threshold reached: credits=${reachedCredits}, 95%=${reached95Percent}, time=${effectiveTime}`);
                    hasShownOverlayRef.current = true;
                    lastThresholdTimeRef.current = effectiveTime;

                    // Fetch next episode info
                    fetchNextEpisode();
                }
            }

            // Manual end detection for HLS: if effective time is within 1 second of known duration
            if (displayDuration > 0 && effectiveTime >= displayDuration - 1 && !video.paused) {
                // At end of video - check if HLS hasn't properly ended
                if (video.currentTime >= (video.duration || 0) - 0.5) {
                    console.log('Manual end detection: effective time reached duration');
                    video.pause();
                    handleEnded();
                }
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
    }, [src, isTranscoding, token, item.id, selectedSubtitleTrack, displayDuration, seekOffset]);

    // Fullscreen change handler
    useEffect(() => {
        const handleFullscreenChange = () => {
            setIsFullscreen(!!document.fullscreenElement);
        };
        document.addEventListener('fullscreenchange', handleFullscreenChange);
        return () => document.removeEventListener('fullscreenchange', handleFullscreenChange);
    }, []);

    // Clean up transcode when browser tab is closed or refreshed
    useEffect(() => {
        if (!isTranscoding || !token || !item.id) return;

        const handleBeforeUnload = () => {
            // Use sendBeacon for reliable delivery even during page unload
            // Using POST /stop endpoint since sendBeacon only sends POST
            const url = `/api/transcode/${item.id}/stop?all=true&token=${token}`;
            navigator.sendBeacon(url);
        };

        window.addEventListener('beforeunload', handleBeforeUnload);
        return () => window.removeEventListener('beforeunload', handleBeforeUnload);
    }, [isTranscoding, token, item.id]);

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
        // Simply adjust currentTime within the current stream
        // The displayed time is calculated as currentTime + seekOffset
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

    // Find the chapter that contains a given time (returns the most recent chapter before that time)


    // Get current chapter index based on playback position
    const getCurrentChapterIndex = useCallback((): number => {
        if (!item.chapters || item.chapters.length === 0) return -1;
        const time = currentTime + seekOffset;
        let idx = -1;
        for (let i = 0; i < item.chapters.length; i++) {
            if (item.chapters[i].startTime <= time) {
                idx = i;
            } else {
                break;
            }
        }
        return idx;
    }, [item.chapters, currentTime, seekOffset]);

    // Skip to previous or next chapter
    const skipToChapter = (direction: 'prev' | 'next') => {
        if (!item.chapters || item.chapters.length === 0) return;

        const currentIdx = getCurrentChapterIndex();
        const currentPlaybackTime = currentTime + seekOffset;

        if (direction === 'prev') {
            // If we're more than 3 seconds into current chapter, go to start of current chapter
            // Otherwise go to previous chapter
            if (currentIdx >= 0) {
                const currentChapterStart = item.chapters[currentIdx].startTime;
                if (currentPlaybackTime - currentChapterStart > 3) {
                    // Go to start of current chapter
                    handleSeekToTime(currentChapterStart);
                } else if (currentIdx > 0) {
                    // Go to previous chapter
                    handleSeekToTime(item.chapters[currentIdx - 1].startTime);
                } else {
                    // Already at first chapter, go to beginning
                    handleSeekToTime(0);
                }
            } else {
                handleSeekToTime(0);
            }
        } else {
            // Go to next chapter, or end of video if at last chapter
            if (currentIdx < item.chapters.length - 1) {
                handleSeekToTime(item.chapters[currentIdx + 1].startTime);
            } else {
                // At last chapter, go to end
                handleSeekToTime(displayDuration);
            }
        }
    };

    // Navigate to previous or next episode (for TV shows)
    const navigateEpisode = async (direction: 'prev' | 'next') => {
        const targetId = direction === 'prev' ? previousEpisodeId : nextEpisodeId;
        if (!targetId) {
            console.log(`[EpisodeNav] No ${direction} episode available`);
            return;
        }

        console.log(`[EpisodeNav] Navigating to ${direction} episode: ${targetId}`);

        // Clean up transcode session before navigating
        if (isTranscoding && token && item.id) {
            try {
                await fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, { method: 'DELETE' });
            } catch { /* Ignore cleanup errors */ }
        }

        navigate(`/play/${targetId}`);
    };

    // Helper that performs the actual seek (Restored)
    const handleSeekToTime = (seekTime: number) => {
        if (!videoRef.current || displayDuration <= 0) return;

        // For transcoding: check if seeking beyond the current video duration (transcoded portion)
        const currentTranscodedDuration = videoRef.current.duration || 0;
        const effectiveCurrentTime = currentTime + seekOffset;

        if (isTranscoding && token && seekTime > effectiveCurrentTime + currentTranscodedDuration + 5) {
            // Seeking beyond transcoded portion - restart transcode at this position
            console.log(`Seeking to ${seekTime}s - beyond transcoded range, restarting transcode`);

            seekAfterLoadRef.current = seekTime;
            setSeekOffset(Math.floor(seekTime));

            fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, { method: 'DELETE' })
                .catch(() => { });

            let hlsUrl = `/api/transcode/${item.id}/master.m3u8?token=${token}&seek=${Math.floor(seekTime)}&sid=${streamId}`;
            if (selectedSubtitleTrack !== null) {
                hlsUrl += `&sub=${selectedSubtitleTrack}`;
            }
            if (selectedAudioTrack !== null && selectedAudioTrack >= 0) {
                hlsUrl += `&audio=${selectedAudioTrack}`;
            }
            setSrc(hlsUrl);
        } else {
            const targetInStream = seekTime - seekOffset;
            if (targetInStream >= 0 && targetInStream <= currentTranscodedDuration) {
                videoRef.current.currentTime = targetInStream;
            } else if (isTranscoding && token && targetInStream < 0) {
                console.log(`Seeking to ${seekTime}s - before current offset, restarting transcode`);
                seekAfterLoadRef.current = seekTime;
                setSeekOffset(Math.floor(seekTime));

                fetch(`/api/transcode/${item.id}?sid=${streamId}&token=${token}`, { method: 'DELETE' })
                    .catch(() => { });

                let hlsUrl = `/api/transcode/${item.id}/master.m3u8?token=${token}&seek=${Math.floor(seekTime)}&sid=${streamId}`;
                if (selectedSubtitleTrack !== null) {
                    hlsUrl += `&sub=${selectedSubtitleTrack}`;
                }
                if (selectedAudioTrack !== null && selectedAudioTrack >= 0) {
                    hlsUrl += `&audio=${selectedAudioTrack}`;
                }
                setSrc(hlsUrl);
            } else {
                videoRef.current.currentTime = Math.min(seekTime, videoRef.current.duration || Infinity);
            }
        }
    };

    // Progress bar mouse handlers for drag and hover


    // Progress bar percentages
    // Calculate displayed time including seek offset (for when transcoding starts from non-zero position)
    const displayedTime = currentTime + seekOffset;


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
                    <div className="absolute inset-0 z-50 flex items-center justify-center bg-black/40 backdrop-blur-sm transition-opacity duration-300">
                        <div className="flex flex-col items-center gap-4">
                            <div className="w-12 h-12 border-4 border-blue-500/30 border-t-blue-500 rounded-full animate-spin" />
                            <div className="text-white/70 text-sm font-medium animate-pulse tracking-wide">
                                {isBuffering ? 'Buffering...' : (isTranscoding ? 'Starting transcoding...' : 'Starting playback...')}
                            </div>
                        </div>
                    </div>
                )}

                {/* Player Toast Notification */}
                {playerToast && (
                    <PlayerToast
                        key={playerToast.message}
                        message={playerToast.message}
                        type={playerToast.type}
                        onDismiss={handleDismissToast}
                    />
                )}

                {/* Next Episode Overlay (for TV Episodes only) */}
                {showNextEpisodeOverlay && nextEpisodeInfo && (
                    <NextEpisodeOverlay
                        currentEpisodeId={item.id}
                        currentEpisodeTitle={item.title}
                        nextEpisode={nextEpisodeInfo}
                        currentRating={item.userRating}
                        onPlayNextResume={handlePlayNextResume}
                        onPlayNextFromStart={handlePlayNextFromStart}
                        onContinueWatching={handleContinueWatching}
                        onReturnToLibrary={handleReturnToLibrary}
                        onRateCurrent={handleRateCurrentEpisode}
                        onPauseVideo={handlePauseVideo}
                        libraryId={item.libraryId}
                    />
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
                    <button
                        type="button"
                        aria-label="Play"
                        className="absolute inset-0 flex items-center justify-center cursor-pointer bg-transparent border-0 p-0 focus-visible:outline-none"
                        onClick={togglePlay}
                    >
                        <div className="w-20 h-20 bg-white/20 backdrop-blur-sm rounded-full flex items-center justify-center hover:bg-white/30 focus-visible:bg-white/30 focus-visible:ring-2 focus-visible:ring-white transition-colors">
                            <svg className="w-10 h-10 text-white ml-1" fill="currentColor" viewBox="0 0 24 24">
                                <path d="M8 5v14l11-7z" />
                            </svg>
                        </div>
                    </button>
                )}

                {/* Custom Controls Bar */}
                <div
                    className={`absolute bottom-0 left-0 right-0 bg-gradient-to-t from-black/90 via-black/60 to-transparent pt-12 pb-3 px-4 transition-opacity duration-300 ${showControls || !isPlaying ? 'opacity-100' : 'opacity-0 pointer-events-none'
                        }`}
                >
                    {/* Progress Bar */}
                    <ProgressBar
                        currentTime={displayedTime}
                        duration={displayDuration}
                        bufferedPercent={bufferedPercent}
                        chapters={item.chapters}
                        creditsStart={item.creditsStart}
                        framePreviewUrl={framePreviewUrl}
                        onSeek={(time) => {
                            seekTargetRef.current = time;
                            // Fetch frame preview while dragging
                            if (frameDebounceRef.current) clearTimeout(frameDebounceRef.current);
                            frameDebounceRef.current = setTimeout(() => {
                                if (!token) return;
                                const url = `/api/transcode/${item.id}/frame?time=${time.toFixed(1)}&token=${token}`;
                                setFramePreviewUrl(url);
                            }, 100);
                        }}
                        onSeekStart={() => {
                            wasPlayingBeforeDragRef.current = !videoRef.current?.paused;
                            videoRef.current?.pause();
                        }}
                        onSeekEnd={() => {
                            setFramePreviewUrl(null);
                            if (seekTargetRef.current !== null) {
                                handleSeekToTime(seekTargetRef.current);
                                seekTargetRef.current = null;
                            }

                            if (wasPlayingBeforeDragRef.current) {
                                videoRef.current?.play().catch(() => { });
                            }
                        }}
                    />

                    {/* Controls row - [Time] | [Navigation] | [Settings] */}
                    <div className="flex items-center justify-between gap-2">
                        {/* Left: Time display */}
                        <div className="flex items-center gap-2 min-w-[140px]">
                            <div className="text-white text-base font-mono">
                                {formatTime(displayedTime)} / {formatTime(displayDuration)}
                            </div>
                        </div>

                        {/* Center: Playback navigation controls */}
                        <div className="flex items-center gap-1">
                            {/* Previous Episode - Only show for TV episodes */}
                            {item.seriesId && (
                                <button
                                    onClick={() => navigateEpisode('prev')}
                                    disabled={!previousEpisodeId}
                                    className={`transition-colors p-1.5 ${previousEpisodeId ? 'text-white/70 hover:text-white' : 'text-white/20 cursor-not-allowed'}`}
                                    title={previousEpisodeId ? 'Previous Episode' : 'No previous episode'}
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M5 6h2v12H5zm4 6l7 5V7l-7 5zm7 0l7 5V7l-7 5z" />
                                    </svg>
                                </button>
                            )}

                            {/* Previous Chapter - ◀| icon */}
                            {item.chapters && item.chapters.length > 0 && (
                                <button
                                    onClick={() => skipToChapter('prev')}
                                    className="text-white/60 hover:text-white transition-colors p-1.5"
                                    title="Previous Chapter"
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M6 6h2v12H6zm12 0l-9 6 9 6V6z" />
                                    </svg>
                                </button>
                            )}

                            {/* Skip backward 10s */}
                            <button
                                onClick={() => skip(-10)}
                                className="text-white/70 hover:text-white transition-colors p-1.5"
                                title="Back 10s (J)"
                            >
                                <svg className="w-7 h-7" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z" />
                                    <text x="9" y="15" fontSize="6" fontWeight="bold">10</text>
                                </svg>
                            </button>

                            {/* Play/Pause - centered and larger */}
                            <button
                                onClick={togglePlay}
                                className="text-white hover:text-blue-400 transition-colors p-2 mx-1"
                                title={isPlaying ? 'Pause (K)' : 'Play (K)'}
                            >
                                {isPlaying ? (
                                    <svg className="w-10 h-10" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z" />
                                    </svg>
                                ) : (
                                    <svg className="w-10 h-10" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M8 5v14l11-7z" />
                                    </svg>
                                )}
                            </button>

                            {/* Skip forward 10s */}
                            <button
                                onClick={() => skip(10)}
                                className="text-white/70 hover:text-white transition-colors p-1.5"
                                title="Forward 10s (L)"
                            >
                                <svg className="w-7 h-7" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M12 5V1l5 5-5 5V7c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6h2c0 4.42-3.58 8-8 8s-8-3.58-8-8 3.58-8 8-8z" />
                                    <text x="9" y="15" fontSize="6" fontWeight="bold">10</text>
                                </svg>
                            </button>

                            {/* Next Chapter - |▶ icon */}
                            {item.chapters && item.chapters.length > 0 && (
                                <button
                                    onClick={() => skipToChapter('next')}
                                    className="text-white/60 hover:text-white transition-colors p-1.5"
                                    title="Next Chapter"
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M6 18l9-6-9-6v12zM16 6v12h2V6h-2z" />
                                    </svg>
                                </button>
                            )}

                            {/* Next Episode - Only show for TV episodes */}
                            {item.seriesId && (
                                <button
                                    onClick={() => navigateEpisode('next')}
                                    disabled={!nextEpisodeId}
                                    className={`transition-colors p-1.5 ${nextEpisodeId ? 'text-white/70 hover:text-white' : 'text-white/20 cursor-not-allowed'}`}
                                    title={nextEpisodeId ? 'Next Episode' : 'No next episode'}
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M4 6l6 6-6 6V6zm6 0l6 6-6 6V6zM17 6h2v12h-2z" />
                                    </svg>
                                </button>
                            )}
                        </div>

                        {/* Right: Settings controls */}
                        <div className="flex items-center gap-2">
                            {/* Volume */}
                            <div className="relative group/volume">
                                <button onClick={toggleMute} className="text-white/70 hover:text-white transition-colors" title="Mute (M)">
                                    {isMuted || volume === 0 ? (
                                        <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                            <path d="M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z" />
                                        </svg>
                                    ) : (
                                        <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                            <path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z" />
                                        </svg>
                                    )}
                                </button>
                                {/* Vertical volume slider popup */}
                                <div className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 opacity-0 scale-y-0 origin-bottom group-hover/volume:opacity-100 group-hover/volume:scale-y-100 pointer-events-none group-hover/volume:pointer-events-auto transition-all duration-200 ease-out">
                                    <div className="bg-black/90 rounded-lg px-3 py-4 flex flex-col items-center shadow-xl border border-white/10">
                                        <input
                                            type="range"
                                            min="0"
                                            max="1"
                                            step="0.05"
                                            value={isMuted ? 0 : volume}
                                            onChange={handleVolumeChange}
                                            className="h-24 accent-blue-500 cursor-pointer"
                                            style={{ writingMode: 'vertical-lr', transform: 'rotate(180deg)' }}
                                        />
                                        <span className="text-white/70 text-xs mt-2">{Math.round((isMuted ? 0 : volume) * 100)}%</span>
                                    </div>
                                </div>
                            </div>

                            {/* Subtitle/Audio Track Selection */}
                            {(subtitleTracks.length > 0 || audioTracks.length > 0) && (
                                <div className="relative">
                                    <button
                                        onClick={() => setShowTrackMenu(!showTrackMenu)}
                                        className="text-white/70 hover:text-white transition-colors"
                                        title="Subtitle & Audio Tracks"
                                    >
                                        <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                            <path d="M20 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 14H4V6h16v12zM6 10h2v2H6zm0 4h8v2H6zm10 0h2v2h-2zm-6-4h8v2h-8z" />
                                        </svg>
                                    </button>
                                    {showTrackMenu && (
                                        <div
                                            className="absolute bottom-full right-0 mb-2 bg-black/95 rounded-lg py-2 min-w-[200px] shadow-xl max-h-80 overflow-y-auto"
                                            onMouseEnter={() => handleMenuInteraction(true)}
                                            onMouseLeave={() => handleMenuInteraction(false)}
                                            onMouseDown={() => handleMenuInteraction(true)}
                                            onScroll={() => handleMenuInteraction(true)}
                                        >
                                            {/* Subtitle Tracks */}
                                            {subtitleTracks.length > 0 && (
                                                <>
                                                    <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold">Subtitles</div>
                                                    <button
                                                        onClick={() => {
                                                            // Capture position BEFORE state changes
                                                            const capturedPosition = effectivePlaybackPositionRef.current;
                                                            pendingSeekPositionRef.current = capturedPosition;
                                                            // Update display IMMEDIATELY to prevent flicker during async fetch
                                                            setCurrentTime(0);
                                                            setSeekOffset(Math.floor(capturedPosition));
                                                            setIsSubtitleChange(true);
                                                            setSelectedSubtitleTrack(-1);
                                                            setShowTrackMenu(false);

                                                        }}
                                                        className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedSubtitleTrack === -1 ? 'text-blue-400' : 'text-white'}`}
                                                    >
                                                        Off
                                                    </button>
                                                    {subtitleTracks.map(track => (
                                                        <button
                                                            key={track.index}
                                                            onClick={() => {
                                                                // Capture position BEFORE state changes
                                                                const capturedPosition = effectivePlaybackPositionRef.current;
                                                                pendingSeekPositionRef.current = capturedPosition;
                                                                // Update display IMMEDIATELY to prevent flicker during async fetch
                                                                setCurrentTime(0);
                                                                setSeekOffset(Math.floor(capturedPosition));
                                                                setIsSubtitleChange(true);
                                                                setSelectedSubtitleTrack(track.index);
                                                                setShowTrackMenu(false);

                                                            }}
                                                            className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedSubtitleTrack === track.index ? 'text-blue-400' : 'text-white'}`}
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
                                                            }}
                                                            className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedAudioTrack === track.index ? 'text-blue-400' : 'text-white'}`}
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
                                                className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${playbackSpeed === speed ? 'text-blue-400' : 'text-white'}`}
                                            >
                                                {speed}x
                                            </button>
                                        ))}
                                    </div>
                                )}
                            </div>

                            {/* Quality Selector */}
                            <div className="relative">
                                <button
                                    onClick={() => setShowQualityMenu(!showQualityMenu)}
                                    className="text-white/70 hover:text-white transition-colors"
                                    title="Video Quality"
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M19.14 12.94c.04-.31.06-.63.06-.94 0-.31-.02-.63-.06-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.44.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.04.31-.06.63-.06.94s.02.63.06.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z" />
                                    </svg>
                                </button>
                                {showQualityMenu && (
                                    <div
                                        className="absolute bottom-full right-0 mb-2 bg-black/95 rounded-lg py-2 min-w-[140px] shadow-xl"
                                        onMouseEnter={() => handleMenuInteraction(true)}
                                        onMouseLeave={() => handleMenuInteraction(false)}
                                    >
                                        <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold">Quality</div>
                                        {['auto', '720p', '1080p', '4k', 'original'].map(quality => (
                                            <button
                                                key={quality}
                                                onClick={() => {
                                                    setSelectedQuality(quality);
                                                    setShowQualityMenu(false);
                                                }}
                                                className={`w-full px-4 py-1.5 text-sm text-left hover:bg-white/10 transition-colors ${selectedQuality === quality ? 'text-blue-400' : 'text-white'}`}
                                            >
                                                {quality === 'auto' ? 'Auto' : quality === 'original' ? 'Original' : quality.toUpperCase()}
                                            </button>
                                        ))}
                                    </div>
                                )}
                            </div>

                            {/* Picture-in-Picture */}
                            {document.pictureInPictureEnabled && (

                                <button
                                    onClick={togglePiP}
                                    className={`transition-colors ${isPiP ? 'text-blue-400' : 'text-white/70 hover:text-white'}`}
                                    title="Picture-in-Picture (P)"
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M19 7h-8v6h8V7zm2-4H3c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H3V5h18v14z" />
                                    </svg>
                                </button>
                            )}

                            {/* Fullscreen */}
                            <button onClick={toggleFullscreen} className="text-white/70 hover:text-white transition-colors" title="Fullscreen (F)">
                                {isFullscreen ? (
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z" />
                                    </svg>
                                ) : (
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
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

            {/* Debug Panel Overlay */}
            {showDebugPanel && token && (
                <PlayerDebugPanel
                    mediaId={item.id}
                    token={token}
                    streamId={streamId}
                    subtitleTrack={selectedSubtitleTrack}
                    clientCapabilities={mediaCapabilities}
                    onClose={() => setShowDebugPanel(false)}
                />
            )}
        </div>
    );
}

/**
 * Premium in-player toast notification with circular progress timer and exit animations.
 */
function PlayerToast({ message, type, onDismiss }: { message: string, type: 'info' | 'success', onDismiss: () => void }) {
    const [isExiting, setIsExiting] = useState(false);

    useEffect(() => {
        // Auto-dismiss after 8 seconds
        const timer = setTimeout(() => {
            setIsExiting(true);
            setTimeout(onDismiss, 500); // Wait for fade-out transition
        }, 8000);

        return () => clearTimeout(timer);
    }, [onDismiss]);

    const handleManualDismiss = () => {
        setIsExiting(true);
        setTimeout(onDismiss, 500);
    };

    return (
        <div className={`absolute top-6 left-1/2 -translate-x-1/2 z-[100] transition-all duration-500 ease-in-out ${isExiting ? 'opacity-0 -translate-y-4 scale-95' : 'opacity-100 translate-y-0 scale-100'
            }`}>
            <div className="px-4 py-2.5 rounded-xl shadow-2xl backdrop-blur-xl border bg-blue-500/20 border-blue-500/40 text-blue-50 flex items-center gap-4 font-medium text-sm sm:text-base whitespace-nowrap">

                <div className="flex items-center gap-3">
                    <div className="w-6 h-6 rounded-full bg-blue-500/40 flex items-center justify-center">
                        <svg className="w-4 h-4 text-blue-200" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            {type === 'success' ? (
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                            ) : (
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            )}
                        </svg>
                    </div>
                    {message}
                </div>

                {/* Dismissal UI with circular progress */}
                <div className="h-6 w-px bg-white/10 mx-1" />

                <button
                    onClick={handleManualDismiss}
                    className="relative w-8 h-8 flex items-center justify-center hover:bg-white/10 rounded-full transition-colors group"
                    title="Dismiss"
                >
                    {/* Background track */}
                    <svg className="absolute w-8 h-8 -rotate-90">
                        <circle
                            cx="16"
                            cy="16"
                            r="10"
                            stroke="currentColor"
                            strokeWidth="2.5"
                            fill="transparent"
                            className="opacity-10"
                        />
                        {/* Progress circle */}
                        <circle
                            cx="16"
                            cy="16"
                            r="10"
                            stroke="currentColor"
                            strokeWidth="2.5"
                            fill="transparent"
                            strokeDasharray="62.83"
                            className="animate-toast-progress"
                        />
                    </svg>
                    {/* Exit X */}
                    <svg className="relative w-4 h-4 text-white/50 group-hover:text-white transition-colors" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                </button>
            </div>
        </div>
    );
}
