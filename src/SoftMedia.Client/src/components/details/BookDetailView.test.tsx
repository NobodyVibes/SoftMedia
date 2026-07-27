import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import BookDetailView from './BookDetailView';
import { getProgress } from '../../services/bookService';
import type { MediaItem } from '../../types';

vi.mock('../../services/bookService', () => ({
    getProgress: vi.fn(),
}));

const baseBook: MediaItem = {
    id: 'b1',
    libraryId: 'lib1',
    title: 'The Shining',
    sortTitle: 'Shining, The',
    dateAdded: '2026-01-01T00:00:00Z',
    type: 'Book',
    container: 'epub',
};

function renderBook(overrides: Partial<MediaItem> = {}) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter>
                <BookDetailView item={{ ...baseBook, ...overrides }} />
            </MemoryRouter>
        </QueryClientProvider>,
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getProgress).mockResolvedValue({
        position: 0, bookLocation: null, lastPlayed: null, isWatched: false,
    });
});

/**
 * Every book in the library showed "Author: Unknown / Publisher: Unknown / ISBN: N/A /
 * Pages: Unknown" because this view read `item.metadata.author` and friends — keys the
 * server never emitted, since the metadata bag is a frozen contract and the underlying
 * values had no typed home on the DTO. These tests pin the typed fields it reads now.
 */
describe('BookDetailView book details', () => {
    it('renders publisher, ISBN and pages from the typed DTO fields', () => {
        renderBook({
            studio: 'Doubleday',
            isbn: '9780385121675',
            pageCount: 447,
            year: 1977,
            director: 'Stephen King',
        });

        expect(screen.getByText('Doubleday')).toBeInTheDocument();
        expect(screen.getByText('9780385121675')).toBeInTheDocument();
        expect(screen.getByText('447')).toBeInTheDocument();
        expect(screen.getByText('1977')).toBeInTheDocument();
    });

    it('shows the scanner-embedded author from `director` when there is no cast', () => {
        renderBook({ director: 'Stephen King' });

        expect(screen.getByText('Author')).toBeInTheDocument();
        expect(screen.getByText('Stephen King')).toBeInTheDocument();
    });

    it('prefers the full author list from cast over the single embedded creator', () => {
        // OpenLibrary writes every author of a work into cast with the character "Author";
        // the embedded read only ever yields one name, so co-authored books need the cast.
        renderBook({
            director: 'Brian Herbert',
            cast: [
                { id: 1, name: 'Brian Herbert', characters: ['Author'], order: 0 },
                { id: 2, name: 'Kevin J. Anderson', characters: ['Author'], order: 1 },
            ],
        });

        expect(screen.getByText('Authors')).toBeInTheDocument();
        expect(screen.getByText('Brian Herbert, Kevin J. Anderson')).toBeInTheDocument();
    });

    it('ignores non-author cast entries', () => {
        renderBook({
            director: 'Stephen King',
            cast: [{ id: 3, name: 'Someone Else', characters: ['Narrator'], order: 0 }],
        });

        expect(screen.getByText('Stephen King')).toBeInTheDocument();
        expect(screen.queryByText('Someone Else')).not.toBeInTheDocument();
    });

    it('omits fields with no data instead of printing Unknown', () => {
        renderBook({ year: 1977 });

        expect(screen.queryByText('Unknown')).not.toBeInTheDocument();
        expect(screen.queryByText('N/A')).not.toBeInTheDocument();
        expect(screen.queryByText('Author')).not.toBeInTheDocument();
        expect(screen.queryByText('Publisher')).not.toBeInTheDocument();
        expect(screen.queryByText('ISBN')).not.toBeInTheDocument();
        expect(screen.queryByText('Pages')).not.toBeInTheDocument();
        // The one field we do have still renders, card and all.
        expect(screen.getByText('Book Details')).toBeInTheDocument();
        expect(screen.getByText('1977')).toBeInTheDocument();
    });

    it('hides the details card entirely when nothing is known', () => {
        renderBook();

        expect(screen.queryByText('Book Details')).not.toBeInTheDocument();
    });

    // The read action lives on the detail page's primary button (under the cover art),
    // not in this view — a body link would be a second, duplicate entry point.
    // Label logic is covered by lib/bookReadLabel.test.ts.
    it('does not render its own read link', () => {
        renderBook({ container: 'epub' });

        expect(screen.queryByRole('link', { name: /read|continue/i })).not.toBeInTheDocument();
    });
});
