import { QueryClient } from '@tanstack/react-query';

/**
 * The app-wide query client, in its own module so non-component code (notably
 * authStore.logout) can reach it without importing main.tsx (circular).
 *
 * R-WI-011 review: account-scoped queries (['contentLimits'], ['apiTokens'], ['totp'],
 * webhooks, …) are keyed without a user id, so after logout → login as someone else the
 * cache could briefly serve the PREVIOUS user's data while refetching. logout() clears
 * the whole cache to make an account switch start cold.
 */
export const queryClient = new QueryClient();
