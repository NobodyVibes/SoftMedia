import { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import Hls from 'hls.js';
import api from '../../services/api';
import { type MediaItem } from '../../types';
import { useTrackSelection } from '../../hooks/useTrackSelection';
import { useAuthStore, getUrlToken, getAccessToken } from '../../store/authStore';
import { NextEpisodeOverlay, type NextEpisodeInfo } from './NextEpisodeOverlay';
import { MovieEndOverlay } from './MovieEndOverlay';
import { postPlayService, type PostPlayInfo } from '../../services/postPlayService';
import { PlayerDebugPanel } from './PlayerDebugPanel';
import { TranscodeExplanationModal } from './TranscodeExplanationModal';
import { ProgressBar } from './ProgressBar';
import { SkipSegmentPill } from './SkipSegmentPill';
import { useMediaCapabilities, createCapabilitiesWithOverrides, type ClientCapabilities } from '../../hooks/useMediaCapabilities';
import { useLocalPreferences } from '../../hooks/useLocalPreferences';
import { useTrickplay, type SpriteFrame } from '../../hooks/useTrickplay';
import { useCast } from '../../hooks/useCast';
import { useMediaSession } from '../../hooks/useMediaSession';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { computeCueShift, applyCueShift, clampUserOffset } from './subtitleSync';
import { buildCueCss } from './subtitleStyle';
import { describeCastReadiness } from '../../hooks/castReadiness';
import { CastDiagnostics } from './CastDiagnostics';



interface VideoPlayerProps {
    item: MediaItem;
    src: string;
}



interface StreamReasonCode {
    code: string;
    params: Record<string, string>;
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
    reasonCodes?: StreamReasonCode[];
}


const PLAYBACK_SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];

/**
 * Capabilities of the Chromecast Default Media Receiver, for building a cast stream plan.
 * The receiver is far more limited than a desktop browser: across all Cast generations it
 * reliably decodes only H.264 + AAC up to 1080p — NOT AV1/HEVC/VP9, MKV, or HDR. We must
 * request a plan tuned to *these* caps rather than reuse the desktop plan, which may target
 * AV1/HEVC at 4K+ that the receiver cannot play (it would connect but never start playback).
 */
const CHROMECAST_CAPABILITIES: ClientCapabilities = {
    videoCodecs: ['h264'],
    audioCodecs: ['aac'],
    maxAudioChannels: 2,
    supportsHdr: false,
    displaySupportsHdr: false,
    codecSupportsHdr: false,
    maxBitrate: 0,
    maxResolution: 1080,
    supportedSubtitleFormats: ['vtt'],
    supportedContainers: ['hls', 'mp4'],
};

/**
 * WS-6 T6.4: mutating transcode calls (DELETE / pause / resume / plan) must carry the
 * ACCESS token in an Authorization header — media tokens are GET/HEAD-only and full
 * access tokens are rejected in query strings. Read at CALL time so token rotation
 * never leaves a stale closure.
 */
function transcodeAuthHeaders(extra?: Record<string, string>): Record<string, string> {
    const t = getAccessToken();
    return { ...(t ? { Authorization: `Bearer ${t}` } : {}), ...extra };
}

/**
 * VideoPlayer component with custom controls, keyboard shortcuts, playback speed, and PiP support.
 * Uses native HTML5 video with hls.js for HLS support, but with custom UI controls.
 */
export default function VideoPlayer({ item, src: initialSrc }: VideoPlayerProps) {
    const videoRef = useRef<HTMLVideoElement>(null);
    // Guards the cast button against rapid re-clicks: `isCasting` only flips after the async
    // SESSION_STARTED event, so it can't protect the in-flight window on its own.
    const castInFlightRef = useRef(false);
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
    // Consolidated overflow menu holding the less-frequently-used controls
    // (speed, quality, PiP, stream explainer) so the right cluster stays compact
    // and the center play/skip controls remain visually centered.
    const [showMoreMenu, setShowMoreMenu] = useState(false);
    const [selectedQuality, setSelectedQuality] = useState<string>('auto');
    const [isPiP, setIsPiP] = useState(false);


    // Track selection state

    const [showTrackMenu, setShowTrackMenu] = useState(false);

    // Duration from FFprobe (when not in metadata)
    const [probedDuration, setProbedDuration] = useState<number>(0);

    // Frame preview for scrubber
    const [framePreviewUrl, setFramePreviewUrl] = useState<string | null>(null);
    const [spriteFrame, setSpriteFrame] = useState<SpriteFrame | null>(null);
    const wasPlayingBeforeDragRef = useRef(false);
    const frameDebounceRef = useRef<NodeJS.Timeout | null>(null);

    // Next Episode Overlay state
    const [showNextEpisodeOverlay, setShowNextEpisodeOverlay] = useState(false);
    const [nextEpisodeInfo, setNextEpisodeInfo] = useState<NextEpisodeInfo | null>(null);
    // Track if we've reached thresholds (reset when seeking backward past them)
    const lastThresholdTimeRef = useRef<number>(0); // Last time we triggered the overlay
    const hasShownOverlayRef = useRef(false); // Whether overlay was shown for this threshold crossing

    // Movie End Overlay state (the movie counterpart of the "Play Next" overlay: post-play
    // recommendations + auto-return to the movie's library)
    const [showMovieEndOverlay, setShowMovieEndOverlay] = useState(false);
    const [movieEndInfo, setMovieEndInfo] = useState<PostPlayInfo | null>(null);
    const hasMarkedWatchedRef = useRef(false); // watched-mark fires once per movie session

    // Adjacent episode navigation state (for prev/next buttons)
    const [previousEpisodeId, setPreviousEpisodeId] = useState<string | null>(null);
    const [nextEpisodeId, setNextEpisodeId] = useState<string | null>(null);

    // Debug panel state
    const [showDebugPanel, setShowDebugPanel] = useState(false);

    // "Why is this playing this way?" explainer (P2-WI-002)
    const [currentPlan, setCurrentPlan] = useState<StreamPlan | null>(null);
    const [showExplanation, setShowExplanation] = useState(false);

    // HDR state tracking for toasts
    const [playerToast, setPlayerToast] = useState<{ message: string; type: 'info' | 'success' | 'error' } | null>(null);
    const lastToastStatusRef = useRef<'hdr' | 'tonemapped' | null>(null);

    // Unique Stream ID to isolate transcode sessions per playback instance
    const [streamId] = useState(() => Math.random().toString(36).substring(2, 11));

    const handleDismissToast = useCallback(() => {
        setPlayerToast(null);
    }, []);

    // Audit H3: prefer the reduced-privilege media token in stream/transcode URLs; fall
    // back to the access token until it loads. Reactive so URLs update when it arrives.
    // Reduced-privilege media token for URL-EMBEDDED stream/transcode auth only (?token=…).
    // WS-6 T6.1: media-only, no access-token fallback — the server rejects full access
    // tokens in query strings, and App.tsx gates the authed UI until this exists.
    // MUTATING transcode calls (DELETE/pause/resume/plan) send the ACCESS token in an
    // Authorization header via transcodeAuthHeaders() — media tokens are GET/HEAD-only.
    // JSON API calls (interaction progress/watched/rate, episode nav) go through the shared
    // axios client instead: its request interceptor reads the CURRENT access token from the
    // store at call time (no stale closures) and its response interceptor transparently
    // refreshes + retries on 401 — which raw fetch with a captured token cannot do.
    const token = useAuthStore((state) => state.mediaToken);
    const navigate = useNavigate();
    const location = useLocation();
    const queryClient = useQueryClient();

    // Playback changes the home page's Continue Watching membership/order (progress advances,
    // items finish). Invalidate with refetchType 'all' so the inactive home query refetches
    // instead of serving stale membership. Called after explicit watched-marks and once on
    // unmount — NEVER inside the periodic progress-save loop (that would refetch every 10s).
    const invalidateContinueWatching = useCallback(() => {
        queryClient.invalidateQueries({ queryKey: ['continueWatching'], refetchType: 'all' });
    }, [queryClient]);

    useEffect(() => {
        return () => invalidateContinueWatching();
    }, [invalidateContinueWatching]);

    // Check for ?start=0 query param to force starting from beginning
    const forceStartFromBeginning = new URLSearchParams(location.search).get('start') === '0';

    // Detect browser media capabilities for stream negotiation
    const { capabilities: mediaCapabilities, isDetecting: isDetectingCapabilities } = useMediaCapabilities();

    // Get user's local preferences (including default streaming quality)
    const { preferences: localPrefs } = useLocalPreferences();

    // R-WI-018 — subtitle appearance (device preference) + per-session sync offset.
    const cueCss = useMemo(() => buildCueCss({
        fontSize: localPrefs.subtitleFontSize,
        color: localPrefs.subtitleColor,
        bgOpacity: localPrefs.subtitleBgOpacity,
        edgeStyle: localPrefs.subtitleEdgeStyle,
    }), [localPrefs.subtitleFontSize, localPrefs.subtitleColor, localPrefs.subtitleBgOpacity, localPrefs.subtitleEdgeStyle]);
    // The offset is per-playback-session by design (like VLC/Plex): drift is a
    // property of the FILE's subtitle track, not of the device.
    const [subtitleOffset, setSubtitleOffset] = useState(0);

    /// Re-apply the user's sync offset to every subtitle track. The server serves
    /// stream-aligned cues (it offsets the VTT on far-seek restarts), so the ONLY
    /// client-side shift is the user's nudge. Idempotent via per-cue anchors.
    const applySubtitleShift = useCallback((userOffset: number) => {
        const video = videoRef.current;
        if (!video?.textTracks) return;
        for (let i = 0; i < video.textTracks.length; i++) {
            const track = video.textTracks[i];
            if (track.label === 'Subtitles' && (track.kind === 'subtitles' || track.kind === 'captions')) {
                applyCueShift(track, computeCueShift(userOffset));
            }
        }
    }, []);
    // Latest-values wrapper for the track element's onload closure (which outlives
    // the render that created it).
    const applyCurrentSubtitleShiftRef = useRef<() => void>(() => { });
    applyCurrentSubtitleShiftRef.current = () => applySubtitleShift(subtitleOffset);

    // Re-apply when the user nudges sync while a track is loaded (fresh tracks
    // pick the offset up via their onload hook).
    useEffect(() => {
        applySubtitleShift(subtitleOffset);
    }, [subtitleOffset, applySubtitleShift]);


    // Pre-baked scrubber sprite sheets (P2-WI-001); falls back to on-demand frames.
    const { frameAt: trickplayFrameAt } = useTrickplay(item.id, token);

    // Chromecast sender (P3-WI-001). The Cast SDK loads from index.html and isn't
    // available on browsers that block it (Firefox, Safari) — button only renders
    // when isCastAvailable is true.
    const { isCastAvailable, isCasting, receiverName, castState, isSecureContext, castUnavailableReason, castNow, stopCasting } = useCast();
    const [castDiagOpen, setCastDiagOpen] = useState(false);
    // Show the cast button when a cast can plausibly start; otherwise (insecure context, or no
    // Cast device on the LAN) show a readiness affordance that explains exactly what's missing.
    const showCastButton = isCastAvailable && castState !== 'no-devices';
    const showCastHelp = (!isCastAvailable && castUnavailableReason === 'insecure-context')
        || (isCastAvailable && castState === 'no-devices');

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

    // Drift belongs to a specific subtitle FILE — switching tracks (EN → FR)
    // must not carry the previous track's nudge along.
    useEffect(() => {
        setSubtitleOffset(0);
    }, [selectedSubtitleTrack]);


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

    // Load saved playback position on mount or when item changes.
    // Auth is read at RUN time (not a reactive dep): this effect RESETS all playback state, so
    // re-running it on the silent ~15-minute token rotation would yank a playing video back to
    // its last saved position. It must fire only when the ITEM changes. The progress fetch
    // itself authenticates via the axios client, which always uses the current token.
    useEffect(() => {
        if (!getUrlToken() || !item.id) return;

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
        hasMarkedWatchedRef.current = false;
        setShowMovieEndOverlay(false);
        setMovieEndInfo(null);
        // R-WI-018: sync drift belongs to the previous item's subtitle file.
        setSubtitleOffset(0);
        console.log(`[VideoPlayer] Reset all state for new item: ${item.id}, forceStartFromBeginning: ${forceStartFromBeginning}`);

        // If ?start=0 query param is present, skip fetching resume position and start from beginning
        if (forceStartFromBeginning) {
            console.log(`[VideoPlayer] Force start from beginning - skipping resume position fetch`);
            setHasLoadedProgress(true);
            return;
        }

        const fetchProgress = async () => {
            try {
                const { data } = await api.get(`/interaction/${item.id}/progress`);
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
                    // A position past the COMPLETION threshold means the item was finished, not
                    // paused: resuming there drops the viewer into the last few seconds (and, on a
                    // transcode, spins up a session for a sliver of video). Mirror the server's
                    // completion rule (MediaCompletionHelper: >=95%) and restart from the top —
                    // the old bound only rejected positions past the END, so a 99% position
                    // "resumed" at the closing credits.
                    const maxValidPosition = durationSeconds > 0
                        ? Math.min(durationSeconds - 5, durationSeconds * 0.95)
                        : Infinity;
                    console.log(`Resume validation for ${item.id}: position=${data.position}, duration=${item.duration}(parsed=${durationSeconds}s), maxValid=${maxValidPosition}`);
                    if (data.position < maxValidPosition) {
                        console.log(`Resuming from saved position: ${data.position}s`);
                        setResumePosition(data.position);
                    } else {
                        console.log(`Saved position ${data.position}s is at/past the completion threshold (${maxValidPosition}s) - starting from beginning`);
                        // Don't set resume position - will start from beginning
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
    }, [item.id, item.duration, forceStartFromBeginning]);

    // Save playback position periodically (every 10 seconds) and on unmount
    useEffect(() => {
        if (!token || !item.id) return;

        const saveProgress = async () => {
            const effectivePosition = currentTime + seekOffset;
            // Only save if position changed significantly (> 5 seconds difference)
            if (Math.abs(effectivePosition - lastSavedPositionRef.current) > 5 && effectivePosition > 0) {
                try {
                    await api.post(`/interaction/${item.id}/progress`, { position: effectivePosition });
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

        // The empty GUID marks "no adjacent episode" (start/end of series). Each direction is
        // fetched independently and resolves to null on any failure, so a stale id from the
        // previously-played episode can never survive into the current one.
        const emptyGuid = '00000000-0000-0000-0000-000000000000';
        const fetchAdjacentId = async (direction: 'previous' | 'next'): Promise<string | null> => {
            try {
                const { data } = await api.get(`/episode/${item.id}/${direction}`);
                return data.episodeId && data.episodeId !== emptyGuid ? data.episodeId : null;
            } catch {
                return null;
            }
        };

        const fetchAdjacentEpisodes = async () => {
            setPreviousEpisodeId(await fetchAdjacentId('previous'));
            setNextEpisodeId(await fetchAdjacentId('next'));
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
                setShowMoreMenu(false);
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

    // Consults the element's own paused flag rather than isPlaying state so the callback is
    // identity-stable and can never act on a stale snapshot — the global keyboard handler
    // below lists it as a dependency without re-subscribing on every play/pause.
    const togglePlay = useCallback(() => {
        const video = videoRef.current;
        if (!video) return;
        if (video.paused) {
            video.play().catch(() => { });
        } else {
            video.pause();
        }
    }, []);

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
                    setShowMoreMenu(false);
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
    }, [resetControlsTimeout, togglePlay]);

    // Determine playback strategy based on container/codec and detected capabilities
    // Wait for progress, subtitle preference, AND capability detection to be loaded before starting transcode
    // Determine playback strategy based on backend plan
    // Wait for progress, subtitle preference, AND capability detection to be loaded before starting
    useEffect(() => {
        // The auth token is read at RUN time, not subscribed reactively: tokens rotate silently
        // every ~15 minutes (axios refresh -> App.tsx re-fetches the media token), and a token
        // dependency here would tear down and restart a healthy stream on every rotation.
        // Requests issued mid-stream get a fresh token per request via xhrSetup (HLS effect below).
        const urlToken = getUrlToken();
        if (!urlToken || !hasLoadedProgress || isDetectingCapabilities) return;

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
                const response = await fetch(`/api/v1/transcode/${item.id}/plan`, {
                    method: 'POST',
                    headers: transcodeAuthHeaders({ 'Content-Type': 'application/json' }),
                    body: JSON.stringify(capabilitiesToSend)
                });

                if (!response.ok || !isMounted) return;

                const plan: StreamPlan = await response.json();
                console.log('[StreamPlan] Received plan:', plan);
                setCurrentPlan(plan);

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

                    // Delete previous transcode session to start fresh. Unconditional
                    // on position (B-16): the sub index is part of the session KEY, so
                    // without this the old sub's ffmpeg kept transcoding alongside the
                    // new one when the change happened in the first second of playback.
                    if (isSubtitleChange) {
                        setIsSubtitleChange(false);
                        fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, { method: 'DELETE', headers: transcodeAuthHeaders() }).catch(() => { });
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
                    const directUrl = `${initialSrc}${initialSrc.includes('?') ? '&' : '?'}token=${urlToken}`;
                    setSrc(directUrl);
                    setIsTranscoding(false);
                }
            }
        };

        fetchStreamPlan();

        return () => { isMounted = false; };

    // Deliberately NOT exhaustive: this effect (re)starts the stream, so reacting to seekOffset,
    // streamId, initialSrc, or localPrefs.* would tear down and reload playback on every seek or
    // preference change — and reacting to the auth token would do the same on every silent
    // ~15-minute rotation (the token is read at run time via getUrlToken instead). It must run
    // only when the item/tracks/quality/capability inputs change; the values above are read
    // fresh from the closure of the run those inputs trigger.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [item, selectedSubtitleTrack, selectedAudioTrack, resumePosition, hasLoadedProgress, isSubtitleChange, forceStartFromBeginning, isDetectingCapabilities, mediaCapabilities, selectedQuality]);



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

                    // Re-stamp the URL-embedded auth token on EVERY manifest/segment request.
                    // The src URL carries the token that was current at stream start, but tokens
                    // rotate silently every ~15 minutes; without this, playback longer than the
                    // starting token's lifetime would begin failing auth mid-stream. Calling
                    // xhr.open here is the documented hls.js way to rewrite the request URL.
                    xhrSetup: (xhr, url) => {
                        const freshToken = getUrlToken();
                        if (!freshToken) return;
                        const rewritten = new URL(url, window.location.origin);
                        rewritten.searchParams.set('token', freshToken);
                        xhr.open('GET', rewritten.toString(), true);
                    },
                });

                hls.loadSource(src);
                hls.attachMedia(video);

                hls.on(Hls.Events.MANIFEST_PARSED, () => {
                    console.log('HLS manifest parsed, ready to play');
                    setIsLoading(false);

                    // Add WebVTT subtitle track directly if subtitles are selected
                    // This approach works better than HLS manifest subtitles for single VTT files
                    const subtitleToken = getUrlToken();
                    // Remove any existing track elements UNCONDITIONALLY — the <video>
                    // element survives HLS re-setup, so a track left behind after
                    // switching to Off (-1) would keep rendering its loaded cues
                    // (review finding on the B-15 guard).
                    const existingTracks = video.querySelectorAll('track');
                    existingTracks.forEach(t => t.remove());

                    if (selectedSubtitleTrack !== null && selectedSubtitleTrack !== -1 && subtitleToken) {
                        // &gen busts caches across far-seek restarts: the URL was
                        // otherwise identical while the VTT content changes per seek
                        // offset (stale copy = subs off by the whole seek).
                        const subtitleUrl = `/api/v1/transcode/${item.id}/subtitles.vtt?token=${subtitleToken}&sub=${selectedSubtitleTrack}&sid=${streamId}&gen=${Math.floor(seekOffset)}`;
                        console.log(`Adding subtitle track: ${subtitleUrl}`);

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
                            // R-WI-018: apply the user's sync offset to the fresh cues
                            // (the server already served them stream-aligned).
                            applyCurrentSubtitleShiftRef.current();
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
                            applyCurrentSubtitleShiftRef.current();
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
                    // 410 Gone = an admin stopped this session server-side. It is TERMINAL:
                    // retrying is exactly what used to resurrect it (the playlist reload
                    // started a fresh ffmpeg), so stop and say why instead of recovering.
                    // Checked before `fatal` because hls.js reports a 410 on a segment as a
                    // non-fatal network error it intends to retry.
                    if (data.response?.code === 410) {
                        setError('Playback was stopped by an administrator.');
                        videoRef.current?.pause();
                        hls.destroy();
                        return;
                    }
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
            // Native direct play: the whole file is the source, so resume by seeking the element to
            // the saved position and then auto-play. The HLS path gets both for free (the server
            // `&seek=` param starts the stream at the resume point and MANIFEST_PARSED calls play());
            // native playback has neither, which is why direct-play files (e.g. browser-compatible
            // .mp4) previously neither auto-played nor resumed. seekOffset is 0 for direct play, so
            // the displayed time tracks the real currentTime.
            video.src = src;
            video.addEventListener('loadedmetadata', () => {
                setIsLoading(false);
                if (seekAfterLoadRef.current > 0) {
                    const dur = video.duration;
                    const target = (isFinite(dur) && dur > 0)
                        ? Math.min(seekAfterLoadRef.current, dur - 1)
                        : seekAfterLoadRef.current;
                    try { video.currentTime = Math.max(0, target); } catch { /* not yet seekable */ }
                    seekAfterLoadRef.current = 0;
                }
                // Autoplay may be blocked without a user gesture; if so the user presses play and the
                // resume seek above has already applied.
                video.play().catch(() => { });
            }, { once: true });
            video.addEventListener('loadeddata', () => setIsLoading(false));
            video.load();
        }

        return () => {
            if (hlsRef.current) {
                hlsRef.current.destroy();
                hlsRef.current = null;
            }
            // Explicitly stop transcode when leaving the player. transcodeAuthHeaders reads
            // the access token fresh at cleanup time — the one from stream start may have
            // rotated since.
            if (isTranscoding && item.id) {
                fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, {
                    method: 'DELETE',
                    headers: transcodeAuthHeaders()
                }).catch(() => { /* Ignore cleanup errors */ });
            }
        };
    }, [src, isTranscoding, item.id, selectedSubtitleTrack, streamId]);

    // Fetch next episode info for "Play Next" overlay
    const fetchNextEpisode = useCallback(async () => {
        if (!token || !item.id) return;

        try {
            const { data } = await api.get<NextEpisodeInfo>(`/episode/${item.id}/next`);
            console.log('[PlayNext] Fetched next episode:', data);
            setNextEpisodeInfo(data);
            setShowNextEpisodeOverlay(true);
        } catch (error) {
            if (isAxiosError(error) && error.response?.status === 404) {
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
                return;
            }
            console.error('[PlayNext] Failed to fetch next episode:', error);
        }
    }, [token, item.id, item.seriesId]);

    // Handle playing the next episode from resume position (default)
    const handlePlayNextResume = useCallback(async () => {
        if (!nextEpisodeInfo || !nextEpisodeInfo.episodeId) return;

        // Mark current episode as watched
        if (token && item.id) {
            try {
                await api.post(`/interaction/${item.id}/watched`, { watched: true });
                invalidateContinueWatching();
            } catch {
                // Silently fail
            }

            // Clean up transcode session before navigating
            if (isTranscoding) {
                fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, {
                    method: 'DELETE',
                    headers: transcodeAuthHeaders()
                }).catch(() => { });
            }
        }

        // Navigate to next episode (will resume from saved position)
        setShowNextEpisodeOverlay(false);
        navigate(`/play/${nextEpisodeInfo.episodeId}`);
    }, [nextEpisodeInfo, token, item.id, isTranscoding, streamId, navigate, invalidateContinueWatching]);

    // Handle playing the next episode from start
    const handlePlayNextFromStart = useCallback(async () => {
        if (!nextEpisodeInfo || !nextEpisodeInfo.episodeId) return;

        // Mark current episode as watched
        if (token && item.id) {
            try {
                await api.post(`/interaction/${item.id}/watched`, { watched: true });
                invalidateContinueWatching();
            } catch {
                // Silently fail
            }

            // Reset the next episode's playback position to 0 and WAIT for it to complete
            try {
                await api.post(`/interaction/${nextEpisodeInfo.episodeId}/progress`, { position: 0 });
                console.log(`[PlayNext] Reset position to 0 for next episode: ${nextEpisodeInfo.episodeId}`);
            } catch {
                // Silently fail
            }

            // Clean up transcode session before navigating
            if (isTranscoding) {
                fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, {
                    method: 'DELETE',
                    headers: transcodeAuthHeaders()
                }).catch(() => { });
            }
        }

        // Navigate to next episode with ?start=0 to force starting from beginning
        setShowNextEpisodeOverlay(false);
        navigate(`/play/${nextEpisodeInfo.episodeId}?start=0`);
    }, [nextEpisodeInfo, token, item.id, isTranscoding, streamId, navigate, invalidateContinueWatching]);

    // Handle return to library
    const handleReturnToLibrary = useCallback(() => {
        setShowNextEpisodeOverlay(false);
        // Mark current episode as watched
        if (token && item.id) {
            api.post(`/interaction/${item.id}/watched`, { watched: true })
                .then(() => invalidateContinueWatching())
                .catch(() => { });

            // Clean up transcode session
            if (isTranscoding) {
                fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, {
                    method: 'DELETE',
                    headers: transcodeAuthHeaders()
                }).catch(() => { });
            }
        }
    }, [token, item.id, isTranscoding, streamId, invalidateContinueWatching]);

    // Handle rating the current episode from the overlay
    const handleRateCurrentEpisode = useCallback(async (rating: number) => {
        if (!token || !item.id) return;

        try {
            await api.post(`/interaction/${item.id}/rate`, { rating });
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

    // ---- Movie end-of-playback (the movie counterpart of the episode "Play Next" flow) ----

    // Flag the movie watched the moment the completion detector fires (credits marker or 98%),
    // once per session — the same rule that decides "finished" server-side, so the Continue
    // Watching row and the library's watched checkmark agree with what the player did.
    const markMovieFinished = useCallback(() => {
        if (hasMarkedWatchedRef.current || !item.id) return;
        hasMarkedWatchedRef.current = true;
        api.post(`/interaction/${item.id}/watched`, { watched: true })
            .then(() => invalidateContinueWatching())
            .catch((error) => {
                console.error('[MovieEnd] Failed to mark watched:', error);
                hasMarkedWatchedRef.current = false; // allow the ended-event retry
            });
    }, [item.id, invalidateContinueWatching]);

    // Credits threshold crossed: mark watched and raise the post-play overlay. The overlay shows
    // immediately; recommendation cards fill in when the fetch lands (and are simply absent if
    // it fails — rating + Back to Library still work).
    const handleMovieThresholdReached = useCallback(() => {
        markMovieFinished();
        setShowMovieEndOverlay(true);
        postPlayService.forMovie(item.id)
            .then(setMovieEndInfo)
            .catch((error) => console.error('[MovieEnd] Failed to fetch post-play recommendations:', error));
    }, [item.id, markMovieFinished]);

    // Dismiss to watch the credits; the overlay stays away for this threshold crossing and the
    // player still navigates home when the video actually ends.
    const handleWatchCredits = useCallback(() => {
        setShowMovieEndOverlay(false);
    }, []);

    // Immersive player's back control: return to wherever playback was launched from when
    // there's in-app history (react-router stamps history.state.idx), else — a deep link or
    // fresh tab — fall back to the media's detail page.
    const handleBack = useCallback(() => {
        if (typeof window.history.state?.idx === 'number' && window.history.state.idx > 0) {
            navigate(-1);
        } else {
            navigate(`/media/${item.id}`);
        }
    }, [navigate, item.id]);

    // Every way OFF the player from the movie overlay (countdown, Back to Library, a
    // recommendation card): stop the transcode session, then navigate.
    const handleLeaveMovie = useCallback((path: string) => {
        setShowMovieEndOverlay(false);
        if (isTranscoding && item.id) {
            fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, {
                method: 'DELETE',
                headers: transcodeAuthHeaders()
            }).catch(() => { });
        }
        navigate(path);
    }, [isTranscoding, item.id, streamId, navigate]);

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
                // sid identifies THIS playback session — without it the lookup misses and the
                // endpoint 404s on every play event (which hls.js emits repeatedly while it
                // retries, flooding the console).
                fetch(`/api/v1/transcode/${item.id}/resume?sid=${streamId}${selectedSubtitleTrack !== null ? `&sub=${selectedSubtitleTrack}` : ''}`, {
                    method: 'POST',
                    headers: transcodeAuthHeaders()
                }).catch(() => { });
            }
        };
        const handlePause = () => {
            setIsPlaying(false);
            // Signal backend to pause transcoding (throttle control)
            if (isTranscoding && token && item.id) {
                fetch(`/api/v1/transcode/${item.id}/pause?sid=${streamId}${selectedSubtitleTrack !== null ? `&sub=${selectedSubtitleTrack}` : ''}`, {
                    method: 'POST',
                    headers: transcodeAuthHeaders()
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
        // The load algorithm resets the element's playbackRate to 1 on every src swap
        // (far-seek restart, quality change, next episode) while the speed state kept
        // the old value — the speed menu showed 2× at 1× playback and the Media Session
        // reported the wrong rate, making the OS scrubber extrapolate too fast and
        // sawtooth. Syncing state to the element fixes both consumers.
        const handleRateChange = () => setPlaybackSpeed(video.playbackRate);
        const handleEnterPiP = () => setIsPiP(true);
        const handleLeavePiP = () => setIsPiP(false);
        const handleEnded = () => {
            console.log('Video ended event fired');
            // A finished MOVIE: ensure the watched flag stuck (idempotent, and the threshold
            // detector below may have been skipped by a seek straight to the end). If the
            // post-play overlay is up, ITS countdown owns navigation — yanking the user away
            // while they browse recommendations (or sit with the countdown paused) would be
            // hostile. Only when the overlay was dismissed (Watch Credits) or never fired does
            // the true end of the video return to the movie's library.
            if (item.type === 'Movie' && !item.seriesId) {
                markMovieFinished();
                if (!showMovieEndOverlay) {
                    handleLeaveMovie(`/libraries/${item.libraryId}`);
                    return;
                }
            }
            // Signal backend to clean up transcode session when video ends
            if (isTranscoding && token && item.id) {
                fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, {
                    method: 'DELETE',
                    headers: transcodeAuthHeaders()
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
            // Movie completion detection — the same credits-marker/98% rule the episode overlay
            // uses, but the payoff is the post-play overlay (mark watched + recommendations +
            // auto-return to the library) instead of "Play Next".
            else if (item.type === 'Movie' && displayDuration > 0 && !isFreshLoad && video.currentTime > 5) {
                const threshold98 = displayDuration * 0.98;
                const creditsStart = item.creditsStart;
                const firstThreshold = (creditsStart && creditsStart > 0)
                    ? Math.min(creditsStart, threshold98)
                    : threshold98;

                const reachedThreshold =
                    (creditsStart && creditsStart > 0 && effectiveTime >= creditsStart)
                    || effectiveTime >= threshold98;

                // Seeked backward past the threshold: allow the overlay to fire again later
                // (the watched flag, once set, intentionally stays).
                if (effectiveTime < firstThreshold - 5) {
                    if (hasShownOverlayRef.current && !showMovieEndOverlay) {
                        console.log('[MovieEnd] User seeked backward, resetting overlay trigger');
                        hasShownOverlayRef.current = false;
                        lastThresholdTimeRef.current = 0;
                    }
                }

                if (reachedThreshold && !hasShownOverlayRef.current && !showMovieEndOverlay) {
                    console.log(`[MovieEnd] Completion threshold reached at ${effectiveTime}s`);
                    hasShownOverlayRef.current = true;
                    lastThresholdTimeRef.current = effectiveTime;
                    handleMovieThresholdReached();
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
        video.addEventListener('ratechange', handleRateChange);
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
            video.removeEventListener('ratechange', handleRateChange);
            video.removeEventListener('enterpictureinpicture', handleEnterPiP);
            video.removeEventListener('leavepictureinpicture', handleLeavePiP);
            video.removeEventListener('ended', handleEnded);
        };
    }, [src, isTranscoding, token, item.id, item.seriesId, item.creditsStart, item.type, item.libraryId, selectedSubtitleTrack, displayDuration, seekOffset, streamId, fetchNextEpisode, showNextEpisodeOverlay, showMovieEndOverlay, markMovieFinished, handleMovieThresholdReached, handleLeaveMovie]);

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
            // WS-6: sendBeacon can't set headers and query tokens no longer authenticate
            // POSTs — a keepalive fetch is the modern equivalent (survives page unload)
            // and carries the access token in the Authorization header.
            fetch(`/api/v1/transcode/${item.id}/stop?all=true`, {
                method: 'POST',
                keepalive: true,
                headers: transcodeAuthHeaders()
            }).catch(() => { });
        };

        window.addEventListener('beforeunload', handleBeforeUnload);
        return () => window.removeEventListener('beforeunload', handleBeforeUnload);
    }, [isTranscoding, token, item.id]);

    // Note: Burn-in subtitles are handled via the HLS URL - no need for text tracks
    // The selectedSubtitleTrack state triggers HLS URL change (via playback strategy useEffect)
    // which restarts the transcode with the subtitle burned in

    // Control actions

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
                await fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, { method: 'DELETE', headers: transcodeAuthHeaders() });
            } catch { /* Ignore cleanup errors */ }
        }

        navigate(`/play/${targetId}`);
    };

    // Helper that performs the actual seek (Restored)
    // R-WI-005: build the far-seek transcode URL preserving the user-choice params the server
    // does NOT restore from the persisted plan — subtitle/audio track AND the burn-in preference
    // (previously dropped on seek, resetting burn-in to off). The quality/security params
    // (resolution/codec/bitrate/HDR) are intentionally omitted: the server re-applies them from
    // the negotiated plan keyed by sid (R-WI-002), so a minimal seek URL can no longer drop the
    // quality decision or bypass the per-user bitrate cap.
    const buildTranscodeSeekUrl = (seekTime: number) => {
        let url = `/api/v1/transcode/${item.id}/master.m3u8?token=${token}&seek=${Math.floor(seekTime)}&sid=${streamId}`;
        if (selectedSubtitleTrack !== null) {
            url += `&sub=${selectedSubtitleTrack}`;
        }
        if (selectedAudioTrack !== null && selectedAudioTrack >= 0) {
            url += `&audio=${selectedAudioTrack}`;
        }
        if (localPrefs.burnSubtitles === 'always') {
            url += `&burnSubtitles=true`;
        }
        return url;
    };

    const handleSeekToTime = (seekTime: number) => {
        if (!videoRef.current || displayDuration <= 0) return;

        // For transcoding: check if seeking beyond the current video duration (transcoded portion)
        const currentTranscodedDuration = videoRef.current.duration || 0;

        // R-WI-015 review: the transcoded window ends at seekOffset + transcoded length —
        // adding currentTime inflated the bound, so seeks landing just past the window
        // clamp-stalled at the live edge instead of restarting the transcode.
        if (isTranscoding && token && seekTime > seekOffset + currentTranscodedDuration + 5) {
            // Seeking beyond transcoded portion - restart transcode at this position
            console.log(`Seeking to ${seekTime}s - beyond transcoded range, restarting transcode`);

            seekAfterLoadRef.current = seekTime;
            setSeekOffset(Math.floor(seekTime));
            // Reset currentTime NOW: displayedTime = currentTime + seekOffset, and the
            // stale pre-seek currentTime otherwise adds onto the new offset until the
            // restarted stream's first timeupdate — the same transient the initial-load
            // path already guards against (and R-WI-015 broadcasts to the OS scrubber).
            setCurrentTime(0);

            fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, { method: 'DELETE', headers: transcodeAuthHeaders() })
                .catch(() => { });

            setSrc(buildTranscodeSeekUrl(seekTime));
        } else {
            const targetInStream = seekTime - seekOffset;
            if (targetInStream >= 0 && targetInStream <= currentTranscodedDuration) {
                videoRef.current.currentTime = targetInStream;
            } else if (isTranscoding && token && targetInStream < 0) {
                console.log(`Seeking to ${seekTime}s - before current offset, restarting transcode`);
                seekAfterLoadRef.current = seekTime;
                setSeekOffset(Math.floor(seekTime));
                setCurrentTime(0); // same transient guard as the forward-restart branch

                fetch(`/api/v1/transcode/${item.id}?sid=${streamId}`, { method: 'DELETE', headers: transcodeAuthHeaders() })
                    .catch(() => { });

                setSrc(buildTranscodeSeekUrl(seekTime));
            } else {
                // Element seeks are STREAM-relative. Seeking the absolute time here
                // double-applied the offset when the element's duration was still
                // unknown (pending restart: duration NaN → no clamp), landing a
                // follow-up seek at seekOffset + seekTime.
                videoRef.current.currentTime = Math.max(0, Math.min(targetInStream, videoRef.current.duration || Infinity));
            }
        }
    };

    // Progress bar mouse handlers for drag and hover


    // Progress bar percentages
    // Calculate displayed time including seek offset (for when transcoding starts from non-zero position)
    const displayedTime = currentTime + seekOffset;

    // R-WI-015: OS media controls. seekto MUST go through handleSeekToTime — after a
    // far seek the element's currentTime is stream-relative (real position is
    // currentTime + seekOffset), so raw element seeking would land in the wrong place.
    // seekbackward/seekforward reuse skip(), the same offset-safe relative skip the
    // keyboard shortcuts use. Position reports displayedTime against the REAL duration.
    useMediaSession({
        // A fatal error unmounts the <video> without a pause event, so isPlaying
        // would stay true — unregister instead of leaving zombie OS controls.
        enabled: !error,
        isPlaying,
        contentId: item.id,
        metadata: {
            title: item.title,
            artist: item.seasonNumber != null && item.episodeNumber != null
                ? `Season ${item.seasonNumber} · Episode ${item.episodeNumber}`
                : undefined,
            // attachAuthToApiUrl is a no-op for /cache/* and external URLs.
            artworkUrl: item.posterPath ? attachAuthToApiUrl(item.posterPath) : null,
        },
        handlers: {
            onPlay: () => { videoRef.current?.play().catch(() => { }); },
            onPause: () => { videoRef.current?.pause(); },
            onSeekBackward: () => skip(-10),
            onSeekForward: () => skip(10),
            onSeekTo: handleSeekToTime,
            // navigateEpisode is the mid-episode-safe primitive: it preserves the
            // target's resume position and marks nothing watched. (The end-of-episode
            // overlay's handlePlayNextFromStart would reset the next episode to 0:00
            // and stamp watched — wrong semantics for an OS button available all
            // episode long.) Ids are fetched at mount, so the OS buttons exist for
            // the whole episode, not just during the credits.
            onNextTrack: nextEpisodeId ? () => { void navigateEpisode('next'); } : undefined,
            onPreviousTrack: previousEpisodeId ? () => { void navigateEpisode('prev'); } : undefined,
        },
        position: { duration: displayDuration, position: displayedTime, playbackRate: playbackSpeed },
    });


    // For HLS streams, buffer is relative to current stream position, need to add seekOffset
    const displayedBuffered = isTranscoding ? bufferedTime + seekOffset : bufferedTime;
    const bufferedPercent = displayDuration > 0 ? (displayedBuffered / displayDuration) * 100 : 0;

    // Skip-segment pill: derive "playhead is inside intro/credits" from displayed
    // time and the timecodes on the item. The NextEpisode overlay takes priority
    // over the credits pill — both can fire on the same threshold and we'd rather
    // show the structured overlay than a redundant skip button.
    const inIntro = item.introStart != null
        && item.introEnd != null
        && displayedTime >= item.introStart
        && displayedTime < item.introEnd;
    const inCredits = !showNextEpisodeOverlay
        && item.creditsStart != null
        && item.creditsEnd != null
        && displayedTime >= item.creditsStart
        && displayedTime < item.creditsEnd;

    const handleSkipIntro = useCallback(() => {
        if (item.introEnd == null) return;
        // Diagnostic: record exactly what the skip handler intends and what the
        // video actually lands on. Compare these numbers if "skip lands past the
        // detected intro" — the seek target should equal item.introEnd.
        console.log('[SkipIntro] introStart=', item.introStart, 'introEnd=', item.introEnd,
            'currentTime=', currentTime, 'seekOffset=', seekOffset,
            'displayedTime=', currentTime + seekOffset);
        handleSeekToTime(item.introEnd);
        // Wait one frame for the seek to apply, then read back where we landed.
        requestAnimationFrame(() => {
            const v = videoRef.current;
            console.log('[SkipIntro] after seek: video.currentTime=', v?.currentTime,
                'displayedTime=', (v?.currentTime ?? 0) + seekOffset,
                'expected=', item.introEnd);
        });
    }, [item.introEnd, item.introStart, currentTime, seekOffset]); // eslint-disable-line react-hooks/exhaustive-deps

    const handleSkipCredits = useCallback(() => {
        if (item.creditsEnd == null) return;
        console.log('[SkipCredits] creditsStart=', item.creditsStart, 'creditsEnd=', item.creditsEnd,
            'currentTime=', currentTime, 'seekOffset=', seekOffset,
            'displayedTime=', currentTime + seekOffset);
        handleSeekToTime(item.creditsEnd);
        requestAnimationFrame(() => {
            const v = videoRef.current;
            console.log('[SkipCredits] after seek: video.currentTime=', v?.currentTime,
                'displayedTime=', (v?.currentTime ?? 0) + seekOffset,
                'expected=', item.creditsEnd);
        });
    }, [item.creditsEnd, item.creditsStart, currentTime, seekOffset]); // eslint-disable-line react-hooks/exhaustive-deps

    // Auto-skip on segment entry — fires once per entry so the user can disable
    // the preference and seek back into the segment without it firing again.
    const introAutoSkipFiredRef = useRef(false);
    useEffect(() => {
        if (!inIntro) {
            introAutoSkipFiredRef.current = false;
            return;
        }
        if (localPrefs.autoSkipIntros !== 'true') return;
        if (introAutoSkipFiredRef.current) return;
        introAutoSkipFiredRef.current = true;
        handleSkipIntro();
    }, [inIntro, localPrefs.autoSkipIntros, handleSkipIntro]);

    const creditsAutoSkipFiredRef = useRef(false);
    useEffect(() => {
        if (!inCredits) {
            creditsAutoSkipFiredRef.current = false;
            return;
        }
        if (localPrefs.autoSkipCredits !== 'true') return;
        if (creditsAutoSkipFiredRef.current) return;
        creditsAutoSkipFiredRef.current = true;
        handleSkipCredits();
    }, [inCredits, localPrefs.autoSkipCredits, handleSkipCredits]);

    if (!token || !src) {
        return (
            <div className="w-full h-full bg-black flex items-center justify-center">
                <div className="text-white/50 animate-pulse">Loading player...</div>
            </div>
        );
    }

    if (error) {
        return (
            <div className="w-full h-full bg-black flex items-center justify-center flex-col gap-4">
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

    // The player fills whatever box its parent gives it (PlayerPage: the whole viewport). The
    // video letterboxes/pillarboxes itself via object-contain, so any source aspect — including
    // ultra-wide low-res files — renders undistorted at every window size.
    return (
        <div className="w-full h-full">
            <div
                ref={containerRef}
                className="relative w-full h-full bg-black overflow-hidden group"
                onMouseMove={resetControlsTimeout}
                onMouseLeave={() => {
                    if (isPlaying) {
                        setShowControls(false);
                        setShowMoreMenu(false);
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

                {/* Top overlay bar: back navigation + title/year (the immersive player has no
                    page chrome above it). Fades with the rest of the controls. */}
                <div
                    className={`absolute top-0 left-0 right-0 z-30 bg-gradient-to-b from-black/80 via-black/40 to-transparent px-4 pt-3 pb-10 flex items-center gap-3 transition-opacity duration-300 ${showControls || !isPlaying ? 'opacity-100' : 'opacity-0 pointer-events-none'}`}
                >
                    <button
                        type="button"
                        onClick={handleBack}
                        aria-label="Back"
                        title="Back"
                        className="text-white/80 hover:text-white focus-visible:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg flex-shrink-0"
                    >
                        <svg className="w-6 h-6" fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" d="M19 12H5m0 0l7 7m-7-7l7-7" />
                        </svg>
                    </button>
                    <div className="min-w-0">
                        <h1 className="text-white text-lg font-semibold leading-tight truncate">{item.title}</h1>
                        {item.year && <p className="text-white/60 text-xs leading-tight">{item.year}</p>}
                    </div>
                </div>

                {/* Player Toast Notification */}
                {playerToast && (
                    <PlayerToast
                        key={playerToast.message}
                        message={playerToast.message}
                        type={playerToast.type}
                        onDismiss={handleDismissToast}
                    />
                )}

                {/* Skip-segment pills. Auto-skip honors useLocalPreferences;
                    when off, the pill is a manual button. The pill auto-fades
                    after 8 seconds so it doesn't loiter on long intros. */}
                <SkipSegmentPill
                    label="Skip Intro"
                    visible={inIntro}
                    onSkip={handleSkipIntro}
                />
                <SkipSegmentPill
                    label="Skip Credits"
                    visible={inCredits}
                    onSkip={handleSkipCredits}
                />

                {/* Next Episode Overlay (for TV Episodes only) */}
                {showMovieEndOverlay && (
                    <MovieEndOverlay
                        movieTitle={item.title}
                        postPlay={movieEndInfo}
                        currentRating={item.personalRating}
                        onRateCurrent={handleRateCurrentEpisode}
                        onWatchCredits={handleWatchCredits}
                        onLeave={handleLeaveMovie}
                        onPauseVideo={handlePauseVideo}
                        libraryId={item.libraryId}
                    />
                )}
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
                {/* R-WI-018: caption appearance rides ::cue — the native renderer the
                    sidecar-VTT path uses. Scoped to this video's class. */}
                <style>{cueCss}</style>
                <video
                    ref={videoRef}
                    className="sm-player-video w-full h-full object-contain cursor-pointer"
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
                        creditsEnd={item.creditsEnd}
                        introStart={item.introStart}
                        introEnd={item.introEnd}
                        framePreviewUrl={framePreviewUrl}
                        spriteFrame={spriteFrame}
                        onSeek={(time) => {
                            seekTargetRef.current = time;

                            // Prefer the pre-baked trickplay tile — instant, no FFmpeg.
                            const sprite = trickplayFrameAt(time);
                            if (sprite) {
                                setSpriteFrame(sprite);
                                setFramePreviewUrl(null);
                                return;
                            }

                            // Fallback: debounced on-demand frame extraction.
                            setSpriteFrame(null);
                            if (frameDebounceRef.current) clearTimeout(frameDebounceRef.current);
                            frameDebounceRef.current = setTimeout(() => {
                                if (!token) return;
                                const url = `/api/v1/transcode/${item.id}/frame?time=${time.toFixed(1)}&token=${token}`;
                                setFramePreviewUrl(url);
                            }, 100);
                        }}
                        onSeekStart={() => {
                            wasPlayingBeforeDragRef.current = !videoRef.current?.paused;
                            videoRef.current?.pause();
                        }}
                        onSeekEnd={() => {
                            setFramePreviewUrl(null);
                            setSpriteFrame(null);
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
                        <div className="flex items-center gap-2 flex-1 min-w-[140px]">
                            <div className="text-white text-base font-mono">
                                {formatTime(displayedTime)} / {formatTime(displayDuration)}
                            </div>
                        </div>

                        {/* Center: Playback navigation controls */}
                        <div className="flex items-center gap-1">
                            {/* Previous Episode - Only show for TV episodes */}
                            {item.seriesId && (
                                <button
                                    type="button"
                                    onClick={() => navigateEpisode('prev')}
                                    disabled={!previousEpisodeId}
                                    aria-label={previousEpisodeId ? 'Previous episode' : 'No previous episode'}
                                    className={`transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${previousEpisodeId ? 'text-white/70 hover:text-white' : 'text-white/20 cursor-not-allowed'}`}
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
                                    type="button"
                                    onClick={() => skipToChapter('prev')}
                                    aria-label="Previous chapter"
                                    className="text-white/60 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                    title="Previous Chapter"
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M6 6h2v12H6zm12 0l-9 6 9 6V6z" />
                                    </svg>
                                </button>
                            )}

                            {/* Skip backward 10s */}
                            <button
                                type="button"
                                onClick={() => skip(-10)}
                                aria-label="Skip back 10 seconds"
                                className="text-white/70 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                title="Back 10s (J)"
                            >
                                <svg className="w-7 h-7" fill="currentColor" viewBox="0 0 24 24">
                                    <path d="M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z" />
                                    <text x="9" y="15" fontSize="6" fontWeight="bold">10</text>
                                </svg>
                            </button>

                            {/* Play/Pause - centered and larger */}
                            <button
                                type="button"
                                onClick={togglePlay}
                                aria-label={isPlaying ? 'Pause' : 'Play'}
                                className="text-white hover:text-blue-400 transition-colors p-2 mx-1 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
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
                                type="button"
                                onClick={() => skip(10)}
                                aria-label="Skip forward 10 seconds"
                                className="text-white/70 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
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
                                    type="button"
                                    onClick={() => skipToChapter('next')}
                                    aria-label="Next chapter"
                                    className="text-white/60 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
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
                                    type="button"
                                    onClick={() => navigateEpisode('next')}
                                    disabled={!nextEpisodeId}
                                    aria-label={nextEpisodeId ? 'Next episode' : 'No next episode'}
                                    className={`transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${nextEpisodeId ? 'text-white/70 hover:text-white' : 'text-white/20 cursor-not-allowed'}`}
                                    title={nextEpisodeId ? 'Next Episode' : 'No next episode'}
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M4 6l6 6-6 6V6zm6 0l6 6-6 6V6zM17 6h2v12h-2z" />
                                    </svg>
                                </button>
                            )}
                        </div>

                        {/* Right: Settings controls */}
                        <div className="flex items-center gap-2 flex-1 justify-end">
                            {/* Volume */}
                            <div className="relative group/volume">
                                <button type="button" onClick={toggleMute} aria-label={isMuted || volume === 0 ? 'Unmute' : 'Mute'} className="text-white/70 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" title="Mute (M)">
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
                                        type="button"
                                        onClick={() => setShowTrackMenu(!showTrackMenu)}
                                        aria-label="Subtitle and audio tracks"
                                        aria-haspopup="menu"
                                        aria-expanded={showTrackMenu}
                                        className="text-white/70 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
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

                            {/* Cast to Chromecast (P3-WI-001 / CC-WI-005). The cast button shows
                                when a cast can start; otherwise a readiness affordance explains why
                                (no HTTPS, or no Cast device on the network). */}
                            {currentPlan && (showCastButton || showCastHelp) && (
                                <div className="relative flex items-center">
                                {showCastButton && (
                                <button
                                    type="button"
                                    onClick={async () => {
                                        if (isCasting) {
                                            stopCasting();
                                            return;
                                        }
                                        if (castInFlightRef.current) return; // ignore re-clicks while a cast is starting
                                        // The Chromecast fetches the stream URL itself, so a
                                        // loopback origin (e.g. the Vite dev server on localhost)
                                        // is unreachable from the TV. Require a LAN-routable host.
                                        // location.hostname returns IPv6 literals without brackets.
                                        const host = window.location.hostname;
                                        if (host === 'localhost' || host === '0.0.0.0' || host.startsWith('127.') || host === '::1') {
                                            setPlayerToast({
                                                message: 'To cast, open SoftMedia using this computer’s network address (e.g. http://192.168.x.x:PORT), not localhost — the TV can’t reach localhost.',
                                                type: 'error',
                                            });
                                            return;
                                        }
                                        castInFlightRef.current = true;
                                        try {
                                            // Request a plan tuned to the Chromecast (H.264/AAC/1080p, its
                                            // own session id) rather than reusing the desktop plan, which
                                            // may target AV1/HEVC/4K+ the receiver can't decode.
                                            const castCaps = createCapabilitiesWithOverrides(CHROMECAST_CAPABILITIES, {
                                                requestedQuality: '1080p',
                                                streamId: `cast-${streamId}`,
                                            });
                                            // ?cast=true → the plan URL carries a long-lived,
                                            // media-scoped cast token (the receiver can't refresh
                                            // the short-lived session JWT mid-movie).
                                            const resp = await fetch(`/api/v1/transcode/${item.id}/plan?cast=true`, {
                                                method: 'POST',
                                                headers: transcodeAuthHeaders({ 'Content-Type': 'application/json' }),
                                                body: JSON.stringify(castCaps),
                                            });
                                            if (!resp.ok) throw new Error(`stream plan request failed (${resp.status})`);
                                            const castPlan: StreamPlan = await resp.json();

                                            // plan.url is "/api/..."; the receiver fetches it directly, so make
                                            // it absolute. It already carries ?token= for query-string auth.
                                            const streamUrl = new URL(castPlan.url, window.location.origin).toString();
                                            const contentType = castPlan.container === 'hls'
                                                ? 'application/vnd.apple.mpegurl'
                                                : 'video/mp4';
                                            await castNow({
                                                streamUrl,
                                                contentType,
                                                title: item.title,
                                                subtitle: item.year ? String(item.year) : undefined,
                                                posterUrl: item.posterPath
                                                    ? new URL(item.posterPath, window.location.origin).toString()
                                                    : undefined,
                                            });
                                            // Hand off: stop local playback so it isn't playing in two places.
                                            videoRef.current?.pause();
                                        } catch (e) {
                                            console.error('[Cast]', e);
                                            setPlayerToast({ message: 'Could not start casting. See the browser console for details.', type: 'error' });
                                        } finally {
                                            castInFlightRef.current = false;
                                        }
                                    }}
                                    aria-label={isCasting ? `Stop casting to ${receiverName ?? 'receiver'}` : 'Cast to device'}
                                    aria-pressed={isCasting}
                                    className={`transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${isCasting ? 'text-blue-400' : 'text-white/70 hover:text-white'}`}
                                    title={isCasting ? `Casting to ${receiverName ?? 'receiver'}` : 'Cast'}
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M1 18v3h3c0-1.66-1.34-3-3-3zm0-4v2c2.76 0 5 2.24 5 5h2c0-3.87-3.13-7-7-7zm0-4v2c4.97 0 9 4.03 9 9h2c0-6.08-4.93-11-11-11zm20-7H3c-1.1 0-2 .9-2 2v3h2V5h18v14h-7v2h7c1.1 0 2-.9 2-2V5c0-1.11-.9-2-2-2z" />
                                    </svg>
                                </button>
                                )}
                                {showCastHelp && (
                                    <button
                                        type="button"
                                        onClick={() => setCastDiagOpen((v) => !v)}
                                        aria-label="Casting unavailable — show readiness"
                                        aria-expanded={castDiagOpen}
                                        title="Casting unavailable — why?"
                                        className="transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded text-white/40 hover:text-white/70 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                    >
                                        <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                            <path d="M1 18v3h3c0-1.66-1.34-3-3-3zm0-4v2c2.76 0 5 2.24 5 5h2c0-3.87-3.13-7-7-7zm0-4v2c4.97 0 9 4.03 9 9h2c0-6.08-4.93-11-11-11zm20-7H3c-1.1 0-2 .9-2 2v3h2V5h18v14h-7v2h7c1.1 0 2-.9 2-2V5c0-1.11-.9-2-2-2z" />
                                        </svg>
                                    </button>
                                )}
                                {castDiagOpen && (
                                    <CastDiagnostics
                                        readiness={describeCastReadiness({
                                            isSecureContext,
                                            hostname: window.location.hostname,
                                            isCastAvailable,
                                            castState,
                                            castUnavailableReason,
                                        })}
                                        onClose={() => setCastDiagOpen(false)}
                                    />
                                )}
                                </div>
                            )}

                            {/* More options — consolidates the less-frequently-used
                                controls (speed, quality, PiP, stream explainer) into a
                                single upward-opening menu so the right cluster stays
                                compact and the center play controls remain centered. */}
                            <div className="relative">
                                <button
                                    type="button"
                                    onClick={() => setShowMoreMenu(!showMoreMenu)}
                                    aria-label="More options"
                                    aria-haspopup="menu"
                                    aria-expanded={showMoreMenu}
                                    className="text-white/70 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                    title="More options"
                                >
                                    <svg className="w-6 h-6" fill="currentColor" viewBox="0 0 24 24">
                                        <path d="M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z" />
                                    </svg>
                                </button>
                                {showMoreMenu && (
                                    <div
                                        className="absolute bottom-full right-0 mb-2 bg-black/95 rounded-lg py-2 min-w-[240px] shadow-xl"
                                        role="menu"
                                        onMouseEnter={() => handleMenuInteraction(true)}
                                        onMouseLeave={() => handleMenuInteraction(false)}
                                    >
                                        {/* Playback Speed */}
                                        <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold">Playback Speed</div>
                                        <div className="flex flex-wrap gap-1 px-3 pb-2 pt-1">
                                            {PLAYBACK_SPEEDS.map(speed => (
                                                <button
                                                    key={speed}
                                                    type="button"
                                                    onClick={() => changePlaybackSpeed(speed)}
                                                    className={`px-2.5 py-1 text-xs rounded-md transition-colors ${playbackSpeed === speed ? 'bg-blue-500 text-white' : 'bg-white/10 text-white/80 hover:bg-white/20'}`}
                                                >
                                                    {speed === 1 ? 'Normal' : `${speed}x`}
                                                </button>
                                            ))}
                                        </div>

                                        {/* Quality */}
                                        <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold border-t border-white/10 mt-1 pt-2">Quality</div>
                                        <div className="flex flex-wrap gap-1 px-3 pb-2 pt-1">
                                            {['auto', '720p', '1080p', '4k', 'original'].map(quality => (
                                                <button
                                                    key={quality}
                                                    type="button"
                                                    onClick={() => setSelectedQuality(quality)}
                                                    className={`px-2.5 py-1 text-xs rounded-md transition-colors ${selectedQuality === quality ? 'bg-blue-500 text-white' : 'bg-white/10 text-white/80 hover:bg-white/20'}`}
                                                >
                                                    {quality === 'auto' ? 'Auto' : quality === 'original' ? 'Original' : quality.toUpperCase()}
                                                </button>
                                            ))}
                                        </div>

                                        {/* Subtitle sync (R-WI-018) — only when a text track is
                                            actually ON (-1 is the Off sentinel, not null). */}
                                        {selectedSubtitleTrack !== null && selectedSubtitleTrack !== -1 && (
                                            <>
                                                <div className="px-3 py-1 text-xs text-white/50 uppercase font-semibold border-t border-white/10 mt-1 pt-2">Subtitle Sync</div>
                                                <div className="flex items-center gap-2 px-3 pb-2 pt-1">
                                                    <button
                                                        type="button"
                                                        aria-label="Subtitles earlier"
                                                        title="Show subtitles earlier"
                                                        disabled={subtitleOffset <= -30}
                                                        onClick={() => setSubtitleOffset(o => clampUserOffset(o - 0.5))}
                                                        className="px-2.5 py-1 text-xs rounded-md bg-white/10 text-white/80 hover:bg-white/20 disabled:opacity-40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors"
                                                    >
                                                        −0.5s
                                                    </button>
                                                    <span className="text-xs text-white min-w-[3.5rem] text-center tabular-nums">
                                                        {subtitleOffset > 0 ? '+' : ''}{subtitleOffset.toFixed(1)}s
                                                    </span>
                                                    <button
                                                        type="button"
                                                        aria-label="Subtitles later"
                                                        title="Show subtitles later"
                                                        disabled={subtitleOffset >= 30}
                                                        onClick={() => setSubtitleOffset(o => clampUserOffset(o + 0.5))}
                                                        className="px-2.5 py-1 text-xs rounded-md bg-white/10 text-white/80 hover:bg-white/20 disabled:opacity-40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors"
                                                    >
                                                        +0.5s
                                                    </button>
                                                    {subtitleOffset !== 0 && (
                                                        <button
                                                            type="button"
                                                            aria-label="Reset subtitle sync"
                                                            onClick={() => setSubtitleOffset(0)}
                                                            className="px-2.5 py-1 text-xs rounded-md text-white/60 hover:bg-white/10 transition-colors"
                                                        >
                                                            Reset
                                                        </button>
                                                    )}
                                                </div>
                                            </>
                                        )}

                                        {/* Toggles / actions */}
                                        <div className="border-t border-white/10 mt-1 pt-1">
                                            {/* Picture-in-Picture */}
                                            {document.pictureInPictureEnabled && (
                                                <button
                                                    type="button"
                                                    onClick={togglePiP}
                                                    role="menuitem"
                                                    aria-pressed={isPiP}
                                                    className="w-full flex items-center gap-3 px-4 py-2 text-sm text-left hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-blue-400 transition-colors"
                                                    title="Picture-in-Picture (P)"
                                                >
                                                    <svg className={`w-5 h-5 ${isPiP ? 'text-blue-400' : 'text-white/70'}`} fill="currentColor" viewBox="0 0 24 24">
                                                        <path d="M19 7h-8v6h8V7zm2-4H3c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 16H3V5h18v14z" />
                                                    </svg>
                                                    <span className={isPiP ? 'text-blue-400' : 'text-white'}>Picture-in-Picture</span>
                                                </button>
                                            )}

                                            {/* "Why is this playing this way?" explainer (P2-WI-002) */}
                                            {currentPlan && (
                                                <button
                                                    type="button"
                                                    onClick={() => { setShowExplanation(true); setShowMoreMenu(false); }}
                                                    role="menuitem"
                                                    className="w-full flex items-center gap-3 px-4 py-2 text-sm text-left text-white hover:bg-white/10 transition-colors"
                                                    title="Why is this playing this way?"
                                                >
                                                    <svg className="w-5 h-5 text-white/70" fill="none" stroke="currentColor" strokeWidth="2" viewBox="0 0 24 24">
                                                        <circle cx="12" cy="12" r="10" />
                                                        <path strokeLinecap="round" d="M12 16v-4M12 8h.01" />
                                                    </svg>
                                                    <span>Why is this playing this way?</span>
                                                </button>
                                            )}
                                        </div>
                                    </div>
                                )}
                            </div>

                            {/* Fullscreen */}
                            <button
                                type="button"
                                onClick={toggleFullscreen}
                                aria-label={isFullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}
                                aria-pressed={isFullscreen}
                                className="text-white/70 hover:text-white transition-colors p-1.5 min-w-[44px] min-h-[44px] flex items-center justify-center rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                title="Fullscreen (F)"
                            >
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

                    {/* Keyboard shortcuts hint — lives inside the controls gradient now that the
                        immersive player has no page space below the video */}
                    <div className="hidden sm:block text-xs text-white/40 text-center mt-1.5 space-x-4">
                        <span>Space: Play/Pause</span>
                        <span>←/→: Seek ±10s</span>
                        <span>↑/↓: Volume</span>
                        <span>M: Mute</span>
                        <span>F: Fullscreen</span>
                    </div>
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

            {/* "Why is this playing this way?" explainer (P2-WI-002) */}
            {showExplanation && currentPlan && (
                <TranscodeExplanationModal plan={currentPlan} onClose={() => setShowExplanation(false)} />
            )}
        </div>
    );
}

/**
 * Premium in-player toast notification with circular progress timer and exit animations.
 */
function PlayerToast({ message, type, onDismiss }: { message: string, type: 'info' | 'success' | 'error', onDismiss: () => void }) {
    const [isExiting, setIsExiting] = useState(false);
    const isError = type === 'error';
    // Errors get red chrome + an alert role; info/success keep the existing blue.
    const boxClass = isError ? 'bg-red-500/20 border-red-500/40 text-red-50' : 'bg-blue-500/20 border-blue-500/40 text-blue-50';
    const swatchClass = isError ? 'bg-red-500/40' : 'bg-blue-500/40';
    const iconClass = isError ? 'text-red-200' : 'text-blue-200';

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
            <div role={isError ? 'alert' : 'status'} className={`px-4 py-2.5 rounded-xl shadow-2xl backdrop-blur-xl border ${boxClass} flex items-center gap-4 font-medium text-sm sm:text-base whitespace-nowrap`}>

                <div className="flex items-center gap-3">
                    <div className={`w-6 h-6 rounded-full ${swatchClass} flex items-center justify-center`}>
                        <svg className={`w-4 h-4 ${iconClass}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            {type === 'success' ? (
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                            ) : type === 'error' ? (
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z" />
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
                    type="button"
                    onClick={handleManualDismiss}
                    aria-label="Dismiss notification"
                    className="relative w-8 h-8 flex items-center justify-center hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-full transition-colors group"
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
