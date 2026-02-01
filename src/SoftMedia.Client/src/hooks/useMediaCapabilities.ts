import { useState, useEffect } from 'react';

/**
 * Client capabilities for stream negotiation.
 * Sent to the backend to determine optimal playback strategy.
 */
export interface ClientCapabilities {
    /** Video codecs the client can decode (e.g., "h264", "hevc", "av1", "vp9") */
    videoCodecs: string[];
    /** Audio codecs the client can decode (e.g., "aac", "ac3", "eac3", "opus") */
    audioCodecs: string[];
    /** Maximum audio channels the client supports (2 = stereo, 6 = 5.1, 8 = 7.1) */
    maxAudioChannels: number;
    /** Whether the client supports HDR playback (Display + Codec) */
    supportsHdr: boolean;
    /** Whether the display hardware reports HDR support */
    displaySupportsHdr?: boolean;
    /** Whether the browser has software support for HDR codecs */
    codecSupportsHdr?: boolean;
    /** Maximum bitrate the client can handle (in kbps). 0 = unlimited */
    maxBitrate: number;
    /** Maximum resolution height the client prefers (e.g., 720, 1080, 2160). 0 = original */
    maxResolution: number;
    /** Subtitle formats the client supports (e.g., "vtt", "ass") */
    supportedSubtitleFormats: string[];
    /** Container formats the client supports (e.g., "mp4", "webm", "hls") */
    supportedContainers: string[];
    /** User's requested quality from player UI (e.g., "auto", "720p", "1080p", "4k", "original") */
    requestedQuality?: string;
    /** Index of the subtitle track to be burned in (if any) */
    subtitleTrackIndex?: number | null;
    /** Unique identifier for this specific playback stream */
    streamId?: string;
}


/**
 * Check if MediaSource supports a specific MIME type.
 * This is the most reliable way to detect codec support in browsers.
 */
function checkMediaSourceSupport(mimeType: string): boolean {
    if (typeof MediaSource === 'undefined') return false;
    return MediaSource.isTypeSupported(mimeType);
}

/**
 * Detect video codec support using MediaSource API.
 */
function detectVideoCodecs(): string[] {
    const codecs: string[] = [];

    // H.264 (AVC) - Universal support
    if (checkMediaSourceSupport('video/mp4; codecs="avc1.42E01E"') ||
        checkMediaSourceSupport('video/mp4; codecs="avc1.4D401E"') ||
        checkMediaSourceSupport('video/mp4; codecs="avc1.64001E"')) {
        codecs.push('h264');
    }

    // HEVC (H.265) - Safari, Edge, some Chrome versions
    if (checkMediaSourceSupport('video/mp4; codecs="hvc1.1.6.L93.B0"') ||
        checkMediaSourceSupport('video/mp4; codecs="hev1.1.6.L93.B0"')) {
        codecs.push('hevc');
    }

    // VP9 - Chrome, Firefox, Edge
    if (checkMediaSourceSupport('video/webm; codecs="vp9"') ||
        checkMediaSourceSupport('video/webm; codecs="vp09.00.10.08"')) {
        codecs.push('vp9');
    }

    // AV1 - Modern Chrome, Firefox
    if (checkMediaSourceSupport('video/mp4; codecs="av01.0.08M.08"') ||
        checkMediaSourceSupport('video/webm; codecs="av01.0.04M.08"')) {
        codecs.push('av1');
    }

    // VP8 - Legacy WebM
    if (checkMediaSourceSupport('video/webm; codecs="vp8"')) {
        codecs.push('vp8');
    }

    // Ensure at least H.264 is present (fallback)
    if (codecs.length === 0) {
        codecs.push('h264');
    }

    return codecs;
}

/**
 * Detect audio codec support using MediaSource API.
 */
function detectAudioCodecs(): string[] {
    const codecs: string[] = [];

    // AAC - Universal
    if (checkMediaSourceSupport('audio/mp4; codecs="mp4a.40.2"')) {
        codecs.push('aac');
    }

    // AC-3 (Dolby Digital) - Safari, Edge
    if (checkMediaSourceSupport('audio/mp4; codecs="ac-3"')) {
        codecs.push('ac3');
    }

    // E-AC-3 (Dolby Digital Plus) - Safari, Edge
    if (checkMediaSourceSupport('audio/mp4; codecs="ec-3"')) {
        codecs.push('eac3');
    }

    // Opus - Chrome, Firefox, Edge
    if (checkMediaSourceSupport('audio/webm; codecs="opus"')) {
        codecs.push('opus');
    }

    // Vorbis - Chrome, Firefox
    if (checkMediaSourceSupport('audio/webm; codecs="vorbis"')) {
        codecs.push('vorbis');
    }

    // MP3 - Universal
    if (checkMediaSourceSupport('audio/mpeg') || checkMediaSourceSupport('audio/mp4; codecs="mp3"')) {
        codecs.push('mp3');
    }

    // FLAC - Chrome, Edge
    if (checkMediaSourceSupport('audio/flac')) {
        codecs.push('flac');
    }

    // Ensure at least AAC is present (fallback)
    if (codecs.length === 0) {
        codecs.push('aac');
    }

    return codecs;
}

/**
 * Detect HDR support using CSS color-gamut media query.
 * Note: This checks display capability, not video decode capability.
 */
function detectHdrDetails(): { displaySupportsHdr: boolean, codecSupportsHdr: boolean } {
    // 1. Check Display Capabilities
    let displaySupportsHdr = false;
    if (typeof window.matchMedia === 'function') {
        // Modern standard for detecting HDR display support
        if (window.matchMedia('(video-dynamic-range: high)').matches) {
            displaySupportsHdr = true;
        }
        // Wide color gamut (P3 or Rec2020) - often synonymous with HDR capability
        else if (window.matchMedia('(color-gamut: p3)').matches || window.matchMedia('(color-gamut: rec2020)').matches) {
            displaySupportsHdr = true;
        }
    }

    // 2. Check Codec Capabilities (Software Check)
    // Even if the screen is HDR, the browser must be able to decode at least one HDR format.
    const codecSupportsHdr =
        checkMediaSourceSupport('video/mp4; codecs="hvc1.2.4.L153.B0"') || // HEVC 10-bit
        checkMediaSourceSupport('video/mp4; codecs="av01.0.09M.10"') ||    // AV1 10-bit
        checkMediaSourceSupport('video/webm; codecs="vp09.02.51.10.01.09.16.09.01"'); // VP9 Profile 2

    return { displaySupportsHdr, codecSupportsHdr };
}

/**
 * Detect specific HDR formats supported by the client.
 * Returns an array of supported formats like ["hdr10", "hlg"].
 */
function detectHdrFormats(): string[] {
    const formats: string[] = [];

    // HDR10 via HEVC Main 10
    if (checkMediaSourceSupport('video/mp4; codecs="hvc1.2.4.L153.B0"')) {
        formats.push('hdr10');
    }

    // HLG via HEVC
    if (checkMediaSourceSupport('video/mp4; codecs="hvc1.2.4.L150.B0"')) {
        formats.push('hlg');
    }

    // HDR10 via AV1
    if (checkMediaSourceSupport('video/mp4; codecs="av01.0.09M.10"')) {
        if (!formats.includes('hdr10')) {
            formats.push('hdr10');
        }
    }

    return formats;
}

/**
 * Detect maximum audio channels.
 * Most browsers support stereo (2), some support surround.
 */
function detectMaxAudioChannels(): number {
    // Check for 5.1 surround via AC-3/E-AC-3 support
    if (checkMediaSourceSupport('audio/mp4; codecs="ac-3"') ||
        checkMediaSourceSupport('audio/mp4; codecs="ec-3"')) {
        return 6; // 5.1 surround
    }

    // Safari with AAC can do 5.1
    if (navigator.userAgent.includes('Safari') && !navigator.userAgent.includes('Chrome')) {
        return 6;
    }

    return 2; // Stereo fallback
}

/**
 * Detect supported container formats.
 */
function detectContainers(): string[] {
    const containers: string[] = [];

    if (checkMediaSourceSupport('video/mp4; codecs="avc1.42E01E"')) {
        containers.push('mp4');
    }
    if (checkMediaSourceSupport('video/webm; codecs="vp9"') ||
        checkMediaSourceSupport('video/webm; codecs="vp8"')) {
        containers.push('webm');
    }
    // HLS is supported via hls.js or native (Safari)
    containers.push('hls');

    if (containers.length === 0) {
        containers.push('mp4');
    }

    return containers;
}

/**
 * Hook to detect browser media capabilities.
 * Returns a ClientCapabilities object suitable for sending to the backend.
 */
export function useMediaCapabilities(): {
    capabilities: ClientCapabilities;
    isDetecting: boolean;
} {
    const [capabilities, setCapabilities] = useState<ClientCapabilities>({
        videoCodecs: ['h264'],
        audioCodecs: ['aac'],
        maxAudioChannels: 2,
        supportsHdr: false,
        maxBitrate: 0,
        maxResolution: 0,
        supportedSubtitleFormats: ['vtt'],
        supportedContainers: ['mp4', 'hls'],
    });
    const [isDetecting, setIsDetecting] = useState(true);

    useEffect(() => {
        // Run detection
        const detect = () => {
            const { displaySupportsHdr, codecSupportsHdr } = detectHdrDetails();
            const detected: ClientCapabilities = {
                videoCodecs: detectVideoCodecs(),
                audioCodecs: detectAudioCodecs(),
                maxAudioChannels: detectMaxAudioChannels(),
                supportsHdr: displaySupportsHdr && codecSupportsHdr,
                displaySupportsHdr,
                codecSupportsHdr,
                maxBitrate: 0, // 0 = unlimited, user/settings can override
                maxResolution: 0, // 0 = original, user/settings can override
                supportedSubtitleFormats: ['vtt'], // WebVTT is universally supported
                supportedContainers: detectContainers(),
            };

            // Log detected HDR formats for debugging
            const hdrFormats = detectHdrFormats();
            console.log('[MediaCapabilities] Detected:', detected);
            console.log('[MediaCapabilities] HDR formats:', hdrFormats.length > 0 ? hdrFormats : 'none');
            setCapabilities(detected);
            setIsDetecting(false);
        };

        // Small delay to ensure DOM is ready
        const timeoutId = setTimeout(detect, 100);
        return () => clearTimeout(timeoutId);
    }, []);

    return { capabilities, isDetecting };
}

/**
 * Create capabilities object with user-specified overrides.
 */
export function createCapabilitiesWithOverrides(
    baseCapabilities: ClientCapabilities,
    overrides: Partial<Pick<ClientCapabilities, 'maxBitrate' | 'maxResolution' | 'requestedQuality' | 'subtitleTrackIndex' | 'streamId'>>
): ClientCapabilities {
    return {
        ...baseCapabilities,
        maxBitrate: overrides.maxBitrate ?? baseCapabilities.maxBitrate,
        maxResolution: overrides.maxResolution ?? baseCapabilities.maxResolution,
        requestedQuality: overrides.requestedQuality ?? baseCapabilities.requestedQuality,
        subtitleTrackIndex: overrides.subtitleTrackIndex ?? baseCapabilities.subtitleTrackIndex,
        streamId: overrides.streamId ?? baseCapabilities.streamId,
    };
}


export default useMediaCapabilities;
