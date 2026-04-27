import { useEffect, useRef, type CSSProperties } from 'react';
import ePub from 'epubjs';
import type { Book, NavItem, Rendition } from 'epubjs';

/**
 * Drives epub.js directly. Replaces react-reader, which
 *   - couldn't expose its hardcoded `trackMouse: true` swipe overlay,
 *   - double-fired arrow-key nav on its own document keyup handler,
 *   - required a remount key to react to epubOptions changes, and
 *   - left epub.js's default 50 ms window-resize throttle in place, which
 *     races against the async display queue during a slow drag-resize and
 *     pins `rendition.location` at the chapter-start CFI (the "resize snaps
 *     back to the start of the chapter" bug).
 *
 * Resize is handled here by unbinding epub.js's internal listener and driving
 * `rendition.resize(w, h)` from a debounced ResizeObserver on our container —
 * guaranteeing exactly one resize per drag-settle.
 */
export interface EpubViewProps {
    /** URL of the EPUB. Changing this re-initialises the book. */
    url: string;
    /**
     * Target location on mount (and any external nav thereafter). For the common
     * resume flow the host fetches saved progress async and then sets this —
     * initial display waits for `book.loaded.navigation` so the final value is
     * usually in hand by then.
     */
    location?: string | number;
    onLocationChange?: (cfi: string) => void;
    onTocChange?: (toc: NavItem[]) => void;
    /** Invoked once after the rendition is created, for host-side wiring. */
    onRenditionReady?: (rendition: Rendition) => void;
    /** Keyup events from inside the content iframe (where window-level keydown
     *  can't reach). */
    onIframeKeyUp?: (event: KeyboardEvent) => void;
    /** Forwarded to `book.renderTo`. Captured at mount only — use a parent
     *  `key` to re-create for spread/direction toggles. */
    epubOptions?: Record<string, unknown>;
    style?: CSSProperties;
    className?: string;
}

const RESIZE_DEBOUNCE_MS = 200;

interface EpubStage {
    resizeFunc?: EventListener;
}
interface EpubManager {
    stage?: EpubStage;
}
interface RenditionInternal {
    manager?: EpubManager;
}

export default function EpubView({
    url,
    location,
    onLocationChange,
    onTocChange,
    onRenditionReady,
    onIframeKeyUp,
    epubOptions,
    style,
    className,
}: EpubViewProps) {
    const containerRef = useRef<HTMLDivElement | null>(null);
    const renditionRef = useRef<Rendition | null>(null);
    const lastReportedRef = useRef<string | null>(null);

    // Pin callbacks in refs so the init effect stays bound to `url` only —
    // parent-render churn must not destroy-and-recreate the book.
    const onLocationChangeRef = useRef(onLocationChange);
    const onTocChangeRef = useRef(onTocChange);
    const onRenditionReadyRef = useRef(onRenditionReady);
    const onIframeKeyUpRef = useRef(onIframeKeyUp);
    const locationRef = useRef(location);
    const epubOptionsRef = useRef(epubOptions);
    useEffect(() => { onLocationChangeRef.current = onLocationChange; }, [onLocationChange]);
    useEffect(() => { onTocChangeRef.current = onTocChange; }, [onTocChange]);
    useEffect(() => { onRenditionReadyRef.current = onRenditionReady; }, [onRenditionReady]);
    useEffect(() => { onIframeKeyUpRef.current = onIframeKeyUp; }, [onIframeKeyUp]);
    useEffect(() => { locationRef.current = location; }, [location]);

    useEffect(() => {
        const el = containerRef.current;
        if (!el) return;

        let cancelled = false;
        let book: Book | null = null;
        let rendition: Rendition | null = null;
        let observer: ResizeObserver | null = null;
        let debounceTimer: number | null = null;
        let lastAppliedSize = { width: 0, height: 0 };

        try {
            book = ePub(url, { openAs: 'epub' });
        } catch {
            return;
        }

        rendition = book.renderTo(el, {
            width: '100%',
            height: '100%',
            ...epubOptionsRef.current,
        });
        renditionRef.current = rendition;

        rendition.on('locationChanged', (loc: unknown) => {
            const cfi = extractCfi(loc);
            if (!cfi) return;
            lastReportedRef.current = cfi;
            onLocationChangeRef.current?.(cfi);
        });

        rendition.on('keyup', (event: KeyboardEvent) => {
            onIframeKeyUpRef.current?.(event);
        });

        onRenditionReadyRef.current?.(rendition);

        // Initial display is deferred to navigation-ready so the host's async
        // progress fetch usually has time to land the saved CFI into
        // `location`. react-reader sequenced the same way.
        book.loaded.navigation
            .then(({ toc }) => {
                if (cancelled || !rendition) return;
                onTocChangeRef.current?.(toc);
                const initial = locationRef.current;
                const target = typeof initial === 'string' && initial.length > 0
                    ? initial
                    : undefined;
                const promise = target !== undefined
                    ? rendition.display(target)
                    : rendition.display();
                if (target !== undefined) {
                    lastReportedRef.current = target;
                }
                promise.catch(() => { /* display errors are non-fatal */ });
            })
            .catch(() => { /* navigation load failure is non-fatal */ });

        // Resize pipeline. Wait for `rendition.started` so the manager/stage
        // wiring is in place, then unbind epub.js's 50 ms window.resize
        // throttle and drive one debounced rendition.resize per drag-settle.
        rendition.started
            .then(() => {
                if (cancelled || !rendition) return;

                const internal = rendition as unknown as RenditionInternal;
                const stage = internal.manager?.stage;
                if (stage?.resizeFunc) {
                    window.removeEventListener('resize', stage.resizeFunc);
                    stage.resizeFunc = undefined;
                }

                if (typeof ResizeObserver === 'undefined') return;

                observer = new ResizeObserver((entries) => {
                    const entry = entries[0];
                    if (!entry) return;
                    const width = Math.round(entry.contentRect.width);
                    const height = Math.round(entry.contentRect.height);
                    if (width <= 0 || height <= 0) return;
                    if (width === lastAppliedSize.width
                        && height === lastAppliedSize.height) return;

                    if (debounceTimer !== null) {
                        window.clearTimeout(debounceTimer);
                    }
                    debounceTimer = window.setTimeout(() => {
                        debounceTimer = null;
                        if (cancelled || !rendition) return;
                        if (width === lastAppliedSize.width
                            && height === lastAppliedSize.height) return;
                        lastAppliedSize = { width, height };
                        try {
                            rendition.resize(width, height);
                        } catch {
                            // Some epub.js builds throw if resize fires before
                            // the first view is rendered. The next observer
                            // tick will retry.
                        }
                    }, RESIZE_DEBOUNCE_MS);
                });
                observer.observe(el);
            })
            .catch(() => { /* rendition.started rejects if the book fails to open */ });

        return () => {
            cancelled = true;
            if (debounceTimer !== null) window.clearTimeout(debounceTimer);
            observer?.disconnect();
            observer = null;
            try { rendition?.destroy(); } catch { /* best-effort */ }
            try { book?.destroy(); } catch { /* best-effort */ }
            rendition = null;
            book = null;
            renditionRef.current = null;
            lastReportedRef.current = null;
        };
        // epubOptionsRef is captured at mount; parent remounts via `key` for
        // spread/direction changes. Only `url` triggers re-init.
    }, [url]);

    // External navigation: when `location` changes to something other than the
    // CFI we most recently reported, treat it as a display request (resume,
    // TOC click from a consumer that doesn't hold the rendition, etc.).
    useEffect(() => {
        const rendition = renditionRef.current;
        if (!rendition) return;
        if (typeof location !== 'string' || location.length === 0) return;
        if (location === lastReportedRef.current) return;
        lastReportedRef.current = location;
        rendition.display(location).catch(() => { /* ignore */ });
    }, [location]);

    return (
        <div
            ref={containerRef}
            data-testid="epub-reader"
            className={className}
            style={{ width: '100%', height: '100%', ...style }}
        />
    );
}

function extractCfi(loc: unknown): string | null {
    if (typeof loc === 'string') return loc;
    if (typeof loc === 'object' && loc !== null && 'start' in loc) {
        const start = (loc as { start: unknown }).start;
        if (typeof start === 'string') return start;
    }
    return null;
}
