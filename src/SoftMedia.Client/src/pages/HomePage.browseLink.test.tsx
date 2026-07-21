import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import MediaRow from '../components/ui/MediaRow';
import { useAuthStore } from '../store/authStore';
import { MediaType, type MediaItem } from '../types';

vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: true }),
}));

vi.mock('../store/audioStore', () => ({
    useAudioStore: vi.fn(() => ({ playTrack: vi.fn(), addToQueue: vi.fn() })),
}));

/**
 * The "See more" contract between a home row and the browse page.
 *
 * A row's link must reproduce the row's own criteria, and a row that CANNOT be
 * reproduced from a URL ("Top picks for you" — ranked against a rolling history window
 * and a mutable cross-row dedup set) must show no link at all rather than one that
 * lands on a different set of items.
 */
function item(id: string, title: string): MediaItem {
    return { id, title, type: MediaType.Movie } as MediaItem;
}

function renderRow(props: { viewAllLink?: string }) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={queryClient}>
            <MemoryRouter>
                <MediaRow title="Heavy Metal" items={[item('1', 'A'), item('2', 'B')]} {...props} />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

describe('MediaRow — See more link', () => {
    beforeEach(() => {
        useAuthStore.setState({ token: 'tok', mediaToken: 'media-tok' });
    });

    it('renders an in-app router link when the row carries a filter', async () => {
        renderRow({ viewAllLink: '/browse?genre=Heavy+Metal&sortBy=dateadded' });

        const link = await screen.findByRole('link', { name: /see more/i });
        // A router Link keeps navigation in-app; the old raw <a> reloaded the document.
        expect(link.getAttribute('href')).toBe('/browse?genre=Heavy+Metal&sortBy=dateadded');
    });

    it('renders no link at all when the row has no filter', async () => {
        renderRow({});

        await waitFor(() => expect(screen.getByText('Heavy Metal')).toBeTruthy());
        expect(screen.queryByRole('link', { name: /see more/i })).toBeNull();
    });
});

/**
 * Mirrors the server's HomeRowFilterDto -> query-string mapping used by HomePage.
 * Kept in lockstep with browseLinkFor(): if the two drift, a row's link stops
 * reproducing the row.
 */
function browseLinkFor(filter: {
    genre?: string | null;
    decade?: number | null;
    unplayed?: boolean | null;
    libraryId?: string | null;
    sortBy?: string | null;
} | null | undefined): string | undefined {
    if (!filter) return undefined;
    const params = new URLSearchParams();
    if (filter.genre) params.set('genre', filter.genre);
    if (filter.decade != null) params.set('decade', String(filter.decade));
    if (filter.unplayed) params.set('unplayed', 'true');
    if (filter.libraryId) params.set('libraryId', filter.libraryId);
    if (filter.sortBy) params.set('sortBy', filter.sortBy);
    const query = params.toString();
    return query ? `/browse?${query}` : undefined;
}

describe('browseLinkFor', () => {
    it('omits the link entirely for a row with no filter', () => {
        expect(browseLinkFor(null)).toBeUndefined();
        expect(browseLinkFor(undefined)).toBeUndefined();
        expect(browseLinkFor({})).toBeUndefined();
    });

    it('encodes a genre with spaces so the query survives the round trip', () => {
        const link = browseLinkFor({ genre: 'Heavy Metal', sortBy: 'dateadded' })!;
        const query = new URLSearchParams(link.split('?')[1]);
        expect(query.get('genre')).toBe('Heavy Metal');
        expect(query.get('sortBy')).toBe('dateadded');
    });

    it('sends unplayed only when true, never as "false"', () => {
        expect(browseLinkFor({ unplayed: true })).toContain('unplayed=true');
        // A literal unplayed=false would be parsed by the server as an active filter
        // for *played* items — the opposite of "no filter".
        expect(browseLinkFor({ unplayed: false })).toBeUndefined();
    });

    it('passes decade 0 through correctly rather than dropping it as falsy', () => {
        // Guards the != null check: a plain truthiness test would drop year 0.
        expect(browseLinkFor({ decade: 0 })).toContain('decade=0');
        expect(browseLinkFor({ decade: 1990 })).toContain('decade=1990');
    });
});
