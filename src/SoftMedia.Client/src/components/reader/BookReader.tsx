import { useState, useEffect, useRef, useCallback } from 'react';
import { toast } from 'sonner';
import { Document, Page, pdfjs } from 'react-pdf';
// Required by react-pdf v10 when text and annotation layers are rendered — without
// these imports the layer spans don't overlay the canvas correctly and selection
// feels broken. Dark-theme overrides for the layer classes live in index.css.
import 'react-pdf/dist/Page/TextLayer.css';
import 'react-pdf/dist/Page/AnnotationLayer.css';
import EpubView from './EpubView';
import type { Rendition, NavItem, Location } from 'epubjs';
import {
    BookCheck,
    BookOpen,
    Bookmark as BookmarkIcon,
    ChevronLeft,
    ChevronRight,
    Eye,
    EyeOff,
    Highlighter,
    List,
    Maximize2,
    BookOpenText,
    MessageSquarePlus,
    Minimize2,
    Pen,
    Play,
    Search as SearchIcon,
    Settings,
    Square,
    Volume2,
    X,
} from 'lucide-react';
import TocDrawer, { type TocItem } from './TocDrawer';
import ReaderSettingsPanel, { PanelSection, SegmentedControl, FontSizeControl, ZoomControl, SliderControl } from './ReaderSettingsPanel';
import BookmarksDrawer from './BookmarksDrawer';
import SearchDrawer, { type SearchHit } from './SearchDrawer';
import HighlightsDrawer, { swatchFor } from './HighlightsDrawer';
import PdfHighlightOverlay from './PdfHighlightOverlay';
import ShortcutHelpSheet from './ShortcutHelpSheet';
import TtsNowPlayingBar, { type SleepTimerMode } from './TtsNowPlayingBar';
import { useSwipe } from '../../hooks/useSwipe';
import { useTts, chunkTextForTts, type TtsSegment } from '../../hooks/useTts';
import { playerBackTarget } from '../../lib/backNavigation';
import {
    useReaderStore,
    useSpread,
    useSetSpread,
    useFontFamily,
    useSetFontFamily,
    useFontSize,
    useSetFontSize,
    useLineHeight,
    useSetLineHeight,
    useMargin,
    useSetMargin,
    useOverridePublisher,
    useSetOverridePublisher,
    useRtl,
    useSetRtl,
    useBrightness,
    useSetBrightness,
    useWarmth,
    useSetWarmth,
    useTtsVoice,
    useSetTtsVoice,
    useTtsRate,
    useSetTtsRate,
    useZoom,
    useSetZoom,
    ZOOM_PCT_MIN,
    ZOOM_PCT_MAX,
    ZOOM_PCT_STEP,
    useReaderTheme,
    useSetReaderTheme,
    type SpreadMode,
    type ReaderFontFamily,
    type LineHeightMode,
    type MarginMode,
    type ReaderTheme,
} from '../../store/readerStore';
import { useNavigate } from 'react-router-dom';
import { API_URL } from '../../services/api';
import {
    createBookmark,
    createHighlight,
    deleteBookmark,
    deleteHighlight,
    endReadingSession,
    getBookInfo,
    getBookPageUrl,
    getBookThumbnailUrl,
    getProgress,
    getReaderPreferences,
    getReadingSessionSummary,
    listBookmarks,
    listHighlights,
    lookupWord,
    markFinished,
    parseHighlightLocation,
    putReaderPreferences,
    startReadingSession,
    updateBookmarkLabel,
    updateHighlight,
    updateProgress,
    type Bookmark,
    type BookInfo,
    type DictionaryLookup,
    type Highlight,
    type HighlightColour,
    type ReaderPreferencesPayload,
    type ReadingSessionSummary,
} from '../../services/bookService';
import { useAuthStore } from '../../store/authStore';
import type { MediaItem } from '../../types';

// Setup PDF worker
pdfjs.GlobalWorkerOptions.workerSrc = new URL(
    'pdfjs-dist/build/pdf.worker.min.mjs',
    import.meta.url,
).toString();

const PROGRESS_SAVE_DEBOUNCE_MS = 1000;

// ER-007: tiny local Fullscreen API shim. Safari ships the feature under
// `webkit*` names rather than the standard ones, and the project rule is
// "no dependency for a six-line shim." Keeping this scoped to the reader
// file because no other surface uses the Fullscreen API today.
type FsDoc = Document & {
    webkitFullscreenElement?: Element | null;
    webkitExitFullscreen?: () => Promise<void> | void;
};
type FsElement = HTMLElement & {
    webkitRequestFullscreen?: () => Promise<void> | void;
};
const getFsElement = (): Element | null => {
    const d = document as FsDoc;
    return d.fullscreenElement ?? d.webkitFullscreenElement ?? null;
};
const requestFs = (el: HTMLElement): Promise<void> => {
    const x = el as FsElement;
    const fn = el.requestFullscreen ?? x.webkitRequestFullscreen;
    return fn ? Promise.resolve(fn.call(el)) : Promise.resolve();
};
const exitFs = (): Promise<void> => {
    const d = document as FsDoc;
    const fn = document.exitFullscreen ?? d.webkitExitFullscreen;
    return fn ? Promise.resolve(fn.call(document)) : Promise.resolve();
};

// ER-011: reader colour palette now lives in CSS variables on [data-reader-root]
// in index.css. Everything that runs outside that DOM scope — the EpubView
// container background (React inline style: CSS var refs work fine) and the
// epub.js iframe theme (separate document: needs computed values) — reads from
// those variables. ER-021 flips them by toggling data-reading-theme.

interface ReaderThemeTokens {
    bg: string;
    fg: string;
    heading: string;
    link: string;
    linkHover: string;
    selection: string;
    fontFamily: string;
    fontSize: string;
    lineHeight: string;
    paddingInline: string;
    paddingBlock: string;
}

// Literal fallback values mirror index.css's dark-theme defaults exactly.
// Used before the reader root is in the DOM and in jsdom where getComputedStyle
// returns empty strings for custom properties.
const THEME_FALLBACK: ReaderThemeTokens = {
    bg: '#111827',
    fg: '#e5e7eb',
    heading: '#ffffff',
    link: '#60a5fa',
    linkHover: '#93c5fd',
    selection: 'rgba(96, 165, 250, 0.45)',
    fontFamily: "'Inter', system-ui, sans-serif",
    fontSize: '100%',
    lineHeight: '1.7',
    paddingInline: '2rem',
    paddingBlock: '1.5rem',
};

// ER-020: slice-value → CSS-value maps. Kept at module scope so the settings
// panel can reuse them for its option labels if needed. OpenDyslexic and
// Merriweather are listed in the store type but not exposed in the UI yet —
// adding the font assets is its own follow-up.
const FONT_FAMILY_CSS: Record<string, string> = {
    'inter': "'Inter', system-ui, sans-serif",
    'georgia': "Georgia, 'Times New Roman', serif",
    'merriweather': "'Merriweather', Georgia, serif",
    'open-dyslexic': "'OpenDyslexic', 'Inter', sans-serif",
    'system-serif': "Georgia, 'Times New Roman', serif",
    'system-sans': "system-ui, -apple-system, 'Segoe UI', sans-serif",
};

const LINE_HEIGHT_CSS: Record<string, string> = {
    tight: '1.4',
    normal: '1.7',
    loose: '2.0',
};

const MARGIN_CSS: Record<string, string> = {
    narrow: '1rem',
    normal: '2rem',
    wide: '3.5rem',
};

function readReaderTokens(root: Element | null): ReaderThemeTokens {
    if (!root || typeof window === 'undefined' || !window.getComputedStyle) {
        return THEME_FALLBACK;
    }
    const cs = window.getComputedStyle(root);
    const take = (name: string, fallback: string) => {
        const v = cs.getPropertyValue(name).trim();
        return v.length > 0 ? v : fallback;
    };
    return {
        bg: take('--reader-bg', THEME_FALLBACK.bg),
        fg: take('--reader-fg', THEME_FALLBACK.fg),
        heading: take('--reader-heading', THEME_FALLBACK.heading),
        link: take('--reader-link', THEME_FALLBACK.link),
        linkHover: take('--reader-link-hover', THEME_FALLBACK.linkHover),
        selection: take('--reader-selection', THEME_FALLBACK.selection),
        fontFamily: take('--reader-font-family', THEME_FALLBACK.fontFamily),
        fontSize: take('--reader-font-size', THEME_FALLBACK.fontSize),
        lineHeight: take('--reader-line-height', THEME_FALLBACK.lineHeight),
        paddingInline: take('--reader-padding-inline', THEME_FALLBACK.paddingInline),
        paddingBlock: take('--reader-padding-block', THEME_FALLBACK.paddingBlock),
    };
}

/**
 * Builds the theme CSS as a plain string. We inject it directly into each
 * EPUB chunk's document as a marked <style> element rather than registering
 * via epub.js's themes.register/select pipeline — the old approach raced
 * with the first chunk's initial paint and sometimes left the page with
 * publisher styles (or browser defaults) until the user nudged the theme.
 * Marking the <style> with `id="softmedia-reader-theme"` lets our strip hook
 * distinguish ours from publisher <style> blocks.
 */
const EPUB_THEME_STYLE_ID = 'softmedia-reader-theme';

function buildEpubThemeCss(t: ReaderThemeTokens, overridePublisher: boolean): string {
    const imp = overridePublisher ? ' !important' : '';
    return `
        /* Anchor the cascade on <html>. Publisher rules on inner elements
         * usually target descendants with their own font-size, so a body-only
         * rule doesn't propagate. Setting html is also the base for rem units,
         * so publisher rem-based styles scale proportionally with our size. */
        html {
            font-size: ${t.fontSize}${imp};
        }
        body {
            background: ${t.bg}${imp};
            color: ${t.fg}${imp};
            font-family: ${t.fontFamily}${imp};
            font-size: ${t.fontSize}${imp};
            line-height: ${t.lineHeight}${imp};
            padding: ${t.paddingBlock} ${t.paddingInline}${imp};
        }
        /* Force common text elements to inherit font-size instead of using
         * hard-coded px / pt values the publisher baked in. Without this a
         * publisher rule on <p> with its own font-size defeats our body
         * override by specificity (an element rule beats a body rule for
         * descendants that set their own size), regardless of !important.
         * Headings are left alone so they keep their relative scaling. */
        p, span, div, li, td, th, dd, dt, blockquote, q, cite, em, strong {
            font-size: inherit${imp};
            color: ${t.fg}${imp};
        }
        h1, h2, h3, h4, h5, h6 { color: ${t.heading}${imp}; }
        a { color: ${t.link}${imp}; }
        a:hover { color: ${t.linkHover}${imp}; }
        img, svg { max-width: 100%; }
        ::selection { background: ${t.selection}${imp}; }
        /* Karaoke highlight for the currently-spoken segment. Uses the
         * browser's CSS Custom Highlight API — painted without DOM mutation
         * so selection, clicks, and existing spans remain intact. Callers
         * (BookReader) register/unregister ranges under this name via
         * CSS.highlights.set('sm-tts-active', Highlight). Fallback: browsers
         * without the API simply don't paint, which is fine — TTS still works,
         * just without the karaoke effect. */
        ::highlight(sm-tts-active) {
            background-color: rgba(255, 215, 64, 0.55);
            color: ${t.fg};
        }
        /* TTS pick-start mode — when armed, flip the cursor over the whole
         * document so every word reads as "tappable to start listening here."
         * Toggled by BookReader via a data attribute on <body> rather than
         * reinjecting the stylesheet, so the cursor flips instantly. */
        body[data-sm-tts-arm="true"],
        body[data-sm-tts-arm="true"] * {
            cursor: pointer !important;
        }
    `;
}

/**
 * Install or replace the reader theme <style> inside a single chunk's
 * document. Idempotent — calling repeatedly with the same CSS is a DOM
 * no-op. Appended to body (not head) so it sits after any surviving
 * publisher styles and wins cascade-by-order in addition to !important.
 */
function applyThemeStyleTo(doc: Document, css: string): void {
    let style = doc.getElementById(EPUB_THEME_STYLE_ID) as HTMLStyleElement | null;
    if (!style) {
        style = doc.createElement('style');
        style.id = EPUB_THEME_STYLE_ID;
        (doc.body ?? doc.head ?? doc.documentElement).appendChild(style);
    }
    if (style.textContent !== css) {
        style.textContent = css;
    }
}

/**
 * Walk a Range's visible text nodes and build a sub-Range matching character
 * offsets `rawStart` / `rawEnd` into `baseRange.toString()`. The invariant:
 * iterating text nodes in document order, clipping each by baseRange bounds,
 * concatenates into the same string `Range.toString()` returns — so char
 * offsets map 1:1 to (node, offset) pairs.
 *
 * Returns null if the offsets couldn't be located (e.g. offsets out of range
 * or no text nodes in the base range). Used by the TTS karaoke pipeline to
 * convert segment.rawStart/rawEnd into the DOM range the highlight API needs.
 */
function rangeForOffsets(
    baseRange: Range,
    rawStart: number,
    rawEnd: number,
): Range | null {
    if (rawStart > rawEnd) return null;
    const doc = baseRange.startContainer.ownerDocument;
    if (!doc) return null;

    const walker = doc.createTreeWalker(
        baseRange.commonAncestorContainer,
        NodeFilter.SHOW_TEXT,
    );
    let accumulated = 0;
    let startNode: Text | null = null;
    let startOffset = 0;
    let endNode: Text | null = null;
    let endOffset = 0;

    while (walker.nextNode()) {
        const node = walker.currentNode as Text;
        if (!baseRange.intersectsNode(node)) continue;

        const nodeRangeStart = node === baseRange.startContainer ? baseRange.startOffset : 0;
        const nodeRangeEnd = node === baseRange.endContainer ? baseRange.endOffset : node.length;
        const clippedLen = nodeRangeEnd - nodeRangeStart;
        if (clippedLen <= 0) continue;

        const segStart = accumulated;
        const segEnd = accumulated + clippedLen;

        if (startNode === null && rawStart >= segStart && rawStart <= segEnd) {
            startNode = node;
            startOffset = nodeRangeStart + (rawStart - segStart);
        }
        if (rawEnd >= segStart && rawEnd <= segEnd) {
            endNode = node;
            endOffset = nodeRangeStart + (rawEnd - segStart);
        }

        accumulated += clippedLen;
        if (startNode && endNode) break;
    }

    if (!startNode || !endNode) return null;
    try {
        const range = doc.createRange();
        range.setStart(startNode, startOffset);
        range.setEnd(endNode, endOffset);
        return range;
    } catch {
        return null;
    }
}

/**
 * Remove publisher <style> blocks while preserving ours. Called from the
 * content hook on every chunk render. Skips any element carrying the
 * sentinel id so the theme style survives.
 */
function stripPublisherStyles(doc: Document): void {
    try {
        doc.querySelectorAll('style').forEach((el) => {
            if (el.id === EPUB_THEME_STYLE_ID) return;
            el.parentNode?.removeChild(el);
        });
    } catch {
        // Cross-origin / detached iframe edge-cases — silent is safe.
    }
}

/**
 * ER-052: humanise a fractional minute count for the stats row. Keeps short
 * sessions readable ("3m") while folding large counts into hours + minutes
 * ("2h 15m"). Accepts a number — not a duration — because the server returns
 * floats with one-decimal precision.
 */
function formatMinutes(mins: number): string {
    if (!Number.isFinite(mins) || mins <= 0) return '0m';
    if (mins < 60) return `${Math.round(mins)}m`;
    const h = Math.floor(mins / 60);
    const m = Math.round(mins - h * 60);
    return m === 0 ? `${h}h` : `${h}h ${m}m`;
}

// ── ER-024 search helpers ────────────────────────────────────────────────────
// Format-specific search providers live at module scope so they can be unit-
// tested without mounting the full reader. Each takes an `isLive()` callback
// the caller uses to signal "this query is superseded — stop doing work."
// Search is deliberately linear — O(pages × text) for PDF, O(spineItems ×
// text) for EPUB — and the isLive check is what keeps huge books responsive.

interface EpubSpineItem {
    href?: string;
    document?: Document;
    load: (requestFn: unknown) => Promise<unknown>;
    unload: () => void;
    find?: (query: string) => Array<{ cfi: string; excerpt: string }>;
}
interface EpubBook {
    spine: { spineItems: EpubSpineItem[] };
    load: (path: string) => unknown;
}

async function searchEpub(
    rendition: Rendition,
    query: string,
    isLive: () => boolean,
): Promise<SearchHit[]> {
    const book = (rendition as unknown as { book: EpubBook }).book;
    const items = book?.spine?.spineItems ?? [];
    const results: SearchHit[] = [];
    for (const item of items) {
        if (!isLive()) return [];
        try {
            await item.load(book.load.bind(book));
            // `find` lands on spine items in epub.js >= 0.3.85. Defensive check
            // keeps older builds from throwing.
            const raw = typeof item.find === 'function' ? item.find(query) : [];
            for (const hit of raw) {
                results.push({
                    key: hit.cfi,
                    excerpt: hit.excerpt,
                    label: item.href,
                });
            }
        } finally {
            try { item.unload(); } catch { /* unload is best-effort */ }
        }
        // Cap hits so a stop-word query doesn't allocate thousands of rows.
        if (results.length >= 200) break;
    }
    return results;
}

async function searchPdf(
    pdf: PdfDocProxy,
    query: string,
    isLive: () => boolean,
): Promise<SearchHit[]> {
    const q = query.toLowerCase();
    const results: SearchHit[] = [];
    for (let p = 1; p <= pdf.numPages; p++) {
        if (!isLive()) return [];
        let text = '';
        try {
            const page = await pdf.getPage(p);
            const content = await page.getTextContent();
            text = content.items
                .map((i) => (i && typeof i.str === 'string' ? i.str : ''))
                .join(' ');
        } catch {
            continue; // Skip unreadable pages; search proceeds.
        }
        const lower = text.toLowerCase();
        let idx = lower.indexOf(q);
        if (idx < 0) continue;
        // One hit per page — the scrubber jumps there; the user scans visually.
        const start = Math.max(0, idx - 40);
        const end = Math.min(text.length, idx + query.length + 40);
        const excerpt =
            (start > 0 ? '\u2026' : '') +
            text.slice(start, end).replace(/\s+/g, ' ').trim() +
            (end < text.length ? '\u2026' : '');
        results.push({
            key: `pdf:page:${p}`,
            excerpt,
            label: `Page ${p}`,
        });
        if (results.length >= 200) break;
    }
    return results;
}

// ── PDF.js shape surface ─────────────────────────────────────────────────────
// Minimal structural types for the react-pdf document proxy we receive in
// onLoadSuccess. Hoisted to module scope so both the component body (outline
// resolution, page counting) and the search provider (ER-024) can reference
// them without redeclaring.

type PdfOutlineRef = { num: number; gen: number; [k: string]: unknown };
type PdfOutlineItem = {
    title: string;
    dest?: string | unknown[] | null;
    items?: PdfOutlineItem[];
};
type PdfTextItem = { str?: string };
type PdfTextContent = { items: PdfTextItem[] };
type PdfViewport = { width: number; height: number };
type PdfPageProxy = {
    getTextContent: () => Promise<PdfTextContent>;
    // Extended surface for ER-032 client-side thumbnails: pdf.js's PDFPageProxy
    // exposes a viewport builder and a render pipeline. We type it structurally
    // rather than importing pdfjs-dist to keep the reader decoupled from the
    // underlying pdfjs type surface, which drifts between minor versions.
    getViewport: (opts: { scale: number }) => PdfViewport;
    render: (opts: { canvasContext: CanvasRenderingContext2D; viewport: PdfViewport }) => {
        promise: Promise<void>;
        cancel?: () => void;
    };
};
type PdfDocProxy = {
    numPages: number;
    getOutline: () => Promise<PdfOutlineItem[] | null>;
    getDestination: (name: string) => Promise<unknown[] | null>;
    getPageIndex: (ref: PdfOutlineRef) => Promise<number>;
    getPage: (pageNumber: number) => Promise<PdfPageProxy>;
};

/**
 * Push the current theme CSS into every EPUB chunk that's currently rendered.
 * Walks rendition.getContents() — epub.js exposes every live Contents wrapper
 * that way — and replaces the sentinel <style> in each. ER-021 drives this on
 * theme change; the content hook below handles newly-rendered chunks.
 * `overridePublisher` (ER-022) decides whether rules get `!important` appended.
 * Safe on a null rendition — it's a no-op.
 */
export function refreshReaderTheme(
    rendition: Rendition | null,
    root: Element | null,
    overridePublisher: boolean = true,
): void {
    if (!rendition) return;
    const css = buildEpubThemeCss(readReaderTokens(root), overridePublisher);
    const r = rendition as unknown as {
        getContents?: () => Array<{ document?: Document }>;
    };
    try {
        const contents = r.getContents?.() ?? [];
        for (const c of contents) {
            if (c.document) applyThemeStyleTo(c.document, css);
        }
    } catch {
        // Theme application is best-effort; failures must never break reading.
    }
}

interface BookReaderProps {
    item: MediaItem;
}

export default function BookReader({ item }: BookReaderProps) {
    const navigate = useNavigate();
    // WS-6 T6.1: the reduced-privilege media token, and ONLY the media token — the
    // server rejects full access tokens in query strings, and App.tsx gates the
    // authed UI until this exists. Subscribed (not getUrlToken()) so the URL
    // re-renders when the token rotates.
    const token = useAuthStore(s => s.mediaToken);

    // SR-WI-063: `path` left the media DTO; the server now guarantees `container`
    // carries the file extension for book-type items instead.
    const ext = (item.container ?? '').toLowerCase();
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

    // EPUB progress tracking, updated from rendition events. We prefer page-based
    // reporting once epub.js's locations index is built (see getRendition below),
    // and fall back to the percentage while it's generating.
    const [currentChapter, setCurrentChapter] = useState<string | null>(null);
    const [percentage, setPercentage] = useState<number>(0);
    const [epubCurrentPage, setEpubCurrentPage] = useState<number>(1);
    const [epubTotalPages, setEpubTotalPages] = useState<number>(0);
    const renditionRef = useRef<Rendition | null>(null);
    const tocRef = useRef<NavItem[]>([]);

    // Tracks which action caused the most recent relocation. The `relocated`
    // event fires both for user-initiated page turns (next/prev/jump) and for
    // out-of-band navigations (initial load, reading-position restore, TOC
    // click). We keep a tight +1/-1 page counter for user navigation — epub.js's
    // location index advances by an unpredictable number of chunks per visible
    // spread, so reading it directly made the displayed page jump several at a
    // time. For external navigation we fall back to percentage × total so the
    // page number lands somewhere sensible.
    const epubNavActionRef = useRef<'next' | 'prev' | 'jump' | null>(null);
    // Stable access to the latest total inside the `relocated` callback, which
    // is registered once with empty deps.
    const epubTotalPagesRef = useRef<number>(0);
    useEffect(() => { epubTotalPagesRef.current = epubTotalPages; }, [epubTotalPages]);

    // Ref-mirror of `percentage` so the one-shot resume-sync effect below
    // can read the latest value without re-firing on every percentage tick.
    const percentageRef = useRef<number>(0);
    useEffect(() => { percentageRef.current = percentage; }, [percentage]);

    // Resume-sync guard. When the user opens a book with a saved CFI, the
    // `relocated` event fires before `book.locations.generate()` finishes,
    // so its external-relocation branch short-circuits (`total === 0`). The
    // counter is left displaying "1 / total" even though the user is mid-
    // book. Once the total becomes known, back-fill the current page from
    // the latest percentage — exactly once per book open so subsequent
    // manual page turns aren't disturbed.
    const epubPageSyncedRef = useRef<boolean>(false);
    useEffect(() => {
        if (epubPageSyncedRef.current) return;
        if (epubTotalPages <= 0) return;
        epubPageSyncedRef.current = true;
        const pct = percentageRef.current;
        const target = Math.max(1, Math.ceil((pct / 100) * epubTotalPages));
        setEpubCurrentPage(target);
    }, [epubTotalPages]);

    // CBZ metadata (page count from backend)
    const [bookInfo, setBookInfo] = useState<BookInfo | null>(null);

    // Finished / IsWatched state. Hydrated from the interaction progress on mount
    // and flipped either by the manual toggle button or the end-of-book auto-fire
    // guarded by `autoMarkedRef` below.
    const [isFinished, setIsFinished] = useState<boolean>(false);

    // Guard: the end-of-book detection must only fire once per reader session.
    // Without this, scrubbing to the last page + navigating back repeatedly would
    // spam the /watched endpoint. Reset only on unmount.
    const autoMarkedRef = useRef<boolean>(false);

    // --- Reader settings panel (ER-010) ---
    // Panel body is assembled by later milestones (ER-002/020/021). Open state
    // lives locally because settings visibility is per-session; persisted prefs
    // themselves live in readerStore.
    const [settingsOpen, setSettingsOpen] = useState(false);
    const settingsButtonRef = useRef<HTMLButtonElement | null>(null);

    // --- Bookmarks (ER-023) ---
    const [bookmarks, setBookmarks] = useState<Bookmark[]>([]);
    const [bookmarksOpen, setBookmarksOpen] = useState(false);
    const bookmarksButtonRef = useRef<HTMLButtonElement | null>(null);

    // --- Highlights (ER-040 / ER-041) ---
    const [highlights, setHighlights] = useState<Highlight[]>([]);
    const [highlightsOpen, setHighlightsOpen] = useState(false);
    const highlightsButtonRef = useRef<HTMLButtonElement | null>(null);
    // Modal highlight mode. When true, click-drag stops turning the page so
    // the user can select text freely. PDF is gated via the outer `useSwipe`
    // callbacks. EPUB needs no iframe-level intervention — EpubView doesn't
    // install any swipe overlay on top of the content, so native text
    // selection works by default regardless of this flag.
    const [highlightModeActive, setHighlightModeActive] = useState(false);
    const highlightModeRef = useRef(false);
    useEffect(() => { highlightModeRef.current = highlightModeActive; }, [highlightModeActive]);

    // TTS pick-start mode — Listen button arms this instead of starting TTS
    // immediately. The next click/tap anywhere in the reader content becomes
    // the TTS start point. Cancelled by Esc, by clicking Listen again, or
    // implicitly by the click that starts TTS. Mirrored into a ref so the
    // click listener installed per-chunk (below) always reads the current
    // flag without re-registering on every toggle.
    const [ttsArming, setTtsArming] = useState(false);
    const ttsArmingRef = useRef(false);
    useEffect(() => { ttsArmingRef.current = ttsArming; }, [ttsArming]);

    // Current spine-item href (updated from the rendition's relocated handler
    // lower down and from the initial-location resolver). Declared here
    // rather than next to the TOC block below because the TTS end-of-chapter
    // sleep-timer observes it.
    const [currentHref, setCurrentHref] = useState<string | null>(null);
    // When set, the drawer opens with the named highlight's row pre-scrolled
    // and its note editor auto-focused. Cleared on the next drawer open so it
    // only fires for the save-then-note flow, not subsequent re-opens.
    const [highlightAutoEditNoteId, setHighlightAutoEditNoteId] = useState<string | null>(null);
    // Pending selection → shown as a small colour picker overlay. The
    // coordinates are client-space for a floating position; the CFI (EPUB)
    // or page (PDF) is captured at selection time because the selection can
    // vanish before the user picks a colour (e.g. on scroll).
    const [pendingSelection, setPendingSelection] = useState<{
        quotedText: string;
        epubCfi?: string;
        pdfPage?: number;
        /** Normalised 0–1 rects relative to the PDF page's rendered dimensions.
         *  Survive zoom/resize because they scale with the page element. */
        pdfRects?: Array<{ x: number; y: number; w: number; h: number }>;
        x: number;
        y: number;
    } | null>(null);

    // --- In-book search (ER-024) ---
    const [searchOpen, setSearchOpen] = useState(false);
    const [searchBusy, setSearchBusy] = useState(false);
    const [searchHits, setSearchHits] = useState<SearchHit[]>([]);
    const searchButtonRef = useRef<HTMLButtonElement | null>(null);
    // Debounce / cancellation — the caller fires onQueryChange on every
    // keystroke; we serialise the provider so a late-returning result can't
    // clobber a newer query.
    const searchSeqRef = useRef(0);
    // pdf.js document + EPUB book reference kept for search providers. The PDF
    // one is filled in onPdfLoaded; the EPUB one is reached through the
    // rendition (book property).
    const pdfDocRef = useRef<PdfDocProxy | null>(null);

    // --- Keyboard shortcut help sheet (ER-054) ---
    const [helpOpen, setHelpOpen] = useState(false);

    // --- Dictionary lookup (ER-051) ---
    // Triggered from the selection toolbar's "Define" button. The popover
    // anchors at the same coordinates as the colour picker so it replaces
    // it in place. Null means no lookup in flight; an object means "show
    // me" regardless of whether it has results.
    const [definition, setDefinition] = useState<{ lookup: DictionaryLookup; x: number; y: number } | null>(null);
    const [definitionBusy, setDefinitionBusy] = useState(false);

    // Pull the first word out of a selection. The dictionary endpoint expects
    // single-word lookups — users who select phrases get a lookup on the
    // first meaningful word, which matches every eReader's behaviour.
    const lookupFromSelection = useCallback(async (pending: {
        quotedText: string; x: number; y: number;
    } | null) => {
        if (!pending) return;
        const first = pending.quotedText.trim().split(/\s+/)[0] ?? '';
        const cleaned = first.replace(/[^\p{L}\p{M}'\-]/gu, '');
        if (cleaned.length === 0) return;
        const x = pending.x;
        const y = pending.y;
        setPendingSelection(null);
        setDefinitionBusy(true);
        try {
            const result = await lookupWord(cleaned);
            setDefinition({ lookup: result, x, y });
        } catch {
            setDefinition({
                lookup: { word: cleaned, definitions: [], available: false },
                x, y,
            });
        } finally {
            setDefinitionBusy(false);
        }
    }, []);

    // --- Spread mode (ER-002) ---
    const spread = useSpread();
    const setSpread = useSetSpread();
    const isDouble = spread === 'double';

    // --- Typography (ER-020) ---
    const fontFamily = useFontFamily();
    const setFontFamily = useSetFontFamily();
    const fontSize = useFontSize();
    const setFontSize = useSetFontSize();
    const lineHeight = useLineHeight();
    const setLineHeight = useSetLineHeight();
    const margin = useMargin();
    const setMargin = useSetMargin();

    // --- Reading theme (ER-021) ---
    const readerTheme = useReaderTheme();
    const setReaderTheme = useSetReaderTheme();

    // --- Publisher-style override (ER-022) ---
    const overridePublisher = useOverridePublisher();
    const setOverridePublisher = useSetOverridePublisher();

    // --- Brightness + warmth (ER-053) ---
    const brightness = useBrightness();
    const setBrightness = useSetBrightness();
    const warmth = useWarmth();
    const setWarmth = useSetWarmth();

    // --- Text-to-speech (ER-050) ---
    const ttsVoice = useTtsVoice();
    const setTtsVoice = useSetTtsVoice();
    const ttsRate = useTtsRate();
    const setTtsRate = useSetTtsRate();

    // Returns the DOM Range covering the visible text of the current EPUB
    // page, or null if the location isn't ready yet. Callers pass an
    // optional CFI override to start the range at a specific point (used by
    // "Listen from here" — extract from the selection's CFI to page end).
    //
    // Uses Contents.range(cfi) — resolves synchronously against the rendered
    // iframe document. `book.getRange(cfi)` returns a Promise in this epub.js
    // build because the spine Section's .document cache isn't populated by
    // the Rendition load path; Contents.range goes straight to the iframe.
    const getVisibleEpubRange = useCallback((
        startCfiOverride?: string,
    ): Range | null => {
        type LocationPart = { cfi?: string };
        type RenditionLocation = { start?: LocationPart; end?: LocationPart };
        type ContentsLike = {
            document?: Document;
            range?: (cfi: string) => Range | null | undefined;
        };
        type ExtractApi = {
            getContents?: () => ContentsLike[];
            currentLocation?: () => RenditionLocation | null | undefined;
        };
        const rendition = renditionRef.current as unknown as ExtractApi | null;
        if (!rendition) return null;

        try {
            const loc = rendition.currentLocation?.();
            const startCfi = startCfiOverride ?? loc?.start?.cfi;
            const endCfi = loc?.end?.cfi;
            const contents = rendition.getContents?.() ?? [];

            // Single-spread: both CFIs live in the same Contents document.
            // Double-spread (contents.length > 1) would span two iframes —
            // TTS range extraction for dual spreads is a follow-up.
            if (startCfi && endCfi && contents.length === 1) {
                const c = contents[0];
                if (c.document && c.range) {
                    const startRange = c.range(startCfi);
                    const endRange = c.range(endCfi);
                    if (startRange && endRange) {
                        const range = c.document.createRange();
                        range.setStart(startRange.startContainer, startRange.startOffset);
                        range.setEnd(endRange.endContainer, endRange.endOffset);
                        return range;
                    }
                }
            }
        } catch { /* fall through to null */ }

        return null;
    }, []);

    // Karaoke highlight: the range currently painted via the CSS Custom
    // Highlight API, plus the document we registered it on (so we can
    // reach that document's CSS.highlights registry on cleanup — the iframe
    // has its own CSS object, not the top window's).
    const ttsHighlightCleanupRef = useRef<(() => void) | null>(null);
    const applyTtsHighlight = useCallback((range: Range | null) => {
        // Clear any prior highlight first — single active segment at a time.
        ttsHighlightCleanupRef.current?.();
        ttsHighlightCleanupRef.current = null;
        if (!range) return;
        const doc = range.startContainer.ownerDocument;
        const win = doc?.defaultView as unknown as {
            CSS?: { highlights?: { set: (k: string, v: unknown) => void; delete: (k: string) => void } };
            Highlight?: new (r: Range) => unknown;
        } | null;
        if (!win?.CSS?.highlights || !win.Highlight) return;
        const hl = new win.Highlight(range);
        win.CSS.highlights.set('sm-tts-active', hl);
        ttsHighlightCleanupRef.current = () => {
            try { win.CSS?.highlights?.delete('sm-tts-active'); } catch { /* ignore */ }
        };
    }, []);
    const clearTtsHighlight = useCallback(() => {
        ttsHighlightCleanupRef.current?.();
        ttsHighlightCleanupRef.current = null;
    }, []);

    // Per-segment DOM ranges computed at speak-time. The useTts onSegmentStart
    // callback receives a segment index; we look up the range for that index
    // and paint it. Kept in a ref so the callback identities inside useTts
    // don't need to refresh when this changes.
    const ttsSegmentRangesRef = useRef<(Range | null)[]>([]);

    // `tts.speak(segments)` voices the queue; onEnd auto-advances to the next
    // page and — via the effect below — re-speaks with the next page's text.
    // The chain stops when the user hits Stop or reaches the end of the book.
    const ttsAdvanceRef = useRef(false);
    const tts = useTts({
        voice: ttsVoice,
        rate: ttsRate,
        onSegmentStart: (i) => {
            applyTtsHighlight(ttsSegmentRangesRef.current[i] ?? null);
        },
        onSegmentEnd: () => {
            clearTtsHighlight();
        },
        onEnd: () => {
            clearTtsHighlight();
            if (!ttsAdvanceRef.current) return;
            // Match the manual `epubNext` flow: set the nav-action ref BEFORE
            // rendition.next() so the relocated handler treats this as a +1
            // user step rather than an external relocation (which would
            // recompute the page counter from percentage math).
            epubNavActionRef.current = 'next';
            renditionRef.current?.next();
        },
    });

    // Pin `tts.speak` into a ref so the location-driven effect below doesn't
    // need `tts` in its dep array — the hook returns a new object every
    // render, so depending on it would re-speak the current page every chunk
    // transition.
    const ttsSpeakRef = useRef(tts.speak);
    useEffect(() => { ttsSpeakRef.current = tts.speak; }, [tts.speak]);

    // Start voicing a concrete DOM Range. Chunks the text, computes per-
    // segment DOM sub-ranges for karaoke highlighting, publishes them to the
    // ref the TTS callbacks read from, and hands the segments to the engine.
    // Returns false if the range produced no speakable text.
    const speakVisibleRange = useCallback((range: Range): boolean => {
        const fullText = range.toString();
        const segments: TtsSegment[] = chunkTextForTts(fullText);
        if (segments.length === 0) return false;
        ttsSegmentRangesRef.current = segments.map((s) =>
            rangeForOffsets(range, s.rawStart, s.rawEnd),
        );
        ttsSpeakRef.current(segments);
        return true;
    }, []);

    // Tracks the last EPUB location we triggered a speak() for. Deduplicates
    // spurious re-fires of the location effect (StrictMode, rapid relocate
    // events) so we don't restart the current page mid-sentence. Seeded by
    // startTts so the click and its accompanying effect don't collide.
    const lastSpokenLocationRef = useRef<string | number | null>(null);

    // When TTS is active and the page flips (user or auto-advance), voice
    // the new page's text. epub.js fires `relocated` before the new spine
    // item has finished painting — so we retry up to 4× at 200ms intervals
    // before giving up on a location.
    useEffect(() => {
        if (!ttsAdvanceRef.current) return;
        if (!isEpub) return;
        const locKey = location ?? null;
        if (lastSpokenLocationRef.current === locKey) return;

        let attempts = 0;
        let tid: number;
        const tryOnce = () => {
            const range = getVisibleEpubRange();
            if (range && speakVisibleRange(range)) {
                lastSpokenLocationRef.current = locKey;
                // eslint-disable-next-line no-console
                console.debug('[TTS] speak loc=', locKey, 'text=', range.toString().slice(0, 60));
                return;
            }
            attempts += 1;
            if (attempts < 4) {
                tid = window.setTimeout(tryOnce, 200);
            } else {
                // eslint-disable-next-line no-console
                console.debug('[TTS] gave up on', locKey, 'after', attempts, 'attempts');
            }
        };
        tid = window.setTimeout(tryOnce, 200);
        return () => window.clearTimeout(tid);
    }, [location, isEpub, getVisibleEpubRange, speakVisibleRange]);

    // "Listen from here" — begin TTS at a specific CFI within the current
    // page. Entry points: (1) the selection floating toolbar's Volume2
    // button, and (2) the in-iframe click when the reader is in pick-start
    // mode (Listen clicked, user then taps a word). Always clears the
    // pick-start flag on entry so a failed pick doesn't leave the reader
    // stuck in armed mode.
    const startTtsFromCfi = useCallback((startCfi: string) => {
        setTtsArming(false);
        if (!isEpub || !tts.supported || tts.voices.length === 0) return;
        const range = getVisibleEpubRange(startCfi);
        if (!range || !speakVisibleRange(range)) {
            toast.error('Could not start listening at that point.');
            return;
        }
        ttsAdvanceRef.current = true;
        lastSpokenLocationRef.current = location ?? null;
    }, [isEpub, tts.supported, tts.voices.length, getVisibleEpubRange, speakVisibleRange, location]);

    // Stable handle for the in-iframe click listener. The content hook is
    // installed once per chunk render; it closes over this ref so it always
    // invokes the latest startTtsFromCfi without needing to re-register.
    const startTtsFromCfiRef = useRef(startTtsFromCfi);
    useEffect(() => { startTtsFromCfiRef.current = startTtsFromCfi; }, [startTtsFromCfi]);

    // Sleep-timer state. `firesAt` is an epoch ms for the numeric modes;
    // `chapterHrefAtArm` records the spine item the user was in when the
    // end-of-chapter mode was armed, so we can detect a chapter change by
    // href comparison (without re-using the TTS advance logic). Timer is
    // cleared on stop, supersede, or explicit off.
    const [sleepTimerMode, setSleepTimerMode] = useState<SleepTimerMode>('off');
    const sleepTimerChapterHrefRef = useRef<string | null>(null);
    const [sleepTimerFiresAt, setSleepTimerFiresAt] = useState<number | null>(null);
    const [sleepTimerNow, setSleepTimerNow] = useState<number>(() => Date.now());

    // Screen Wake Lock — held while TTS is active so the device doesn't
    // sleep mid-listen. `navigator.wakeLock` releases automatically when the
    // tab becomes hidden; we re-acquire on visibility change.
    const wakeLockRef = useRef<WakeLockSentinel | null>(null);

    // Flip a data attribute on every rendered chunk's <body> when arming
    // toggles. The theme CSS binds a `cursor: pointer` rule to that attribute
    // (see buildEpubThemeCss) so the user gets an immediate visual cue that
    // the page content is now a pick target. Updating the attribute rather
    // than reinjecting the stylesheet keeps the flip instant.
    useEffect(() => {
        const rendition = renditionRef.current as unknown as {
            getContents?: () => Array<{ document?: Document }>;
        } | null;
        const contents = rendition?.getContents?.() ?? [];
        for (const c of contents) {
            const body = c.document?.body;
            if (!body) continue;
            if (ttsArming) body.dataset.smTtsArm = 'true';
            else delete body.dataset.smTtsArm;
        }
    }, [ttsArming]);

    const stopTts = useCallback(() => {
        ttsAdvanceRef.current = false;
        lastSpokenLocationRef.current = null;
        clearTtsHighlight();
        ttsSegmentRangesRef.current = [];
        // Stop clears any armed sleep timer — the user has opted out of
        // listening, so a deferred auto-stop would just fire against nothing.
        setSleepTimerMode('off');
        setSleepTimerFiresAt(null);
        sleepTimerChapterHrefRef.current = null;
        tts.stop();
    }, [tts, clearTtsHighlight]);

    // Sleep timer — numeric modes (5m / 15m / 30m) fire once at the set
    // timestamp. End-of-chapter is a separate effect that watches currentHref.
    // 'off' clears both channels.
    const setSleepTimer = useCallback((mode: SleepTimerMode) => {
        setSleepTimerMode(mode);
        if (mode === 'off') {
            setSleepTimerFiresAt(null);
            sleepTimerChapterHrefRef.current = null;
            return;
        }
        if (mode === 'eoc') {
            setSleepTimerFiresAt(null);
            // Anchor on the chapter that's visible RIGHT NOW; the change
            // detection effect below compares against this reference.
            sleepTimerChapterHrefRef.current = currentHref;
            return;
        }
        const minutes = mode === '5m' ? 5 : mode === '15m' ? 15 : 30;
        setSleepTimerFiresAt(Date.now() + minutes * 60_000);
        sleepTimerChapterHrefRef.current = null;
    }, [currentHref]);

    // Fire the timer — cleans up on deps change so resetting the timer
    // cancels the previous one.
    useEffect(() => {
        if (sleepTimerFiresAt === null) return;
        const delay = Math.max(0, sleepTimerFiresAt - Date.now());
        const id = window.setTimeout(() => {
            stopTts();
        }, delay);
        return () => window.clearTimeout(id);
    }, [sleepTimerFiresAt, stopTts]);

    // End-of-chapter: stop when the spine item changes vs the one anchored
    // at timer-arm time.
    useEffect(() => {
        if (sleepTimerMode !== 'eoc') return;
        const anchor = sleepTimerChapterHrefRef.current;
        if (anchor && currentHref && currentHref !== anchor) {
            stopTts();
        }
    }, [currentHref, sleepTimerMode, stopTts]);

    // Drive the bar's countdown label. 15s cadence is a compromise between
    // responsive display and idle CPU.
    useEffect(() => {
        if (sleepTimerFiresAt === null) return;
        setSleepTimerNow(Date.now());
        const id = window.setInterval(() => setSleepTimerNow(Date.now()), 15_000);
        return () => window.clearInterval(id);
    }, [sleepTimerFiresAt]);

    // Screen Wake Lock — hold while TTS is active so the screen doesn't dim
    // mid-listen. The browser auto-releases when the tab hides; we
    // re-acquire on visibility change so returning to the tab restores it.
    useEffect(() => {
        const active = tts.isSpeaking || tts.isPaused;
        type WakeLockApi = { request: (name: string) => Promise<WakeLockSentinel> };
        const nav = navigator as Navigator & { wakeLock?: WakeLockApi };
        if (!nav.wakeLock) return;
        if (!active) {
            wakeLockRef.current?.release().catch(() => { /* ignore */ });
            wakeLockRef.current = null;
            return;
        }
        let cancelled = false;
        const acquire = async () => {
            try {
                const lock = await nav.wakeLock!.request('screen');
                if (cancelled) {
                    lock.release().catch(() => { /* ignore */ });
                    return;
                }
                wakeLockRef.current = lock;
            } catch {
                // Feature unavailable or user denied — silent; TTS still works.
            }
        };
        acquire();
        const onVis = () => {
            if (document.visibilityState === 'visible' && !wakeLockRef.current) {
                acquire();
            }
        };
        document.addEventListener('visibilitychange', onVis);
        return () => {
            cancelled = true;
            document.removeEventListener('visibilitychange', onVis);
            wakeLockRef.current?.release().catch(() => { /* ignore */ });
            wakeLockRef.current = null;
        };
    }, [tts.isSpeaking, tts.isPaused]);

    // Surface utterance errors from the browser speech engine. `lastError` is
    // set asynchronously by `utterance.onerror` and carries strings like
    // `synthesis-failed`, `audio-busy`, `not-allowed` — routinely silent
    // failure modes that otherwise leave the user wondering why nothing
    // happened. We skip `empty-text` because `startTts` already toasts that
    // case with a clearer message.
    useEffect(() => {
        if (!tts.lastError || tts.lastError === 'empty-text') return;
        toast.error(`Listen failed: ${tts.lastError}`);
    }, [tts.lastError]);

    // --- Reading direction (ER-031) ---
    // When true, the page layout mirrors (right page read first) and
    // gesture/keyboard semantics flip so ArrowLeft / swipe-right = next —
    // matching every major manga reader's convention.
    const rtl = useRtl();
    const setRtl = useSetRtl();

    // --- PDF client-side thumbnail renderer (ER-032 polish) ---
    // The server-side thumbnail endpoint rejects PDF (no rasteriser). For
    // PDF we draw the page to an off-screen canvas via pdf.js and hand the
    // data URL to PageControls. Kept at 0.3× scale so the canvas stays
    // around 240×340px — fast enough for keystroke-level previews.
    const renderPdfThumbnail = useCallback(async (p: number): Promise<string | null> => {
        const pdf = pdfDocRef.current;
        if (!pdf) return null;
        try {
            const page = await pdf.getPage(p);
            const viewport = page.getViewport({ scale: 0.3 });
            const canvas = document.createElement('canvas');
            canvas.width = Math.max(1, Math.round(viewport.width));
            canvas.height = Math.max(1, Math.round(viewport.height));
            const ctx = canvas.getContext('2d');
            if (!ctx) return null;
            await page.render({ canvasContext: ctx, viewport }).promise;
            return canvas.toDataURL('image/jpeg', 0.7);
        } catch {
            return null;
        }
    }, []);

    // --- Zoom (ER-030) — PDF/CBZ only ---
    const zoom = useZoom();
    const setZoom = useSetZoom();

    // ER-040: PDF selection capture. The text layer lives inside swipeRef;
    // `mouseup` fires on the outer container when the selection ends, and
    // `window.getSelection()` returns the selected text. We use the page
    // number we already track for the location. Only runs for PDF —
    // EPUB selection is handled through epub.js's `selected` event above.
    //
    // ER-040 polish: we also capture per-line rects, normalised 0–1 against
    // the containing PDF page's bounding rect. Normalising lets the overlay
    // survive zoom changes and window resizes without re-measuring.
    useEffect(() => {
        const el = swipeRef.current;
        if (!el || !isPdf) return;
        const handler = () => {
            const sel = window.getSelection();
            const quoted = sel?.toString().trim() ?? '';
            if (quoted.length === 0) {
                setPendingSelection(null);
                return;
            }
            const range = sel?.rangeCount ? sel.getRangeAt(0) : null;
            const rect = range?.getBoundingClientRect();

            // Find the containing `.react-pdf__Page` element so we can
            // normalise to its dimensions. If the selection spans two pages
            // (rare but possible in a double-spread), we only normalise
            // against the anchor page — second-page rects fall outside the
            // stored overlay and simply aren't painted.
            const anchorNode = range?.startContainer;
            const pageEl = anchorNode
                ? (anchorNode.nodeType === Node.ELEMENT_NODE
                    ? (anchorNode as Element).closest('.react-pdf__Page')
                    : anchorNode.parentElement?.closest('.react-pdf__Page'))
                : null;
            const pageRect = pageEl?.getBoundingClientRect();

            let pdfRects: Array<{ x: number; y: number; w: number; h: number }> | undefined;
            if (range && pageRect && pageRect.width > 0 && pageRect.height > 0) {
                const raw = Array.from(range.getClientRects());
                pdfRects = raw
                    .map((r) => ({
                        x: (r.left - pageRect.left) / pageRect.width,
                        y: (r.top - pageRect.top) / pageRect.height,
                        w: r.width / pageRect.width,
                        h: r.height / pageRect.height,
                    }))
                    // Empty or negative rects can appear at selection boundaries —
                    // filter them so the overlay doesn't paint zero-size divs.
                    .filter((r) => r.w > 0.001 && r.h > 0.001);
            }

            setPendingSelection({
                quotedText: quoted,
                pdfPage: pageNumber,
                pdfRects,
                x: (rect?.left ?? 0) + (rect?.width ?? 0) / 2,
                y: (rect?.top ?? 0) - 8,
            });
        };
        el.addEventListener('mouseup', handler);
        return () => el.removeEventListener('mouseup', handler);
    }, [isPdf, pageNumber]);

    // Ctrl+wheel zoom on desktop. Touch pinch is handled by the browser's
    // default viewport behaviour — we don't override it. Only attached for
    // PDF/CBZ because EPUB uses font-size for "zoom" (ER-020).
    useEffect(() => {
        const el = swipeRef.current;
        if (!el) return;
        if (!(isPdf || isCbz)) return;
        const handler = (e: WheelEvent) => {
            if (!e.ctrlKey) return;
            e.preventDefault();
            const current = typeof zoom === 'number' ? zoom : 100;
            const delta = e.deltaY > 0 ? -ZOOM_PCT_STEP : ZOOM_PCT_STEP;
            const next = Math.max(ZOOM_PCT_MIN, Math.min(ZOOM_PCT_MAX, current + delta));
            setZoom(next);
        };
        // passive:false because we preventDefault to stop the browser's own zoom.
        el.addEventListener('wheel', handler, { passive: false });
        return () => el.removeEventListener('wheel', handler);
    }, [isPdf, isCbz, zoom, setZoom]);

    // --- Per-book preference overrides (ER-012) ---
    // Fetched on mount and applied to the store via setters so the reader
    // shows the book-specific values. Changes to the panel afterwards update
    // the store (global defaults) as usual; explicit "Save for this book"
    // writes the current effective values back as an override.
    const [hasBookOverride, setHasBookOverride] = useState(false);
    // Track whether hydration has finished so the first-render save-button
    // state reflects the server reality, not the optimistic default.
    const [overrideHydrated, setOverrideHydrated] = useState(false);

    useEffect(() => {
        let cancelled = false;
        (async () => {
            const prefs = await getReaderPreferences(item.id).catch(() => null);
            if (cancelled) return;
            if (prefs) {
                // Apply each present field. Absent fields defer to the global
                // default already in the store.
                const s = useReaderStore.getState();
                if (prefs.spread) s.setSpread(prefs.spread);
                if (prefs.theme) s.setTheme(prefs.theme);
                if (prefs.fontFamily) s.setFontFamily(prefs.fontFamily as ReaderFontFamily);
                if (typeof prefs.fontSize === 'number') s.setFontSize(prefs.fontSize);
                if (prefs.lineHeight) s.setLineHeight(prefs.lineHeight);
                if (prefs.margin) s.setMargin(prefs.margin);
                if (typeof prefs.rtl === 'boolean') s.setRtl(prefs.rtl);
                if (prefs.zoom !== undefined) s.setZoom(prefs.zoom);
                setHasBookOverride(true);
            }
            setOverrideHydrated(true);
        })();
        return () => { cancelled = true; };
    }, [item.id]);

    const saveBookOverride = useCallback(async () => {
        const s = useReaderStore.getState();
        const payload: ReaderPreferencesPayload = {
            schemaVersion: 1,
            spread: s.spread,
            theme: s.theme,
            fontFamily: s.fontFamily,
            fontSize: s.fontSize,
            lineHeight: s.lineHeight,
            margin: s.margin,
            // ER-022 / ER-030 / ER-031: also carry the toggles / zoom / rtl so
            // "Save for this book" captures the full effective state the user
            // sees right now, not just a subset of typography fields.
            zoom: s.zoom,
            rtl: s.rtl,
        };
        try {
            await putReaderPreferences(item.id, payload);
            setHasBookOverride(true);
        } catch {
            // Silent: user can retry. A toast system would go here if the
            // project had one plumbed through the reader surface today.
        }
    }, [item.id]);

    const clearBookOverride = useCallback(async () => {
        try {
            await putReaderPreferences(item.id, null);
            setHasBookOverride(false);
        } catch {
            // Silent — see saveBookOverride.
        }
    }, [item.id]);

    // --- Bookmark load (ER-023) ---
    // The CRUD handlers themselves are defined further down — they depend on
    // `scheduleSave` / `renditionRef` which are set up later in the component.
    // Only the fetch effect lives here because it has no such dependency.
    useEffect(() => {
        let cancelled = false;
        listBookmarks(item.id)
            .then((rows) => { if (!cancelled) setBookmarks(rows); })
            .catch(() => { /* network failure — sheet shows empty, user can retry */ });
        return () => { cancelled = true; };
    }, [item.id]);

    // --- Reading session tracking (ER-052) ---
    const [stats, setStats] = useState<ReadingSessionSummary | null>(null);
    const sessionIdRef = useRef<string | null>(null);
    // Pages turned during this session — diff between first observed page and
    // current. Used when ending the session so idle closures don't persist.
    const sessionStartPageRef = useRef<number | null>(null);
    const sessionPagesReadRef = useRef<number>(0);
    // Idle timeout: if no pointer / key activity for 5 minutes, close the
    // session so leaving the tab open overnight doesn't inflate stats.
    const idleTimerRef = useRef<number | null>(null);
    const IDLE_TIMEOUT_MS = 5 * 60 * 1000;

    // Load summary + start a session on mount. Wraps both calls so a network
    // failure on the start doesn't block the summary fetch — each is
    // independent.
    useEffect(() => {
        let cancelled = false;
        getReadingSessionSummary(item.id)
            .then((s) => { if (!cancelled) setStats(s); })
            .catch(() => { /* stats remain null — UI hides the row */ });
        startReadingSession(item.id)
            .then((sid) => { if (!cancelled) sessionIdRef.current = sid; })
            .catch(() => { /* non-fatal — stats for this visit simply won't count */ });
        return () => { cancelled = true; };
    }, [item.id]);

    // Mirror current page into a ref so the unmount cleanup can read the
    // latest value without needing to re-close over pageNumber.
    const pageNumberRef = useRef(pageNumber);
    useEffect(() => { pageNumberRef.current = pageNumber; }, [pageNumber]);

    // Track pages turned — the first observed page seeds the "start", every
    // subsequent page advances the read counter (only forward counts to avoid
    // scrubbing inflating stats).
    useEffect(() => {
        if (pageNumber <= 0) return;
        if (sessionStartPageRef.current === null) {
            sessionStartPageRef.current = pageNumber;
            return;
        }
        // Monotonic forward: only positive deltas accumulate.
        const delta = pageNumber - sessionStartPageRef.current;
        if (delta > sessionPagesReadRef.current) {
            sessionPagesReadRef.current = delta;
        }
    }, [pageNumber]);

    // Idle-timeout arming: any pointer/key activity resets the timer; when it
    // fires we close the session with whatever was read. Reader unmount
    // short-circuits through the cleanup below, so the timer only fires while
    // the reader is open but neglected.
    useEffect(() => {
        const mediaId = item.id;
        const closeOnIdle = () => {
            const sid = sessionIdRef.current;
            if (!sid) return;
            sessionIdRef.current = null;
            const pages = sessionPagesReadRef.current;
            // Fire-and-forget; a failed close is non-fatal. Server discards
            // zero-page sessions so this also cleans up walk-aways cleanly.
            endReadingSession(mediaId, sid, pages).catch(() => { /* ignore */ });
        };
        const poke = () => {
            if (idleTimerRef.current !== null) window.clearTimeout(idleTimerRef.current);
            idleTimerRef.current = window.setTimeout(closeOnIdle, IDLE_TIMEOUT_MS);
        };
        poke();
        window.addEventListener('pointermove', poke);
        window.addEventListener('keydown', poke);
        return () => {
            window.removeEventListener('pointermove', poke);
            window.removeEventListener('keydown', poke);
            if (idleTimerRef.current !== null) window.clearTimeout(idleTimerRef.current);
        };
    }, [item.id]);

    // Close on unmount (book change / reader close). Using refs in the cleanup
    // means the effect can depend on a stable dep list and still capture the
    // latest session id + page count.
    useEffect(() => {
        const mediaId = item.id;
        return () => {
            const sid = sessionIdRef.current;
            if (!sid) return;
            sessionIdRef.current = null;
            const pages = sessionPagesReadRef.current;
            endReadingSession(mediaId, sid, pages).catch(() => { /* ignore */ });
        };
    }, [item.id]);

    // Re-register the EPUB theme when the palette changes. The outer DOM
    // picks up the new CSS variables via [data-reading-theme] automatically;
    // only the sandboxed EPUB iframe needs a manual refresh since it doesn't
    // inherit the host document's custom properties. We run on the next
    // animation frame so the data-attribute flip and the iframe restyle land
    // in the same visual tick — prevents a brief dark-on-sepia flash.
    useEffect(() => {
        const root = readerRootRef.current;
        if (!root) return;
        const raf = window.requestAnimationFrame(() => {
            refreshReaderTheme(renditionRef.current, root, overridePublisher);
        });
        return () => window.cancelAnimationFrame(raf);
    }, [readerTheme, overridePublisher]);

    // Mirror slice values onto the reader root's CSS variables so any DOM
    // layer (pdf.js selection, epub.js body — via refreshReaderTheme —, future
    // zoom/RTL work) resolves from a single source of truth. Setting custom
    // properties via element.style is reactive in CSS; no component re-render
    // is required to propagate. refreshReaderTheme afterwards injects the
    // new values into the EPUB iframe, which doesn't inherit outer CSS vars.
    useEffect(() => {
        const root = readerRootRef.current;
        if (!root) return;
        root.style.setProperty('--reader-font-family', FONT_FAMILY_CSS[fontFamily]);
        root.style.setProperty('--reader-font-size', `${fontSize}%`);
        root.style.setProperty('--reader-line-height', LINE_HEIGHT_CSS[lineHeight]);
        root.style.setProperty('--reader-padding-inline', MARGIN_CSS[margin]);
        refreshReaderTheme(renditionRef.current, root, overridePublisher);
    }, [fontFamily, fontSize, lineHeight, margin, overridePublisher]);

    // When spread toggles, round the current page down to an even-starting pair
    // (1-based: 1,3,5,... are left pages in double mode) so the display lands on
    // a natural pairing. Without this, flipping mid-book at page 35 would show
    // (35, 36) but a subsequent Prev would jump to (33, 34) — slightly jarring.
    useEffect(() => {
        if (!isDouble) return;
        if (!initialPageLoaded) return;
        setPageNumber(prev => (prev % 2 === 0 ? Math.max(1, prev - 1) : prev));
    }, [isDouble, initialPageLoaded]);

    // --- Table of Contents (ER-004) ---
    // EPUB and PDF both populate this through different code paths; CBZ/CBR
    // are intentionally omitted for now because ComicInfo.xml doesn't yet
    // capture per-page chapter markers (see ereader-roadmap ER-004 follow-up).
    const [tocItems, setTocItems] = useState<TocItem[]>([]);
    const [tocOpen, setTocOpen] = useState(false);
    // currentHref lives with the TTS sleep-timer block above — the
    // end-of-chapter timer observes it and needs it declared before the
    // TTS hook runs. It's still set from the TOC/relocation paths below.
    // Focus-restore target when the drawer closes, per the TocDrawer contract.
    const tocButtonRef = useRef<HTMLButtonElement | null>(null);

    // Swipe target for PDF/CBZ. EPUB has no swipe overlay — native touch
    // interactions land directly on the iframe; page-turn on touch is not
    // wired for EPUB today. The hook itself is bound further down — after
    // changePage is memoised — so the handlers don't churn the effect every
    // render.
    const swipeRef = useRef<HTMLDivElement | null>(null);

    // Reader-root reference for refreshReaderTheme. The root carries the
    // [data-reader-root] attribute + ER-021's [data-reading-theme], which is
    // where the CSS variables resolve.
    const readerRootRef = useRef<HTMLDivElement | null>(null);

    // --- Immersive + fullscreen (ER-007) ---
    // Immersive hides the header chrome + PageControls pill. It's a per-session
    // toggle (no persistence until ER-010 lands). When the chrome is hidden,
    // pointer/mouse movement briefly reveals it again — the chromeRevealTimer
    // clears the reveal after a short idle.
    const [immersive, setImmersive] = useState(false);
    const [chromeRevealed, setChromeRevealed] = useState(false);
    const chromeRevealTimer = useRef<number | null>(null);
    const [fullscreen, setFullscreen] = useState(false);

    // Chrome is visible when NOT immersive, OR when immersive but the user
    // recently moved the pointer. Compute once so every chrome element stays
    // in lockstep.
    const chromeVisible = !immersive || chromeRevealed;

    // Reveal-on-move: only binds while immersive is true so the non-immersive
    // path has zero event-listener cost.
    useEffect(() => {
        if (!immersive) return;
        const reveal = () => {
            setChromeRevealed(true);
            if (chromeRevealTimer.current !== null) {
                window.clearTimeout(chromeRevealTimer.current);
            }
            chromeRevealTimer.current = window.setTimeout(() => {
                setChromeRevealed(false);
                chromeRevealTimer.current = null;
            }, 2000);
        };
        window.addEventListener('pointermove', reveal);
        return () => {
            window.removeEventListener('pointermove', reveal);
            if (chromeRevealTimer.current !== null) {
                window.clearTimeout(chromeRevealTimer.current);
                chromeRevealTimer.current = null;
            }
        };
    }, [immersive]);

    // Keep `fullscreen` in sync with the browser's actual fullscreen element —
    // the user can exit via F11 or Esc without going through our button.
    useEffect(() => {
        const handler = () => setFullscreen(Boolean(getFsElement()));
        document.addEventListener('fullscreenchange', handler);
        document.addEventListener('webkitfullscreenchange', handler);
        return () => {
            document.removeEventListener('fullscreenchange', handler);
            document.removeEventListener('webkitfullscreenchange', handler);
        };
    }, []);

    const toggleFullscreen = useCallback(async () => {
        try {
            if (getFsElement()) {
                await exitFs();
            } else {
                await requestFs(document.documentElement);
            }
        } catch {
            // Browser refused (e.g., required-user-gesture violation) — the
            // fullscreenchange listener will keep our state correct either way.
        }
    }, []);

    const toggleImmersive = useCallback(() => {
        setImmersive(v => {
            const next = !v;
            // Entering immersive: start with chrome hidden so the mode is visible
            // immediately. Leaving immersive: drop any pending reveal timer.
            if (!next && chromeRevealTimer.current !== null) {
                window.clearTimeout(chromeRevealTimer.current);
                chromeRevealTimer.current = null;
            }
            setChromeRevealed(false);
            return next;
        });
    }, []);

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
                    setIsFinished(progress.isWatched);
                    // Re-opening a previously finished book should not re-fire the
                    // auto-mark. Pre-arm the guard so the end-of-book effect stays
                    // quiet until the user explicitly un-finishes the book.
                    if (progress.isWatched) autoMarkedRef.current = true;
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

    // --- Bookmark CRUD handlers (ER-023) ---
    // Placed here because `scheduleSave` above must be declared before
    // `jumpToBookmark` captures it — putting the CRUD block earlier would
    // trigger a TDZ on render.

    const addBookmarkAtCurrent = useCallback(async () => {
        // Build the location payload per format. EPUB stores the CFI (opaque
        // to server); PDF/CBZ store the page number. Exactly one field is sent
        // so the server's one-of-two invariant holds.
        const body: { position?: number; cfi?: string } = {};
        if (isEpub && typeof location === 'string' && location.length > 0) {
            body.cfi = location;
        } else if ((isPdf || isCbz) && pageNumber > 0) {
            body.position = pageNumber;
        } else {
            return; // Not enough information yet (book hasn't loaded).
        }
        try {
            const created = await createBookmark(item.id, body);
            setBookmarks((prev) => [...prev, created]);
        } catch {
            // Non-fatal; the user can retry.
        }
    }, [isEpub, isPdf, isCbz, location, pageNumber, item.id]);

    const jumpToBookmark = useCallback((bookmark: Bookmark) => {
        setBookmarksOpen(false);
        if (bookmark.cfi && isEpub) {
            renditionRef.current?.display(bookmark.cfi);
            return;
        }
        if (bookmark.position != null && (isPdf || isCbz)) {
            const clamped = Math.min(
                Math.max(1, Math.floor(bookmark.position)),
                numPages || bookmark.position,
            );
            setPageNumber(clamped);
            if (initialPageLoaded) scheduleSave(clamped);
        }
    }, [isEpub, isPdf, isCbz, numPages, initialPageLoaded, scheduleSave]);

    const renameBookmark = useCallback(async (id: string, label: string | null) => {
        try {
            await updateBookmarkLabel(item.id, id, label);
            setBookmarks((prev) => prev.map((b) => (b.id === id ? { ...b, label } : b)));
        } catch {
            // Silent.
        }
    }, [item.id]);

    const removeBookmark = useCallback(async (id: string) => {
        const prev = bookmarks;
        // Optimistic remove; rollback on failure.
        setBookmarks(prev.filter((b) => b.id !== id));
        try {
            await deleteBookmark(item.id, id);
        } catch {
            setBookmarks(prev);
        }
    }, [bookmarks, item.id]);

    // --- Highlight handlers (ER-040 / ER-041) ---
    // Selection capture + CRUD. Placed after scheduleSave/renditionRef so the
    // render-to-rendition flow is already established.

    // Fetch highlights on mount. Empty list = nothing to render.
    useEffect(() => {
        let cancelled = false;
        listHighlights(item.id)
            .then((rows) => { if (!cancelled) setHighlights(rows); })
            .catch(() => { /* silent — next save attempt will retry */ });
        return () => { cancelled = true; };
    }, [item.id]);

    // Save from a pending selection → create + render + clear the toolbar.
    // Returns the created highlight id on success so the "Highlight + note"
    // flow can open the drawer pointing at the new row.
    const saveHighlight = useCallback(async (colour: HighlightColour): Promise<string | null> => {
        const pending = pendingSelection;
        if (!pending) return null;
        setPendingSelection(null);

        const body = pending.epubCfi
            ? {
                colour,
                quotedText: pending.quotedText,
                location: { type: 'epub' as const, cfi: pending.epubCfi },
            }
            : pending.pdfPage != null
            ? {
                colour,
                quotedText: pending.quotedText,
                location: {
                    type: 'pdf' as const,
                    page: pending.pdfPage,
                    rects: pending.pdfRects,
                },
            }
            : null;
        if (!body) return null;

        try {
            const created = await createHighlight(item.id, body);
            setHighlights((prev) => [...prev, created]);
            return created.id;
        } catch {
            // Non-fatal; the selection is already gone by now.
            return null;
        }
    }, [pendingSelection, item.id]);

    const changeHighlightColour = useCallback(async (id: string, colour: HighlightColour) => {
        // Optimistic; rollback on failure so the UI doesn't lie about server state.
        const prev = highlights;
        setHighlights(prev.map((h) => (h.id === id ? { ...h, colour } : h)));
        try {
            await updateHighlight(item.id, id, { colour });
        } catch {
            setHighlights(prev);
        }
    }, [highlights, item.id]);

    const changeHighlightNote = useCallback(async (id: string, note: string | null) => {
        const prev = highlights;
        setHighlights(prev.map((h) => (h.id === id ? { ...h, note } : h)));
        try {
            // Server interprets empty note as "cleared"; send empty string to
            // wipe the column rather than leave the previous value behind.
            await updateHighlight(item.id, id, { note: note ?? '' });
        } catch {
            setHighlights(prev);
        }
    }, [highlights, item.id]);

    const removeHighlight = useCallback(async (id: string) => {
        const prev = highlights;
        setHighlights(prev.filter((h) => h.id !== id));
        try {
            await deleteHighlight(item.id, id);
        } catch {
            setHighlights(prev);
        }
    }, [highlights, item.id]);

    const jumpToHighlight = useCallback((h: Highlight) => {
        setHighlightsOpen(false);
        const loc = parseHighlightLocation(h.locationJson);
        if (!loc) return;
        if (loc.type === 'epub' && isEpub) {
            renditionRef.current?.display(loc.cfi);
            return;
        }
        if (loc.type === 'pdf' && isPdf) {
            const clamped = Math.min(Math.max(1, loc.page), numPages || loc.page);
            setPageNumber(clamped);
            if (initialPageLoaded) scheduleSave(clamped);
        }
    }, [isEpub, isPdf, numPages, initialPageLoaded, scheduleSave]);

    // Re-apply all highlights to the EPUB rendition whenever they change OR
    // whenever the rendition re-layouts (relocated). epub.js annotations are
    // durable across page turns but evaporate on a rendition rebuild (e.g.,
    // spread toggle) — the rebuild calls `getRendition`, which re-invokes
    // this effect through renditionRef.
    useEffect(() => {
        if (!isEpub) return;
        type Annotations = {
            add: (type: string, cfiRange: string, data: unknown, cb: () => void, className: string, styles: Record<string, string>) => unknown;
            remove: (cfiRange: string, type: string) => void;
        };
        const rendition = renditionRef.current as unknown as { annotations?: Annotations } | null;
        const ann = rendition?.annotations;
        if (!ann) return;

        const applied: string[] = [];
        for (const h of highlights) {
            const loc = parseHighlightLocation(h.locationJson);
            if (!loc || loc.type !== 'epub') continue;
            try {
                ann.add(
                    'highlight',
                    loc.cfi,
                    { id: h.id },
                    () => { /* epub.js click hook: intentionally no-op, sheet is the primary UI */ },
                    `softmedia-highlight-${h.id}`,
                    { 'background-color': swatchFor(h.colour), 'fill-opacity': '0.6' },
                );
                applied.push(loc.cfi);
            } catch {
                /* cfi already annotated or invalid — skip */
            }
        }
        return () => {
            // Clean up on every effect re-run so colour / deletion changes
            // don't leave stale overlays behind.
            for (const cfi of applied) {
                try { ann.remove(cfi, 'highlight'); } catch { /* ignore */ }
            }
        };
    }, [isEpub, highlights]);

    // --- In-book search (ER-024) ---
    // Format-specific providers below. Both are idempotent: they tag the
    // current query with a monotonically-increasing sequence, and abandon
    // their result if a newer query has started by the time they finish.

    const runSearch = useCallback(async (query: string) => {
        const trimmed = query.trim();
        const mySeq = ++searchSeqRef.current;

        if (trimmed.length < 2) {
            setSearchHits([]);
            setSearchBusy(false);
            return;
        }
        setSearchBusy(true);

        try {
            let hits: SearchHit[] = [];
            if (isEpub && renditionRef.current) {
                hits = await searchEpub(renditionRef.current, trimmed, () => mySeq === searchSeqRef.current);
            } else if (isPdf && pdfDocRef.current) {
                hits = await searchPdf(pdfDocRef.current, trimmed, () => mySeq === searchSeqRef.current);
            }
            if (mySeq !== searchSeqRef.current) return; // superseded
            setSearchHits(hits);
        } catch {
            if (mySeq === searchSeqRef.current) setSearchHits([]);
        } finally {
            if (mySeq === searchSeqRef.current) setSearchBusy(false);
        }
    }, [isEpub, isPdf]);

    const jumpToSearchHit = useCallback((hit: SearchHit) => {
        setSearchOpen(false);
        if (isEpub && renditionRef.current) {
            renditionRef.current.display(hit.key);
            return;
        }
        if (isPdf && hit.key.startsWith('pdf:page:')) {
            const n = parseInt(hit.key.slice('pdf:page:'.length), 10);
            if (!Number.isNaN(n)) {
                const clamped = Math.min(Math.max(1, n), numPages || n);
                setPageNumber(clamped);
                if (initialPageLoaded) scheduleSave(clamped);
            }
        }
    }, [isEpub, isPdf, numPages, initialPageLoaded, scheduleSave]);

    // Disabled reason when the current format doesn't support text search.
    // CBZ/CBR pages are rasterised — OCR would be a separate milestone.
    const searchDisabledReason = isCbz
        ? 'Search isn\u2019t available for comic archives in this version.'
        : null;

    // For PDF, the "current chapter" is whichever outline entry points at the
    // highest page number <= pageNumber. Keeping currentHref in sync with that
    // lets the drawer's highlight move as the user pages forward. Computed in
    // an effect (rather than the PDF-load callback) because it has to recompute
    // on every page turn.
    useEffect(() => {
        if (!isPdf || tocItems.length === 0) return;
        const flatten = (items: TocItem[], acc: TocItem[] = []): TocItem[] => {
            for (const it of items) {
                acc.push(it);
                if (it.children) flatten(it.children, acc);
            }
            return acc;
        };
        const candidates = flatten(tocItems)
            .filter(i => typeof i.pageNumber === 'number' && i.pageNumber <= pageNumber)
            .sort((a, b) => (b.pageNumber ?? 0) - (a.pageNumber ?? 0));
        setCurrentHref(candidates[0]?.href ?? null);
    }, [isPdf, pageNumber, tocItems]);

    const onTocJump = useCallback((item: TocItem) => {
        setTocOpen(false);
        if (isEpub && item.href) {
            renditionRef.current?.display(item.href);
            return;
        }
        if ((isPdf || isCbz) && typeof item.pageNumber === 'number') {
            const clamped = Math.min(Math.max(1, Math.floor(item.pageNumber)), numPages || item.pageNumber);
            setPageNumber(clamped);
            if (initialPageLoaded) scheduleSave(clamped);
        }
    }, [isEpub, isPdf, isCbz, numPages, initialPageLoaded, scheduleSave]);

    // --- Finished / mark-as-read ---

    // Manual toggle. Optimistic update so the button flips instantly; on server
    // failure we revert so the UI reflects reality. The auto-fire guard is also
    // flipped here: marking finished pre-arms the guard (so we don't fire again
    // if the user happens to be on the last page); marking unfinished clears it
    // so a subsequent re-read can trigger end-of-book detection.
    const toggleFinished = useCallback(async () => {
        const next = !isFinished;
        setIsFinished(next);
        autoMarkedRef.current = next;
        try {
            await markFinished(item.id, next);
        } catch {
            setIsFinished(!next);
            autoMarkedRef.current = !next;
        }
    }, [isFinished, item.id]);

    // Auto-mark finished when the user reaches the end of the book. Fires exactly
    // once per reader session via autoMarkedRef — scrubbing to the end and back
    // does not re-trigger. For PDF/CBZ we only fire after numPages is known
    // (avoids a false trigger during the transient 0-page-count window before
    // the document loads). For EPUB we use percentage ≥ 98 because the final
    // location is often a licence page users don't actually read to.
    useEffect(() => {
        if (autoMarkedRef.current) return;
        if (!initialPageLoaded && !initialLocationLoaded) return;

        const reachedEnd =
            ((isPdf || isCbz) && numPages > 0 && pageNumber >= numPages) ||
            (isEpub && percentage >= 98);
        if (!reachedEnd) return;

        autoMarkedRef.current = true;
        setIsFinished(true);
        markFinished(item.id, true).catch(() => {
            // Network blip — revert the guard and the optimistic flag so the
            // next page transition gets another chance to mark.
            autoMarkedRef.current = false;
            setIsFinished(false);
        });
    }, [item.id, isPdf, isCbz, isEpub, numPages, pageNumber, percentage,
        initialPageLoaded, initialLocationLoaded]);

    // --- PDF handlers ---

    // react-pdf v10 passes the full PDFDocumentProxy here. We use it for the
    // page count, the outline, and search indexing. The proxy is stashed in
    // pdfDocRef so ER-024's search provider can call getPage/getTextContent
    // without depending on load-time timing.
    const onPdfLoaded = useCallback(async (pdf: PdfDocProxy) => {
        setNumPages(pdf.numPages);
        pdfDocRef.current = pdf;

        // Build the TOC best-effort. Any failure (malformed outline, missing
        // destinations) is non-fatal — the drawer simply won't surface for
        // that document. Destinations resolve asynchronously because the PDF
        // format lets an outline reference a named destination that needs a
        // separate lookup to yield a page index.
        try {
            const outline = await pdf.getOutline();
            if (!outline || outline.length === 0) {
                setTocItems([]);
                return;
            }

            const mapItem = async (entry: PdfOutlineItem): Promise<TocItem | null> => {
                let pageNumber: number | undefined;
                try {
                    const dest = typeof entry.dest === 'string'
                        ? await pdf.getDestination(entry.dest)
                        : entry.dest;
                    const pageRef = Array.isArray(dest) ? dest[0] as PdfOutlineRef : undefined;
                    if (pageRef && typeof pageRef === 'object') {
                        const idx = await pdf.getPageIndex(pageRef);
                        if (Number.isFinite(idx)) pageNumber = idx + 1;
                    }
                } catch {
                    // Leaving pageNumber undefined makes the row a no-op on
                    // click; we still show the label so nested parents render.
                }

                const children = entry.items && entry.items.length > 0
                    ? (await Promise.all(entry.items.map(mapItem))).filter((c): c is TocItem => c !== null)
                    : undefined;

                return {
                    label: (entry.title ?? '').trim() || '(untitled)',
                    // PDF TOC entries have no stable href — synthesise one from
                    // the page number so current-chapter matching can work in
                    // the future. Empty when unresolved.
                    href: pageNumber !== undefined ? `pdf:page:${pageNumber}` : '',
                    pageNumber,
                    children,
                };
            };

            const mapped = (await Promise.all(outline.map(mapItem)))
                .filter((i): i is TocItem => i !== null);
            setTocItems(mapped);
        } catch {
            setTocItems([]);
        }
    }, []);

    // --- Page navigation (shared PDF + CBZ) ---
    // ER-002: in double-spread mode, one nav step advances by two pages so the
    // user always moves between spreads, not within one.
    const changePage = useCallback((offset: number) => {
        const step = isDouble ? offset * 2 : offset;
        setPageNumber(prev => {
            const next = Math.min(Math.max(1, prev + step), numPages || Infinity);
            if (next !== prev && initialPageLoaded) {
                scheduleSave(next);
            }
            return next;
        });
    }, [numPages, scheduleSave, initialPageLoaded, isDouble]);

    // --- Touch swipe for PDF/CBZ (ER-006 + ER-031 RTL flip) ---
    // In LTR, swipe-left advances (thumb drags the page leftward to reveal the
    // next). In RTL that convention inverts — swipe-right advances because
    // reading flow runs right-to-left. Depend on rtl so the handlers rebind
    // when the user toggles direction.
    // highlightModeActive short-circuits both callbacks so click-drag in the
    // PDF text layer performs a text selection (native browser behaviour)
    // instead of turning the page. Arrow keys / PageControls chevrons still
    // work for navigation while highlight mode is on.
    const onSwipeLeft = useCallback(() => {
        if (highlightModeActive) return;
        if (isPdf || isCbz) changePage(rtl ? -1 : 1);
    }, [isPdf, isCbz, changePage, rtl, highlightModeActive]);
    const onSwipeRight = useCallback(() => {
        if (highlightModeActive) return;
        if (isPdf || isCbz) changePage(rtl ? 1 : -1);
    }, [isPdf, isCbz, changePage, rtl, highlightModeActive]);
    useSwipe(swipeRef, { onSwipeLeft, onSwipeRight });

    // --- EPUB handlers ---
    const locationChanged = useCallback((epubcifi: string | number) => {
        setLocation(epubcifi);
        if (initialLocationLoaded && typeof epubcifi === 'string') {
            scheduleSave(0, epubcifi);
        }
    }, [scheduleSave, initialLocationLoaded]);

    const tocChanged = useCallback((toc: NavItem[]) => {
        tocRef.current = toc;
        // Mirror the epub.js NavItem tree into our shared TocItem shape so the
        // drawer can render the same data without learning about epub.js types.
        // Labels are defensive-trimmed — publisher EPUBs frequently pad TOC
        // entries with leading whitespace or HTML artefacts.
        const mapNav = (node: NavItem): TocItem => ({
            label: (node.label ?? '').trim() || '(untitled)',
            href: node.href ?? '',
            children: node.subitems?.map(mapNav),
        });
        setTocItems(toc.map(mapNav));
    }, []);

    const getRendition = useCallback((rendition: Rendition) => {
        renditionRef.current = rendition;

        // ER-011 / ER-022 / ER-040: theme application, publisher-style
        // stripping, and selection capture all happen per-chunk from the
        // content hook. The hook fires once per spine chunk as it renders,
        // which is the earliest reliable point where the chunk's document
        // exists. Doing selection capture here (rather than through
        // `rendition.on('selected')`) is more robust — the epub.js event
        // isn't reliably emitted across versions; mouseup/touchend inside
        // the iframe document always fires.
        type ContentsLike = {
            document: Document;
            // epub.js exposes cfiFromRange on the Contents wrapper for
            // translating an iframe DOM Range into an EPUB CFI.
            cfiFromRange?: (range: Range) => string;
        };
        const hooks = (rendition as unknown as {
            hooks?: { content?: { register: (fn: (contents: ContentsLike) => void) => void } };
        }).hooks;
        hooks?.content?.register((contents) => {
            const state = useReaderStore.getState();
            const css = buildEpubThemeCss(
                readReaderTokens(readerRootRef.current),
                state.overridePublisher,
            );
            // Strip publisher styles first so they can't clobber ours via
            // cascade order, THEN install the theme. Our sentinel-marked
            // style survives the strip.
            if (state.overridePublisher) {
                stripPublisherStyles(contents.document);
            }
            applyThemeStyleTo(contents.document, css);

            // ── Selection capture ───────────────────────────────────────
            // Fires the floating toolbar whenever the user finishes a
            // selection inside this chunk. Listens on both mouseup (desktop)
            // and touchend (tablet). No cleanup needed — the iframe document
            // is torn down when the chunk unloads.
            const doc = contents.document;
            const onSelectionEnd = () => {
                const sel = doc.defaultView?.getSelection?.() ?? doc.getSelection?.();
                const quoted = sel?.toString().trim() ?? '';
                if (quoted.length === 0) return;
                const range = sel?.rangeCount ? sel.getRangeAt(0) : null;
                if (!range) return;

                // Build the CFI. epub.js's cfiFromRange handles the spine
                // item prefix automatically; we just pass the DOM range.
                let cfi = '';
                try {
                    cfi = contents.cfiFromRange?.(range) ?? '';
                } catch {
                    cfi = '';
                }
                if (!cfi) return;

                // getBoundingClientRect inside the iframe is relative to the
                // iframe; add the iframe's outer-viewport rect so the floating
                // toolbar lands over the selection on the outer page.
                const rect = range.getBoundingClientRect();
                const iframe = readerRootRef.current?.querySelector('iframe');
                const iframeRect = iframe?.getBoundingClientRect();
                const x = rect.left + (iframeRect?.left ?? 0) + rect.width / 2;
                const y = rect.top + (iframeRect?.top ?? 0) - 8;
                setPendingSelection({
                    quotedText: quoted,
                    epubCfi: cfi,
                    x,
                    y,
                });
            };
            doc.addEventListener('mouseup', onSelectionEnd);
            doc.addEventListener('touchend', onSelectionEnd);

            // ── TTS pick-start click ────────────────────────────────────
            // When the reader is in "armed" mode (Listen clicked but not
            // yet speaking), the next tap inside the content starts TTS
            // from that point. Uses `caretRangeFromPoint` (Blink/WebKit)
            // with a caretPositionFromPoint fallback so we can resolve the
            // click into a DOM position, then lift it to a CFI via epub.js.
            // Gate with `ttsArmingRef` so the listener stays installed but
            // inert during normal reading — simpler than attaching /
            // detaching on every arm toggle.
            const onPickStartClick = (ev: MouseEvent) => {
                if (!ttsArmingRef.current) return;
                ev.preventDefault();
                ev.stopPropagation();
                const pickDoc = (ev.target as Element | null)?.ownerDocument ?? doc;
                type PointApi = Document & {
                    caretRangeFromPoint?: (x: number, y: number) => Range | null;
                    caretPositionFromPoint?: (
                        x: number, y: number,
                    ) => { offsetNode: Node; offset: number } | null;
                };
                const api = pickDoc as PointApi;
                let range: Range | null = null;
                if (api.caretRangeFromPoint) {
                    range = api.caretRangeFromPoint(ev.clientX, ev.clientY);
                } else if (api.caretPositionFromPoint) {
                    const p = api.caretPositionFromPoint(ev.clientX, ev.clientY);
                    if (p) {
                        range = pickDoc.createRange();
                        range.setStart(p.offsetNode, p.offset);
                        range.collapse(true);
                    }
                }
                if (!range) return;
                let cfi = '';
                try { cfi = contents.cfiFromRange?.(range) ?? ''; } catch { /* ignore */ }
                if (!cfi) return;
                startTtsFromCfiRef.current(cfi);
            };
            doc.addEventListener('click', onPickStartClick);
        });

        rendition.on('relocated', (loc: Location) => {
            // Percentage is typically 0–1; some builds expose 0–100. Normalise.
            const rawPct = loc.start?.percentage ?? 0;
            const pctNormalised = rawPct <= 1 ? rawPct : rawPct / 100;
            setPercentage(Math.max(0, Math.min(100, Math.round(pctNormalised * 100))));

            // Page-number update depends on HOW we got here. When the user
            // clicked next/prev (or pressed an arrow), epub.js advances one
            // spread — which is exactly +1 page from the user's perspective,
            // regardless of how many chars or locations that spread happens to
            // contain. Reading `loc.start.location` instead would cause the
            // number to jump 2-5 at a time on typical viewports. For jumps and
            // external relocations (initial load, TOC click) we derive from
            // percentage so the number lands in roughly the right place.
            const action = epubNavActionRef.current;
            epubNavActionRef.current = null;
            const total = epubTotalPagesRef.current;
            if (action === 'next') {
                setEpubCurrentPage(p => total > 0 ? Math.min(p + 1, total) : p + 1);
            } else if (action === 'prev') {
                setEpubCurrentPage(p => Math.max(1, p - 1));
            } else if (action === 'jump') {
                // Jump target was set optimistically by epubJumpToPage; keep it.
            } else {
                // External relocation — only sync once we have a total.
                if (total > 0) {
                    setEpubCurrentPage(Math.max(1, Math.ceil(pctNormalised * total)));
                }
            }

            const href = loc.start?.href;
            if (href) {
                setCurrentHref(href);
                const base = href.split('#')[0];
                const match = tocRef.current.find(t => {
                    const tBase = t.href.split('#')[0];
                    return tBase === base || base.endsWith(tBase);
                });
                setCurrentChapter(match?.label?.trim() ?? null);
            }
        });

        // Build the CFI→page index so we can paginate. 1024 chars per location is
        // the epub.js default — gives a page count that matches a typical paperback
        // within an order of magnitude without being prohibitively slow to build.
        // Runs after book.ready so the spine is loaded.
        // The cast goes via `unknown` because epub.js's shipped `Book` type
        // doesn't expose `locations.total`, `locations.percentageFromCfi`, or
        // `rendition.currentLocation` — they exist at runtime but not in the
        // .d.ts.
        type LocationsApi = {
            generate: (chars: number) => Promise<string[]>;
            total: number;
            percentageFromCfi: (cfi: string) => number;
        };
        type EpubBook = {
            ready: Promise<void>;
            locations: LocationsApi;
        };
        type CurrentLocation = { start?: { cfi?: string } };
        const book = rendition.book as unknown as EpubBook;
        const renditionWithCurrent = rendition as unknown as {
            currentLocation: () => CurrentLocation | null | undefined;
        };
        book.ready
            .then(() => book.locations.generate(1024))
            .then(() => {
                const total = book.locations.total ?? 0;
                if (total <= 0) return;
                setEpubTotalPages(total);

                // CRITICAL: `relocated` fires on initial resume BEFORE this
                // promise resolves, and at that moment loc.start.percentage
                // is 0 (no locations index exists yet to compute from). So
                // both `percentage` state and `epubCurrentPage` are wrong
                // until something triggers a recompute. Without this block
                // the counter is stuck at "1 / total" for the whole session.
                //
                // Now that the index is built, re-derive both values from
                // the rendition's current CFI directly.
                try {
                    const cfi = renditionWithCurrent.currentLocation()?.start?.cfi;
                    if (!cfi) return;
                    const pct = book.locations.percentageFromCfi(cfi);
                    if (!Number.isFinite(pct)) return;
                    const pctClamped = Math.max(0, Math.min(1, pct));
                    setPercentage(Math.round(pctClamped * 100));
                    setEpubCurrentPage(Math.max(1, Math.ceil(pctClamped * total)));
                    // Mark the one-shot resume-sync effect as already done
                    // so it doesn't stomp back on top with stale data.
                    epubPageSyncedRef.current = true;
                } catch {
                    // Fall through — the resume-sync effect will take over.
                }
            })
            .catch(() => {
                // Failure leaves us on the percentage-based label — non-fatal.
            });
    }, []);

    const epubPrev = useCallback(() => {
        // Flag the upcoming relocation as user-initiated so the `relocated`
        // handler can increment/decrement the page counter by exactly 1.
        epubNavActionRef.current = 'prev';
        renditionRef.current?.prev();
    }, []);
    const epubNext = useCallback(() => {
        epubNavActionRef.current = 'next';
        renditionRef.current?.next();
    }, []);

    // Manual page jump. We map page → percentage rather than using
    // cfiFromLocation, because our page counter is a click counter (1 click =
    // 1 page) — not a chars-per-location index. `cfiFromPercentage` lands us
    // at the correct fraction of the book. The optimistic setEpubCurrentPage
    // here is kept by the `relocated` handler via the 'jump' action flag.
    const epubJumpToPage = useCallback((page: number) => {
        const rendition = renditionRef.current;
        if (!rendition || epubTotalPages <= 0) return;
        const clamped = Math.min(Math.max(1, Math.floor(page)), epubTotalPages);
        const book = rendition.book as unknown as {
            locations: { cfiFromPercentage: (p: number) => string | null }
        };
        const cfi = book.locations.cfiFromPercentage(clamped / epubTotalPages);
        if (cfi) {
            epubNavActionRef.current = 'jump';
            setEpubCurrentPage(clamped);
            rendition.display(cfi);
        }
    }, [epubTotalPages]);

    // Iframe-internal keyup forwarder. The window-level keydown handler below
    // can't reach arrow presses when focus is inside the book's content
    // iframe, so EpubView pipes those events back out through `onIframeKeyUp`.
    const epubHandleIframeKey = useCallback((event: KeyboardEvent) => {
        if (event.repeat) return;
        if (event.key === 'ArrowRight' || event.key === 'PageDown') {
            epubNext();
        } else if (event.key === 'ArrowLeft' || event.key === 'PageUp') {
            epubPrev();
        }
    }, [epubNext, epubPrev]);

    // --- Keyboard navigation (all formats) ---
    useEffect(() => {
        const handler = (e: KeyboardEvent) => {
            // Ignore when the user is typing in a text field (including our own
            // page-number input).
            const target = e.target as HTMLElement | null;
            if (target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA') return;

            // OS-level key auto-repeat: when a user holds an arrow key, keydown
            // fires continuously. Reading that as page turns chews through the
            // book. Single presses work because `e.repeat` is false on the first.
            if (e.repeat) return;

            // ER-023: `b` adds a bookmark at the current position. Single-key
            // shortcut: conflict-free because the earlier INPUT/TEXTAREA check
            // already filters out typing contexts.
            if (e.key === 'b' || e.key === 'B') {
                addBookmarkAtCurrent();
                e.preventDefault();
                return;
            }

            // ER-024: `/` opens in-book search, matching vim / terminal muscle
            // memory. Only meaningful when the format supports text search —
            // skipping CBZ avoids an empty drawer.
            if (e.key === '/' && !isCbz) {
                setSearchOpen(true);
                e.preventDefault();
                return;
            }

            // `h` toggles highlight mode — quick way to enter/exit without
            // reaching for the header button. Hidden for CBZ since there's
            // no text layer to highlight.
            if ((e.key === 'h' || e.key === 'H') && !isCbz) {
                setHighlightModeActive((v) => !v);
                e.preventDefault();
                return;
            }

            // `p` pauses / resumes TTS while active. No-op when TTS isn't
            // running so the key stays inert during normal reading.
            if ((e.key === 'p' || e.key === 'P') && isEpub && tts.supported) {
                if (tts.isPaused) {
                    tts.resume();
                    e.preventDefault();
                    return;
                }
                if (tts.isSpeaking) {
                    tts.pause();
                    e.preventDefault();
                    return;
                }
            }

            // `[` / `]` skip one sentence back / forward during TTS. Chosen
            // over arrow keys so arrow-driven page turns remain the same
            // while listening; keys picked for their proximity on US
            // keyboards and clear left/right semantic (bracket orientation).
            if ((e.key === '[' || e.key === ']') && isEpub && tts.supported
                && (tts.isSpeaking || tts.isPaused)) {
                tts.skip(e.key === '[' ? -1 : 1);
                e.preventDefault();
                return;
            }

            // ER-054: power-user shortcuts. Routed off the single-char key
            // rather than keyCode so the set behaves across layouts. Match is
            // case-insensitive because most users don't distinguish between
            // `t` and `T` when pressing a shortcut.
            const ch = e.key.length === 1 ? e.key.toLowerCase() : e.key;

            // Help sheet — `?` is Shift+/ on US layouts; match both explicit
            // `?` and the Shift+/ combo.
            if (ch === '?' || (e.shiftKey && ch === '/')) {
                setHelpOpen(true);
                e.preventDefault();
                return;
            }
            // TOC (`t`) — only when data is available; otherwise the key is
            // a no-op rather than opening an empty drawer.
            if (ch === 't' && tocItems.length > 0) {
                setTocOpen(true);
                e.preventDefault();
                return;
            }
            // Fullscreen / immersive / theme cycle.
            if (ch === 'f') {
                toggleFullscreen();
                e.preventDefault();
                return;
            }
            if (ch === 'i') {
                toggleImmersive();
                e.preventDefault();
                return;
            }
            if (ch === 'z') {
                // Cycle through the three reading themes. The store clamps
                // unknown values, so a future 4th theme slots in by extending
                // this array.
                const cycle: Array<ReaderTheme> = ['dark', 'sepia', 'high-contrast'];
                const nextIdx = (cycle.indexOf(readerTheme) + 1) % cycle.length;
                setReaderTheme(cycle[nextIdx]);
                e.preventDefault();
                return;
            }
            // Font size (EPUB) or zoom (PDF/CBZ). `+` arrives as `+` on most
            // layouts but `=` without shift on US — accept both.
            if (ch === '+' || ch === '=') {
                if (isEpub) {
                    setFontSize(fontSize + 10);
                } else if (isPdf || isCbz) {
                    const current = typeof zoom === 'number' ? zoom : 100;
                    setZoom(Math.min(400, current + 25));
                }
                e.preventDefault();
                return;
            }
            if (ch === '-' || ch === '_') {
                if (isEpub) {
                    setFontSize(fontSize - 10);
                } else if (isPdf || isCbz) {
                    const current = typeof zoom === 'number' ? zoom : 100;
                    setZoom(Math.max(50, current - 25));
                }
                e.preventDefault();
                return;
            }

            if (e.key === 'Escape') {
                // ER-007: cascade so Escape peels one layer at a time. The
                // TocDrawer installs a capture-phase Escape handler that runs
                // first and closes the drawer without ever reaching here.
                if (ttsArming) {
                    // Highest priority after overlays: a stray Esc mustn't
                    // leap past a pending pick-start and collapse the
                    // immersive/back-nav cascade.
                    setTtsArming(false);
                    e.preventDefault();
                    return;
                }
                if (getFsElement()) {
                    // Exiting fullscreen is asynchronous; return so the user's
                    // next Escape keystroke handles the next layer. The browser
                    // itself would exit fullscreen on this Escape regardless —
                    // we invoke explicitly to keep our state tracker in sync.
                    exitFs();
                    return;
                }
                if (immersive) {
                    toggleImmersive();
                    return;
                }
                // Last Escape layer leaves the reader — to the book's detail page,
                // never browser history (same rule as the Close button above).
                navigate(playerBackTarget(item));
                return;
            }

            // ER-031: RTL inverts the arrow→direction mapping. PageUp/PageDown
            // stay absolute (Up is always back, Down is always forward) — they're
            // spatial, not directional, so flipping them would fight muscle memory.
            const rightIsForward = !rtl;
            const forward = (rightIsForward && e.key === 'ArrowRight')
                || (!rightIsForward && e.key === 'ArrowLeft')
                || e.key === 'PageDown';
            const backward = (rightIsForward && e.key === 'ArrowLeft')
                || (!rightIsForward && e.key === 'ArrowRight')
                || e.key === 'PageUp';
            if (!forward && !backward) return;

            if (isPdf || isCbz) {
                changePage(forward ? 1 : -1);
            } else if (isEpub) {
                if (forward) epubNext();
                else epubPrev();
            }
            e.preventDefault();
            e.stopPropagation();
        };
        window.addEventListener('keydown', handler);
        return () => window.removeEventListener('keydown', handler);
    }, [
        isPdf, isCbz, isEpub, changePage, navigate, epubNext, epubPrev,
        immersive, toggleImmersive, rtl, addBookmarkAtCurrent, item.id,
        // ER-054 deps
        tocItems.length, toggleFullscreen, readerTheme, setReaderTheme,
        fontSize, setFontSize, zoom, setZoom,
        // TTS pause/resume (p), pick-start cancel (Esc)
        tts, ttsArming,
    ]);

    const canPrev = pageNumber > 1;
    const canNext = numPages > 0 && pageNumber < numPages;

    // EPUB label prefers the page-based form once the locations index is built.
    // Until then it shows the percentage so the user still sees progress.
    // Spread labelling for EPUB is left to epub.js's own spread rendering —
    // the page counter remains a single-number read because epub.js advances
    // by a full spread per nav, which our counter already models as +1.
    const epubLabel = epubTotalPages > 0
        ? `${epubCurrentPage || 1} / ${epubTotalPages}`
        : `${percentage}%`;

    // ER-002: PDF/CBZ label reads "12–13 / 340" in spread mode when the pair
    // fits, and falls back to single-number form at the last odd page.
    const pdfCbzLabel = (() => {
        if (numPages <= 0) return `${pageNumber}`;
        if (isDouble && pageNumber + 1 <= numPages) {
            return `${pageNumber}–${pageNumber + 1} / ${numPages}`;
        }
        return `${pageNumber} / ${numPages}`;
    })();

    return (
        <div
            ref={readerRootRef}
            data-reader-root
            data-reading-theme={readerTheme}
            data-immersive={immersive ? 'true' : 'false'}
            data-fullscreen={fullscreen ? 'true' : 'false'}
            className="fixed inset-0 bg-gray-900 z-50 flex flex-col"
        >
            {/* Header. When immersive is active, the header slides out of view
                but stays interactive on reveal. `aria-hidden` toggles so screen
                readers don't announce hidden controls. */}
            <div
                data-no-swipe
                aria-hidden={!chromeVisible}
                className={`h-14 bg-gray-800 flex items-center justify-between px-4 shadow-md z-10 transition-opacity duration-200 ${
                    chromeVisible ? 'opacity-100' : 'opacity-0 pointer-events-none'
                }`}
            >
                <h2 className="text-white font-medium truncate">{item.title}</h2>
                <div className="flex items-center gap-1">
                    {tocItems.length > 0 && (
                        <button
                            ref={tocButtonRef}
                            type="button"
                            aria-label="Table of contents"
                            aria-expanded={tocOpen}
                            title={currentChapter ? `Contents · ${currentChapter}` : 'Contents'}
                            onClick={() => setTocOpen(true)}
                            className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            <List size={24} />
                        </button>
                    )}
                    {/* Listen is a tri-state toggle:
                          idle     → click arms pick-start mode
                          armed    → click cancels (user changed their mind)
                          speaking → click stops.
                        Arming doesn't immediately start audio; the next tap
                        inside the page content becomes the TTS start point.
                        Preconditions (support, voices) are checked at arm
                        time so we don't silently swallow the click. */}
                    {isEpub && tts.supported && (
                        <button
                            type="button"
                            aria-label={
                                tts.isSpeaking || tts.isPaused ? 'Stop listening'
                                    : ttsArming ? 'Cancel Listen'
                                    : 'Listen (TTS)'
                            }
                            aria-pressed={tts.isSpeaking || tts.isPaused || ttsArming}
                            title={
                                tts.isSpeaking || tts.isPaused ? 'Stop listening'
                                    : ttsArming ? 'Tap a sentence to start listening — click again to cancel'
                                    : 'Listen'
                            }
                            onClick={() => {
                                if (tts.isSpeaking || tts.isPaused) {
                                    stopTts();
                                    return;
                                }
                                if (ttsArming) {
                                    setTtsArming(false);
                                    return;
                                }
                                if (tts.voices.length === 0) {
                                    toast.error(
                                        'No system voices found. Install your OS language pack or try a different browser.',
                                    );
                                    return;
                                }
                                setTtsArming(true);
                            }}
                            className={`min-w-[44px] min-h-[44px] p-2 rounded-full transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                                tts.isSpeaking || tts.isPaused ? 'text-green-400'
                                    : ttsArming ? 'text-amber-300 ring-2 ring-amber-300/70 animate-pulse'
                                    : 'text-white'
                            }`}
                        >
                            {tts.isSpeaking || tts.isPaused ? <Square size={20} /> : <Volume2 size={22} />}
                        </button>
                    )}
                    {/* Pause / Skip / Speed / Timer / Stop all live on the
                        TtsNowPlayingBar rendered below. The header keeps only
                        the Listen entry point so mid-listen controls don't
                        double up in two places. */}
                    {!isCbz && (
                        <button
                            ref={searchButtonRef}
                            type="button"
                            aria-label="Search in book"
                            aria-expanded={searchOpen}
                            title="Search (/)"
                            onClick={() => setSearchOpen(true)}
                            className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            <SearchIcon size={22} />
                        </button>
                    )}
                    {!isCbz && (
                        <>
                            {/* Highlight-mode toggle. When active, click-drag
                                selects text instead of turning the page —
                                essential on PDF where text layer and swipe
                                surface share a DOM. Accent + aria-pressed
                                communicate the modal state. */}
                            <button
                                type="button"
                                aria-label={highlightModeActive ? 'Exit highlight mode' : 'Enter highlight mode'}
                                aria-pressed={highlightModeActive}
                                title={highlightModeActive ? 'Exit highlight mode (h)' : 'Highlight mode (h)'}
                                onClick={() => setHighlightModeActive((v) => !v)}
                                className={`min-w-[44px] min-h-[44px] p-2 rounded-full transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                                    highlightModeActive
                                        ? 'bg-yellow-500/30 text-yellow-300 hover:bg-yellow-500/40'
                                        : 'text-white hover:bg-gray-700'
                                }`}
                            >
                                <Pen size={20} />
                            </button>
                            <button
                                ref={highlightsButtonRef}
                                type="button"
                                aria-label="Highlights list"
                                aria-expanded={highlightsOpen}
                                title={`Highlights${highlights.length > 0 ? ` (${highlights.length})` : ''}`}
                                onClick={() => setHighlightsOpen(true)}
                                className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 relative"
                            >
                                <Highlighter size={22} />
                                {highlights.length > 0 && (
                                    <span className="absolute -top-0.5 -right-0.5 bg-blue-500 text-white text-[10px] font-semibold rounded-full min-w-[18px] h-[18px] flex items-center justify-center px-1">
                                        {highlights.length > 99 ? '99+' : highlights.length}
                                    </span>
                                )}
                            </button>
                        </>
                    )}
                    <button
                        ref={bookmarksButtonRef}
                        type="button"
                        aria-label="Bookmarks"
                        aria-expanded={bookmarksOpen}
                        title={`Bookmarks${bookmarks.length > 0 ? ` (${bookmarks.length})` : ''}`}
                        onClick={() => setBookmarksOpen(true)}
                        className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 relative"
                    >
                        <BookmarkIcon size={22} />
                        {bookmarks.length > 0 && (
                            <span className="absolute -top-0.5 -right-0.5 bg-blue-500 text-white text-[10px] font-semibold rounded-full min-w-[18px] h-[18px] flex items-center justify-center px-1">
                                {bookmarks.length > 99 ? '99+' : bookmarks.length}
                            </span>
                        )}
                    </button>
                    <button
                        ref={settingsButtonRef}
                        type="button"
                        aria-label="Reader settings"
                        aria-expanded={settingsOpen}
                        title="Reader settings"
                        onClick={() => setSettingsOpen(true)}
                        className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        <Settings size={22} />
                    </button>
                    <button
                        type="button"
                        aria-label={immersive ? 'Show chrome' : 'Hide chrome (immersive)'}
                        aria-pressed={immersive}
                        title={immersive ? 'Show chrome' : 'Hide chrome (immersive)'}
                        onClick={toggleImmersive}
                        className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        {immersive ? <Eye size={22} /> : <EyeOff size={22} />}
                    </button>
                    <button
                        type="button"
                        aria-label={fullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}
                        aria-pressed={fullscreen}
                        title={fullscreen ? 'Exit fullscreen' : 'Enter fullscreen'}
                        onClick={toggleFullscreen}
                        className="min-w-[44px] min-h-[44px] p-2 rounded-full text-white transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        {fullscreen ? <Minimize2 size={22} /> : <Maximize2 size={22} />}
                    </button>
                    <button
                        type="button"
                        aria-label={isFinished ? 'Mark as unfinished' : 'Mark as finished'}
                        aria-pressed={isFinished}
                        title={isFinished ? 'Mark as unfinished' : 'Mark as finished'}
                        onClick={toggleFinished}
                        className={`min-w-[44px] min-h-[44px] p-2 rounded-full transition hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${
                            isFinished ? 'text-green-400' : 'text-white'
                        }`}
                    >
                        {isFinished ? <BookCheck size={24} /> : <BookOpen size={24} />}
                    </button>
                    <button
                        type="button"
                        aria-label="Close reader"
                        // Always the book's detail page, never browser history — the
                        // reader is launched from home rows, search, and the detail page
                        // alike, so history-back is entry-dependent (lib/backNavigation.ts).
                        onClick={() => navigate(playerBackTarget(item))}
                        className="min-w-[44px] min-h-[44px] p-2 hover:bg-gray-700 rounded-full text-white transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        <X size={24} />
                    </button>
                </div>
            </div>

            {/* Content — one shared container frames all three formats so the
                visual proportions (max-width, centering, background, controls)
                feel identical regardless of the underlying file type.
                ER-021: background uses --reader-bg so the content surface
                reflects the selected reading theme while the header chrome
                above stays dark. */}
            <div
                ref={swipeRef}
                className="flex-1 relative overflow-hidden"
                style={{ backgroundColor: 'var(--reader-bg)' }}
            >
                {/* Highlight-mode status pill. Rendered above the content
                    with pointer-events-none so it never swallows a click. The
                    yellow accent mirrors the header button's active state so
                    the two read as one mode. Click-through to exit is via the
                    header button or `h` shortcut. */}
                {highlightModeActive && (
                    <div
                        aria-hidden
                        className="absolute top-3 left-1/2 -translate-x-1/2 z-30 pointer-events-none bg-yellow-500/90 text-gray-900 text-xs font-medium px-3 py-1 rounded-full shadow-lg flex items-center gap-1.5"
                    >
                        <Pen size={12} />
                        Highlight mode — drag to select, press h to exit
                    </div>
                )}

                {/* TTS pick-start banner — matches the highlight-mode pill's
                    placement so the two modal states feel parallel. Amber
                    rather than yellow so the user can visually distinguish
                    at a glance; content names the cancel gesture explicitly
                    (Esc) because armed mode can silently eat page turns
                    otherwise. Clickable so taps on the pill itself cancel
                    instead of being interpreted as a pick target. */}
                {ttsArming && (
                    <button
                        type="button"
                        onClick={() => setTtsArming(false)}
                        className="absolute top-3 left-1/2 -translate-x-1/2 z-30 bg-amber-400/95 hover:bg-amber-400 text-gray-900 text-xs font-medium px-3 py-1 rounded-full shadow-lg flex items-center gap-1.5"
                    >
                        <Volume2 size={12} />
                        Tap a sentence to start listening — Esc to cancel
                    </button>
                )}

                {/* ER-053: brightness + warmth overlay. Sits absolutely
                    positioned over the content but *under* the PageControls
                    pill (z-10 vs z-20 / z-60). pointer-events-none keeps the
                    underlying content interactive. Rendered only when non-
                    default so a fresh install doesn't pay for an always-on
                    layer. */}
                {(brightness < 1 || warmth > 0) && (
                    <div
                        aria-hidden
                        className="absolute inset-0 z-10 pointer-events-none"
                        style={{
                            // Darken via a black layer at (1 - brightness) alpha.
                            // Warm via a low-saturation amber tint whose alpha
                            // scales with warmth. Both layers composite through
                            // one div using a linear-gradient trick wasn't
                            // worth the complexity — two nested layers read
                            // cleaner and the cost is one extra div.
                            background: `rgba(0, 0, 0, ${1 - brightness})`,
                        }}
                    >
                        {warmth > 0 && (
                            <div
                                className="absolute inset-0"
                                style={{
                                    // Soft amber — the alpha caps at 0.35 to
                                    // avoid making text unreadable even at max.
                                    background: `rgba(255, 170, 60, ${warmth * 0.35})`,
                                    mixBlendMode: 'multiply',
                                }}
                            />
                        )}
                    </div>
                )}
                {/* The shared reading surface: max-w-4xl centered column. */}
                <div className="h-full w-full max-w-4xl mx-auto relative flex items-center justify-center">
                    {isPdf && (() => {
                        // ER-030: resolve the zoom setting to concrete width/height
                        // props for react-pdf. fit-width keeps the existing
                        // behaviour; fit-page uses the height prop so the page
                        // sizes to the viewport; a numeric percent multiplies the
                        // fit-width baseline. Only width OR height is passed —
                        // react-pdf's layout breaks if both are set.
                        const baseWidth = isDouble
                            ? Math.min(780, (window.innerWidth - 60) / 2)
                            : Math.min(800, window.innerWidth - 40);
                        const pageProps: { width?: number; height?: number } = (() => {
                            if (zoom === 'fit-page') {
                                return { height: Math.max(200, window.innerHeight - 160) };
                            }
                            if (typeof zoom === 'number') {
                                return { width: baseWidth * (zoom / 100) };
                            }
                            return { width: baseWidth };
                        })();
                        return (
                            <div className="h-full w-full overflow-auto flex justify-center items-center py-4">
                                <Document
                                    file={fileUrl}
                                    // react-pdf's OnDocumentLoadSuccess callback
                                    // types the `pdf` argument as PDFDocumentProxy,
                                    // but our local `PdfDocProxy` is a narrower
                                    // structural alias covering only what we use
                                    // (numPages, getOutline, getPage, etc.). The
                                    // cast is safe — PDFDocumentProxy has a
                                    // superset of the members we consume.
                                    onLoadSuccess={onPdfLoaded as unknown as (pdf: unknown) => void}
                                    className={`shadow-2xl flex gap-2 ${isDouble && rtl ? 'flex-row-reverse' : ''}`}
                                >
                                    {/* ER-040 polish: each Page wrapped in a
                                        `position: relative` container so the
                                        PdfHighlightOverlay's `inset: 0` fills
                                        the page exactly for rect painting. */}
                                    <div className="relative">
                                        <Page
                                            pageNumber={pageNumber}
                                            renderTextLayer={true}
                                            renderAnnotationLayer={true}
                                            className="max-w-none"
                                            {...pageProps}
                                        />
                                        <PdfHighlightOverlay
                                            highlights={highlights}
                                            pageNumber={pageNumber}
                                        />
                                    </div>
                                    {isDouble && pageNumber + 1 <= (numPages || 0) && (
                                        <div className="relative">
                                            <Page
                                                pageNumber={pageNumber + 1}
                                                renderTextLayer={true}
                                                renderAnnotationLayer={true}
                                                className="max-w-none"
                                                {...pageProps}
                                            />
                                            <PdfHighlightOverlay
                                                highlights={highlights}
                                                pageNumber={pageNumber + 1}
                                            />
                                        </div>
                                    )}
                                </Document>
                            </div>
                        );
                    })()}

                    {isCbz && (() => {
                        // ER-030: CBZ zoom uses CSS sizing rather than react-pdf's
                        // width/height props. fit-width fills the column (current);
                        // fit-page uses h-full so the image caps at viewport height;
                        // numeric percent is a transform scale on top of fit-width.
                        const imgCls = (() => {
                            if (zoom === 'fit-page') {
                                return 'h-full max-h-full shadow-2xl object-contain';
                            }
                            return 'max-h-full max-w-full shadow-2xl object-contain';
                        })();
                        const imgStyle: React.CSSProperties = typeof zoom === 'number'
                            ? { transform: `scale(${zoom / 100})`, transformOrigin: 'center center' }
                            : {};
                        return (
                            <div className={`h-full w-full overflow-auto p-4 flex justify-center items-center gap-2 ${isDouble && rtl ? 'flex-row-reverse' : ''}`}>
                                {bookInfo === null ? (
                                    <div className="text-gray-400">Loading…</div>
                                ) : bookInfo.pageCount === null || bookInfo.pageCount === 0 ? (
                                    <div className="text-gray-400">This archive has no pages.</div>
                                ) : (
                                    <>
                                        <img
                                            key={`p-${pageNumber}`}
                                            src={getBookPageUrl(item.id, pageNumber)}
                                            alt={`Page ${pageNumber}`}
                                            referrerPolicy="no-referrer"
                                            className={imgCls}
                                            style={imgStyle}
                                        />
                                        {isDouble && pageNumber + 1 <= numPages && (
                                            <img
                                                key={`p-${pageNumber + 1}`}
                                                src={getBookPageUrl(item.id, pageNumber + 1)}
                                                alt={`Page ${pageNumber + 1}`}
                                                referrerPolicy="no-referrer"
                                                className={imgCls}
                                                style={imgStyle}
                                            />
                                        )}
                                    </>
                                )}
                            </div>
                        );
                    })()}

                    {isEpub && (
                        <div className="h-full w-full relative">
                            <EpubView
                                // ER-002 + ER-031: spread and direction are
                                // captured at renderTo time, so toggling
                                // either remounts via the key to rebuild the
                                // rendition. The `location` prop then re-
                                // applies the saved CFI so position is
                                // preserved across the toggle.
                                key={`epub-${spread}-${rtl ? 'rtl' : 'ltr'}`}
                                url={fileUrl}
                                location={location}
                                onLocationChange={locationChanged}
                                onTocChange={tocChanged}
                                onRenditionReady={getRendition}
                                onIframeKeyUp={epubHandleIframeKey}
                                epubOptions={{
                                    spread: isDouble ? 'always' : 'none',
                                    flow: 'paginated',
                                    direction: rtl ? 'rtl' : 'ltr',
                                }}
                                style={{ backgroundColor: 'var(--reader-bg)' }}
                            />
                        </div>
                    )}

                    {!isPdf && !isEpub && !isCbz && (
                        <div className="flex items-center justify-center h-full text-gray-500">
                            <p>Unsupported format for web reader. Please download to view.</p>
                        </div>
                    )}
                </div>

                {/* Bottom-chrome shared row — holds PageControls and (when TTS
                    is active) the TtsNowPlayingBar pill side by side. Positions
                    itself at `absolute bottom-8 left-1/2 -translate-x-1/2` so
                    both pills float over the reader's bottom edge in the same
                    slot, and wraps onto multiple rows on narrow viewports
                    rather than overflowing. `flex-wrap-reverse` flips the wrap
                    axis so the FIRST DOM child stays at the bottom when the
                    row overflows: the familiar PageControls pill keeps its
                    bottom-8 anchor and the TTS pill stacks ABOVE it (rather
                    than the other way round, which pushed PageControls up
                    over the reader text). Fade-layer applies to the whole
                    row so immersive mode hides both pills consistently. */}
                {(isPdf || isCbz || isEpub) && (
                    <div
                        aria-hidden={!chromeVisible}
                        className={`absolute bottom-8 left-1/2 -translate-x-1/2 z-20 flex flex-wrap-reverse items-center justify-center gap-2 max-w-[96vw] transition-opacity duration-200 ${
                            chromeVisible ? 'opacity-100' : 'opacity-0 pointer-events-none'
                        }`}
                    >
                    <PageControls
                        label={
                            // ER-002: PDF/CBZ share the same label computation —
                            // numPages-aware, spread-aware, odd-end-aware.
                            (isPdf || isCbz) ? pdfCbzLabel : epubLabel
                        }
                        currentPage={isEpub ? (epubCurrentPage || 1) : pageNumber}
                        totalPages={
                            isEpub
                                ? (epubTotalPages > 0 ? epubTotalPages : null)
                                : (numPages > 0 ? numPages : null)
                        }
                        onJumpTo={
                            isEpub
                                ? epubJumpToPage
                                : (p: number) => {
                                    const clamped = Math.min(Math.max(1, Math.floor(p)), numPages || p);
                                    setPageNumber(clamped);
                                    if (initialPageLoaded) scheduleSave(clamped);
                                }
                        }
                        canPrev={isEpub ? true : canPrev}
                        canNext={isEpub ? true : canNext}
                        onPrev={isEpub ? epubPrev : () => changePage(-1)}
                        onNext={isEpub ? epubNext : () => changePage(1)}
                        thumbnailUrl={
                            // ER-032: only CBZ has a server-rendered thumbnail.
                            // CBR paths go through IsSupportedArchive the same
                            // way, but this reader currently only branches on
                            // isCbz — extend this check alongside any future
                            // CBR branch.
                            isCbz
                                ? (p: number) => getBookThumbnailUrl(item.id, p, 'sm')
                                : undefined
                        }
                        renderThumbnail={isPdf ? renderPdfThumbnail : undefined}
                    />
                    {/* Mid-listen control surface sits as a sibling pill in the
                        same bottom row so page nav and TTS controls never
                        overlap. Only mounts while TTS is active or paused. */}
                    <TtsNowPlayingBar
                        visible={tts.isSpeaking || tts.isPaused}
                        chapter={currentChapter}
                        isPaused={tts.isPaused}
                        rate={ttsRate}
                        onRateChange={setTtsRate}
                        onPauseToggle={() => tts.isPaused ? tts.resume() : tts.pause()}
                        onStop={stopTts}
                        onSkipBack={() => tts.skip(-1)}
                        onSkipForward={() => tts.skip(1)}
                        sleepTimerMode={sleepTimerMode}
                        sleepTimerRemainingMs={
                            sleepTimerFiresAt !== null
                                ? Math.max(0, sleepTimerFiresAt - sleepTimerNow)
                                : null
                        }
                        onSetSleepTimer={setSleepTimer}
                    />
                    </div>
                )}
            </div>

            <TocDrawer
                items={tocItems}
                currentHref={currentHref}
                open={tocOpen}
                onJump={onTocJump}
                onClose={() => {
                    setTocOpen(false);
                    // Restore focus to the button that opened the drawer so
                    // keyboard users don't lose their place (drawer close is
                    // effectively a modal dismissal).
                    window.setTimeout(() => tocButtonRef.current?.focus(), 0);
                }}
            />

            {/* ER-054: keyboard-shortcut help sheet (`?`). Centered modal
                driven by the SHORTCUTS constant so additions there flow
                into this list automatically. */}
            <ShortcutHelpSheet open={helpOpen} onClose={() => setHelpOpen(false)} />


            {/* ER-040: highlights drawer. CBZ has no text selection path, so
                the header button is hidden above — the drawer still mounts
                here so state is available for EPUB/PDF. */}
            <HighlightsDrawer
                items={highlights}
                bookTitle={item.title}
                open={highlightsOpen}
                onJump={jumpToHighlight}
                onDelete={removeHighlight}
                onChangeColour={changeHighlightColour}
                onChangeNote={changeHighlightNote}
                autoEditNoteId={highlightAutoEditNoteId}
                onClose={() => {
                    setHighlightsOpen(false);
                    // Clear the one-shot auto-edit signal so re-opening the
                    // drawer later doesn't re-trigger the editor.
                    setHighlightAutoEditNoteId(null);
                    window.setTimeout(() => highlightsButtonRef.current?.focus(), 0);
                }}
            />

            {/* ER-040: floating colour picker shown while a selection is pending.
                Positioned at the selection rect via inline style (fixed). Blur
                dismisses — a mousedown anywhere else also clears pendingSelection
                through the native selectionchange flow. */}
            {pendingSelection && (
                <div
                    role="toolbar"
                    aria-label="Highlight colour"
                    data-no-swipe
                    className="fixed z-[60] bg-gray-800 shadow-2xl rounded-full px-2 py-1 flex items-center gap-1 border border-gray-700"
                    style={{
                        left: Math.max(8, Math.min(window.innerWidth - 200, pendingSelection.x - 100)),
                        top: Math.max(8, pendingSelection.y - 44),
                    }}
                    onMouseDown={(e) => e.stopPropagation()}
                >
                    {(['yellow', 'green', 'blue', 'pink', 'orange'] as HighlightColour[]).map((c) => (
                        <button
                            key={c}
                            type="button"
                            aria-label={`Highlight ${c}`}
                            onClick={() => saveHighlight(c)}
                            className="w-7 h-7 rounded-full hover:scale-110 transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            style={{ backgroundColor: swatchFor(c) }}
                        />
                    ))}
                    {/* Highlight + note: save the highlight in the default
                        colour, then open the drawer with the new row's note
                        editor auto-focused so the user can type straight away.
                        This covers the "I need to jot down WHY this passage
                        matters" flow without a second trip to the drawer. */}
                    <button
                        type="button"
                        aria-label="Highlight with note"
                        title="Highlight & add note"
                        onClick={async () => {
                            const id = await saveHighlight('yellow');
                            if (id) {
                                setHighlightAutoEditNoteId(id);
                                setHighlightsOpen(true);
                            }
                        }}
                        className="ml-1 min-w-[32px] min-h-[32px] flex items-center justify-center rounded-full text-gray-200 hover:text-white hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        <MessageSquarePlus size={16} />
                    </button>
                    {/* ER-051: Define. Only renders when the selection is a
                        single word — multi-word selections are for highlight
                        colour only. */}
                    {pendingSelection.quotedText.trim().split(/\s+/).length === 1 && (
                        <button
                            type="button"
                            aria-label="Define"
                            title="Define"
                            disabled={definitionBusy}
                            onClick={() => lookupFromSelection(pendingSelection)}
                            className="ml-1 min-w-[32px] min-h-[32px] flex items-center justify-center rounded-full text-gray-200 hover:text-white hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 disabled:opacity-50"
                        >
                            <BookOpenText size={16} />
                        </button>
                    )}
                    <button
                        type="button"
                        aria-label="Cancel selection"
                        onClick={() => setPendingSelection(null)}
                        className="ml-1 min-w-[32px] min-h-[32px] flex items-center justify-center rounded-full text-gray-300 hover:text-white hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                    >
                        <X size={16} />
                    </button>
                </div>
            )}

            {/* ER-051: dictionary popover. Anchored at the same coordinates
                as the selection toolbar so the transition from "highlight vs
                define" choice to "read the definition" feels like one flow. */}
            {definition && (
                <div
                    role="dialog"
                    aria-label={`Definition of ${definition.lookup.word}`}
                    data-no-swipe
                    className="fixed z-[60] bg-gray-800 shadow-2xl rounded-lg p-3 border border-gray-700 w-[320px] max-w-[90vw]"
                    style={{
                        left: Math.max(8, Math.min(window.innerWidth - 328, definition.x - 160)),
                        top: Math.max(8, definition.y + 24),
                    }}
                    onMouseDown={(e) => e.stopPropagation()}
                >
                    <div className="flex items-start justify-between gap-2">
                        <div className="flex-1 min-w-0">
                            <div className="font-medium text-white truncate">{definition.lookup.word}</div>
                        </div>
                        <button
                            type="button"
                            aria-label="Close definition"
                            onClick={() => setDefinition(null)}
                            className="min-w-[32px] min-h-[32px] flex items-center justify-center rounded-full text-gray-400 hover:text-white hover:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            <X size={14} />
                        </button>
                    </div>
                    <div className="mt-2 text-sm text-gray-200">
                        {!definition.lookup.available ? (
                            <p className="text-gray-400">
                                No dictionary installed on this server. Drop a JSON dataset at
                                {' '}<code className="text-xs">data/dictionary.json</code>{' '}
                                to enable lookups.
                            </p>
                        ) : definition.lookup.definitions.length === 0 ? (
                            <p className="text-gray-400 italic">No definition found.</p>
                        ) : (
                            <ol className="list-decimal list-inside space-y-1">
                                {definition.lookup.definitions.map((d, i) => (
                                    <li key={i}>{d}</li>
                                ))}
                            </ol>
                        )}
                    </div>
                </div>
            )}

            {/* ER-024: in-book search drawer. Providers dispatch by format at
                runtime; CBZ renders a disabled-reason message instead. */}
            <SearchDrawer
                open={searchOpen}
                busy={searchBusy}
                hits={searchHits}
                disabledReason={searchDisabledReason}
                onQueryChange={runSearch}
                onJump={jumpToSearchHit}
                onClose={() => {
                    setSearchOpen(false);
                    window.setTimeout(() => searchButtonRef.current?.focus(), 0);
                }}
            />

            {/* ER-023: bookmarks drawer. Backed by listBookmarks / createBookmark /
                updateBookmarkLabel / deleteBookmark; per-user-per-book scope enforced
                server-side. */}
            <BookmarksDrawer
                items={bookmarks}
                open={bookmarksOpen}
                onAdd={addBookmarkAtCurrent}
                onJump={jumpToBookmark}
                onRename={renameBookmark}
                onDelete={removeBookmark}
                onClose={() => {
                    setBookmarksOpen(false);
                    window.setTimeout(() => bookmarksButtonRef.current?.focus(), 0);
                }}
            />

            {/* ER-010: settings drawer. Sections are composed here so the panel
                component stays agnostic about which prefs the reader exposes. */}
            <ReaderSettingsPanel
                open={settingsOpen}
                onClose={() => {
                    setSettingsOpen(false);
                    window.setTimeout(() => settingsButtonRef.current?.focus(), 0);
                }}
            >
                {/* ER-002: Display. Single / Double toggle applies to all three
                    formats; EPUB remounts its rendition so epub.js picks up
                    the new spread option from renderTo. */}
                <PanelSection
                    title="Display"
                    description="Side-by-side pages suit comics and landscape screens."
                >
                    <SegmentedControl<SpreadMode>
                        label="Page layout"
                        value={spread}
                        options={[
                            { value: 'single', label: 'Single' },
                            { value: 'double', label: 'Double' },
                        ]}
                        onChange={setSpread}
                    />
                    {/* ER-031: reading direction. Labelled with a string value
                        ('ltr'/'rtl') so SegmentedControl's generic T binds cleanly. */}
                    <SegmentedControl<'ltr' | 'rtl'>
                        label="Reading direction"
                        value={rtl ? 'rtl' : 'ltr'}
                        options={[
                            { value: 'ltr', label: 'Left → Right' },
                            { value: 'rtl', label: 'Right → Left', hint: 'Manga, Arabic, Hebrew' },
                        ]}
                        onChange={(v) => setRtl(v === 'rtl')}
                    />
                </PanelSection>

                {/* ER-021: Theme. Affects the reader viewport only — the rest of
                    SoftMedia stays dark by policy. */}
                <PanelSection
                    title="Reader theme"
                    description="Only the reading surface changes. The rest of SoftMedia stays dark."
                >
                    <SegmentedControl<ReaderTheme>
                        label="Palette"
                        value={readerTheme}
                        options={[
                            { value: 'dark', label: 'Dark' },
                            { value: 'sepia', label: 'Sepia' },
                            { value: 'high-contrast', label: 'Contrast', hint: 'High contrast' },
                        ]}
                        onChange={setReaderTheme}
                    />
                </PanelSection>

                {/* ER-050: Text-to-speech. EPUB-only (no OCR for PDF/CBZ in
                    this milestone). Voice list is populated asynchronously by
                    the browser — we display a "(default)" fallback option so
                    the select stays usable during that load. */}
                {isEpub && tts.supported && (
                    <PanelSection
                        title="Listen"
                        description="Uses your browser's built-in voices. Runs entirely on your device."
                    >
                        <div>
                            <label className="text-sm text-gray-200 mb-1 block">Voice</label>
                            <div className="flex gap-2">
                                <select
                                    value={ttsVoice ?? ''}
                                    onChange={(e) => setTtsVoice(e.target.value || null)}
                                    className="flex-1 bg-gray-900 text-white text-sm rounded px-2 py-2 min-h-[44px] focus:outline-none focus:ring-2 focus:ring-blue-400"
                                >
                                    <option value="">(system default)</option>
                                    {tts.voices.map((v) => (
                                        <option key={v.name} value={v.name}>
                                            {v.name} — {v.lang}
                                        </option>
                                    ))}
                                </select>
                                {/* Voice preview. Disabled while main TTS is
                                    active because window.speechSynthesis only
                                    holds one utterance and a preview would
                                    interrupt the book. Plays directly against
                                    speechSynthesis rather than through useTts
                                    so it doesn't fire karaoke callbacks. */}
                                <button
                                    type="button"
                                    aria-label="Preview voice"
                                    title={tts.isSpeaking || tts.isPaused
                                        ? 'Stop listening to preview a voice'
                                        : 'Preview voice'}
                                    disabled={tts.isSpeaking || tts.isPaused}
                                    onClick={() => {
                                        const synth = window.speechSynthesis;
                                        if (!synth) return;
                                        const sample = 'Hello. This is a preview of the selected voice.';
                                        const u = new SpeechSynthesisUtterance(sample);
                                        const v = ttsVoice
                                            ? tts.voices.find((x) => x.name === ttsVoice)
                                            : null;
                                        if (v) u.voice = v;
                                        u.rate = ttsRate;
                                        synth.cancel();
                                        synth.speak(u);
                                    }}
                                    className="min-w-[44px] min-h-[44px] flex items-center justify-center rounded bg-gray-800 hover:bg-gray-700 text-white disabled:opacity-40 disabled:cursor-not-allowed focus:outline-none focus:ring-2 focus:ring-blue-400"
                                >
                                    <Play size={18} />
                                </button>
                            </div>
                        </div>
                        <SliderControl
                            label="Speed"
                            min={0.5}
                            max={2.0}
                            step={0.1}
                            value={ttsRate}
                            onChange={setTtsRate}
                            valueLabel={(v) => `${v.toFixed(1)}×`}
                        />
                    </PanelSection>
                )}

                {/* ER-053: night-reading overlay. Applies to all three formats
                    because it's a post-render CSS layer, not a content change. */}
                <PanelSection
                    title="Night reading"
                    description="Dim the screen and warm the colour without touching your OS brightness."
                >
                    <SliderControl
                        label="Brightness"
                        min={0.3}
                        max={1.0}
                        step={0.05}
                        value={brightness}
                        onChange={setBrightness}
                        valueLabel={(v) => `${Math.round(v * 100)}%`}
                    />
                    <SliderControl
                        label="Warmth"
                        min={0}
                        max={1}
                        step={0.05}
                        value={warmth}
                        onChange={setWarmth}
                        valueLabel={(v) => (v === 0 ? 'Neutral' : `+${Math.round(v * 100)}%`)}
                    />
                </PanelSection>

                {/* ER-052: reading stats. Rendered only when the summary
                    endpoint returned something non-trivial — a fresh book with
                    no completed sessions shows nothing. Totals are across all
                    closed sessions for this (user, book). */}
                {stats && stats.sessionCount > 0 && (
                    <PanelSection
                        title="Your reading stats"
                        description="Time spent with this book across completed sessions."
                    >
                        <div className="flex items-center justify-between text-sm">
                            <span className="text-gray-400">Time read</span>
                            <span className="font-mono text-gray-100">
                                {formatMinutes(stats.totalMinutes)}
                            </span>
                        </div>
                        <div className="flex items-center justify-between text-sm">
                            <span className="text-gray-400">Pages read</span>
                            <span className="font-mono text-gray-100">{stats.totalPages}</span>
                        </div>
                        <div className="flex items-center justify-between text-sm">
                            <span className="text-gray-400">Pages / min</span>
                            <span className="font-mono text-gray-100">
                                {stats.pagesPerMinute.toFixed(2)}
                            </span>
                        </div>
                        <div className="flex items-center justify-between text-sm">
                            <span className="text-gray-400">Sessions</span>
                            <span className="font-mono text-gray-100">{stats.sessionCount}</span>
                        </div>
                    </PanelSection>
                )}

                {/* ER-012: per-book overrides. Saving captures the current panel
                    values as this book's override; clearing reverts to the global
                    defaults stored in readerStore. Keeping this as a separate
                    section with explanatory copy avoids the Kindle-style
                    "where did my change go?" confusion. */}
                <PanelSection
                    title="For this book"
                    description={hasBookOverride
                        ? 'This book uses saved overrides. Clear to fall back to your global defaults.'
                        : 'Save the current settings as this book\u2019s own. Global defaults stay untouched for other books.'}
                >
                    <div className="flex gap-2">
                        <button
                            type="button"
                            onClick={saveBookOverride}
                            disabled={!overrideHydrated}
                            className="flex-1 min-h-[44px] px-3 py-2 text-sm rounded-md bg-gradient-to-r from-blue-500 to-purple-500 text-white shadow disabled:opacity-40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            Save for this book
                        </button>
                        <button
                            type="button"
                            onClick={clearBookOverride}
                            disabled={!hasBookOverride}
                            className="flex-1 min-h-[44px] px-3 py-2 text-sm rounded-md text-gray-200 hover:bg-gray-700 disabled:opacity-30 disabled:hover:bg-transparent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            Clear override
                        </button>
                    </div>
                </PanelSection>

                {/* ER-030: Zoom. PDF and CBZ only — EPUB's "zoom" is font size (ER-020).
                    Persists via ER-012 as part of the per-book override payload. */}
                {(isPdf || isCbz) && (
                    <PanelSection
                        title="Zoom"
                        description="Ctrl + scroll wheel also adjusts the zoom on desktop."
                    >
                        <ZoomControl value={zoom} onChange={setZoom} />
                    </PanelSection>
                )}

                {/* ER-020: Typography. EPUB-only — rasterised PDF/CBZ don't honour
                    font/line/margin. Hiding rather than disabling avoids a panel
                    full of controls that appear broken. */}
                {isEpub && (
                    <PanelSection
                        title="Typography"
                        description="Applies live. Toggle publisher override if a book ignores your settings."
                    >
                        <SegmentedControl<ReaderFontFamily>
                            label="Font"
                            value={fontFamily}
                            options={[
                                { value: 'inter', label: 'Inter' },
                                { value: 'georgia', label: 'Georgia' },
                                { value: 'system-serif', label: 'Serif', hint: 'System serif' },
                                { value: 'system-sans', label: 'Sans', hint: 'System sans-serif' },
                            ]}
                            onChange={setFontFamily}
                        />
                        <FontSizeControl value={fontSize} onChange={setFontSize} />
                        <SegmentedControl<LineHeightMode>
                            label="Line height"
                            value={lineHeight}
                            options={[
                                { value: 'tight', label: 'Tight' },
                                { value: 'normal', label: 'Normal' },
                                { value: 'loose', label: 'Loose' },
                            ]}
                            onChange={setLineHeight}
                        />
                        <SegmentedControl<MarginMode>
                            label="Margin"
                            value={margin}
                            options={[
                                { value: 'narrow', label: 'Narrow' },
                                { value: 'normal', label: 'Normal' },
                                { value: 'wide', label: 'Wide' },
                            ]}
                            onChange={setMargin}
                        />
                        {/* ER-022: override-publisher toggle. Default on — the
                            dark theme being defeated is the worse failure mode. */}
                        <label className="flex items-center gap-3 min-h-[44px] text-sm text-gray-200 cursor-pointer select-none">
                            <input
                                type="checkbox"
                                checked={overridePublisher}
                                onChange={(e) => setOverridePublisher(e.target.checked)}
                                className="size-4 accent-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            />
                            <span className="flex-1">
                                Override publisher styles
                                <span className="block text-xs text-gray-500">
                                    Forces your font/size/colours over the book's own CSS.
                                </span>
                            </span>
                        </label>
                    </PanelSection>
                )}
            </ReaderSettingsPanel>
        </div>
    );
}

interface PageControlsProps {
    /** Fallback text content shown when totalPages is null (e.g. "42%" during
     *  EPUB locations generation). When currentPage + totalPages are both set,
     *  the label is clickable and becomes an input for manual page jump. */
    label: string;
    currentPage?: number;
    totalPages?: number | null;
    /** Called with a validated page number (clamped by the parent). */
    onJumpTo?: (page: number) => void;
    canPrev: boolean;
    canNext: boolean;
    onPrev: () => void;
    onNext: () => void;
    /** ER-032: optional thumbnail URL builder. When provided, a small preview
     *  renders above the pill while the page-number input is active. */
    thumbnailUrl?: (pageNumber: number) => string;
    /** ER-032 (PDF): async renderer returning a data URL. Used when the
     *  source can't be served by the backend (e.g., PDF pages are rendered
     *  client-side via pdf.js). Takes precedence over thumbnailUrl if both
     *  are provided. */
    renderThumbnail?: (pageNumber: number) => Promise<string | null>;
}

function PageControls({
    label,
    currentPage,
    totalPages,
    onJumpTo,
    canPrev,
    canNext,
    onPrev,
    onNext,
    thumbnailUrl,
    renderThumbnail,
}: PageControlsProps) {
    const canEdit = typeof currentPage === 'number' && typeof totalPages === 'number' && totalPages > 0 && !!onJumpTo;
    const [editing, setEditing] = useState(false);
    const [draft, setDraft] = useState('');
    const [asyncPreview, setAsyncPreview] = useState<string | null>(null);

    // Parse + clamp the draft so the preview only shows for pages that actually
    // exist. The preview URL re-computes on every keystroke but the <img> tag
    // dedupes identical src — no extra network traffic for rapid edits.
    const draftPage = (() => {
        const n = parseInt(draft, 10);
        if (!totalPages || !Number.isFinite(n) || n < 1 || n > totalPages) return null;
        return n;
    })();
    const syncUrl = thumbnailUrl && draftPage !== null ? thumbnailUrl(draftPage) : null;

    // Async renderer path (ER-032 PDF). Debounced via a small delay so
    // rapid typing doesn't fire a render per keystroke; the sequence number
    // guards against late-arriving renders clobbering a newer target.
    useEffect(() => {
        if (!renderThumbnail || draftPage === null) {
            setAsyncPreview(null);
            return;
        }
        let cancelled = false;
        const id = window.setTimeout(() => {
            renderThumbnail(draftPage)
                .then((url) => { if (!cancelled) setAsyncPreview(url); })
                .catch(() => { if (!cancelled) setAsyncPreview(null); });
        }, 120);
        return () => {
            cancelled = true;
            window.clearTimeout(id);
        };
    }, [draftPage, renderThumbnail]);

    const previewUrl = renderThumbnail ? asyncPreview : syncUrl;

    const beginEdit = useCallback(() => {
        if (!canEdit) return;
        setDraft(String(currentPage ?? 1));
        setEditing(true);
    }, [canEdit, currentPage]);

    const commitEdit = useCallback(() => {
        if (!onJumpTo || !totalPages) {
            setEditing(false);
            return;
        }
        const n = parseInt(draft, 10);
        if (!Number.isNaN(n)) onJumpTo(n);
        setEditing(false);
    }, [onJumpTo, totalPages, draft]);

    return (
        // The `relative` wrapper creates a positioning context for the
        // thumbnail preview, which anchors itself to the pill via
        // `bottom-full`. The pill itself no longer positions; its parent
        // (BookReader's bottom-chrome flex row) owns bottom placement so
        // PageControls can sit alongside TtsNowPlayingBar without either
        // floating over the reader's text body. Fixed width stays so the
        // prev/next chevrons don't shift as page digits grow or shrink.
        <div className="relative">
        {editing && previewUrl && (
            <div
                data-no-swipe
                className="absolute bottom-full mb-3 left-1/2 -translate-x-1/2 bg-gray-800/95 rounded-md p-1.5 shadow-xl backdrop-blur-sm"
                aria-hidden
            >
                <img
                    src={previewUrl}
                    alt=""
                    referrerPolicy="no-referrer"
                    className="block max-w-[160px] max-h-[220px] object-contain rounded"
                    /* Any network error (e.g. server-side rendering unavailable
                       for this format) silently hides the image. The text input
                       stays usable regardless. */
                    onError={(e) => { (e.currentTarget as HTMLImageElement).style.visibility = 'hidden'; }}
                />
                {draftPage !== null && (
                    <div className="text-center text-[11px] text-gray-300 mt-1">Page {draftPage}</div>
                )}
            </div>
        )}
        <div data-no-swipe className="w-[280px] bg-gray-800/90 rounded-full px-3 py-2 flex items-center justify-between shadow-xl backdrop-blur-sm text-white">
            <button
                type="button"
                aria-label="Previous page"
                disabled={!canPrev}
                onClick={onPrev}
                className="flex-shrink-0 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full disabled:opacity-30 hover:text-blue-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <ChevronLeft />
            </button>

            <div className="flex-1 flex items-center justify-center overflow-hidden px-2">
                {editing && canEdit && typeof totalPages === 'number' ? (
                    <form
                        onSubmit={(e) => { e.preventDefault(); commitEdit(); }}
                        className="flex items-center gap-1 font-mono text-sm"
                    >
                        <input
                            type="number"
                            min={1}
                            max={totalPages}
                            autoFocus
                            value={draft}
                            onChange={(e) => setDraft(e.target.value)}
                            onBlur={commitEdit}
                            onKeyDown={(e) => {
                                if (e.key === 'Escape') { setEditing(false); }
                                // Let Enter submit via the form; stop propagation
                                // so the reader's global nav handler doesn't also
                                // fire on that keystroke.
                                e.stopPropagation();
                            }}
                            className="w-16 bg-gray-700 text-white text-right rounded px-1 py-0.5 focus:outline-none focus:ring-1 focus:ring-blue-400 [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                            aria-label="Jump to page"
                        />
                        <span className="text-gray-400">/ {totalPages}</span>
                    </form>
                ) : canEdit ? (
                    <button
                        type="button"
                        onClick={beginEdit}
                        className="font-mono text-sm select-none truncate hover:text-blue-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded px-1"
                        title="Click to jump to a specific page"
                    >
                        {label}
                    </button>
                ) : (
                    <span className="font-mono text-sm select-none truncate" title={label}>
                        {label}
                    </span>
                )}
            </div>

            <button
                type="button"
                aria-label="Next page"
                disabled={!canNext}
                onClick={onNext}
                className="flex-shrink-0 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-full disabled:opacity-30 hover:text-blue-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <ChevronRight />
            </button>
        </div>
        </div>
    );
}
