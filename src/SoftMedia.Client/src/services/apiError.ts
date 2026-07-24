import axios from 'axios';

/**
 * SR-WI-061 — one place that understands every error body the server can send.
 *
 * The API now emits RFC 7807 ProblemDetails ({ title, detail, status, traceId, ...ext })
 * for error responses, but plain-string bodies (e.g. `BadRequest("text")`) and a few
 * legacy `{ error }` / `{ message }` shapes may still exist in the wild (older servers,
 * proxies). This helper prefers the most specific human text available and otherwise
 * returns the caller's fallback, so toast/error sites never render "[object Object]".
 */
export function extractApiError(err: unknown, fallback: string): string {
    if (axios.isAxiosError(err)) {
        const data: unknown = err.response?.data;
        if (typeof data === 'string' && data.trim().length > 0) return data;
        if (data && typeof data === 'object') {
            const body = data as Record<string, unknown>;
            // ProblemDetails first (detail is the specific text, title the summary),
            // then the legacy shapes.
            for (const key of ['detail', 'title', 'message', 'error']) {
                const value = body[key];
                if (typeof value === 'string' && value.trim().length > 0) return value;
            }
        }
    }
    return fallback;
}

/**
 * Machine-read discriminator from a ProblemDetails extension (or a legacy `{ error }`
 * body): e.g. `password_change_required`. Returns undefined when absent — never use
 * this for display text; that's what {@link extractApiError} is for.
 */
export function extractApiErrorCode(err: unknown): string | undefined {
    if (axios.isAxiosError(err)) {
        const data: unknown = err.response?.data;
        if (data && typeof data === 'object') {
            const value = (data as Record<string, unknown>).error;
            if (typeof value === 'string') return value;
        }
    }
    return undefined;
}
