/**
 * Lazy injector for the Google Cast Web Sender SDK (P3-WI-001, SR-WI-041).
 *
 * The SDK used to be a blocking <script> in index.html, charging every visitor
 * a gstatic round-trip + parse before first paint even if they never cast.
 * It is now injected on demand the first time a cast-capable surface mounts
 * (useCast — i.e. the video player).
 *
 * ORDERING CONTRACT: callers MUST register `window.__onGCastApiAvailable`
 * BEFORE calling injectCastSdk(). The SDK invokes that callback when it
 * finishes initialising; registering after injection races the script load.
 * useCast honours this: it hooks the callback first, then injects.
 *
 * `loadCastFramework=1` requests the higher-level cast.framework wrapper (vs
 * the lower-level chrome.cast API). Cast requires HTTPS in production;
 * localhost is exempt for dev.
 *
 * NOTE: Subresource Integrity (integrity="sha384-...") is intentionally NOT
 * applied. Google ships this URL as a mutable, latest-tracking build that is
 * updated in place to stay in sync with receiver-protocol changes; pinning a
 * hash would break Cast the next time Google rolls the SDK. There is no
 * versioned alternative URL — every Cast-enabled site loads it this way.
 * Defense-in-depth belongs at the CSP layer (allow only gstatic.com as an
 * off-origin script-src), not at the script tag.
 */

const CAST_SDK_BASE = 'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js';
export const CAST_SDK_SRC = `${CAST_SDK_BASE}?loadCastFramework=1`;

let injected = false;

/**
 * Idempotently appends the Cast sender SDK <script> to the document head.
 * Safe to call from every useCast mount (StrictMode double-mounts included) —
 * the module flag plus a DOM probe guarantee at most one tag.
 */
export function injectCastSdk(doc: Document = document): void {
    if (injected) return;
    // A tag may already exist outside our control (e.g. a stale cached
    // index.html from before the lazy-load change, or another hook instance
    // in a different module graph). Never double-inject.
    if (doc.querySelector(`script[src^="${CAST_SDK_BASE}"]`)) {
        injected = true;
        return;
    }
    const script = doc.createElement('script');
    script.src = CAST_SDK_SRC;
    script.async = true;
    // If gstatic is unreachable (offline LAN, ad-blocked), surface the same
    // signal the SDK itself uses for "cast can't run here" so useCast can
    // report unavailability instead of waiting forever.
    script.onerror = () => window.__onGCastApiAvailable?.(false);
    doc.head.appendChild(script);
    injected = true;
}

/** Test-only: resets the module-level double-inject guard. */
export function resetCastSdkInjectionForTests(): void {
    injected = false;
}
