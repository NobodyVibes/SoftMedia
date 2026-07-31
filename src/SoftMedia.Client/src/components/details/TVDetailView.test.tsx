import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { StrictMode, useState } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import TVDetailView from './TVDetailView';
import { scrollSelectionIntoView } from '../../lib/scrollSelectionIntoView';
import api from '../../services/api';
import type { MediaItem } from '../../types';

vi.mock('../../services/api', () => ({
    default: { get: vi.fn() },
}));

const series: MediaItem = {
    id: 'series-1',
    libraryId: 'lib-1',
    title: 'Severance',
    sortTitle: 'Severance',
    dateAdded: '2026-01-01T00:00:00Z',
    type: 'Series',
};

function episode(season: number, number: number, extra: Partial<MediaItem> = {}): MediaItem {
    return {
        id: `s${season}e${number}`,
        libraryId: 'lib-1',
        title: `S${season}E${number}`,
        sortTitle: `S${season}E${number}`,
        dateAdded: '2026-01-01T00:00:00Z',
        type: 'Episode',
        seasonNumber: season,
        episodeNumber: number,
        durationSeconds: 3000,
        ...extra,
    };
}

const EPISODES = [
    episode(1, 1, { watched: true }),
    episode(1, 2, { watched: true }),
    episode(2, 1, { watched: true }),
    episode(2, 2, { progress: 42 }),
    episode(2, 3),
];

/** Mirrors MediaDetailPage: the selected episode is parent state fed back as a prop. */
function Harness({ onEpisodeSelect, ...props }: Partial<React.ComponentProps<typeof TVDetailView>>) {
    const [selectedEpisodeId, setSelectedEpisodeId] = useState<string | null>(null);
    return (
        <TVDetailView
            item={series}
            selectedEpisodeId={selectedEpisodeId}
            onEpisodeSelect={(ep) => {
                setSelectedEpisodeId(ep.id);
                onEpisodeSelect?.(ep);
            }}
            {...props}
        />
    );
}

function renderView(props: Partial<React.ComponentProps<typeof TVDetailView>> = {}, episodes = EPISODES) {
    vi.mocked(api.get).mockImplementation((url: string) => {
        if (url.includes('/episodes')) return Promise.resolve({ data: episodes } as never);
        if (url.includes('/seasons')) {
            return Promise.resolve({
                data: [
                    { number: 1, poster: null, episodeCount: 2, premiereDate: null },
                    { number: 2, poster: null, episodeCount: 3, premiereDate: null },
                ],
            } as never);
        }
        return Promise.resolve({ data: [] } as never);
    });

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        // StrictMode as in main.tsx: mount effects run twice, which is where the
        // resume selection is most fragile.
        <StrictMode>
            <QueryClientProvider client={queryClient}>
                <MemoryRouter>
                    <Harness {...props} />
                </MemoryRouter>
            </QueryClientProvider>
        </StrictMode>,
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no scrollIntoView; the component calls it optionally, so stub it
    // to observe the call.
    HTMLElement.prototype.scrollIntoView = vi.fn();
});

/**
 * A partly-watched show used to open on season 1 episode 1 no matter how far in
 * the user was. The strips now land on the episode the Play button would resume,
 * which is the server's next-episode target — the two must never disagree.
 */
describe('TVDetailView resume selection', () => {
    it('selects the season and episode of the resume target', async () => {
        const onEpisodeSelect = vi.fn();
        renderView({ resumeEpisodeId: 's2e2', onEpisodeSelect });

        // Season 2 is selected: its episodes are the ones on screen.
        expect(await screen.findByRole('button', { name: 'Episode 2: S2E2' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Episode 1: S1E1' })).not.toBeInTheDocument();

        expect(screen.getByRole('button', { name: 'Episode 2: S2E2' })).toHaveAttribute('aria-pressed', 'true');
        await waitFor(() => expect(onEpisodeSelect).toHaveBeenCalledWith(expect.objectContaining({ id: 's2e2' })));
    });

    it('leaves an untouched series on the first season with nothing highlighted', async () => {
        const unwatched = [episode(1, 1), episode(1, 2), episode(2, 1)];
        const onEpisodeSelect = vi.fn();
        renderView({ resumeEpisodeId: 's1e1', onEpisodeSelect }, unwatched);

        expect(await screen.findByRole('button', { name: 'Episode 1: S1E1' })).toHaveAttribute('aria-pressed', 'false');
        expect(onEpisodeSelect).not.toHaveBeenCalled();
    });

    // The server fills Progress in only when it knows the episode's duration, so a
    // library that never probed durations reports a resume position and no progress
    // at all. The selection must not depend on those fields.
    it('selects a resume target the episode list shows no progress for', async () => {
        const noProgressFields = [episode(1, 1), episode(1, 2), episode(2, 1)];
        renderView({ resumeEpisodeId: 's1e1', resumeHasPosition: true }, noProgressFields);

        expect(await screen.findByRole('button', { name: 'Episode 1: S1E1' })).toHaveAttribute('aria-pressed', 'true');
    });

    it('selects a next-up episode past the first one even with no position saved', async () => {
        const noProgressFields = [episode(1, 1), episode(1, 2), episode(2, 1)];
        renderView({ resumeEpisodeId: 's2e1', resumeHasPosition: false }, noProgressFields);

        expect(await screen.findByRole('button', { name: 'Episode 1: S2E1' })).toHaveAttribute('aria-pressed', 'true');
    });

    it('gives up waiting on a stalled lookup and shows season 1', async () => {
        vi.useFakeTimers({ shouldAdvanceTime: true });
        try {
            renderView({ resumeEpisodeId: undefined, resumeEpisodePending: true });
            await screen.findByText('Seasons');
            expect(screen.queryByRole('button', { name: 'Episode 1: S1E1' })).not.toBeInTheDocument();

            await act(async () => { vi.advanceTimersByTime(2000); });

            expect(await screen.findByRole('button', { name: 'Episode 1: S1E1' })).toBeInTheDocument();
        } finally {
            vi.useRealTimers();
        }
    });

    it('holds the episode list until the resume lookup settles, then applies it', async () => {
        const { rerender } = renderTwoPhase();

        // Pending: no season committed yet, so no episode rows (skeleton instead).
        await screen.findByText('Seasons');
        expect(screen.queryByRole('button', { name: 'Episode 1: S1E1' })).not.toBeInTheDocument();

        rerender(true);
        expect(await screen.findByRole('button', { name: 'Episode 3: S2E3' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Episode 1: S1E1' })).not.toBeInTheDocument();
    });

    it('keeps a manually picked season when the resume target arrives late', async () => {
        const { rerender } = renderTwoPhase();

        rerender(false, null);
        expect(await screen.findByRole('button', { name: 'Episode 1: S1E1' })).toBeInTheDocument();

        fireEvent.click(screen.getByRole('button', { name: /Season 2/ }));
        expect(await screen.findByRole('button', { name: 'Episode 3: S2E3' })).toBeInTheDocument();

        // The lookup lands afterwards pointing at season 1 — the user's pick wins.
        rerender(false, 's1e2');
        await waitFor(() => expect(screen.queryByRole('button', { name: 'Episode 2: S1E2' })).not.toBeInTheDocument());
        expect(screen.getByRole('button', { name: 'Episode 3: S2E3' })).toBeInTheDocument();
    });
});

describe('season click scrolls to the episodes', () => {
    it('brings the episode section to the top of the page on a season click', async () => {
        renderView();
        await screen.findByRole('button', { name: 'Episode 1: S1E1' });
        expect(HTMLElement.prototype.scrollIntoView).not.toHaveBeenCalled();

        fireEvent.click(screen.getByRole('button', { name: /Season 2/ }));

        expect(HTMLElement.prototype.scrollIntoView).toHaveBeenCalledWith(
            expect.objectContaining({ block: 'start' }),
        );
        expect(await screen.findByRole('button', { name: 'Episode 3: S2E3' })).toBeInTheDocument();
    });

    it('leaves the page alone when the resume target picks the season', async () => {
        renderView({ resumeEpisodeId: 's2e2' });

        await screen.findByRole('button', { name: 'Episode 2: S2E2' });
        expect(HTMLElement.prototype.scrollIntoView).not.toHaveBeenCalled();
    });
});

/**
 * The strips sit below the fold on load, so revealing a resume selection must
 * move the strip's own scroller and never the page.
 */
describe('scrollSelectionIntoView', () => {
    /** jsdom does no layout, so the box metrics are supplied by hand. */
    function buildStrip(metrics: { scrollWidth: number; clientWidth: number; scrollHeight: number; clientHeight: number }) {
        const boundary = document.createElement('div');
        const container = document.createElement('div');
        const item = document.createElement('div');
        boundary.appendChild(container);
        container.appendChild(item);

        for (const [key, value] of Object.entries(metrics)) {
            Object.defineProperty(container, key, { value, configurable: true });
        }
        Object.defineProperty(container, 'scrollLeft', { value: 0, writable: true });
        Object.defineProperty(container, 'scrollTop', { value: 0, writable: true });
        container.getBoundingClientRect = () => ({ left: 0, width: 400, top: 0, height: 200 }) as DOMRect;
        item.getBoundingClientRect = () => ({ left: 700, width: 300, top: 500, height: 100 }) as DOMRect;

        return { boundary, container, item };
    }

    it('centres the item in a horizontally scrollable strip', () => {
        const { boundary, container, item } = buildStrip({
            scrollWidth: 3000, clientWidth: 400, scrollHeight: 200, clientHeight: 200,
        });

        scrollSelectionIntoView(item, boundary);

        expect(container.scrollLeft).toBe(650); // 700 - (400 - 300) / 2
        expect(container.scrollTop).toBe(0);
    });

    it('scrolls vertically when that is the axis the container scrolls on', () => {
        const { boundary, container, item } = buildStrip({
            scrollWidth: 400, clientWidth: 400, scrollHeight: 2000, clientHeight: 200,
        });

        scrollSelectionIntoView(item, boundary);

        expect(container.scrollTop).toBe(450); // 500 - (200 - 100) / 2
        expect(container.scrollLeft).toBe(0);
    });

    it('does nothing when nothing inside the boundary scrolls — the page must not move', () => {
        const { boundary, container, item } = buildStrip({
            scrollWidth: 400, clientWidth: 400, scrollHeight: 200, clientHeight: 200,
        });
        const outer = document.createElement('div');
        outer.appendChild(boundary);
        Object.defineProperty(outer, 'scrollHeight', { value: 5000, configurable: true });
        Object.defineProperty(outer, 'clientHeight', { value: 800, configurable: true });
        Object.defineProperty(outer, 'scrollTop', { value: 0, writable: true });

        scrollSelectionIntoView(item, boundary);

        expect(container.scrollLeft).toBe(0);
        expect(container.scrollTop).toBe(0);
        expect(outer.scrollTop).toBe(0);
    });
});

/** Renders with the resume lookup in flight, returning a way to land its result. */
function renderTwoPhase() {
    vi.mocked(api.get).mockImplementation((url: string) => {
        if (url.includes('/episodes')) return Promise.resolve({ data: EPISODES } as never);
        if (url.includes('/seasons')) return Promise.resolve({ data: [] } as never);
        return Promise.resolve({ data: [] } as never);
    });

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const tree = (resumeEpisodeId: string | null, pending: boolean) => (
        <StrictMode>
            <QueryClientProvider client={queryClient}>
                <MemoryRouter>
                    <TVDetailView item={series} resumeEpisodeId={resumeEpisodeId} resumeEpisodePending={pending} />
                </MemoryRouter>
            </QueryClientProvider>
        </StrictMode>
    );

    const { rerender } = render(tree(null, true));
    return {
        rerender: (settledWithResume: boolean, id: string | null = 's2e3') =>
            rerender(tree(settledWithResume ? 's2e3' : id, false)),
    };
}
