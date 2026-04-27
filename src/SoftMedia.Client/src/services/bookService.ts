import api, { API_URL } from './api';
import { useAuthStore } from '../store/authStore';

export type BookFormat = 'pdf' | 'epub' | 'cbz' | 'cbr';

export interface BookInfo {
    id: string;
    format: BookFormat | string;
    pageCount: number | null;
}

export interface BookProgress {
    position: number;
    bookLocation: string | null;
    lastPlayed: string | null;
    isWatched: boolean;
}

export async function getBookInfo(id: string): Promise<BookInfo> {
    const res = await api.get<BookInfo>(`/books/${id}/info`);
    return res.data;
}

/**
 * Builds a URL for a CBZ page image. Token is appended as a query param so
 * <img> tags (which cannot send Authorization headers) can load pages.
 */
export function getBookPageUrl(id: string, pageNumber: number): string {
    const token = useAuthStore.getState().token;
    const tokenParam = token ? `?token=${encodeURIComponent(token)}` : '';
    return `${API_URL}/books/${id}/page/${pageNumber}${tokenParam}`;
}

/**
 * ER-032: thumbnail URL for CBZ/CBR scrubber previews. Token appended so
 * <img> tags can load. Sizes: sm=160px, md=240px, lg=360px. The server
 * returns 400 for non-archive formats — consumers should only call this
 * when the book format warrants a server-side thumbnail.
 */
export function getBookThumbnailUrl(
    id: string,
    pageNumber: number,
    size: 'sm' | 'md' | 'lg' = 'sm',
): string {
    const token = useAuthStore.getState().token;
    const params = new URLSearchParams();
    params.set('size', size);
    if (token) params.set('token', token);
    return `${API_URL}/books/${id}/thumbnail/${pageNumber}?${params.toString()}`;
}

export async function getProgress(id: string): Promise<BookProgress> {
    const res = await api.get<BookProgress>(`/interaction/${id}/progress`);
    return res.data;
}

export async function updateProgress(
    id: string,
    position: number,
    bookLocation?: string | null,
): Promise<void> {
    await api.post(`/interaction/${id}/progress`, {
        position,
        bookLocation: bookLocation ?? null,
    });
}

/**
 * Mark a book as finished / unfinished. Server-side this upserts the
 * UserMediaInteraction row and flips IsWatched, which removes the book
 * from the "Continue Reading" shelf via the repository's Watched filter.
 */
export async function markFinished(id: string, isFinished: boolean): Promise<void> {
    await api.post(`/interaction/${id}/watched`, { watched: isFinished });
}

// ── ER-012: per-book reader preference overrides ─────────────────────────────

/**
 * Opaque shape owned by the client — fields mirror ReaderPrefs subset in
 * readerStore.ts, but every key is optional because only user-saved overrides
 * travel the wire. The server stores this JSON-as-string; payload schema is
 * versioned so a future client can detect old shapes.
 */
export interface ReaderPreferencesPayload {
    schemaVersion: 1;
    spread?: 'single' | 'double';
    theme?: 'dark' | 'sepia' | 'high-contrast';
    fontFamily?: string;
    fontSize?: number;
    lineHeight?: 'tight' | 'normal' | 'loose';
    margin?: 'narrow' | 'normal' | 'wide';
    zoom?: 'fit-width' | 'fit-page' | number;
    rtl?: boolean;
}

interface ReaderPreferencesApiResponse {
    schemaVersion: number;
    preferencesJson: string | null;
    updatedAt: string | null;
}

/**
 * Returns `null` when no override row exists for this (user, book) pair.
 * A parse failure on the JSON blob also returns null — the caller falls back
 * to global defaults, which is the safe behaviour.
 */
export async function getReaderPreferences(
    id: string,
): Promise<ReaderPreferencesPayload | null> {
    const res = await api.get<ReaderPreferencesApiResponse>(
        `/interaction/${id}/reader-preferences`,
    );
    if (res.data.schemaVersion === 0 || !res.data.preferencesJson) return null;
    try {
        return JSON.parse(res.data.preferencesJson) as ReaderPreferencesPayload;
    } catch {
        return null;
    }
}

/**
 * Save or clear per-book overrides. Passing `null` or an empty object is
 * treated server-side as a delete — keeps "revert to global defaults" a
 * single-call operation.
 */
export async function putReaderPreferences(
    id: string,
    payload: ReaderPreferencesPayload | null,
): Promise<void> {
    const body = {
        schemaVersion: payload?.schemaVersion ?? 1,
        preferencesJson: payload === null ? null : JSON.stringify(payload),
    };
    await api.put(`/interaction/${id}/reader-preferences`, body);
}

// ── ER-023: Bookmarks ────────────────────────────────────────────────────────

export interface Bookmark {
    id: string;
    position: number | null;
    cfi: string | null;
    label: string | null;
    createdAt: string;
}

interface CreateBookmarkBody {
    position?: number;
    cfi?: string;
    label?: string;
}

export async function listBookmarks(bookId: string): Promise<Bookmark[]> {
    const res = await api.get<Bookmark[]>(`/books/${bookId}/bookmarks`);
    return res.data;
}

export async function createBookmark(bookId: string, body: CreateBookmarkBody): Promise<Bookmark> {
    const res = await api.post<Bookmark>(`/books/${bookId}/bookmarks`, body);
    return res.data;
}

export async function updateBookmarkLabel(
    bookId: string,
    bookmarkId: string,
    label: string | null,
): Promise<void> {
    await api.patch(`/books/${bookId}/bookmarks/${bookmarkId}`, { label });
}

export async function deleteBookmark(bookId: string, bookmarkId: string): Promise<void> {
    await api.delete(`/books/${bookId}/bookmarks/${bookmarkId}`);
}

// ── ER-040 / ER-041: Highlights ──────────────────────────────────────────────

export type HighlightColour = 'yellow' | 'green' | 'blue' | 'pink' | 'orange';

/**
 * Shape of LocationJson field for EPUB / PDF. Owned by the client — the server
 * treats the column as opaque JSON.
 */
export type HighlightLocation =
    | { type: 'epub'; cfi: string }
    | { type: 'pdf'; page: number; rects?: Array<{ x: number; y: number; w: number; h: number }> };

export interface Highlight {
    id: string;
    locationJson: string;
    colour: HighlightColour | string;
    quotedText: string;
    note: string | null;
    createdAt: string;
    updatedAt: string;
}

export interface CreateHighlightBody {
    location: HighlightLocation;
    colour: HighlightColour;
    quotedText: string;
    note?: string;
}

export async function listHighlights(bookId: string): Promise<Highlight[]> {
    const res = await api.get<Highlight[]>(`/books/${bookId}/highlights`);
    return res.data;
}

export async function createHighlight(
    bookId: string,
    body: CreateHighlightBody,
): Promise<Highlight> {
    const res = await api.post<Highlight>(`/books/${bookId}/highlights`, {
        locationJson: JSON.stringify(body.location),
        colour: body.colour,
        quotedText: body.quotedText,
        note: body.note ?? null,
    });
    return res.data;
}

export async function updateHighlight(
    bookId: string,
    highlightId: string,
    patch: { colour?: HighlightColour | string; note?: string | null },
): Promise<void> {
    await api.patch(`/books/${bookId}/highlights/${highlightId}`, patch);
}

export async function deleteHighlight(bookId: string, highlightId: string): Promise<void> {
    await api.delete(`/books/${bookId}/highlights/${highlightId}`);
}

/**
 * Parse a stored highlight's opaque LocationJson into the typed shape. Returns
 * null on malformed rows so callers can skip rendering rather than crash.
 */
// ── ER-051: Offline dictionary ───────────────────────────────────────────────

export interface DictionaryLookup {
    word: string;
    definitions: string[];
    /**
     * False when the server reports the dictionary dataset isn't installed
     * (paired with an HTTP 501). The client surfaces this as an explanatory
     * empty state rather than a generic error.
     */
    available: boolean;
}

export async function lookupWord(word: string): Promise<DictionaryLookup> {
    try {
        const res = await api.get<DictionaryLookup>(`/dictionary/${encodeURIComponent(word)}`);
        return res.data;
    } catch (err) {
        // 501 is the "dataset not installed" signal — translate it into a
        // typed empty state the UI can render without a toast.
        const status = (err as { response?: { status?: number; data?: DictionaryLookup } })?.response?.status;
        if (status === 501) {
            const data = (err as { response?: { data?: DictionaryLookup } }).response?.data;
            return data ?? { word, definitions: [], available: false };
        }
        throw err;
    }
}

// ── ER-052: Reading sessions ─────────────────────────────────────────────────

export interface ReadingSessionSummary {
    sessionCount: number;
    totalMinutes: number;
    totalPages: number;
    pagesPerMinute: number;
}

export async function startReadingSession(mediaId: string): Promise<string> {
    const res = await api.post<{ sessionId: string }>(`/interaction/${mediaId}/sessions/start`, {});
    return res.data.sessionId;
}

export async function endReadingSession(
    mediaId: string,
    sessionId: string,
    pagesRead: number,
): Promise<void> {
    await api.post(`/interaction/${mediaId}/sessions/${sessionId}/end`, { pagesRead });
}

export async function getReadingSessionSummary(mediaId: string): Promise<ReadingSessionSummary> {
    const res = await api.get<ReadingSessionSummary>(`/interaction/${mediaId}/sessions/summary`);
    return res.data;
}

export function parseHighlightLocation(locationJson: string): HighlightLocation | null {
    try {
        const raw = JSON.parse(locationJson) as {
            type?: string;
            cfi?: string;
            page?: number;
            rects?: Array<{ x?: number; y?: number; w?: number; h?: number }>;
        };
        if (raw.type === 'epub' && typeof raw.cfi === 'string') {
            return { type: 'epub', cfi: raw.cfi };
        }
        if (raw.type === 'pdf' && typeof raw.page === 'number') {
            // Rects are optional — older highlights stored before the overlay
            // feature won't have them; the PDF list sheet still works via
            // jump-to-page. Only include well-formed entries.
            const rects = Array.isArray(raw.rects)
                ? raw.rects.flatMap((r) => {
                    if (
                        typeof r.x === 'number' && typeof r.y === 'number'
                        && typeof r.w === 'number' && typeof r.h === 'number'
                    ) {
                        return [{ x: r.x, y: r.y, w: r.w, h: r.h }];
                    }
                    return [];
                })
                : undefined;
            return { type: 'pdf', page: raw.page, rects };
        }
    } catch { /* fall through */ }
    return null;
}
