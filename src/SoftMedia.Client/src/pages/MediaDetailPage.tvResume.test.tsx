import { render, screen } from '@testing-library/react';
import { StrictMode } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import MediaDetailPage from './MediaDetailPage';
import { MediaType, type MediaItem } from '../types';

vi.mock('../services/api', () => ({
    default: { get: vi.fn().mockResolvedValue({ data: [] }), post: vi.fn(), put: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock('../hooks/useMediaHub', () => ({ useMediaHub: vi.fn() }));
vi.mock('../hooks/useMediaTokenRefresh', () => ({
    useMediaTokenRefresh: vi.fn(),
    default: vi.fn(),
}));
vi.mock('../components/details/ExtraPlayerModal', () => ({ ExtraPlayerModal: () => null }));

const series = {
    id: 'series-1',
    libraryId: 'lib-1',
    title: 'Severance',
    type: MediaType.Series,
} as unknown as MediaItem;

function episode(season: number, number: number, extra: Partial<MediaItem> = {}) {
    return {
        id: `s${season}e${number}`,
        libraryId: 'lib-1',
        title: `S${season}E${number}`,
        type: MediaType.Episode,
        seasonNumber: season,
        episodeNumber: number,
        durationSeconds: 3000,
        ...extra,
    } as unknown as MediaItem;
}

const EPISODES = [
    episode(1, 1, { watched: true }),
    episode(1, 2, { watched: true }),
    episode(2, 1, { watched: true }),
    episode(2, 2, { progress: 42 }),
    episode(2, 3),
];

/**
 * Everything already in cache, exactly as it is when the user comes back to the
 * detail page from the player: the detail view has its episodes on its FIRST
 * render, so its resume selection fires during the mount commit.
 */
function renderWarm(existing?: QueryClient) {
    const queryClient = existing ?? new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(['media', 'series-1'], series);
    queryClient.setQueryData(['library', 'lib-1'], { id: 'lib-1', type: 'TV' });
    queryClient.setQueryData(['series', 'series-1', 'episodes'], EPISODES);
    queryClient.setQueryData(['series', 'series-1', 'seasons'], [
        { number: 1, poster: null, episodeCount: 2, premiereDate: null },
        { number: 2, poster: null, episodeCount: 3, premiereDate: null },
    ]);
    queryClient.setQueryData(['series', 'series-1', 'next-episode'], {
        episodeId: 's2e2',
        seasonNumber: 2,
        episodeNumber: 2,
        title: 'S2E2',
        resumePosition: 600,
        isSeriesComplete: false,
    });

    // StrictMode mirrors main.tsx — it double-invokes mount effects, which is
    // exactly the condition a warm cache puts the resume selection under.
    const { unmount } = render(
        <StrictMode>
            <QueryClientProvider client={queryClient}>
                <MemoryRouter initialEntries={['/media/series-1']}>
                    <Routes>
                        <Route path="/media/:id" element={<MediaDetailPage />} />
                    </Routes>
                </MemoryRouter>
            </QueryClientProvider>
        </StrictMode>,
    );
    return { queryClient, unmount };
}

beforeEach(() => {
    vi.clearAllMocks();
    HTMLElement.prototype.scrollIntoView = vi.fn();
});

describe('series detail page — resume selection', () => {
    it('highlights the resume episode, not just its season', async () => {
        renderWarm();

        const card = await screen.findByRole('button', { name: 'Episode 2: S2E2' });
        expect(card).toHaveAttribute('aria-pressed', 'true');
    });

    // Leaving the page and coming back is the case that broke: the second visit
    // finds every query cached, so the selection has to survive being applied
    // during the mount commit rather than after a query resolves.
    it('still selects the resume season and episode on a return visit', async () => {
        const { queryClient, unmount } = renderWarm();
        await screen.findByRole('button', { name: 'Episode 2: S2E2' });
        unmount();

        renderWarm(queryClient);

        const card = await screen.findByRole('button', { name: 'Episode 2: S2E2' });
        expect(card).toHaveAttribute('aria-pressed', 'true');
        // Season 2 is the one on screen — season 1's episodes are not.
        expect(screen.queryByRole('button', { name: 'Episode 1: S1E1' })).not.toBeInTheDocument();
    });

    it('names the resume episode on the Resume button', async () => {
        renderWarm();

        expect(await screen.findByRole('button', { name: /Resume from 10:00/ })).toHaveTextContent('S2 E2 · S2E2');
    });
});
