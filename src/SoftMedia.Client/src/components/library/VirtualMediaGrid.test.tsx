import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import VirtualMediaGrid from './VirtualMediaGrid';
import { MediaType, type MediaItem } from '../../types';

// Lazy-image plumbing (LoadingImage) needs an IntersectionObserver; jsdom has none.
vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: true }),
}));

vi.mock('../../store/audioStore', () => ({
    useAudioStore: vi.fn(() => ({ playTrack: vi.fn(), addToQueue: vi.fn(), playPlaylist: vi.fn() })),
}));

// ---- jsdom layout shims -----------------------------------------------------
// jsdom computes no layout: clientWidth/getBoundingClientRect are 0 and
// ResizeObserver/matchMedia don't exist. Simulate a viewport so the grid can
// compute real columns and the virtualizer a real visible range.

let viewportWidth = 1280;
let viewportHeight = 800;
let compactViewport = false;

class ResizeObserverStub {
    observe() { /* layout never changes mid-test */ }
    unobserve() { /* noop */ }
    disconnect() { /* noop */ }
}

function installLayoutShims() {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub);
    window.matchMedia = ((query: string) => ({
        matches: compactViewport,
        media: query,
        onchange: null,
        addEventListener: () => { /* noop */ },
        removeEventListener: () => { /* noop */ },
        addListener: () => { /* noop */ },
        removeListener: () => { /* noop */ },
        dispatchEvent: () => false,
    })) as typeof window.matchMedia;

    // clientWidth feeds the grid's own column math; offsetWidth/offsetHeight
    // feed @tanstack/virtual-core's getRect() for the scroll viewport.
    for (const [prop, getter] of [
        ['clientWidth', () => viewportWidth],
        ['clientHeight', () => viewportHeight],
        ['offsetWidth', () => viewportWidth],
        ['offsetHeight', () => viewportHeight],
    ] as const) {
        Object.defineProperty(HTMLElement.prototype, prop, { configurable: true, get: getter });
    }
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(() => ({
        x: 0, y: 0, top: 0, left: 0, right: viewportWidth, bottom: viewportHeight,
        width: viewportWidth, height: viewportHeight,
        toJSON: () => ({}),
    } as DOMRect));
}

function makeItems(count: number): MediaItem[] {
    return Array.from({ length: count }, (_, i) => ({
        id: `item-${i + 1}`,
        title: `Movie ${i + 1}`,
        type: MediaType.Movie,
    } as MediaItem));
}

function renderGrid(items: MediaItem[], libraryType = 'Movie') {
    return render(
        <MemoryRouter>
            <main>
                <VirtualMediaGrid
                    items={items}
                    libraryType={libraryType}
                    hoveredId={null}
                    setHoveredId={() => { /* noop */ }}
                    isRevealed={() => true}
                    onImageLoad={() => { /* noop */ }}
                    onImageError={() => { /* noop */ }}
                />
            </main>
        </MemoryRouter>
    );
}

describe('VirtualMediaGrid', () => {
    beforeEach(() => {
        viewportWidth = 1280;
        viewportHeight = 800;
        compactViewport = false;
        installLayoutShims();
    });

    afterEach(() => {
        vi.restoreAllMocks();
        vi.unstubAllGlobals();
        // Remove the prototype getters so other suites see stock jsdom.
        for (const prop of ['clientWidth', 'clientHeight', 'offsetWidth', 'offsetHeight']) {
            delete (HTMLElement.prototype as unknown as Record<string, unknown>)[prop];
        }
    });

    it('mounts a bounded number of cards, not the whole 500-item library (SR-WI-042)', () => {
        renderGrid(makeItems(500));

        // Movie cards render as links; only viewport + overscan rows may exist.
        const cards = screen.getAllByRole('link');
        expect(cards.length).toBeGreaterThan(0);
        expect(cards.length).toBeLessThan(80);

        // Items far below the fold must not be in the DOM at all.
        expect(screen.queryByTitle('Movie 500')).toBeNull();
    });

    it('reserves the full scroll height for all rows', () => {
        renderGrid(makeItems(500));

        // 1280px wide desktop → 5 columns of 192px → 100 rows × (400 + 32)px.
        const grid = screen.getByTestId('virtual-media-grid');
        expect(grid.style.height).toBe(`${100 * 432}px`);
    });

    it('keeps the exact desktop layout: fixed 192px columns with gap-8', () => {
        renderGrid(makeItems(20));

        const grid = screen.getByTestId('virtual-media-grid');
        const firstRow = grid.firstElementChild as HTMLElement;
        // floor((1280 + 32) / (192 + 32)) = 5 columns, unchanged card width.
        expect(firstRow.style.gridTemplateColumns).toBe('repeat(5, 192px)');
        expect(firstRow.style.gap).toBe('32px');
    });

    it('switches to adaptive minmax-style columns on phone widths', () => {
        viewportWidth = 375;
        compactViewport = true;

        renderGrid(makeItems(20));

        const grid = screen.getByTestId('virtual-media-grid');
        const firstRow = grid.firstElementChild as HTMLElement;
        // max(2, floor((375 + 16) / (110 + 16))) = 3 columns ≥110px wide —
        // no more single-column-with-dead-margin phones.
        expect(firstRow.style.gridTemplateColumns).toBe('repeat(3, 114px)');
        expect(firstRow.style.gap).toBe('16px');
    });

    it('keeps mounted cards keyboard-reachable (real links, no tabindex=-1)', () => {
        renderGrid(makeItems(10));

        const cards = screen.getAllByRole('link');
        for (const card of cards) {
            expect(card).not.toHaveAttribute('tabindex', '-1');
        }
    });
});
