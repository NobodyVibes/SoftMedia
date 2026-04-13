import { useEffect, useRef, useState } from 'react';
import { useInView } from 'react-intersection-observer';

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
            {/* Actual image - only rendered when in viewport */}
            {inView && (
                <img
                    src={src}
                    alt={alt}
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
