import { useEffect, useMemo } from 'react';

/**
 * R-WI-015 — Media Session API integration (OS media controls / lock screen).
 *
 * One shared hook owns the arbitration problem: the persistent audio player and
 * the video player can both be alive in one tab, but `navigator.mediaSession`
 * is a single global. Rule: the session follows whatever most recently STARTED
 * PLAYING; a paused owner keeps the session (so lock-screen "resume" works)
 * until another player starts or it unmounts, at which point ownership falls
 * back to the most recent prior claimant (e.g. video page closes → music
 * controls return).
 *
 * Progressive enhancement: every browser call is feature-detected and wrapped
 * in try/catch — on browsers without the API the hook is a no-op.
 */

export interface MediaSessionTrackMetadata {
    title: string;
    artist?: string;
    album?: string;
    /** Already-resolved URL (auth token attached where needed). */
    artworkUrl?: string | null;
}

export interface MediaSessionHandlers {
    onPlay?: () => void;
    onPause?: () => void;
    onPreviousTrack?: () => void;
    onNextTrack?: () => void;
    onSeekBackward?: () => void;
    onSeekForward?: () => void;
    /** Receives the absolute target time in seconds (offset-aware for HLS — see VideoPlayer). */
    onSeekTo?: (time: number) => void;
}

export interface MediaSessionPosition {
    /** Real media duration in seconds (NOT the HLS element duration). May be 0/NaN/Infinity while loading — skipped until finite. */
    duration: number;
    /** Real playback position in seconds (currentTime + seekOffset for HLS). */
    position: number;
    playbackRate?: number;
}

export interface UseMediaSessionOptions {
    /** Register while true (player has content loaded). False = fully unregistered. */
    enabled: boolean;
    /** Drives ownership claims (rising edge) and playbackState. */
    isPlaying: boolean;
    /**
     * Identity of the loaded content (track/item id). A claim is re-asserted when
     * this changes while playing — an edge-triggered boolean alone misses "user
     * started a NEW track on an already-playing player" (review finding: picking a
     * song from the queue while a video owned the session never reclaimed).
     */
    contentId?: string | null;
    metadata: MediaSessionTrackMetadata | null;
    handlers: MediaSessionHandlers;
    position?: MediaSessionPosition | null;
}

const isSupported = () => typeof navigator !== 'undefined' && 'mediaSession' in navigator;

/** Action name ↔ handler key. Every entry is (re)bound on ownership change; absent handlers bind null. */
const ACTIONS: Array<[MediaSessionAction, keyof MediaSessionHandlers]> = [
    ['play', 'onPlay'],
    ['pause', 'onPause'],
    ['previoustrack', 'onPreviousTrack'],
    ['nexttrack', 'onNextTrack'],
    ['seekbackward', 'onSeekBackward'],
    ['seekforward', 'onSeekForward'],
    ['seekto', 'onSeekTo'],
];

interface PositionBaseline {
    at: number;
    position: number;
    rate: number;
    duration: number;
    playing: boolean;
}

interface Instance {
    /** Latest render snapshot — wrappers and applyAll always read through this. */
    current: UseMediaSessionOptions;
    /**
     * Last position snapshot PUSHED TO THE BROWSER by this instance. Lives on the
     * instance (not a hook ref) so applyAll can reset it on every ownership gain —
     * the drift throttle must never skip based on a baseline the browser no longer
     * holds (review finding: a stale baseline could swallow the first seek after
     * regaining ownership).
     */
    lastReport: PositionBaseline | null;
    applyAll: () => void;
}

// ---- module-level arbitration registry ----
let instances: Instance[] = [];   // registration order
let claimOrder: Instance[] = [];  // claim history; last entry has ownership priority
let appliedOwner: Instance | null = null;

function currentOwner(): Instance | null {
    return claimOrder[claimOrder.length - 1] ?? instances[instances.length - 1] ?? null;
}

function updateOwner(): void {
    const owner = currentOwner();
    if (owner === appliedOwner) return;
    appliedOwner = owner;
    if (owner) owner.applyAll();
    else clearSession();
}

function registerInstance(inst: Instance): void {
    if (!instances.includes(inst)) instances.push(inst);
    updateOwner();
}

function unregisterInstance(inst: Instance): void {
    instances = instances.filter(i => i !== inst);
    claimOrder = claimOrder.filter(i => i !== inst);
    updateOwner();
}

function claimInstance(inst: Instance): void {
    if (!instances.includes(inst)) return;
    claimOrder = claimOrder.filter(i => i !== inst);
    claimOrder.push(inst);
    updateOwner();
}

function isOwner(inst: Instance): boolean {
    return appliedOwner === inst;
}

function clearSession(): void {
    if (!isSupported()) return;
    const ms = navigator.mediaSession;
    try { ms.metadata = null; } catch { /* unsupported */ }
    try { ms.playbackState = 'none'; } catch { /* unsupported */ }
    try { ms.setPositionState?.(); } catch { /* unsupported */ }
    for (const [action] of ACTIONS) {
        try { ms.setActionHandler(action, null); } catch { /* action not recognized by this browser */ }
    }
}

/** Test-only: drop all registrants and forget the applied owner. Does not touch navigator. */
export function _resetMediaSessionRegistryForTests(): void {
    instances = [];
    claimOrder = [];
    appliedOwner = null;
}

// ---- per-concern appliers (owner only) ----

function applyMetadata(meta: MediaSessionTrackMetadata | null): void {
    if (!isSupported()) return;
    try {
        if (!meta || typeof MediaMetadata === 'undefined') {
            navigator.mediaSession.metadata = null;
            return;
        }
        navigator.mediaSession.metadata = new MediaMetadata({
            title: meta.title,
            artist: meta.artist ?? '',
            album: meta.album ?? '',
            artwork: meta.artworkUrl ? [{ src: meta.artworkUrl }] : [],
        });
    } catch { /* progressive enhancement — never break playback over metadata */ }
}

function applyPlaybackState(isPlaying: boolean): void {
    if (!isSupported()) return;
    try { navigator.mediaSession.playbackState = isPlaying ? 'playing' : 'paused'; } catch { /* unsupported */ }
}

function applyPositionState(pos: MediaSessionPosition | null | undefined): void {
    if (!isSupported() || typeof navigator.mediaSession.setPositionState !== 'function') return;
    try {
        if (!pos || !Number.isFinite(pos.duration) || pos.duration <= 0) {
            navigator.mediaSession.setPositionState();
            return;
        }
        navigator.mediaSession.setPositionState({
            duration: pos.duration,
            // Clamp: a mid-seek position beyond duration throws a TypeError.
            position: Math.min(Math.max(pos.position, 0), pos.duration),
            playbackRate: pos.playbackRate && pos.playbackRate > 0 ? pos.playbackRate : 1,
        });
    } catch { /* invalid transient state — next update corrects it */ }
}

function bindActions(inst: Instance): void {
    if (!isSupported()) return;
    const ms = navigator.mediaSession;
    for (const [action, key] of ACTIONS) {
        const has = typeof inst.current.handlers[key] === 'function';
        try {
            if (!has) {
                ms.setActionHandler(action, null);
            } else if (action === 'seekto') {
                // Stable wrapper reads the LATEST handler at call time — VideoPlayer's
                // handleSeekToTime is recreated every render (it closes over seekOffset).
                ms.setActionHandler('seekto', (details: MediaSessionActionDetails) => {
                    // Scrubber drags emit a burst of fastSeek intermediates before the
                    // final (non-fastSeek) event. For HLS video each far seek tears down
                    // and restarts the transcode — acting on intermediates causes a
                    // restart storm, so only the settled seek is honoured.
                    if (details.fastSeek) return;
                    const fn = inst.current.handlers.onSeekTo;
                    if (fn && typeof details.seekTime === 'number') fn(details.seekTime);
                });
            } else {
                ms.setActionHandler(action, () => { inst.current.handlers[key]?.(); });
            }
        } catch { /* this browser doesn't recognize the action name — skip it */ }
    }
}

export function useMediaSession(options: UseMediaSessionOptions): void {
    const inst = useMemo<Instance>(() => {
        const instance: Instance = {
            current: options,
            lastReport: null,
            applyAll: () => {
                bindActions(instance);
                applyMetadata(instance.current.metadata);
                applyPlaybackState(instance.current.isPlaying);
                applyPositionState(instance.current.position);
                // Re-baseline the drift throttle to exactly what the browser now holds.
                const pos = instance.current.position;
                instance.lastReport = pos && Number.isFinite(pos.duration) && pos.duration > 0
                    ? {
                        at: Date.now(),
                        position: pos.position,
                        rate: pos.playbackRate && pos.playbackRate > 0 ? pos.playbackRate : 1,
                        duration: pos.duration,
                        playing: instance.current.isPlaying,
                    }
                    : null;
            },
        };
        return instance;
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);
    inst.current = options;

    const { enabled, isPlaying, contentId, metadata, position } = options;

    // Registration lifecycle.
    useEffect(() => {
        if (!enabled) return;
        registerInstance(inst);
        return () => unregisterInstance(inst);
    }, [enabled, inst]);

    // Ownership follows whatever most recently started playing. contentId is a dep
    // so switching to a NEW track/item while already playing re-claims (the boolean
    // alone has no edge to fire on in that case).
    useEffect(() => {
        if (enabled && isPlaying) claimInstance(inst);
    }, [enabled, isPlaying, contentId, inst]);

    // Owner-only incremental updates (applyAll covered the claim moment).
    const title = metadata?.title;
    const artist = metadata?.artist;
    const album = metadata?.album;
    const artworkUrl = metadata?.artworkUrl;
    useEffect(() => {
        if (isOwner(inst)) applyMetadata(inst.current.metadata);
    }, [title, artist, album, artworkUrl, inst]);

    useEffect(() => {
        if (isOwner(inst)) applyPlaybackState(isPlaying);
    }, [isPlaying, inst]);

    // Handler PRESENCE can change mid-life (e.g. nexttrack appears once the next
    // episode is known) — rebind on presence-signature change, not on every render.
    const handlerSignature = ACTIONS
        .map(([action, key]) => (typeof options.handlers[key] === 'function' ? action : ''))
        .join(',');
    useEffect(() => {
        if (isOwner(inst)) bindActions(inst);
    }, [handlerSignature, inst]);

    // Position: the UA extrapolates position from the last setPositionState call,
    // so only re-report on discontinuities (seek/jump, rate, duration, play/pause)
    // rather than every timeupdate tick. The baseline lives on the instance so
    // applyAll can re-anchor it on every ownership gain.
    const posDuration = position?.duration;
    const posPosition = position?.position;
    const posRate = position?.playbackRate ?? 1;
    useEffect(() => {
        if (!isOwner(inst)) return;
        if (posDuration === undefined || posPosition === undefined) return;
        const now = Date.now();
        const last = inst.lastReport;
        if (last && last.duration === posDuration && last.rate === posRate && last.playing === isPlaying) {
            if (isPlaying) {
                const expected = last.position + ((now - last.at) / 1000) * last.rate;
                if (Math.abs(posPosition - expected) < 2) return; // UA is extrapolating correctly
            } else if (posPosition === last.position) {
                return; // paused and unmoved — nothing to correct
            }
            // Paused with ANY movement falls through: the UA does not extrapolate
            // while paused, so even a sub-2s nudge would otherwise display wrong
            // forever (review finding).
        }
        inst.lastReport = { at: now, position: posPosition, rate: posRate, duration: posDuration, playing: isPlaying };
        applyPositionState(inst.current.position);
    }, [posDuration, posPosition, posRate, isPlaying, inst]);
}
