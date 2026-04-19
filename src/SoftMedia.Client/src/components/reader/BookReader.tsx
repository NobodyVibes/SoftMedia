import { useState, useEffect, useRef, useCallback } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import { ReactReader, ReactReaderStyle, type IReactReaderStyle } from 'react-reader';
import type { Rendition, NavItem, Location } from 'epubjs';
import { ChevronLeft, ChevronRight, X } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { API_URL } from '../../services/api';
import {
    getBookInfo,
    getBookPageUrl,
    getProgress,
    updateProgress,
    type BookInfo,
} from '../../services/bookService';
import { useAuthStore } from '../../store/authStore';
import type { MediaItem } from '../../types';

// Setup PDF worker
pdfjs.GlobalWorkerOptions.workerSrc = new URL(
    'pdfjs-dist/build/pdf.worker.min.mjs',
    import.meta.url,
).toString();

const PROGRESS_SAVE_DEBOUNCE_MS = 1000;

// Tailwind-equivalent colour tokens the reader uses (kept in JS-space so we can
// both pass them to react-reader and inject them into the epub.js iframe).
const READER_BG = '#111827';        // bg-gray-900
const READER_TEXT = '#e5e7eb';      // text-gray-200
const READER_LINK = '#60a5fa';      // text-blue-400

// Override react-reader's default chrome so the EPUB frame matches the rest of
// SoftMedia's dark reader. We hide the library's title strip, TOC button, and
// default arrow buttons — our own header and PageControls replace them — and
// flatten the background so the EPUB content area blends into the app frame.
const unifiedReaderStyles: IReactReaderStyle = {
    ...ReactReaderStyle,
    titleArea: { ...ReactReaderStyle.titleArea, display: 'none' },
    tocButton: { ...ReactReaderStyle.tocButton, display: 'none' },
    tocArea: { ...ReactReaderStyle.tocArea, display: 'none' },
    tocBackground: { ...ReactReaderStyle.tocBackground, display: 'none' },
    prev: { ...ReactReaderStyle.prev, display: 'none' },
    next: { ...ReactReaderStyle.next, display: 'none' },
    reader: { ...ReactReaderStyle.reader, top: 0, bottom: 72, left: 0, right: 0 },
    readerArea: { ...ReactReaderStyle.readerArea, backgroundColor: READER_BG },
};

interface BookReaderProps {
    item: MediaItem;
}

export default function BookReader({ item }: BookReaderProps) {
    const navigate = useNavigate();
    const token = useAuthStore(s => s.token);

    const ext = (item.path?.split('.').pop() ?? '').toLowerCase();
    const isPdf = ext === 'pdf';
    const isEpub = ext === 'epub';
    const isCbz = ext === 'cbz';

    const fileUrl = `${API_URL}/stream/${item.id}${token ? `?token=${encodeURIComponent(token)}` : ''}`;

    // Shared page state for PDF and CBZ
    const [pageNumber, setPageNumber] = useState<number>(1);
    const [numPages, setNumPages] = useState<number>(0);
    const [initialPageLoaded, setInitialPageLoaded] = useState<boolean>(false);

    // EPUB uses an opaque CFI location
    const [location, setLocation] = useState<string | number>(0);
    const [initialLocationLoaded, setInitialLocationLoaded] = useState<boolean>(false);

    // EPUB progress tracking (chapter + percentage), updated from rendition events
    const [currentChapter, setCurrentChapter] = useState<string | null>(null);
    const [percentage, setPercentage] = useState<number>(0);
    const renditionRef = useRef<Rendition | null>(null);
    const tocRef = useRef<NavItem[]>([]);

    // CBZ metadata (page count from backend)
    const [bookInfo, setBookInfo] = useState<BookInfo | null>(null);

    // Debounce handle for progress writes
    const saveTimer = useRef<number | null>(null);

    // --- Load initial progress + CBZ info on mount ---
    useEffect(() => {
        let cancelled = false;

        (async () => {
            try {
                const [progress, info] = await Promise.all([
                    getProgress(item.id).catch(() => null),
                    isCbz ? getBookInfo(item.id).catch(() => null) : Promise.resolve(null),
                ]);

                if (cancelled) return;

                if (info) {
                    setBookInfo(info);
                    if (info.pageCount) setNumPages(info.pageCount);
                }

                if (progress) {
                    if ((isPdf || isCbz) && progress.position > 0) {
                        setPageNumber(Math.max(1, Math.floor(progress.position)));
                    }
                    if (isEpub && progress.bookLocation) {
                        setLocation(progress.bookLocation);
                    }
                }
            } finally {
                if (!cancelled) {
                    setInitialPageLoaded(true);
                    setInitialLocationLoaded(true);
                }
            }
        })();

        return () => {
            cancelled = true;
        };
    }, [item.id, isPdf, isCbz, isEpub]);

    // --- Debounced progress save ---
    const scheduleSave = useCallback((position: number, bookLocation?: string | null) => {
        if (saveTimer.current !== null) {
            window.clearTimeout(saveTimer.current);
        }
        saveTimer.current = window.setTimeout(() => {
            updateProgress(item.id, position, bookLocation ?? null).catch(() => {
                // Silent failure — next page turn will try again.
            });
        }, PROGRESS_SAVE_DEBOUNCE_MS);
    }, [item.id]);

    // Flush pending save on unmount.
    useEffect(() => {
        return () => {
            if (saveTimer.current !== null) {
                window.clearTimeout(saveTimer.current);
            }
        };
    }, []);

    // --- PDF handlers ---
    const onPdfLoaded = useCallback(({ numPages: n }: { numPages: number }) => {
        setNumPages(n);
    }, []);

    // --- Page navigation (shared PDF + CBZ) ---
    const changePage = useCallback((offset: number) => {
        setPageNumber(prev => {
            const next = Math.min(Math.max(1, prev + offset), numPages || Infinity);
            if (next !== prev && initialPageLoaded) {
                scheduleSave(next);
            }
            return next;
        });
    }, [numPages, scheduleSave, initialPageLoaded]);

    // --- EPUB handlers ---
    const locationChanged = useCallback((epubcifi: string | number) => {
        setLocation(epubcifi);
        if (initialLocationLoaded && typeof epubcifi === 'string') {
            scheduleSave(0, epubcifi);
        }
    }, [scheduleSave, initialLocationLoaded]);

    const tocChanged = useCallback((toc: NavItem[]) => {
        tocRef.current = toc;
    }, []);

    const getRendition = useCallback((rendition: Rendition) => {
        renditionRef.current = rendition;

        // Inject a dark theme into the EPUB's iframe so the text content matches
        // the rest of the reader (dark bg + light text). epub.js applies these
        // rules to the <body> of every rendered chunk.
        try {
            rendition.themes.register('softmedia-dark', {
                body: {
                    background: READER_BG,
                    color: READER_TEXT,
                    'font-family': 'Inter, system-ui, sans-serif',
                    'line-height': '1.7',
                    padding: '1.5rem 2rem',
                },
                'p, div, span, li': { color: READER_TEXT },
                'h1, h2, h3, h4, h5, h6': { color: '#ffffff' },
                a: { color: READER_LINK },
                'img, svg': { 'max-width': '100%' },
                '::selection': { background: 'rgba(96, 165, 250, 0.35)' },
            });
            rendition.themes.select('softmedia-dark');
        } catch {
            // Theme registration is best-effort — failures shouldn't break reading.
        }

        rendition.on('relocated', (loc: Location) => {
            // Percentage is typically 0–1; some builds expose 0–100. Normalise.
            const raw = loc.start?.percentage ?? 0;
            const pct = raw <= 1 ? Math.round(raw * 100) : Math.round(raw);
            setPercentage(Math.max(0, Math.min(100, pct)));

            const href = loc.start?.href;
            if (href) {
                const base = href.split('#')[0];
                const match = tocRef.current.find(t => {
                    const tBase = t.href.split('#')[0];
                    return tBase === base || base.endsWith(tBase);
                });
                setCurrentChapter(match?.label?.trim() ?? null);
            }
        });
    }, []);

    const epubPrev = useCallback(() => {
        renditionRef.current?.prev();
    }, []);
    const epubNext = useCallback(() => {
        renditionRef.current?.next();
    }, []);

    // --- Keyboard navigation (all formats) ---
    useEffect(() => {
        const handler = (e: KeyboardEvent) => {
            // Ignore when the user is typing in a text field.
            const target = e.target as HTMLElement | null;
            if (target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA') return;

            if (e.key === 'Escape') {
                navigate(-1);
                return;
            }

            const forward = e.key === 'ArrowRight' || e.key === 'PageDown';
            const backward = e.key === 'ArrowLeft' || e.key === 'PageUp';
            if (!forward && !backward) return;

            if (isPdf || isCbz) {
                changePage(forward ? 1 : -1);
            } else if (isEpub) {
                if (forward) epubNext();
                else epubPrev();
            }
        };
        window.addEventListener('keydown', handler);
        return () => window.removeEventListener('keydown', handler);
    }, [isPdf, isCbz, isEpub, changePage, navigate, epubNext, epubPrev]);

    const canPrev = pageNumber > 1;
    const canNext = numPages > 0 && pageNumber < numPages;

    // EPUB status label: "Chapter · 42%" when a chapter is known, otherwise just "42%".
    const epubLabel = currentChapter
        ? `${currentChapter} · ${percentage}%`
        : `${percentage}%`;

    return (
        <div className="fixed inset-0 bg-gray-900 z-50 flex flex-col">
            {/* Header */}
            <div className="h-14 bg-gray-800 flex items-center justify-between px-4 shadow-md z-10">
                <h2 className="text-white font-medium truncate">{item.title}</h2>
                <button
                    type="button"
                    aria-label="Close reader"
                    onClick={() => navigate(-1)}
                    className="min-w-[44px] min-h-[44px] p-2 hover:bg-gray-700 rounded-full text-white transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                >
                    <X size={24} />
                </button>
            </div>

            {/* Content — one shared container frames all three formats so the
                visual proportions (max-width, centering, background, controls)
                feel identical regardless of the underlying file type. */}
            <div className="flex-1 relative overflow-hidden bg-gray-900">
                {/* The shared reading surface: max-w-4xl centered column. */}
                <div className="h-full w-full max-w-4xl mx-auto relative flex items-center justify-center">
                    {isPdf && (
                        <div className="h-full w-full overflow-auto flex justify-center py-4">
                            <Document
                                file={fileUrl}
                                onLoadSuccess={onPdfLoaded}
                                className="shadow-2xl"
                            >
                                <Page
                                    pageNumber={pageNumber}
                                    renderTextLayer={false}
                                    renderAnnotationLayer={false}
                                    className="max-w-full"
                                    width={Math.min(800, window.innerWidth - 40)}
                                />
                            </Document>
                        </div>
                    )}

                    {isCbz && (
                        <div className="h-full w-full overflow-auto p-4 flex justify-center items-center">
                            {bookInfo === null ? (
                                <div className="text-gray-400">Loading…</div>
                            ) : bookInfo.pageCount === null || bookInfo.pageCount === 0 ? (
                                <div className="text-gray-400">This archive has no pages.</div>
                            ) : (
                                <img
                                    key={pageNumber}
                                    src={getBookPageUrl(item.id, pageNumber)}
                                    alt={`Page ${pageNumber}`}
                                    className="max-h-full max-w-full shadow-2xl object-contain"
                                />
                            )}
                        </div>
                    )}

                    {isEpub && (
                        <div className="h-full w-full relative">
                            <ReactReader
                                url={fileUrl}
                                location={location}
                                locationChanged={locationChanged}
                                tocChanged={tocChanged}
                                getRendition={getRendition}
                                showToc={false}
                                readerStyles={unifiedReaderStyles}
                                epubInitOptions={{ openAs: 'epub' }}
                            />
                        </div>
                    )}

                    {!isPdf && !isEpub && !isCbz && (
                        <div className="flex items-center justify-center h-full text-gray-500">
                            <p>Unsupported format for web reader. Please download to view.</p>
                        </div>
                    )}
                </div>

                {/* Single shared PageControls pill — lives at the frame level so
                    its placement is identical for every format. */}
                {(isPdf || isCbz || isEpub) && (
                    <PageControls
                        label={
                            isPdf ? `${pageNumber}${numPages > 0 ? ` / ${numPages}` : ''}`
                                : isCbz ? `${pageNumber} / ${numPages}`
                                : epubLabel
                        }
                        canPrev={isEpub ? true : canPrev}
                        canNext={isEpub ? true : canNext}
                        onPrev={isEpub ? epubPrev : () => changePage(-1)}
                        onNext={isEpub ? epubNext : () => changePage(1)}
                    />
                )}
            </div>
        </div>
    );
}

interface PageControlsProps {
    /** Middle text content, e.g. "5 / 25" for PDF/CBZ or "Chapter 3 · 42%" for EPUB. */
    label: string;
    canPrev: boolean;
    canNext: boolean;
    onPrev: () => void;
    onNext: () => void;
}

function PageControls({ label, canPrev, canNext, onPrev, onNext }: PageControlsProps) {
    return (
        <div className="absolute bottom-8 left-1/2 -translate-x-1/2 bg-gray-800/90 rounded-full px-6 py-2 flex items-center gap-3 shadow-xl backdrop-blur-sm text-white z-20 max-w-[90%]">
            <button
                type="button"
                aria-label="Previous page"
                disabled={!canPrev}
                onClick={onPrev}
                className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full disabled:opacity-30 hover:text-blue-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <ChevronLeft />
            </button>
            <span className="font-mono select-none truncate" title={label}>
                {label}
            </span>
            <button
                type="button"
                aria-label="Next page"
                disabled={!canNext}
                onClick={onNext}
                className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full disabled:opacity-30 hover:text-blue-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <ChevronRight />
            </button>
        </div>
    );
}
