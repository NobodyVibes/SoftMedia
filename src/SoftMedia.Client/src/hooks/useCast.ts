import { useEffect, useState, useCallback, useRef } from 'react';
import type { CastState, CastUnavailableReason } from './castReadiness';

/**
 * Google Cast Web Sender integration (P3-WI-001).
 *
 * The Cast SDK is loaded by the <script> tag in index.html (cannot use npm).
 * It announces itself via `window.__onGCastApiAvailable`; this hook registers
 * that callback on first mount, then initialises the framework against the
 * default media receiver. From there it exposes the current session state
 * plus `castNow(...)` / `stopCasting()` for the player to drive.
 *
 * The cast.framework / chrome.cast globals are typed as `any` because the
 * SDK ships no @types package — Google's TypeScript definitions are not on
 * DefinitelyTyped. Keeping the surface area narrow (only what the player
 * needs) limits the `any` blast radius.
 */

declare global {
    interface Window {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        __onGCastApiAvailable?: (available: boolean) => void;
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        cast?: any;
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        chrome?: any;
    }
}

export interface CastMediaInfo {
    /** Absolute URL the receiver will fetch. MUST already carry the JWT (?token=). */
    streamUrl: string;
    /** MIME type — application/vnd.apple.mpegurl for HLS, video/mp4 for direct play. */
    contentType: string;
    title: string;
    subtitle?: string;
    posterUrl?: string;
}

interface UseCastResult {
    /** True once the SDK has initialised. False on browsers without Cast support (Firefox/Safari). */
    isCastAvailable: boolean;
    /** True while a cast session is active. */
    isCasting: boolean;
    /** Friendly name of the receiver currently casting to, if any. */
    receiverName: string | null;
    /** Device-discovery state from the SDK: 'no-devices' means no Cast receivers on the LAN. */
    castState: CastState;
    /** Whether the page is a secure context (HTTPS or localhost). */
    isSecureContext: boolean;
    /** Why casting is unavailable, when it is (for the readiness diagnostics). */
    castUnavailableReason: CastUnavailableReason;
    /** Opens the receiver chooser, starts a session if needed, and loads the media. */
    castNow: (info: CastMediaInfo) => Promise<void>;
    /** Tears down the active cast session. No-op when not casting. */
    stopCasting: () => void;
}

let frameworkInitialised = false;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function mapCastState(w: any, sdkState: unknown): CastState {
    const CS = w?.cast?.framework?.CastState ?? {};
    if (sdkState === CS.NO_DEVICES_AVAILABLE) return 'no-devices';
    if (sdkState === CS.CONNECTED) return 'connected';
    if (sdkState === CS.CONNECTING) return 'connecting';
    if (sdkState === CS.NOT_CONNECTED) return 'available';
    return 'unknown';
}

export function useCast(): UseCastResult {
    const [isCastAvailable, setIsCastAvailable] = useState(false);
    const [isCasting, setIsCasting] = useState(false);
    const [receiverName, setReceiverName] = useState<string | null>(null);
    const [castState, setCastState] = useState<CastState>('unknown');
    // SDK reported it can't run (e.g. Firefox/Safari). Insecure-context is derived separately.
    const [sdkUnsupported, setSdkUnsupported] = useState(false);
    const isSecureContext = typeof window !== 'undefined' ? window.isSecureContext : false;
    const sessionListenerRef = useRef<((event: unknown) => void) | null>(null);
    const stateListenerRef = useRef<((event: unknown) => void) | null>(null);

    useEffect(() => {
        // The Cast SDK may load before or after this component mounts. Handle both:
        // if cast.framework is already on window, init immediately; otherwise hook
        // __onGCastApiAvailable and wait.
        const tryInit = () => {
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            const w = window as any;
            if (!w.cast?.framework || !w.chrome?.cast) return false;

            if (!frameworkInitialised) {
                try {
                    w.cast.framework.CastContext.getInstance().setOptions({
                        // Google's official default media receiver ID. Plays HLS + MP4
                        // out-of-the-box without requiring a custom receiver app.
                        receiverApplicationId: w.chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
                        // Auto-rejoin a session if one was already running.
                        autoJoinPolicy: w.chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED,
                    });
                    frameworkInitialised = true;
                } catch (e) {
                    console.warn('[Cast] init failed', e);
                    return false;
                }
            }

            setIsCastAvailable(true);

            // Listen for session state changes.
            const context = w.cast.framework.CastContext.getInstance();
            const SessionStateEventType = w.cast.framework.CastContextEventType.SESSION_STATE_CHANGED;
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            const handler = (event: any) => {
                const state = event.sessionState;
                const SS = w.cast.framework.SessionState;
                const active = state === SS.SESSION_STARTED || state === SS.SESSION_RESUMED;
                setIsCasting(active);
                if (active) {
                    const session = context.getCurrentSession();
                    setReceiverName(session?.getCastDevice?.()?.friendlyName ?? null);
                } else if (state === SS.SESSION_ENDED) {
                    setReceiverName(null);
                }
            };
            context.addEventListener(SessionStateEventType, handler);
            sessionListenerRef.current = handler;

            // Track device discovery so the readiness UI can say "no Cast devices found"
            // (e.g. when the only TV on the network is an LG/Samsung that isn't a receiver).
            const CastStateEventType = w.cast.framework.CastContextEventType.CAST_STATE_CHANGED;
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            const stateHandler = (event: any) => setCastState(mapCastState(w, event.castState));
            context.addEventListener(CastStateEventType, stateHandler);
            stateListenerRef.current = stateHandler;
            setCastState(mapCastState(w, context.getCastState?.()));

            // Reflect any session already in progress (e.g. page reload).
            const existing = context.getCurrentSession();
            if (existing) {
                setIsCasting(true);
                setReceiverName(existing.getCastDevice?.()?.friendlyName ?? null);
            }
            return true;
        };

        if (!tryInit()) {
            const prev = window.__onGCastApiAvailable;
            window.__onGCastApiAvailable = (available: boolean) => {
                prev?.(available);
                if (available) tryInit();
                else setSdkUnsupported(true); // browser/page can't run Cast
            };
        }

        return () => {
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            const w = window as any;
            const ctx = w.cast?.framework?.CastContext?.getInstance?.();
            const evtTypes = w.cast?.framework?.CastContextEventType;
            if (ctx && evtTypes?.SESSION_STATE_CHANGED && sessionListenerRef.current) {
                try { ctx.removeEventListener(evtTypes.SESSION_STATE_CHANGED, sessionListenerRef.current); } catch { /* SDK teardown is best-effort */ }
            }
            if (ctx && evtTypes?.CAST_STATE_CHANGED && stateListenerRef.current) {
                try { ctx.removeEventListener(evtTypes.CAST_STATE_CHANGED, stateListenerRef.current); } catch { /* best-effort */ }
            }
        };
    }, []);

    // Derived: why is casting unavailable? An insecure (non-localhost HTTP) context disables
    // the Cast API in Chrome; otherwise the SDK itself may report unsupported.
    const castUnavailableReason: CastUnavailableReason = isCastAvailable
        ? null
        : (!isSecureContext ? 'insecure-context' : (sdkUnsupported ? 'no-sdk' : null));

    const castNow = useCallback(async (info: CastMediaInfo) => {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const w = window as any;
        if (!w.cast?.framework || !w.chrome?.cast) {
            throw new Error('Cast SDK not loaded.');
        }
        const context = w.cast.framework.CastContext.getInstance();

        // Open the receiver chooser if no session is active yet. Remember whether we were the
        // one who started it, so we can tear it back down if loading the media then fails.
        const hadSession = !!context.getCurrentSession();
        if (!hadSession) {
            try {
                await context.requestSession();
            } catch (e) {
                // User cancelled the chooser, or no receivers found. Treat as no-op.
                console.info('[Cast] requestSession aborted', e);
                return;
            }
        }

        const session = context.getCurrentSession();
        if (!session) return;

        const mediaInfo = new w.chrome.cast.media.MediaInfo(info.streamUrl, info.contentType);
        mediaInfo.metadata = new w.chrome.cast.media.GenericMediaMetadata();
        mediaInfo.metadata.title = info.title;
        if (info.subtitle) mediaInfo.metadata.subtitle = info.subtitle;
        if (info.posterUrl) {
            mediaInfo.metadata.images = [new w.chrome.cast.Image(info.posterUrl)];
        }

        const request = new w.chrome.cast.media.LoadRequest(mediaInfo);
        try {
            await session.loadMedia(request);
        } catch (e) {
            console.error('[Cast] loadMedia failed', e);
            // Don't strand the user on a connected-but-idle receiver. If we opened this
            // session just to load this media, end it so the UI returns to "not casting".
            if (!hadSession) {
                try { session.endSession(true); } catch { /* best-effort teardown */ }
            }
            throw e;
        }
    }, []);

    const stopCasting = useCallback(() => {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const w = window as any;
        const session = w.cast?.framework?.CastContext?.getInstance?.()?.getCurrentSession?.();
        // `true` here = stop the receiver app, not just disconnect the sender.
        if (session) session.endSession(true);
    }, []);

    return { isCastAvailable, isCasting, receiverName, castState, isSecureContext, castUnavailableReason, castNow, stopCasting };
}
