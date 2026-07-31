import type { Rendition } from 'epubjs';

/**
 * EPUB reader theming — the CSS pushed into every epub.js chunk document.
 *
 * Extracted from BookReader.tsx: these are plain functions (no components), and
 * a component file that also exports helpers defeats Fast Refresh for the whole
 * module. BookReader consumes them for its content hook and theme effects;
 * BookReader.test.tsx exercises refreshReaderTheme directly.
 *
 * ER-011: reader colour palette lives in CSS variables on [data-reader-root]
 * in index.css. Everything that runs outside that DOM scope — the EpubView
 * container background (React inline style: CSS var refs work fine) and the
 * epub.js iframe theme (separate document: needs computed values) — reads from
 * those variables. ER-021 flips them by toggling data-reading-theme.
 */

export interface ReaderThemeTokens {
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
export const THEME_FALLBACK: ReaderThemeTokens = {
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

export function readReaderTokens(root: Element | null): ReaderThemeTokens {
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
export const EPUB_THEME_STYLE_ID = 'softmedia-reader-theme';

export function buildEpubThemeCss(t: ReaderThemeTokens, overridePublisher: boolean): string {
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
export function applyThemeStyleTo(doc: Document, css: string): void {
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
 * Push the current theme CSS into every EPUB chunk that's currently rendered.
 * Walks rendition.getContents() — epub.js exposes every live Contents wrapper
 * that way — and replaces the sentinel <style> in each. ER-021 drives this on
 * theme change; BookReader's content hook handles newly-rendered chunks.
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
