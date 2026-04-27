import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import BookReader from './BookReader';
import { MediaType, type MediaItem } from '../../types';
import * as bookService from '../../services/bookService';
import { useReaderStore } from '../../store/readerStore';

vi.mock('../../services/bookService');
vi.mock('react-pdf', () => ({
    Document: ({ children }: { children?: React.ReactNode }) => <div data-testid="pdf-document">{children}</div>,
    Page: ({ pageNumber }: { pageNumber: number }) => <div data-testid="pdf-page">{pageNumber}</div>,
    pdfjs: { GlobalWorkerOptions: { workerSrc: '' } },
}));
// BookReader now drives epub.js directly via the local EpubView. Stub it with a
// static testid-bearing div so the engine doesn't try to fetch/parse real books
// in jsdom, and so the chrome tests can assert the reader mounted.
vi.mock('./EpubView', () => ({
    default: () => <div data-testid="epub-reader" />,
}));
vi.mock('../../store/authStore', () => ({
    useAuthStore: Object.assign(
        (selector: (s: { token: string | null }) => unknown) => selector({ token: 'test-token' }),
        { getState: () => ({ token: 'test-token' }) },
    ),
}));

// Global safe defaults for bookService mocks. Vitest's vi.mock() auto-stubs each
// export as vi.fn() returning undefined — on which `.catch()` throws. Clearing
// here at the top level (instead of in every describe) means inner beforeEach
// blocks no longer call clearAllMocks — their own per-test stubs compose on
// top of this baseline.
beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(bookService.getReaderPreferences).mockResolvedValue(null);
    vi.mocked(bookService.putReaderPreferences).mockResolvedValue();
    vi.mocked(bookService.markFinished).mockResolvedValue();
    vi.mocked(bookService.updateProgress).mockResolvedValue();
    vi.mocked(bookService.listBookmarks).mockResolvedValue([]);
    vi.mocked(bookService.createBookmark).mockResolvedValue({
        id: 'bm-stub', position: 1, cfi: null, label: null, createdAt: new Date().toISOString(),
    });
    vi.mocked(bookService.deleteBookmark).mockResolvedValue();
    vi.mocked(bookService.updateBookmarkLabel).mockResolvedValue();
    vi.mocked(bookService.listHighlights).mockResolvedValue([]);
    vi.mocked(bookService.createHighlight).mockResolvedValue({
        id: 'h-stub', locationJson: '{}', colour: 'yellow', quotedText: '', note: null,
        createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
    });
    vi.mocked(bookService.deleteHighlight).mockResolvedValue();
    vi.mocked(bookService.updateHighlight).mockResolvedValue();
    vi.mocked(bookService.startReadingSession).mockResolvedValue('session-stub');
    vi.mocked(bookService.endReadingSession).mockResolvedValue();
    vi.mocked(bookService.getReadingSessionSummary).mockResolvedValue({
        sessionCount: 0, totalMinutes: 0, totalPages: 0, pagesPerMinute: 0,
    });
    vi.mocked(bookService.lookupWord).mockResolvedValue({
        word: '', definitions: [], available: true,
    });
    vi.mocked(bookService.parseHighlightLocation).mockImplementation((raw: string) => {
        try {
            const o = JSON.parse(raw) as { type?: string; cfi?: string; page?: number };
            if (o.type === 'epub' && o.cfi) return { type: 'epub', cfi: o.cfi };
            if (o.type === 'pdf' && typeof o.page === 'number') return { type: 'pdf', page: o.page };
        } catch { /* fallthrough */ }
        return null;
    });
});

const cbzItem: MediaItem = {
    id: 'book-1',
    title: 'Test Comic',
    type: MediaType.Book,
    path: '/lib/comic.cbz',
    dateAdded: new Date().toISOString(),
    libraryId: 'lib1',
    sortTitle: 'Test Comic',
};

const epubItem: MediaItem = {
    id: 'book-2',
    title: 'Test EPUB',
    type: MediaType.Book,
    path: '/lib/story.epub',
    dateAdded: new Date().toISOString(),
    libraryId: 'lib1',
    sortTitle: 'Test EPUB',
};

describe('BookReader (CBZ)', () => {
    beforeEach(() => {
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('loads CBZ info and renders the first page on mount when no progress exists', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockReturnValue('/api/v1/books/book-1/page/1?token=t');

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => {
            const img = screen.getByAltText('Page 1') as HTMLImageElement;
            expect(img.src).toContain('/page/1');
        });

        expect(screen.getByText('1 / 5')).toBeInTheDocument();
    });

    it('resumes from saved page when progress > 0', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 3, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => {
            expect(screen.getByAltText('Page 3')).toBeInTheDocument();
        });
        expect(screen.getByText('3 / 10')).toBeInTheDocument();
    });

    it('advances on right-arrow keypress and saves progress (debounced)', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.updateProgress).mockResolvedValue();

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());

        // Switch to fake timers AFTER mount/effect resolution so waitFor can poll above.
        vi.useFakeTimers();
        try {
            fireEvent.keyDown(window, { key: 'ArrowRight' });
            fireEvent.keyDown(window, { key: 'ArrowRight' });

            expect(screen.getByAltText('Page 3')).toBeInTheDocument();
            expect(bookService.updateProgress).not.toHaveBeenCalled();

            await act(async () => {
                vi.advanceTimersByTime(1000);
            });

            expect(bookService.updateProgress).toHaveBeenCalledTimes(1);
            expect(bookService.updateProgress).toHaveBeenLastCalledWith('book-1', 3, null);
        } finally {
            vi.useRealTimers();
        }
    });
});

describe('BookReader (EPUB unified chrome)', () => {
    beforeEach(() => {
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('renders the EPUB reader plus a unified PageControls pill with percentage label', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });

        render(
            <MemoryRouter>
                <BookReader item={epubItem} />
            </MemoryRouter>,
        );

        // The EPUB engine mount is mocked — we verify our own chrome renders alongside it.
        expect(screen.getByTestId('epub-reader')).toBeInTheDocument();

        // PageControls share the same Prev/Next aria-labels across all three formats.
        expect(screen.getByLabelText('Previous page')).toBeInTheDocument();
        expect(screen.getByLabelText('Next page')).toBeInTheDocument();

        // Initial label shows "0%" until the rendition fires its first relocated event.
        expect(screen.getByText('0%')).toBeInTheDocument();
    });
});

describe('BookReader — double-page spread (ER-002)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('CBZ renders two adjacent images when spread is double', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        act(() => useReaderStore.getState().setSpread('double'));

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => {
            expect(screen.getByAltText('Page 1')).toBeInTheDocument();
            expect(screen.getByAltText('Page 2')).toBeInTheDocument();
        });
    });

    it('CBZ at last odd page renders only the left-side image', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 5, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5, // odd total
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        // Landing on the last page triggers the end-of-book auto-mark; stub it
        // so the effect's .catch() has something to chain from.
        vi.mocked(bookService.markFinished).mockResolvedValue();
        act(() => useReaderStore.getState().setSpread('double'));

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        // Resume lands on page 5; pair (5, 6) — but 6 doesn't exist, so only
        // page 5 renders. Spread logic must not paint a non-existent page.
        await waitFor(() => expect(screen.getByAltText('Page 5')).toBeInTheDocument());
        expect(screen.queryByAltText('Page 6')).not.toBeInTheDocument();
    });

    it('PageControls label reads "n–(n+1) / total" in spread mode', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        act(() => useReaderStore.getState().setSpread('double'));

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => expect(screen.getByText('1–2 / 10')).toBeInTheDocument());
    });

    it('arrow-key navigation advances by 2 in spread mode', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        act(() => useReaderStore.getState().setSpread('double'));

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());

        fireEvent.keyDown(window, { key: 'ArrowRight' });
        // After one spread-step: pair (3, 4).
        await waitFor(() => expect(screen.getByAltText('Page 3')).toBeInTheDocument());
        expect(screen.getByAltText('Page 4')).toBeInTheDocument();
    });
});

describe('BookReader — per-book overrides (ER-012)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('applies fetched per-book overrides to the store on mount', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.getReaderPreferences).mockResolvedValue({
            schemaVersion: 1,
            theme: 'sepia',
            fontSize: 140,
            spread: 'double',
        });

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => {
            const s = useReaderStore.getState();
            expect(s.theme).toBe('sepia');
            expect(s.fontSize).toBe(140);
            expect(s.spread).toBe('double');
        });
    });

    it('save-for-this-book button PUTs the current store state', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.getReaderPreferences).mockResolvedValue(null);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        // Change the theme before saving — the PUT should reflect it.
        act(() => useReaderStore.getState().setTheme('high-contrast'));

        fireEvent.click(await screen.findByLabelText('Reader settings'));
        fireEvent.click(await screen.findByRole('button', { name: /save for this book/i }));

        await waitFor(() => {
            expect(bookService.putReaderPreferences).toHaveBeenCalledTimes(1);
        });
        const args = vi.mocked(bookService.putReaderPreferences).mock.calls[0];
        expect(args[0]).toBe('book-1');
        expect(args[1]?.theme).toBe('high-contrast');
        expect(args[1]?.schemaVersion).toBe(1);
    });

    it('clear-override button is disabled until a server override exists', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.getReaderPreferences).mockResolvedValue(null);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        fireEvent.click(await screen.findByLabelText('Reader settings'));
        const clearBtn = await screen.findByRole('button', { name: /clear override/i });
        expect(clearBtn).toBeDisabled();
    });
});

describe('BookReader — keyboard shortcuts (ER-054)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('opens the help sheet on `?`', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());

        fireEvent.keyDown(window, { key: '?' });
        expect(await screen.findByRole('dialog', { name: /keyboard shortcuts/i })).toBeInTheDocument();
    });

    it('cycles reading theme on `z`', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());

        // Default is 'dark'. z → sepia → high-contrast → dark.
        expect(useReaderStore.getState().theme).toBe('dark');
        fireEvent.keyDown(window, { key: 'z' });
        expect(useReaderStore.getState().theme).toBe('sepia');
        fireEvent.keyDown(window, { key: 'z' });
        expect(useReaderStore.getState().theme).toBe('high-contrast');
        fireEvent.keyDown(window, { key: 'z' });
        expect(useReaderStore.getState().theme).toBe('dark');
    });

    it('zooms PDF/CBZ on `+` and `-`', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());

        // CBZ defaults to zoom=fit-width. `+` should seed it at 125% (100+25).
        fireEvent.keyDown(window, { key: '+' });
        expect(useReaderStore.getState().zoom).toBe(125);
        fireEvent.keyDown(window, { key: '-' });
        expect(useReaderStore.getState().zoom).toBe(100);
    });
});

describe('BookReader — in-book search (ER-024)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('opens the search drawer when the `/` shortcut fires (PDF)', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });

        render(
            <MemoryRouter>
                <BookReader item={{
                    id: 'book-pdf', title: 'Test PDF', type: MediaType.Book,
                    path: '/lib/test.pdf', dateAdded: new Date().toISOString(),
                    libraryId: 'lib1', sortTitle: 'Test PDF',
                }} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByTestId('pdf-document')).toBeInTheDocument());

        fireEvent.keyDown(window, { key: '/' });

        expect(await screen.findByRole('dialog', { name: /search in book/i }))
            .toBeInTheDocument();
    });

    it('shows disabled copy for CBZ', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        // The search header button is hidden for CBZ — verify.
        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());
        expect(screen.queryByLabelText('Search in book')).not.toBeInTheDocument();
    });
});

describe('BookReader — highlights (ER-040/041)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('renders the highlights header button with a count when data loads', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.listHighlights).mockResolvedValue([
            { id: 'h1', locationJson: '{"type":"epub","cfi":"x"}', colour: 'yellow',
              quotedText: 'a pithy quote', note: null,
              createdAt: '2026-01-01', updatedAt: '2026-01-01' },
            { id: 'h2', locationJson: '{"type":"epub","cfi":"y"}', colour: 'green',
              quotedText: 'another', note: 'with a note',
              createdAt: '2026-01-02', updatedAt: '2026-01-02' },
        ]);

        render(
            <MemoryRouter>
                <BookReader item={epubItem} />
            </MemoryRouter>,
        );

        const btn = await screen.findByLabelText('Highlights list');
        expect(btn.textContent).toContain('2');
    });

    it('toggles highlight mode on `h` and shows the status pill', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });

        render(
            <MemoryRouter>
                <BookReader item={epubItem} />
            </MemoryRouter>,
        );
        // Initially off.
        expect(screen.queryByText(/highlight mode/i)).not.toBeInTheDocument();

        fireEvent.keyDown(window, { key: 'h' });
        expect(await screen.findByText(/highlight mode/i)).toBeInTheDocument();

        // Header button reflects the active state.
        const btn = screen.getByLabelText('Exit highlight mode');
        expect(btn).toHaveAttribute('aria-pressed', 'true');

        // Toggle off — pill gone, button label flipped.
        fireEvent.keyDown(window, { key: 'h' });
        await waitFor(() => expect(screen.queryByText(/highlight mode/i)).not.toBeInTheDocument());
    });

    it('hides the highlights header button for CBZ (no text selection)', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 1')).toBeInTheDocument());
        expect(screen.queryByLabelText('Highlights list')).not.toBeInTheDocument();
    });
});

describe('BookReader — bookmarks (ER-023)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('renders the header button with a count of loaded bookmarks', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.listBookmarks).mockResolvedValue([
            { id: 'a', position: 3, cfi: null, label: 'Later', createdAt: '2026-01-01' },
            { id: 'b', position: 7, cfi: null, label: null, createdAt: '2026-01-02' },
        ]);

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        const btn = await screen.findByLabelText('Bookmarks');
        // Badge renders the count.
        expect(btn.textContent).toContain('2');
    });

    it('`b` shortcut creates a bookmark at the current page (CBZ)', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 4, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.listBookmarks).mockResolvedValue([]);
        vi.mocked(bookService.createBookmark).mockResolvedValue({
            id: 'new-bm', position: 4, cfi: null, label: null, createdAt: '2026-04-19T00:00:00Z',
        });

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 4')).toBeInTheDocument());

        fireEvent.keyDown(window, { key: 'b' });

        await waitFor(() => {
            expect(bookService.createBookmark).toHaveBeenCalledWith('book-1', { position: 4 });
        });
    });
});

describe('BookReader — RTL reading direction (ER-031)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('flips arrow-key → direction mapping when rtl is on', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 2, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        act(() => useReaderStore.getState().setRtl(true));

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        await waitFor(() => expect(screen.getByAltText('Page 2')).toBeInTheDocument());

        // In RTL, ArrowLeft should advance — user reads right-to-left.
        fireEvent.keyDown(window, { key: 'ArrowLeft' });
        await waitFor(() => expect(screen.getByAltText('Page 3')).toBeInTheDocument());

        // ArrowRight should retreat in RTL.
        fireEvent.keyDown(window, { key: 'ArrowRight' });
        await waitFor(() => expect(screen.getByAltText('Page 2')).toBeInTheDocument());
    });

    it('reverses the spread flex order when RTL + Double', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        act(() => {
            useReaderStore.getState().setRtl(true);
            useReaderStore.getState().setSpread('double');
        });

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );
        const page1 = await screen.findByAltText('Page 1');
        const container = page1.parentElement!;
        expect(container.className).toMatch(/flex-row-reverse/);
    });
});

describe('BookReader — reading theme (ER-021)', () => {
    beforeEach(() => {
        window.localStorage.removeItem('softmedia.reader.prefs.v1');
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    afterEach(() => {
        act(() => useReaderStore.getState().resetReaderPrefs());
    });

    it('applies the current theme to the reader root as data-reading-theme', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);

        const { container } = render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        // Default theme.
        const root = container.querySelector('[data-reader-root]');
        expect(root).toHaveAttribute('data-reading-theme', 'dark');

        // Swap to sepia through the store and expect the attribute to follow.
        await act(async () => {
            useReaderStore.getState().setTheme('sepia');
        });
        expect(root).toHaveAttribute('data-reading-theme', 'sepia');
    });
});

describe('BookReader — mark as finished', () => {
    beforeEach(() => {
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('does not auto-mark when opening mid-book', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 2, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 10,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.markFinished).mockResolvedValue();

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => expect(screen.getByAltText('Page 2')).toBeInTheDocument());
        // Allow any queued microtasks in the end-of-book effect to settle.
        await act(async () => { await Promise.resolve(); });

        expect(bookService.markFinished).not.toHaveBeenCalled();
    });

    it('auto-marks finished exactly once when navigating to the last page', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 2, bookLocation: null, lastPlayed: null, isWatched: false,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 3,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.markFinished).mockResolvedValue();

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        await waitFor(() => expect(screen.getByAltText('Page 2')).toBeInTheDocument());

        // Advance to the last page.
        fireEvent.keyDown(window, { key: 'ArrowRight' });
        await waitFor(() => expect(screen.getByAltText('Page 3')).toBeInTheDocument());

        // Auto-fire must hit exactly once. Press ArrowLeft+ArrowRight to confirm
        // scrubbing doesn't repeat it.
        await waitFor(() => {
            expect(bookService.markFinished).toHaveBeenCalledWith('book-1', true);
        });

        fireEvent.keyDown(window, { key: 'ArrowLeft' });
        fireEvent.keyDown(window, { key: 'ArrowRight' });

        expect(bookService.markFinished).toHaveBeenCalledTimes(1);
    });

    it('hydrates the toggle button from progress and round-trips on click', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null, isWatched: true,
        });
        vi.mocked(bookService.getBookInfo).mockResolvedValue({
            id: 'book-1', format: 'cbz', pageCount: 5,
        });
        vi.mocked(bookService.getBookPageUrl).mockImplementation((_id, n) => `/page/${n}`);
        vi.mocked(bookService.markFinished).mockResolvedValue();

        render(
            <MemoryRouter>
                <BookReader item={cbzItem} />
            </MemoryRouter>,
        );

        // Initial state reflects isWatched=true from the interaction.
        const unfinishBtn = await screen.findByLabelText('Mark as unfinished');
        expect(unfinishBtn).toHaveAttribute('aria-pressed', 'true');

        // Hydrated finished state pre-arms the guard — auto-fire must stay quiet.
        expect(bookService.markFinished).not.toHaveBeenCalled();

        fireEvent.click(unfinishBtn);

        await waitFor(() =>
            expect(bookService.markFinished).toHaveBeenCalledWith('book-1', false));
        expect(await screen.findByLabelText('Mark as finished')).toBeInTheDocument();
    });
});
