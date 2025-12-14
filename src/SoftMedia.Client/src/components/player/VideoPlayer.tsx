import { useEffect, useRef, useState } from 'react';
import Hls from 'hls.js';
import { type MediaItem } from '../../types';
import { useAuthStore } from '../../store/authStore';

interface VideoPlayerProps {
    item: MediaItem;
    src: string;
}

/**
 * VideoPlayer component that handles Direct Play vs HLS Transcoding.
 * Uses native HTML5 video with hls.js for reliable cross-browser HLS support.
 */
export default function VideoPlayer({ item, src: initialSrc }: VideoPlayerProps) {
    const videoRef = useRef<HTMLVideoElement>(null);
    const hlsRef = useRef<Hls | null>(null);
    const [src, setSrc] = useState<string>('');
    const [isTranscoding, setIsTranscoding] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(true);
    const [currentTime, setCurrentTime] = useState(0);
    const token = useAuthStore((state) => state.token);

    // Get actual duration from media item metadata (in seconds)
    const getActualDuration = (): number => {
        if (!item.duration) return 0;
        if (typeof item.duration === 'number') return item.duration;
        // Parse "2h 15m" format or just return 0 if unparsable
        const match = item.duration.match(/(?:(\d+)h)?\s*(?:(\d+)m)?/);
        if (match) {
            const hours = parseInt(match[1] || '0', 10);
            const minutes = parseInt(match[2] || '0', 10);
            return hours * 3600 + minutes * 60;
        }
        return 0;
    };

    // Format seconds to "HH:MM:SS" or "MM:SS"
    const formatTime = (seconds: number): string => {
        if (!seconds || isNaN(seconds)) return '0:00';
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        if (h > 0) {
            return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        }
        return `${m}:${s.toString().padStart(2, '0')}`;
    };

    const actualDuration = getActualDuration();

    // Determine playback strategy based on container/codec
    useEffect(() => {
        if (!token) return;

        const container = item.container?.toLowerCase() || '';
        const videoCodec = item.videoCodec?.toLowerCase() || '';

        // Browsers natively support: MP4 (H.264/AAC), WebM (VP8/VP9/Opus), Ogg
        const directPlayContainers = ['mp4', 'webm', 'ogg', 'mov', 'm4v'];
        const directPlayCodecs = ['h264', 'avc', 'avc1', 'vp8', 'vp9', 'av1'];

        const isContainerSupported = directPlayContainers.includes(container);
        const isCodecSupported = !videoCodec || directPlayCodecs.some(c => videoCodec.includes(c));
        const needsTranscode = !isContainerSupported || !isCodecSupported;

        if (needsTranscode) {
            console.log(`Format ${container}/${videoCodec} not supported, switching to HLS transcoding.`);
            const hlsUrl = `/api/transcode/${item.id}/master.m3u8?token=${token}`;
            setSrc(hlsUrl);
            setIsTranscoding(true);
        } else {
            console.log(`Direct play supported for ${container}/${videoCodec}`);
            const directUrl = `${initialSrc}${initialSrc.includes('?') ? '&' : '?'}token=${token}`;
            setSrc(directUrl);
            setIsTranscoding(false);
        }
    }, [item, initialSrc, token]);

    // Setup HLS.js or native playback
    useEffect(() => {
        if (!src || !videoRef.current) return;

        const video = videoRef.current;
        setIsLoading(true);
        setError(null);

        // Cleanup previous HLS instance
        if (hlsRef.current) {
            hlsRef.current.destroy();
            hlsRef.current = null;
        }

        if (isTranscoding && src.includes('.m3u8')) {
            // HLS stream - use hls.js with retry configuration
            if (Hls.isSupported()) {
                const hls = new Hls({
                    debug: false,
                    enableWorker: true,
                    lowLatencyMode: false,
                    // Start from the beginning, not live edge
                    startPosition: 0,
                    liveSyncDurationCount: 0,
                    liveBackBufferLength: Infinity,
                    // Increase retries for transcoding startup time
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
                    video.play().catch(e => console.log('Autoplay prevented:', e));
                });

                hls.on(Hls.Events.ERROR, (_, data) => {
                    console.log('HLS Error:', data.type, data.details, data.fatal);

                    if (data.fatal) {
                        switch (data.type) {
                            case Hls.ErrorTypes.NETWORK_ERROR:
                                // Try to recover from network error
                                console.log('Attempting to recover from network error...');
                                hls.startLoad();
                                break;
                            case Hls.ErrorTypes.MEDIA_ERROR:
                                console.log('Attempting to recover from media error...');
                                hls.recoverMediaError();
                                break;
                            default:
                                console.error('Unrecoverable HLS error:', data);
                                setError(`Playback error: ${data.type}`);
                                hls.destroy();
                                break;
                        }
                    }
                });

                hlsRef.current = hls;
            } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
                // Native HLS support (Safari)
                video.src = src;
                video.addEventListener('loadedmetadata', () => {
                    setIsLoading(false);
                    video.play().catch(e => console.log('Autoplay prevented:', e));
                });
            } else {
                setError('HLS is not supported in this browser');
                setIsLoading(false);
            }
        } else {
            // Direct play - native video
            video.src = src;
            video.addEventListener('loadeddata', () => setIsLoading(false));
            video.load();
        }

        return () => {
            if (hlsRef.current) {
                hlsRef.current.destroy();
                hlsRef.current = null;
            }
        };
    }, [src, isTranscoding]);

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
            <div className="relative aspect-video bg-black rounded-xl overflow-hidden shadow-2xl">
                {isLoading && (
                    <div className="absolute inset-0 flex items-center justify-center bg-black/50 z-10">
                        <div className="text-white animate-pulse">
                            {isTranscoding ? 'Starting transcoding...' : 'Loading video...'}
                        </div>
                    </div>
                )}
                <video
                    ref={videoRef}
                    className="w-full h-full"
                    controls
                    playsInline
                    poster={item.posterPath || undefined}
                    onTimeUpdate={(e) => setCurrentTime(e.currentTarget.currentTime)}
                >
                    Your browser does not support the video tag.
                </video>
                {/* Custom duration display for transcoding (shows actual duration from metadata) */}
                {isTranscoding && actualDuration > 0 && !isLoading && (
                    <div className="absolute bottom-16 right-4 bg-black/80 text-white text-sm px-3 py-1 rounded-lg z-20 font-mono">
                        {formatTime(currentTime)} / {formatTime(actualDuration)}
                    </div>
                )}
            </div>
            {isTranscoding && (
                <div className="text-xs text-white/50 text-center mt-2">
                    Transcoding via FFmpeg (HLS) • Actual duration: {formatTime(actualDuration)}
                </div>
            )}
        </div>
    );
}
