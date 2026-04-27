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
});
