import { useAuthStore } from '../store/authStore';

/**
 * Re-render the calling component whenever the media token rotates.
 *
 * Browsers can't attach an `Authorization` header to an `<img>` load, so media
 * URLs carry the reduced-privilege media token in the query string. That token is
 * baked into the URL at render time — which means a component that renders one and
 * never re-renders keeps serving a URL whose token has since rotated or expired.
 * The request then 401s and the artwork silently disappears, with no recovery
 * short of a full page reload.
 *
 * `LoadingImage` solves this internally by subscribing to the token itself. This
 * hook is the equivalent for components that build media URLs into a plain
 * `<img>` (or a CSS `background-image`) without going through it: call it once at
 * the top of the component and the URL is rebuilt with a fresh token on every
 * rotation. Because changing an `<img>`'s `src` re-triggers the fetch, that is
 * also what recovers an image that already failed.
 *
 * Deliberately returns nothing — callers keep using whichever resolver
 * (`resolveCardPosterUrl`, `attachAuthToApiUrl`, a local helper) already suits
 * them. The hook exists purely for its subscription, so the resolver they call
 * during render observes the current token.
 */
export function useMediaTokenRefresh(): void {
    useAuthStore((s) => s.mediaToken);
}

export default useMediaTokenRefresh;
