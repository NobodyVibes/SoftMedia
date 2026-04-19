import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import BookReader from './BookReader';
import { MediaType, type MediaItem } from '../../types';
import * as bookService from '../../services/bookService';

vi.mock('../../services/bookService');
vi.mock('react-pdf', () => ({
    Document: ({ children }: { children?: React.ReactNode }) => <div data-testid="pdf-document">{children}</div>,
    Page: ({ pageNumber }: { pageNumber: number }) => <div data-testid="pdf-page">{pageNumber}</div>,
    pdfjs: { GlobalWorkerOptions: { workerSrc: '' } },
}));
vi.mock('react-reader', () => ({
    ReactReader: () => <div data-testid="epub-reader" />,
    // BookReader now imports ReactReaderStyle to build its unified readerStyles object.
    // Stub with empty CSSProperties per key — shape matches IReactReaderStyle.
    ReactReaderStyle: new Proxy({}, { get: () => ({}) }),
}));
vi.mock('../../store/authStore', () => ({
    useAuthStore: Object.assign(
        (selector: (s: { token: string | null }) => unknown) => selector({ token: 'test-token' }),
        { getState: () => ({ token: 'test-token' }) },
    ),
}));

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
        vi.clearAllMocks();
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('loads CBZ info and renders the first page on mount when no progress exists', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null,
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
            position: 3, bookLocation: null, lastPlayed: null,
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
            position: 0, bookLocation: null, lastPlayed: null,
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
        vi.clearAllMocks();
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('renders the EPUB reader plus a unified PageControls pill with percentage label', async () => {
        vi.mocked(bookService.getProgress).mockResolvedValue({
            position: 0, bookLocation: null, lastPlayed: null,
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
