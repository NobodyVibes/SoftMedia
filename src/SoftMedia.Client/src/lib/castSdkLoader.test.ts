import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { injectCastSdk, resetCastSdkInjectionForTests, CAST_SDK_SRC } from './castSdkLoader';

const SDK_SELECTOR = 'script[src^="https://www.gstatic.com/cv/js/sender/v1/cast_sender.js"]';

function sdkScripts() {
    return document.querySelectorAll<HTMLScriptElement>(SDK_SELECTOR);
}

describe('castSdkLoader', () => {
    beforeEach(() => {
        resetCastSdkInjectionForTests();
        document.querySelectorAll('script').forEach((s) => s.remove());
        delete window.__onGCastApiAvailable;
    });

    afterEach(() => {
        delete window.__onGCastApiAvailable;
    });

    it('appends the SDK script to <head> with the framework flag', () => {
        injectCastSdk();

        const scripts = sdkScripts();
        expect(scripts.length).toBe(1);
        expect(scripts[0].src).toBe(CAST_SDK_SRC);
        expect(scripts[0].parentElement).toBe(document.head);
        expect(scripts[0].async).toBe(true);
    });

    it('never double-injects across repeated calls (StrictMode double-mount)', () => {
        injectCastSdk();
        injectCastSdk();
        injectCastSdk();

        expect(sdkScripts().length).toBe(1);
    });

    it('does not inject when a tag already exists in the document (stale cached index.html)', () => {
        const existing = document.createElement('script');
        existing.src = CAST_SDK_SRC;
        document.head.appendChild(existing);

        injectCastSdk();

        expect(sdkScripts().length).toBe(1);
    });

    it('signals cast unavailability through __onGCastApiAvailable when the script fails to load', () => {
        // The hook registers this BEFORE injecting — mirror that ordering here.
        const onAvailable = vi.fn();
        window.__onGCastApiAvailable = onAvailable;

        injectCastSdk();
        sdkScripts()[0].dispatchEvent(new Event('error'));

        expect(onAvailable).toHaveBeenCalledWith(false);
    });
});
