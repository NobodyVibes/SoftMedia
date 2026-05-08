import { useEffect, useRef, useState } from 'react';
import { useInView } from 'react-intersection-observer';
import { attachAuthToApiUrl } from '../../lib/mediaImageUrl';
import { useAuthStore } from '../../store/authStore';

interface LoadingImageProps {
    src: string | null | undefined;
    alt: string;
    className?: string;
    fallback?: React.ReactNode;
    /** When defined, suppresses individual fade-in until true (for batch reveal). */
    groupReady?: boolean;
    /** Called when the image finishes loading (for batch counting). */
    onLoad?: () => void;
    /** Called when the image fails to load (for batch counting). */
    onError?: () => void;
}

export default function LoadingImage({
    src,
    alt,
    className = '',
    fallback,
    groupReady,
    onLoad,
    onError,
}: LoadingImageProps) {
    const { ref, inView } = useInView({ triggerOnce: true, rootMargin: '200px' });
    const [status, setStatus] = useState<'idle' | 'loaded' | 'error'>('idle');
    const offViewportSignaledRef = useRef(false);

    // Subscribe to the auth token. Without this, a silent token refresh
    // (driven by the axios refresh-token interceptor) does NOT trigger a
    // re-render here, so any <img> rendered before the refresh keeps its
    // stale token and 401s indefinitely. By selecting the token via Zustand
    // we re-render when it rotates and `attachAuthToApiUrl` below picks up
    // the fresh value.
    const token = useAuthStore((s) => s.token);

    // Reset the failure state when src or token changes — otherwise an image
    // that 401'd with a stale token stays in `status === 'error'` forever
    // and never retries with the refreshed token.
    useEffect(() => {
        setStatus('idle');
        offViewportSignaledRef.current = false;
    }, [src, token]);

    // Off-viewport auto-signal: if this slot never enters the viewport within
    // a brief window, tell the cascade coordinator to advance past it. The
    // image will still fade in individually when scrolled into view and loaded.
    // This keeps the cascade responsive while guaranteeing in-viewport items
    // reveal in strict left-to-right order.
    useEffect(() => {
        if (!onLoad || inView || offViewportSignaledRef.current) return;
        const t = setTimeout(() => {
            if (!offViewportSignaledRef.current) {
                offViewportSignaledRef.current = true;
                onLoad();
            }
        }, 120);
        return () => clearTimeout(t);
    }, [inView, onLoad]);

    if (!src || status === 'error') {
        return fallback ? <>{fallback}</> : null;
    }

    // In batch mode: visible when both loaded AND group is ready
    // In standalone mode (groupReady undefined): visible as soon as loaded
    const isVisible = status === 'loaded' && (groupReady === undefined || groupReady);

    return (
        <div ref={ref} className="relative w-full h-full overflow-hidden">
            {/* Skeleton placeholder - visible while loading */}
            {!isVisible && (
                <div className="absolute inset-0 bg-gradient-to-br from-gray-800 via-gray-700 to-gray-800 animate-pulse z-10" />
            )}
            {/* Actual image - only rendered when in viewport.
                Routes `src` through `attachAuthToApiUrl` so `/api/v1/*` paths
                carry a query-string access token (browsers can't attach
                Bearer headers to <img> loads), and sets referrerPolicy to
                prevent the access token leaking cross-origin via Referer. */}
            {inView && (
                <img
                    src={attachAuthToApiUrl(src)}
                    alt={alt}
                    referrerPolicy="no-referrer"
                    className={`${className} transition-opacity duration-200 ${isVisible ? 'opacity-100' : 'opacity-0'}`}
                    onLoad={() => {
                        setStatus('loaded');
                        // Only call cascade onLoad if we didn't already signal off-viewport
                        if (!offViewportSignaledRef.current) {
                            offViewportSignaledRef.current = true;
                            onLoad?.();
                        }
                    }}
                    onError={() => {
                        setStatus('error');
                        if (!offViewportSignaledRef.current) {
                            offViewportSignaledRef.current = true;
                            onError?.();
                        }
                    }}
                />
            )}
        </div>
    );
}
