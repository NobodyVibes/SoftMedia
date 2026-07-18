import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { queryClient } from '../lib/queryClient';

export interface AuthUser {
    id: string;
    username: string;
    role: string;
}

interface AuthState {
    user: AuthUser | null;
    token: string | null;
    // Reduced-privilege "media" token used in media URLs that ride in the query string
    // (audit H3). Kept in MEMORY only (excluded from persistence below) and refreshed by a
    // top-level effect in App.tsx; URLs fall back to `token` until it loads.
    mediaToken: string | null;
    isAuthenticated: boolean;
    login: (user: AuthUser, token: string) => void;
    logout: () => void;
    setMediaToken: (mediaToken: string | null) => void;
}

export const useAuthStore = create<AuthState>()(
    persist(
        (set) => ({
            user: null,
            token: null,
            mediaToken: null,
            isAuthenticated: false,
            login: (user, token) => set({ user, token, isAuthenticated: true }),
            logout: () => {
                set({ user: null, token: null, mediaToken: null, isAuthenticated: false });
                // Account-scoped queries (['contentLimits'], ['apiTokens'], …) are keyed without
                // a user id — clear the cache so the next login can't briefly see the previous
                // user's data (R-WI-011 review).
                queryClient.clear();
            },
            setMediaToken: (mediaToken) => set({ mediaToken }),
        }),
        {
            name: 'auth-storage', // name of the item in the storage (must be unique)
            // Do NOT persist mediaToken — it's short-lived and re-fetched on demand.
            partialize: (state) => ({
                user: state.user,
                token: state.token,
                isAuthenticated: state.isAuthenticated,
            }),
        }
    )
);

/**
 * The token to embed in media URLs (`?token=` / `?access_token=`): the reduced-privilege
 * media token when available, otherwise the full access token (audit H3). Pure read — the
 * media token is fetched by the lifecycle effect in App.tsx, so URLs degrade gracefully to
 * the access token rather than ever breaking playback.
 */
export function getUrlToken(): string | null {
    const s = useAuthStore.getState();
    return s.mediaToken ?? s.token;
}
