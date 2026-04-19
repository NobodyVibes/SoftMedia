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
