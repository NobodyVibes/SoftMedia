import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

/**
 * Universal Client CI guard.
 *
 * Scans every .tsx/.ts file under src/components for interactive-element
 * violations the audit identified. Runs as part of `npm test` so regressions
 * fail the build rather than sneaking in unnoticed. See
 * docs/todos/06-universal-client-a11y.md and docs/rules/01-core-philosophy.md.
 *
 * Each check:
 * 1. Flags a pattern, then
 * 2. Requires that the same element also carries the corresponding a11y
 *    attributes on the same element (role="button", tabIndex, onKeyDown, etc.).
 */

const COMPONENTS_ROOT = join(__dirname, '..', 'components');

function collectTsxFiles(dir: string): string[] {
    const entries = readdirSync(dir);
    const out: string[] = [];
    for (const entry of entries) {
        const full = join(dir, entry);
        const stat = statSync(full);
        if (stat.isDirectory()) {
            out.push(...collectTsxFiles(full));
        } else if (full.endsWith('.tsx') || full.endsWith('.ts')) {
            // Skip test files — fixtures are free to mock bad patterns
            if (!full.endsWith('.test.tsx') && !full.endsWith('.test.ts')) {
                out.push(full);
            }
        }
    }
    return out;
}

/**
 * Capture every opening `<div …>` tag (possibly multiline) and return its
 * start line. Uses a minimal brace-aware walker rather than a single regex so
 * JSX attribute interpolations with `>` characters don't terminate the tag
 * prematurely.
 */
function findOpeningDivTags(source: string): { startIdx: number; tag: string }[] {
    const hits: { startIdx: number; tag: string }[] = [];
    let i = 0;
    while (i < source.length) {
        const start = source.indexOf('<div', i);
        if (start === -1) break;

        // Skip if preceded by an identifier char — avoids false hits like `</div>` or identifiers
        const prev = source[start - 1];
        if (prev && /[A-Za-z0-9_]/.test(prev)) {
            i = start + 4;
            continue;
        }

        // Walk forward respecting JSX brace nesting to find the closing '>'
        let j = start + 4;
        let depth = 0;
        while (j < source.length) {
            const ch = source[j];
            if (ch === '{') depth++;
            else if (ch === '}') depth--;
            else if (ch === '>' && depth === 0) break;
            j++;
        }
        if (j >= source.length) break;
        hits.push({ startIdx: start, tag: source.slice(start, j + 1) });
        i = j + 1;
    }
    return hits;
}

function hasAllRequired(tag: string, required: RegExp[]): boolean {
    return required.every((re) => re.test(tag));
}

function lineOf(source: string, idx: number): number {
    return source.slice(0, idx).split('\n').length;
}

const files = collectTsxFiles(COMPONENTS_ROOT);

describe('Universal Client a11y guard', () => {
    it('finds component files to audit', () => {
        expect(files.length).toBeGreaterThan(10);
    });

    it('no <div onClick> without role="button", tabIndex, and onKeyDown', () => {
        const violations: string[] = [];

        for (const file of files) {
            const source = readFileSync(file, 'utf-8');
            const tags = findOpeningDivTags(source);
            for (const { startIdx, tag } of tags) {
                if (!/\bonClick\s*=/.test(tag)) continue;

                const ok = hasAllRequired(tag, [
                    /\brole\s*=\s*["']button["']/,
                    /\btabIndex\s*=/,
                    /\bonKeyDown\s*=/,
                ]);
                if (!ok) {
                    const line = lineOf(source, startIdx);
                    violations.push(`${relative(COMPONENTS_ROOT, file)}:${line}`);
                }
            }
        }

        if (violations.length > 0) {
            const message =
                `Found ${violations.length} <div onClick> element(s) lacking ` +
                `role="button" + tabIndex + onKeyDown.\n` +
                `Convert each to a <button> element, or if a button would nest ` +
                `invalidly, add role="button" tabIndex={0} and an onKeyDown handler ` +
                `that maps Enter/Space to the same action as onClick.\n\n` +
                violations.map((v) => `  • ${v}`).join('\n');
            throw new Error(message);
        }
        expect(violations).toEqual([]);
    });

    it('no <th onClick> (sortable headers must wrap the label in a <button>)', () => {
        const violations: string[] = [];
        const thOnClickPattern = /<th\b[^>]*?\bonClick\s*=/gms;

        for (const file of files) {
            const source = readFileSync(file, 'utf-8');
            let m: RegExpExecArray | null;
            while ((m = thOnClickPattern.exec(source)) !== null) {
                violations.push(`${relative(COMPONENTS_ROOT, file)}:${lineOf(source, m.index)}`);
            }
        }

        if (violations.length > 0) {
            throw new Error(
                `Found ${violations.length} <th onClick> occurrence(s). ` +
                    `Sortable column headers must wrap the label in a <button> ` +
                    `and set aria-sort on the <th>. See UserListTable for the pattern.\n` +
                    violations.map((v) => `  • ${v}`).join('\n')
            );
        }
        expect(violations).toEqual([]);
    });

    /**
     * Files whose icon-only buttons MUST carry `aria-label` and whose `hover:`
     * classes MUST be paired with `focus-visible:` classes. Adding a file
     * here is a one-way ratchet: once on the list, regressions fail CI.
     *
     * Add a file here only after it has been verified clean. Do NOT add a
     * file with known violations — fix it first, then add.
     */
    const STRICT_A11Y_FILES = new Set<string>([
        // Player surface
        'player/ProgressBar.tsx',
        'player/VideoPlayer.tsx',
        'player/PersistentPlayer.tsx',
        'player/NextEpisodeOverlay.tsx',
        'player/PlayerDebugPanel.tsx',
        'player/visualizers/VisualizerSelector.tsx',
        // Library card
        'items/MediaCard.tsx',
        // Reader surface
        'reader/BookReader.tsx',
        'reader/BookmarksDrawer.tsx',
        'reader/HighlightsDrawer.tsx',
        'reader/ReaderSettingsPanel.tsx',
        'reader/SearchDrawer.tsx',
        'reader/ShortcutHelpSheet.tsx',
        'reader/TocDrawer.tsx',
        'reader/TtsNowPlayingBar.tsx',
        'reader/PdfHighlightOverlay.tsx',
        'reader/EpubView.tsx',
    ]);

    /**
     * SVG-only icon buttons in strictly-audited files MUST carry `aria-label`
     * so VoiceOver / NVDA / ChromeVox can announce their action. The 2026-04-26
     * audit found ~10 unlabelled buttons in VideoPlayer.tsx; D2 fixed them and
     * this guard prevents the regression.
     */
    it('icon-only <button> elements in audited files must have aria-label', () => {
        const buttonPattern = /<button\b([^>]*?)>([\s\S]*?)<\/button>/g;
        const violations: string[] = [];

        for (const file of files) {
            const rel = relative(COMPONENTS_ROOT, file).replace(/\\/g, '/');
            if (!STRICT_A11Y_FILES.has(rel)) continue;

            const source = readFileSync(file, 'utf-8');
            let m: RegExpExecArray | null;
            while ((m = buttonPattern.exec(source)) !== null) {
                const openTag = m[1];
                const body = m[2];

                if (/\baria-label\s*=/.test(openTag)) continue;

                // No <svg> child → button is probably text-only and its
                // visible text is its label.
                if (!/<svg\b/.test(body)) continue;

                // Strip JSX expressions, SVG blocks, and tags; what's left is
                // the rendered text. Non-empty means the icon has visible
                // accompanying text — skip.
                const stripped = body
                    .replace(/<svg\b[\s\S]*?<\/svg>/g, '')
                    .replace(/\{[\s\S]*?\}/g, '')
                    .replace(/<[^>]*>/g, '')
                    .trim();
                if (stripped.length > 0) continue;

                violations.push(`${rel}:${lineOf(source, m.index)}`);
            }
        }

        if (violations.length > 0) {
            throw new Error(
                `Found ${violations.length} icon-only <button> element(s) in ` +
                    `audited files without aria-label. Add aria-label="<the ` +
                    `action>" so screen-reader and TV-remote users hear ` +
                    `something meaningful. \`title=\` is NOT a substitute — ` +
                    `it isn't reliably announced.\n` +
                    violations.map((v) => `  • ${v}`).join('\n')
            );
        }
        expect(violations).toEqual([]);
    });

    /**
     * Hover treatments on interactive elements MUST be paired with a focus-
     * visible treatment so keyboard / remote users see the same affordance.
     * SDD §8.3 Universal Client rule 2. Same scoping as above.
     */
    it('every <button> with hover: classes in audited files has focus-visible: pair', () => {
        const buttonPattern = /<button\b([^>]*)>/g;
        const violations: string[] = [];

        for (const file of files) {
            const rel = relative(COMPONENTS_ROOT, file).replace(/\\/g, '/');
            if (!STRICT_A11Y_FILES.has(rel)) continue;

            const source = readFileSync(file, 'utf-8');
            let m: RegExpExecArray | null;
            while ((m = buttonPattern.exec(source)) !== null) {
                const tag = m[1];
                if (!/\bhover:[a-z-]+/.test(tag)) continue;
                if (/\bfocus-visible:/.test(tag)) continue;

                violations.push(`${rel}:${lineOf(source, m.index)}`);
            }
        }

        if (violations.length > 0) {
            throw new Error(
                `Found ${violations.length} <button> with hover: classes but no ` +
                    `focus-visible: pair (in audited files). Add a focus-visible: ` +
                    `variant — e.g. ` +
                    `\`focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none\`.\n` +
                    violations.map((v) => `  • ${v}`).join('\n')
            );
        }
        expect(violations).toEqual([]);
    });
});

/**
 * SR-WI-051 — dialog-semantics guard. The six modals below were migrated to
 * the shared <Modal> primitive (src/components/ui/Modal.tsx), which supplies
 * role="dialog", aria-modal, aria-labelledby (wired to the title), focus
 * trap, Escape-to-close, backdrop dismissal, focus return, and body scroll
 * lock. These static checks make sure nobody quietly reverts a modal to a
 * hand-rolled overlay, and that the primitive itself keeps its contract.
 * Runtime assertions live in components/modals/modalDialogSemantics.test.tsx
 * and components/ui/Modal.test.tsx.
 */
describe('Modal dialog semantics guard (SR-WI-051)', () => {
    /** One-way ratchet: once a modal adopts <Modal>, it must stay adopted. */
    const SHARED_MODAL_ADOPTERS = [
        'modals/ConfirmationModal.tsx',
        'modals/StreamingModal.tsx',
        'modals/LibraryAccessModal.tsx',
        'modals/RatingsModal.tsx',
        'admin/CreateUserModal.tsx',
        'admin/ResetPasswordModal.tsx',
    ];

    it('every migrated modal renders through the shared <Modal> primitive with a title', () => {
        const violations: string[] = [];

        for (const rel of SHARED_MODAL_ADOPTERS) {
            const source = readFileSync(join(COMPONENTS_ROOT, rel), 'utf-8');

            if (!/import\s*\{\s*Modal\s*\}\s*from\s*['"]\.{1,2}\/ui\/Modal['"]/.test(source)) {
                violations.push(`${rel}: does not import { Modal } from ui/Modal`);
            }
            if (!/<Modal\b/.test(source)) {
                violations.push(`${rel}: does not render <Modal>`);
            }
            if (!/<Modal\b[\s\S]*?\btitle\s*=/.test(source)) {
                violations.push(`${rel}: <Modal> is missing the title prop (aria-labelledby source)`);
            }
            // A hand-rolled full-screen overlay alongside <Modal> means the
            // dialog semantics were bypassed for some code path.
            if (/fixed inset-0/.test(source)) {
                violations.push(`${rel}: contains a hand-rolled "fixed inset-0" overlay — render through <Modal> instead`);
            }
        }

        if (violations.length > 0) {
            throw new Error(
                `Modal migration regressions:\n` + violations.map((v) => `  • ${v}`).join('\n')
            );
        }
        expect(violations).toEqual([]);
    });

    it('the shared Modal primitive keeps its dialog contract', () => {
        const source = readFileSync(join(COMPONENTS_ROOT, 'ui', 'Modal.tsx'), 'utf-8');

        expect(source).toMatch(/role="dialog"/);
        expect(source).toMatch(/aria-modal="true"/);
        expect(source).toMatch(/aria-labelledby=/);
        expect(source).toMatch(/'Escape'/); // Escape-to-close handler
        expect(source).toMatch(/'Tab'/); // focus trap
    });

    it('no Tailwind v3 bg-opacity-* utilities (removed in v4 — backdrop renders opaque)', () => {
        const violations: string[] = [];

        for (const file of files) {
            const source = readFileSync(file, 'utf-8');
            const m = /\bbg-opacity-\d+/.exec(source);
            if (m) {
                violations.push(`${relative(COMPONENTS_ROOT, file)}:${lineOf(source, m.index)} (${m[0]})`);
            }
        }

        if (violations.length > 0) {
            throw new Error(
                `Found ${violations.length} bg-opacity-* usage(s). Tailwind v4 removed ` +
                    `these utilities, so they silently do nothing (e.g. a "translucent" ` +
                    `backdrop renders fully opaque). Use slash opacity instead: ` +
                    `\`bg-black/50\`, not \`bg-black bg-opacity-50\`.\n` +
                    violations.map((v) => `  • ${v}`).join('\n')
            );
        }
        expect(violations).toEqual([]);
    });
});
