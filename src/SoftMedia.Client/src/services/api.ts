import axios from 'axios';
import type { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { useAuthStore, type AuthUser } from '../store/authStore';

interface RefreshResponseBody {
    accessToken: string;
    user: AuthUser;
}

export const API_URL = '/api/v1';
const REFRESH_ENDPOINT = '/auth/refresh-token';

const api: AxiosInstance = axios.create({
    baseURL: API_URL,
    headers: {
        'Content-Type': 'application/json',
    },
    withCredentials: true, // Refresh cookie is HttpOnly; browser attaches it automatically
});

// Request Interceptor: attach the current access token. We don't overwrite an
// Authorization header the caller has already set — that path is used by the
// refresh-retry replay to swap in the fresh token.
api.interceptors.request.use(
    (config) => {
        const token = useAuthStore.getState().token;
        if (token && !config.headers?.Authorization) {
            config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
    },
    (error) => Promise.reject(error)
);

// Single-flight refresh. Multiple concurrent 401s queue behind one in-flight
// /auth/refresh-token call so we don't hammer the server or create rotation
// races where several refreshes produce conflicting tokens.
let refreshInFlight: Promise<string> | null = null;

function refreshAccessToken(): Promise<string> {
    if (refreshInFlight) return refreshInFlight;

    // Diagnostic: tell the console exactly when a refresh fires and how it
    // resolves. The "idle user gets logged out" bug had no breadcrumbs in
    // the browser; this surfaces the chain so we can spot whether refresh
    // is silently failing, what the response status was, and whether logout
    // was triggered.
    // eslint-disable-next-line no-console
    console.info('[auth] refresh starting at', new Date().toISOString());

    refreshInFlight = axios
        .post<RefreshResponseBody>(`${API_URL}${REFRESH_ENDPOINT}`, {}, { withCredentials: true })
        .then((response) => {
            const { accessToken, user } = response.data;
            useAuthStore.getState().login(user, accessToken);
            // eslint-disable-next-line no-console
            console.info('[auth] refresh succeeded — new access token issued');
            return accessToken;
        })
        .catch((err) => {
            const status = axios.isAxiosError(err) ? err.response?.status : 'no-response';
            const body = axios.isAxiosError(err)
                ? (typeof err.response?.data === 'string' ? err.response.data : JSON.stringify(err.response?.data))
                : String(err);
            // eslint-disable-next-line no-console
            console.warn(`[auth] refresh failed status=${status} body=${body}`);
            throw err;
        })
        .finally(() => {
            refreshInFlight = null;
        });

    return refreshInFlight;
}

/**
 * Pure decision helper. Exported for unit tests.
 *
 * Returns true only when the error is a 401 on an endpoint OTHER than
 * /auth/refresh-token AND we haven't already retried this request. This
 * intentionally excludes 403, 404, 5xx, and network errors — those must not
 * trigger refresh and, crucially, must not destroy the session.
 */
export function shouldAttemptRefresh(
    error: AxiosError,
    refreshEndpoint: string = REFRESH_ENDPOINT
): boolean {
    if (error.response?.status !== 401) return false;

    const config = error.config as
        | (InternalAxiosRequestConfig & { _retry?: boolean })
        | undefined;
    if (!config) return false;

    // Never loop on the refresh endpoint itself — if /refresh-token returns 401
    // we surface that directly rather than trying to refresh the refresh.
    if ((config.url ?? '').includes(refreshEndpoint)) return false;

    if (config._retry) return false;
    return true;
}

/**
 * Pure decision helper. Exported for unit tests.
 *
 * Distinguishes "refresh token is genuinely invalid" (log the user out) from
 * "refresh attempt failed transiently" (network, 5xx — keep the session
 * intact; caller will see the original failure and can retry later).
 */
export function isRefreshRejectionInvalid(error: unknown): boolean {
    if (axios.isAxiosError(error)) {
        return error.response?.status === 401;
    }
    return false;
}

// Response Interceptor: on 401 for non-refresh endpoints, attempt a single
// refresh (deduped across concurrent failures). On success, replay the
// original request with the new token. On refresh failure, forward the
// ORIGINAL error — and only logout if refresh explicitly rejected the token.
api.interceptors.response.use(
    (response) => response,
    async (error: AxiosError) => {
        if (!shouldAttemptRefresh(error)) {
            return Promise.reject(error);
        }

        const config = error.config as InternalAxiosRequestConfig & {
            _retry?: boolean;
        };
        config._retry = true;

        try {
            const newToken = await refreshAccessToken();
            config.headers = config.headers ?? ({} as InternalAxiosRequestConfig['headers']);
            config.headers.Authorization = `Bearer ${newToken}`;
            return api(config);
        } catch (refreshError) {
            if (isRefreshRejectionInvalid(refreshError)) {
                // eslint-disable-next-line no-console
                console.warn('[auth] refresh returned 401 — logging out');
                useAuthStore.getState().logout();
            } else {
                // eslint-disable-next-line no-console
                console.warn('[auth] refresh failed transiently — keeping session');
            }
            return Promise.reject(error);
        }
    }
);

// Media-token lifecycle (audit H3). Fetches the reduced-privilege media token and stores it
// in memory so media URLs (?token=/?access_token=) carry it instead of the full access token.
// Single-flight so concurrent callers share one request; failures are swallowed (URLs fall
// back to the access token via getUrlToken). Driven by a top-level effect in App.tsx.
let mediaTokenInFlight: Promise<void> | null = null;

export function fetchMediaToken(): Promise<void> {
    if (mediaTokenInFlight) return mediaTokenInFlight;

    mediaTokenInFlight = api
        .get<{ token: string }>('/auth/media-token')
        .then((response) => {
            useAuthStore.getState().setMediaToken(response.data.token);
        })
        .catch(() => {
            // Leave mediaToken as-is; media URLs degrade to the access token.
        })
        .finally(() => {
            mediaTokenInFlight = null;
        });

    return mediaTokenInFlight;
}

export default api;
